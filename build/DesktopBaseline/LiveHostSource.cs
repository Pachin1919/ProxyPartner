using ProxyPartnerLite.Desktop;
using ProxyPartnerLite.Host;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

internal sealed class LiveHostSource : IDesktopSnapshotSource, IDesktopCurrentTestSource, IDesktopProxyControlSource, IDisposable
{
    private sealed record SelectionRequest(DesktopProxyControl Control, string Group, string Member);
    private static readonly DesktopResult Untested = new(null, null, null, null, "—", ResultState.Missing);
    private static readonly TimeSpan NativeFreshness = TimeSpan.FromMinutes(15);
    private const int ParallelServerLimit = 5;
    private const int ParallelWebsiteNodeLimit = 8;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim requested = new(0, 1);
    private readonly Task poll;
    private readonly object wakeGate = new();
    private int queuedOrRunning;
    private int sweepRequested;
    private string? nodeRequested;
    private SelectionRequest? selectionRequested;
    private DesktopSnapshot snapshot = new("Clash Verge", CollectionState.Connecting, null, null, []);

    internal LiveHostSource() => poll = Task.Run(PollAsync);

    public DesktopSnapshot GetSnapshot() => Volatile.Read(ref snapshot);
    public event Action? SnapshotChanged;

    public void RequestCurrentTest()
    {
        if (Interlocked.CompareExchange(ref queuedOrRunning, 1, 0) == 0)
            Wake();
    }

    public void RequestSweep()
    {
        Interlocked.Exchange(ref sweepRequested, 1);
        Interlocked.CompareExchange(ref queuedOrRunning, 1, 0);
        Wake();
    }

    public void RequestNodeTest(string node)
    {
        Interlocked.Exchange(ref nodeRequested, node);
        Wake();
    }

    public void RequestProxySelection(DesktopProxyControl control, string group, string member)
    {
        Interlocked.Exchange(ref selectionRequested, new(control, group, member));
        Wake();
    }

    private void Wake()
    {
        lock (wakeGate)
            if (requested.CurrentCount == 0) requested.Release();
    }

    private static bool TryTakeNode(ref string? priority, ref Queue<string> queue, out string name, out bool counted)
    {
        if (priority is not { } requested)
        {
            counted = true;
            return queue.TryDequeue(out name!);
        }
        name = requested;
        priority = null;
        counted = queue.Contains(requested);
        if (counted) queue = new(queue.Where(queued => queued != requested));
        return true;
    }

    private async Task PollAsync()
    {
        var client = new ReadOnlyHostClient();
        var readOptions = new HostReadOptions(IncludeMode: true);
        var samples = new List<WebsiteProbe>(ReadOnlyHostClient.CurrentProbeTargetCount);
        var history = new Dictionary<(string Node, string Target), WebsiteWindow>();
        var serverResults = new Dictionary<string, DesktopResult>(StringComparer.Ordinal);
        string serverPath = Environment.GetEnvironmentVariable("PROXYPARTNER_SERVER_TARGETS") ??
            Path.Combine(AppContext.BaseDirectory, "ServerTargets.json");
        var serverMap = ServerTargets.Load(serverPath);
        var profiles = new Dictionary<string, ProbeProfile>(StringComparer.Ordinal);
        var exitV4 = new Dictionary<string, DesktopExitIp>(StringComparer.Ordinal);
        var exitV6 = new Dictionary<string, DesktopExitIp>(StringComparer.Ordinal);
        var batch = new Queue<string>();
        var websiteBatch = new Queue<string>();
        var serverActive = new Dictionary<Task<DesktopResult>, (string Name, string Host, bool Counted)>(ParallelServerLimit);
        var websiteActive = new Dictionary<Task<(List<WebsiteProbe> Sites, IPAddress? First, IPAddress? Second)>,
            (string Name, string Revision, bool Counted)>(ParallelWebsiteNodeLimit);
        string? serverPriority = null, websitePriority = null;
        int batchTotal = 0, batchDone = 0;
        int websiteTotal = 0, websiteDone = 0;
        bool sweepStarted = false;
        string? unavailableNode = null;
        WebsiteProbe? current = null;
        Task<WebsiteProbe?>? currentTask = null;
        var currentSamples = new ConcurrentQueue<WebsiteProbe>();
        IReadOnlyList<HostGroup>? testedGroups = null;
        string? testedMode = null;
        DesktopProxySelection? lastSelection = null;
        int? previousPid = null;
        Task<bool>? wake = null;
        bool HasPendingRequest() => Volatile.Read(ref nodeRequested) is not null ||
            Volatile.Read(ref selectionRequested) is not null ||
            Volatile.Read(ref sweepRequested) != 0 || currentTask is null && Volatile.Read(ref queuedOrRunning) != 0;
        try
        {
            do
            {
                var read = await client.ReadAsync(readOptions, cancellationToken: stop.Token).ConfigureAwait(false);
                if (stop.IsCancellationRequested) return;
                if (read.Facts is { } facts && read.CoreProcessId is int corePid)
                {
                    if (previousPid != corePid)
                    {
                        await Task.WhenAll(serverActive.Keys.Cast<Task>().Concat(websiteActive.Keys)).ConfigureAwait(false);
                        if (currentTask is not null)
                        {
                            await currentTask.ConfigureAwait(false);
                            currentTask = null;
                            currentSamples.Clear();
                            Volatile.Write(ref queuedOrRunning, 0);
                        }
                        serverActive.Clear();
                        websiteActive.Clear();
                        serverPriority = websitePriority = null;
                        samples.Clear();
                        history.Clear();
                        serverResults.Clear();
                        batch.Clear();
                        websiteBatch.Clear();
                        exitV4.Clear();
                        exitV6.Clear();
                        batchTotal = batchDone = 0;
                        websiteTotal = websiteDone = 0;
                        sweepStarted = false;
                        unavailableNode = null;
                        current = null;
                        testedGroups = null;
                        lastSelection = null;
                        previousPid = corePid;
                    }
                    var command = Interlocked.Exchange(ref selectionRequested, null);
                    if (command is not null)
                    {
                        bool confirmed = read.Status == HostStatus.ConnectedPipe &&
                            await client.SelectMemberAsync(command.Control.CorePid, command.Control.CoreStarted,
                                command.Group, command.Member, stop.Token).ConfigureAwait(false);
                        lastSelection = new(command.Group, command.Member, confirmed);
                        if (confirmed)
                        {
                            current = null;
                            testedGroups = null;
                            RequestCurrentTest();
                            continue;
                        }
                    }
                    bool startSweep = Interlocked.Exchange(ref sweepRequested, 0) == 1 &&
                        batch.Count == 0 && websiteBatch.Count == 0 && serverActive.Count == 0 && websiteActive.Count == 0;
                    string? priority = Interlocked.Exchange(ref nodeRequested, null);
                    if (startSweep || priority is not null)
                    {
                        var updatedMap = ServerTargets.Load(serverPath);
                        foreach (string name in serverResults.Keys.ToArray())
                            if (!updatedMap.TryGetValue(name, out string? host) || host != serverMap[name])
                                serverResults.Remove(name);
                        serverMap = updatedMap;
                        var updatedProfiles = IndependentNodeProbe.LoadConfigured(
                            read.Status == HostStatus.ConnectedPipe && read.VerifiedPeerProcessId == corePid);
                        foreach (var (name, old) in profiles)
                        {
                            if (updatedProfiles.TryGetValue(name, out var replacement) && replacement.Revision == old.Revision)
                                continue;
                            exitV4.Remove(name);
                            exitV6.Remove(name);
                            foreach (var target in ReadOnlyHostClient.WebsiteTargets)
                            {
                                var key = (name, target.Id);
                                if (history.TryGetValue(key, out var window) && window.Latest.Independent)
                                    history.Remove(key);
                            }
                        }
                        profiles = updatedProfiles;
                    }
                    var eligible = facts.Nodes.Select(n => n.Name).Where(serverMap.ContainsKey)
                        .Order(StringComparer.Ordinal).ToArray();
                    var websiteEligible = facts.Nodes.Where(n => n.Type == "Vless" && profiles.ContainsKey(n.Name))
                        .Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
                    DesktopProxyControl? control = read.Status == HostStatus.ConnectedPipe &&
                        read.CoreStartedAtUtc is { } coreStarted
                        ? new(corePid, coreStarted, facts.Groups.Where(group => group.Type == "Selector")
                            .Select(group => new DesktopProxyGroup(group.Name, group.Selected, group.Members)).ToArray(),
                            lastSelection) : null;
                    string? globalSelection = null;
                    if (string.Equals(facts.Mode, "global", StringComparison.OrdinalIgnoreCase))
                    {
                        string? name = "GLOBAL";
                        for (int depth = 0; depth <= facts.Groups.Count && name is not null; depth++)
                        {
                            if (facts.Nodes.Any(node => node.Name == name)) { globalSelection = name; break; }
                            string? selected = facts.Groups.FirstOrDefault(group => group.Name == name)?.Selected;
                            if (selected == name) break;
                            name = selected;
                        }
                    }
                    if (priority is not null)
                    {
                        bool currentRequested = priority == current?.Node;
                        if (currentRequested) Interlocked.CompareExchange(ref queuedOrRunning, 1, 0);
                        bool canPing = Array.BinarySearch(eligible, priority, StringComparer.Ordinal) >= 0;
                        bool canProbe = websiteEligible.Contains(priority);
                        serverPriority = canPing && !serverActive.Values.Any(job => job.Name == priority) ? priority : null;
                        websitePriority = canProbe && !websiteActive.Values.Any(job => job.Name == priority) ? priority : null;
                        unavailableNode = canProbe || currentRequested ? null : priority;
                    }
                    if (startSweep) unavailableNode = null;
                    void Emit()
                    {
                        var testingNodes = serverActive.Values.Select(job => job.Name)
                            .Concat(websiteActive.Values.Select(job => job.Name)).ToArray();
                        var sweep = new DesktopServerSweep(batchDone, batchTotal, sweepStarted, unavailableNode,
                            websiteDone, websiteTotal);
                        PublishFacts(facts, samples, history.Values, serverResults, exitV4, exitV6, current,
                            sweep, running: currentTask is not null,
                            pathChanged: samples.Count != 0 && testedGroups is null, testingNodes, globalSelection, control);
                    }
                    void RecordSite(WebsiteProbe sample)
                    {
                        if (sample.Node is not { } node) return;
                        var key = (node, sample.TargetId);
                        if (history.TryGetValue(key, out var window) && window.Latest.Independent == sample.Independent)
                            window.Add(sample);
                        else history[key] = new(sample);
                    }
                    async Task<(List<WebsiteProbe> Sites, IPAddress? First, IPAddress? Second)> MeasureIndependentAsync(
                        string name, ProbeProfile profile)
                    {
                        var sites = new List<WebsiteProbe>(ReadOnlyHostClient.CurrentProbeTargetCount);
                        var observed = await IndependentNodeProbe.MeasureAsync(name, profile,
                            ReadOnlyHostClient.WebsiteTargets, IndependentNodeProbe.EchoUrl,
                            IndependentNodeProbe.CorroboratingEchoUrl, sites.Add, stop.Token)
                            .ConfigureAwait(false);
                        return (sites, observed.First, observed.Second);
                    }
                    void RecordIndependent(string name, (List<WebsiteProbe> Sites, IPAddress? First, IPAddress? Second) measured)
                    {
                        if (stop.IsCancellationRequested) return;
                        foreach (var sample in measured.Sites) RecordSite(sample);
                        if (measured.First is { } first)
                            RecordExit(name, first, measured.Second?.AddressFamily == first.AddressFamily ? measured.Second : null);
                        if (measured.Second is { } second && measured.First?.AddressFamily != second.AddressFamily)
                            RecordExit(name, second, null);
                    }
                    void RecordExit(string name, IPAddress address, IPAddress? corroboration)
                    {
                        var family = address.AddressFamily == AddressFamily.InterNetwork ? exitV4 : exitV6;
                        family[name] = UpdateExitObservation(family.GetValueOrDefault(name), address,
                            corroboration, DateTimeOffset.UtcNow);
                    }
                    if (testedGroups is not null && (testedMode != facts.Mode ||
                        !testedGroups.Select(g => (g.Name, g.Selected))
                            .SequenceEqual(facts.Groups.Select(g => (g.Name, g.Selected)))))
                    {
                        testedGroups = null;
                        current = null;
                    }
                    if (current is not null && facts.ObservedAtUtc - current.ObservedUtc > TimeSpan.FromSeconds(45))
                        current = null;
                    if (currentTask is null && Volatile.Read(ref queuedOrRunning) != 0)
                    {
                        samples.Clear();
                        testedGroups = facts.Groups;
                        testedMode = facts.Mode;
                        currentTask = client.MeasureCurrentPathAsync(corePid, facts, sample =>
                        {
                            currentSamples.Enqueue(sample);
                            Wake();
                        }, stop.Token);
                    }
                    if (startSweep)
                    {
                        batch = new Queue<string>(eligible.OrderBy(name => name != current?.Node));
                        batchTotal = batch.Count;
                        batchDone = 0;
                        websiteBatch = new Queue<string>(websiteEligible.Order(StringComparer.Ordinal)
                            .OrderBy(name => name != current?.Node));
                        websiteTotal = websiteBatch.Count;
                        websiteDone = 0;
                        sweepStarted = true;
                    }
                    while (!stop.IsCancellationRequested && !HasPendingRequest())
                    {
                        while (serverActive.Count < ParallelServerLimit &&
                            TryTakeNode(ref serverPriority, ref batch, out string name, out bool counted))
                        {
                            if (Array.BinarySearch(eligible, name, StringComparer.Ordinal) < 0)
                            {
                                if (counted) batchTotal--;
                                continue;
                            }
                            string host = serverMap[name];
                            serverActive.Add(ServerTargets.MeasureAsync(host, stop.Token), (name, host, counted));
                        }
                        while (websiteActive.Count < ParallelWebsiteNodeLimit &&
                            TryTakeNode(ref websitePriority, ref websiteBatch, out string name, out bool counted))
                        {
                            if (!websiteEligible.Contains(name))
                            {
                                if (counted) websiteTotal--;
                                continue;
                            }
                            var profile = profiles[name];
                            websiteActive.Add(MeasureIndependentAsync(name, profile), (name, profile.Revision, counted));
                        }
                        Emit();
                        if (serverActive.Count == 0 && websiteActive.Count == 0 && currentTask is null) break;

                        wake ??= requested.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
                        var pending = serverActive.Keys.Cast<Task>().Concat(websiteActive.Keys).Append(wake);
                        if (currentTask is not null) pending = pending.Append(currentTask);
                        await Task.WhenAny(pending).ConfigureAwait(false);
                        if (stop.IsCancellationRequested) return;
                        if (wake.IsCompleted)
                        {
                            await wake.ConfigureAwait(false);
                            wake = null;
                        }
                        foreach (var (finished, job) in serverActive.Where(job => job.Key.IsCompleted).ToArray())
                        {
                            var result = await finished.ConfigureAwait(false);
                            serverActive.Remove(finished);
                            if (Array.BinarySearch(eligible, job.Name, StringComparer.Ordinal) < 0)
                            {
                                if (job.Counted) batchTotal--;
                            }
                            else if (serverMap[job.Name] != job.Host)
                            {
                                if (job.Counted) batch.Enqueue(job.Name);
                            }
                            else
                            {
                                serverResults[job.Name] = result;
                                if (job.Counted) batchDone++;
                            }
                        }
                        foreach (var (finished, job) in websiteActive.Where(job => job.Key.IsCompleted).ToArray())
                        {
                            var result = await finished.ConfigureAwait(false);
                            websiteActive.Remove(finished);
                            if (!websiteEligible.Contains(job.Name))
                            {
                                if (job.Counted) websiteTotal--;
                            }
                            else if (profiles[job.Name].Revision != job.Revision)
                            {
                                if (job.Counted) websiteBatch.Enqueue(job.Name);
                            }
                            else
                            {
                                RecordIndependent(job.Name, result);
                                if (job.Counted) websiteDone++;
                            }
                        }
                        bool currentFinished = currentTask?.IsCompleted == true;
                        while (currentSamples.TryDequeue(out var sample))
                        {
                            samples.Add(sample);
                            if (testedGroups is not null) RecordSite(sample);
                        }
                        if (currentFinished)
                        {
                            var verified = await currentTask!.ConfigureAwait(false);
                            current = testedGroups is null ? null : verified ?? current;
                            currentTask = null;
                            var names = facts.Nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
                            foreach (var key in history.Keys.ToArray())
                                if (!names.Contains(key.Node)) history.Remove(key);
                            Volatile.Write(ref queuedOrRunning, 0);
                        }
                    }
                    if (HasPendingRequest()) continue;
                }
                else
                {
                    Interlocked.Exchange(ref selectionRequested, null);
                    if (GetSnapshot() is { Collection: not CollectionState.Disconnected } previous)
                    {
                        Publish(previous with
                        {
                            Collection = CollectionState.Disconnected,
                            VerifiedCurrentPath = null,
                            ProxyControl = null,
                            Rows = previous.Rows.Select(row => row.IsCurrent
                                ? row with { IsCurrent = false, CurrentTarget = null, SelectionOnly = false } : row).ToArray()
                        });
                    }
                }
                wake ??= requested.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
                await wake.ConfigureAwait(false);
                wake = null;
            } while (!stop.IsCancellationRequested);
        }
        finally
        {
            await Task.WhenAll(serverActive.Keys.Cast<Task>().Concat(websiteActive.Keys)).ConfigureAwait(false);
            if (currentTask is not null) await currentTask.ConfigureAwait(false);
        }
    }

    internal static DesktopExitIp UpdateExitObservation(DesktopExitIp? previous, IPAddress address,
        IPAddress? corroboration, DateTimeOffset now)
    {
        string value = address.ToString();
        bool agrees = address.Equals(corroboration);
        bool changed = agrees && previous?.LastVerifiedAddress is { } last && last != value;
        bool continues = agrees && !changed && previous is { ConfirmedSamples: > 0 } &&
            now - previous.ObservedUtc <= TimeSpan.FromHours(1);
        return new(value, now, (previous?.Changes ?? 0) + (changed ? 1 : 0),
            corroboration is null ? 1 : 2, agrees ? null : corroboration?.ToString(),
            agrees ? value : previous?.LastVerifiedAddress,
            continues ? previous!.ConsistentSince : agrees ? now : null,
            agrees ? (continues ? previous!.ConfirmedSamples + 1 : 1) : 0);
    }

    private void PublishFacts(HostFacts facts, List<WebsiteProbe> samples, IEnumerable<WebsiteWindow> history,
        Dictionary<string, DesktopResult> serverResults, Dictionary<string, DesktopExitIp> exitV4,
        Dictionary<string, DesktopExitIp> exitV6, WebsiteProbe? current, DesktopServerSweep sweep,
        bool running, bool pathChanged, IReadOnlyCollection<string>? testingNodes, string? globalSelection,
        DesktopProxyControl? control)
    {
        var byNode = history.ToLookup(s => s.Latest.Node, StringComparer.Ordinal);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rows = new DesktopRow[facts.Nodes.Count + facts.Groups.Count];
        int index = 0;
        foreach (var node in facts.Nodes)
        {
            bool selectionOnly = current is null && globalSelection == node.Name;
            bool isCurrent = current?.Node == node.Name || selectionOnly;
            DesktopResult native = node.Native is { } last
                ? new("native_unknown", last.ElapsedMs, null, last.ObservedUtc, "Mihomo",
                    facts.ObservedAtUtc - last.ObservedUtc > NativeFreshness ? ResultState.Stale :
                    last.ElapsedMs.HasValue ? ResultState.Measured : ResultState.Failed)
                : Untested;
            var server = serverResults.GetValueOrDefault(node.Name);
            DateTimeOffset? lastObserved = native.ObservedUtc;
            if (server?.ObservedUtc is { } serverTime && (lastObserved is null || serverTime > lastObserved))
                lastObserved = serverTime;
            var row = new DesktopRow("node:" + node.Name, node.Name, NodeKind.Leaf,
                isCurrent, native, Untested, Untested, Untested, lastObserved,
                CurrentTarget: isCurrent ? current?.TargetId : null, ServerPing: server,
                Testing: testingNodes?.Contains(node.Name) == true || isCurrent && running,
                ExitV4: exitV4.GetValueOrDefault(node.Name), ExitV6: exitV6.GetValueOrDefault(node.Name),
                SelectionOnly: selectionOnly);
            int? cap = null;
            string? capTarget = null;
            double points = 0;
            int siteCount = 0, stableCount = 0;
            bool? independentSource = null;
            bool mixedSources = false;
            foreach (var window in byNode[node.Name])
            {
                var sample = window.Latest;
                var result = new DesktopResult(sample.TargetId, sample.ElapsedMs, sample.HttpStatus, sample.ObservedUtc,
                    sample.Independent ? "VLESS · HTTPS" : "HTTPS", sample.HttpStatus switch
                    {
                        401 => ResultState.AuthenticationRequired,
                        403 => ResultState.Restricted,
                        < 400 => ResultState.Measured,
                        null => sample.TimedOut ? ResultState.TimedOut : ResultState.Failed,
                        _ => ResultState.HttpError
                    });
                row = sample.TargetId switch
                {
                    "chatgpt_web" => row with { ChatGpt = result },
                    "openai_api" => row with { OpenAi = result },
                    "google" => row with { Google = result },
                    "youtube" => row with { YouTube = result },
                    "anthropic_api" => row with { Anthropic = result },
                    _ => row
                };
                if (row.LastObservedUtc is null || sample.ObservedUtc > row.LastObservedUtc)
                    row = row with { LastObservedUtc = sample.ObservedUtc };
                if (window.Score(now) is { } score)
                {
                    independentSource ??= sample.Independent;
                    mixedSources |= independentSource != sample.Independent;
                    points += score.Score;
                    siteCount++;
                    if (score.HasStability) stableCount++;
                }
                if (sample.TargetId is "openai_api" or "chatgpt_web" or "anthropic_api" &&
                    now - sample.ObservedUtc <= WebsiteWindow.Freshness)
                {
                    int? targetCap = sample.TimedOut ? 20 : sample.ElapsedMs > 2500 ? 40 : null;
                    if (targetCap.HasValue && (cap is null || targetCap < cap))
                    {
                        cap = targetCap;
                        capTarget = sample.TargetId;
                    }
                }
            }
            if (siteCount != 0)
            {
                int? score = siteCount == ReadOnlyHostClient.CurrentProbeTargetCount && !mixedSources
                    ? Math.Min((int)Math.Round(points / siteCount), cap ?? 100) : null;
                QualityState state = score is >= 80 ? QualityState.Good : score is < 60 || cap.HasValue
                    ? QualityState.Poor : score.HasValue ? QualityState.Restricted : QualityState.Insufficient;
                row = row with { Quality = new(score, state, independentSource == true ? "VLESS · HTTPS" : "HTTPS",
                    cap, capTarget, siteCount, stableCount) };
            }
            rows[index++] = row;
        }
        foreach (var group in facts.Groups)
            rows[index++] = new("group:" + group.Name, group.Name, NodeKind.Group, false,
                Untested, Untested, Untested, Untested, null, SelectedMember: group.Selected);

        var test = running || samples.Count != 0
            ? new DesktopCurrentTest(running, samples.Count, ReadOnlyHostClient.CurrentProbeTargetCount,
                samples.Count(s => s.Node is not null || s.Direct), samples.Count(s => s.TimedOut),
                samples.Any(s => s.Attempted), current?.TargetId,
                current?.Direct == true, pathChanged)
            : null;
        Publish(new("Mihomo " + facts.Version, CollectionState.Observing, current?.Node, facts.ObservedAtUtc, rows, test, sweep, control));
    }

    private void Publish(DesktopSnapshot next)
    {
        Volatile.Write(ref snapshot, next);
        SnapshotChanged?.Invoke();
    }

    public void Dispose()
    {
        stop.Cancel();
        try { poll.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            requested.Dispose();
            stop.Dispose();
        }
    }
}
