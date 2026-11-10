using ProxyPartnerLite.Desktop;
using ProxyPartnerLite.Host;
using System.Net.Sockets;

internal sealed class LiveHostSource : IDesktopSnapshotSource, IDesktopCurrentTestSource, IDisposable
{
    private static readonly DesktopResult Untested = new(null, null, null, null, "—", ResultState.Missing);
    private static readonly TimeSpan NativeFreshness = TimeSpan.FromMinutes(15);
    private const int ParallelServerLimit = 5;
    private const int ParallelWebsiteNodeLimit = 1;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim requested = new(0, 1);
    private readonly Task poll;
    private readonly object wakeGate = new();
    private int queuedOrRunning;
    private int sweepRequested;
    private string? nodeRequested;
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

    private void Wake()
    {
        lock (wakeGate)
            if (requested.CurrentCount == 0) requested.Release();
    }

    private async Task PollAsync()
    {
        var client = new ReadOnlyHostClient();
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
        int batchTotal = 0, batchDone = 0;
        int websiteTotal = 0, websiteDone = 0;
        bool sweepStarted = false;
        string? unavailableNode = null;
        WebsiteProbe? current = null;
        IReadOnlyList<HostGroup>? testedGroups = null;
        int? previousPid = null;
        do
        {
            var read = await client.ReadAsync(cancellationToken: stop.Token).ConfigureAwait(false);
            if (stop.IsCancellationRequested) return;
            if (read.Facts is { } facts && read.CoreProcessId is int corePid)
            {
                if (previousPid != corePid)
                {
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
                    previousPid = corePid;
                }
                bool startSweep = Interlocked.Exchange(ref sweepRequested, 0) == 1 &&
                    batch.Count == 0 && websiteBatch.Count == 0;
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
                string? pingPriority = null;
                if (priority is not null)
                {
                    bool currentRequested = priority == current?.Node;
                    if (currentRequested) Interlocked.CompareExchange(ref queuedOrRunning, 1, 0);
                    bool canPing = Array.BinarySearch(eligible, priority, StringComparer.Ordinal) >= 0;
                    if (canPing) pingPriority = priority;
                    unavailableNode = websiteEligible.Contains(priority) || currentRequested ? null : priority;
                }
                if (startSweep) unavailableNode = null;
                void Emit(bool running, IReadOnlyCollection<string>? testingNodes = null)
                {
                    var sweep = new DesktopServerSweep(batchDone, batchTotal, sweepStarted, unavailableNode,
                        websiteDone, websiteTotal);
                    PublishFacts(facts, samples, history.Values, serverResults, exitV4, exitV6, current,
                        sweep, running, pathChanged: samples.Count != 0 && testedGroups is null, testingNodes);
                }
                void RecordSite(WebsiteProbe sample)
                {
                    if (sample.Node is not { } node) return;
                    var key = (node, sample.TargetId);
                    if (history.TryGetValue(key, out var window) && window.Latest.Independent == sample.Independent)
                        window.Add(sample);
                    else history[key] = new(sample);
                }
                async Task<(List<WebsiteProbe> Sites, System.Net.IPAddress? Address)> MeasureIndependentAsync(string name)
                {
                    var sites = new List<WebsiteProbe>(ReadOnlyHostClient.CurrentProbeTargetCount);
                    var address = await IndependentNodeProbe.MeasureAsync(name, profiles[name],
                        ReadOnlyHostClient.WebsiteTargets, IndependentNodeProbe.EchoUrl, sites.Add, stop.Token)
                        .ConfigureAwait(false);
                    return (sites, address);
                }
                void RecordIndependent(string name, (List<WebsiteProbe> Sites, System.Net.IPAddress? Address) measured)
                {
                    if (stop.IsCancellationRequested) return;
                    foreach (var sample in measured.Sites) RecordSite(sample);
                    if (measured.Address is not { } address) return;
                    var family = address.AddressFamily == AddressFamily.InterNetwork ? exitV4 : exitV6;
                    string value = address.ToString();
                    var previous = family.GetValueOrDefault(name);
                    int changes = previous is null ? 0 : previous.Changes + (previous.Address == value ? 0 : 1);
                    family[name] = new(value, DateTimeOffset.UtcNow, changes);
                }
                if (Volatile.Read(ref queuedOrRunning) == 1)
                {
                    samples.Clear();
                    testedGroups = facts.Groups;
                    Emit(running: true);
                    current = await client.MeasureCurrentPathAsync(corePid, facts, sample =>
                    {
                        samples.Add(sample);
                        RecordSite(sample);
                        Emit(running: true);
                    }, stop.Token).ConfigureAwait(false);
                    var names = facts.Nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (var key in history.Keys.ToArray())
                        if (!names.Contains(key.Node)) history.Remove(key);
                    Volatile.Write(ref queuedOrRunning, 0);
                    if (stop.IsCancellationRequested) return;
                }
                else if (testedGroups is not null && !testedGroups.Select(g => (g.Name, g.Selected))
                    .SequenceEqual(facts.Groups.Select(g => (g.Name, g.Selected))))
                {
                    testedGroups = null;
                    current = null;
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
                if (priority is not null && websiteEligible.Contains(priority))
                {
                    bool queued = websiteBatch.Contains(priority, StringComparer.Ordinal);
                    if (queued) websiteBatch = new Queue<string>(websiteBatch.Where(name => name != priority));
                    Emit(running: false, testingNodes: [priority]);
                    RecordIndependent(priority, await MeasureIndependentAsync(priority).ConfigureAwait(false));
                    if (queued) websiteDone++;
                }
                if (pingPriority is not null && batch.Contains(pingPriority, StringComparer.Ordinal))
                    batch = new Queue<string>(batch.Where(name => name != pingPriority).Prepend(pingPriority));
                else if (pingPriority is not null)
                {
                    Emit(running: false, testingNodes: [pingPriority]);
                    serverResults[pingPriority] = await ServerTargets.MeasureAsync(serverMap[pingPriority], stop.Token)
                        .ConfigureAwait(false);
                }
                Emit(running: false);
                if (batch.Count != 0)
                {
                    var active = new Dictionary<Task<DesktopResult>, string>(ParallelServerLimit);
                    while (true)
                    {
                        while (active.Count < ParallelServerLimit && !stop.IsCancellationRequested &&
                            Volatile.Read(ref nodeRequested) is null && Volatile.Read(ref queuedOrRunning) == 0 &&
                            batch.TryDequeue(out string? name))
                        {
                            if (Array.BinarySearch(eligible, name, StringComparer.Ordinal) < 0)
                            {
                                batchTotal--;
                                continue;
                            }
                            active.Add(ServerTargets.MeasureAsync(serverMap[name], stop.Token), name);
                        }
                        if (active.Count == 0) break;

                        Emit(running: false, testingNodes: active.Values);
                        var finished = await Task.WhenAny(active.Keys).ConfigureAwait(false);
                        if (stop.IsCancellationRequested)
                        {
                            await Task.WhenAll(active.Keys).ConfigureAwait(false);
                            return;
                        }
                        serverResults[active[finished]] = await finished.ConfigureAwait(false);
                        active.Remove(finished);
                        batchDone++;
                    }
                    Emit(running: false);
                }
                if (websiteBatch.Count != 0)
                {
                    var active = new Dictionary<Task<(List<WebsiteProbe> Sites, System.Net.IPAddress? Address)>, string>(
                        ParallelWebsiteNodeLimit);
                    while (true)
                    {
                        while (active.Count < ParallelWebsiteNodeLimit && !stop.IsCancellationRequested &&
                            Volatile.Read(ref nodeRequested) is null && Volatile.Read(ref queuedOrRunning) == 0 &&
                            websiteBatch.TryDequeue(out string? websiteNode))
                        {
                            if (!websiteEligible.Contains(websiteNode))
                            {
                                websiteTotal--;
                                continue;
                            }
                            active.Add(MeasureIndependentAsync(websiteNode), websiteNode);
                        }
                        if (active.Count == 0) break;

                        Emit(running: false, testingNodes: active.Values);
                        var finished = await Task.WhenAny(active.Keys).ConfigureAwait(false);
                        if (stop.IsCancellationRequested)
                        {
                            await Task.WhenAll(active.Keys).ConfigureAwait(false);
                            return;
                        }
                        RecordIndependent(active[finished], await finished.ConfigureAwait(false));
                        active.Remove(finished);
                        websiteDone++;
                    }
                    Emit(running: false);
                }
            }
            else if (GetSnapshot() is { Collection: not CollectionState.Disconnected } previous)
            {
                Publish(previous with
                {
                    Collection = CollectionState.Disconnected,
                    VerifiedCurrentPath = null,
                    Rows = previous.Rows.Select(row => row.IsCurrent
                        ? row with { IsCurrent = false, CurrentTarget = null } : row).ToArray()
                });
            }
            await requested.WaitAsync(TimeSpan.FromSeconds(30), stop.Token).ConfigureAwait(false);
        } while (!stop.IsCancellationRequested);
    }

    private void PublishFacts(HostFacts facts, List<WebsiteProbe> samples, IEnumerable<WebsiteWindow> history,
        Dictionary<string, DesktopResult> serverResults, Dictionary<string, DesktopExitIp> exitV4,
        Dictionary<string, DesktopExitIp> exitV6, WebsiteProbe? current, DesktopServerSweep sweep,
        bool running, bool pathChanged, IReadOnlyCollection<string>? testingNodes)
    {
        var byNode = history.ToLookup(s => s.Latest.Node, StringComparer.Ordinal);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rows = new DesktopRow[facts.Nodes.Count + facts.Groups.Count];
        int index = 0;
        foreach (var node in facts.Nodes)
        {
            bool isCurrent = current?.Node == node.Name;
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
                ExitV4: exitV4.GetValueOrDefault(node.Name), ExitV6: exitV6.GetValueOrDefault(node.Name));
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
        Publish(new("Mihomo " + facts.Version, CollectionState.Observing, current?.Node, facts.ObservedAtUtc, rows, test, sweep));
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
