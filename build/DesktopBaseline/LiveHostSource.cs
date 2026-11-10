using ProxyPartnerLite.Desktop;
using ProxyPartnerLite.Host;

internal sealed class LiveHostSource : IDesktopSnapshotSource, IDesktopCurrentTestSource, IDisposable
{
    private static readonly DesktopResult Untested = new(null, null, null, null, "—", ResultState.Missing);
    private static readonly TimeSpan NativeFreshness = TimeSpan.FromMinutes(15);
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
        var batch = new Queue<string>();
        int batchTotal = 0, batchDone = 0;
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
                    batchTotal = batchDone = 0;
                    sweepStarted = false;
                    unavailableNode = null;
                    current = null;
                    testedGroups = null;
                    previousPid = corePid;
                }
                bool startSweep = Interlocked.Exchange(ref sweepRequested, 0) == 1 && batch.Count == 0;
                string? priority = Interlocked.Exchange(ref nodeRequested, null);
                if (startSweep || priority is not null)
                {
                    var updatedMap = ServerTargets.Load(serverPath);
                    foreach (string name in serverResults.Keys.ToArray())
                        if (!updatedMap.TryGetValue(name, out string? host) || host != serverMap[name])
                            serverResults.Remove(name);
                    serverMap = updatedMap;
                }
                var eligible = facts.Nodes.Select(n => n.Name).Where(serverMap.ContainsKey)
                    .Order(StringComparer.Ordinal).ToArray();
                if (priority is not null)
                {
                    bool currentRequested = priority == current?.Node;
                    if (currentRequested) Interlocked.CompareExchange(ref queuedOrRunning, 1, 0);
                    bool canPing = Array.BinarySearch(eligible, priority, StringComparer.Ordinal) >= 0;
                    unavailableNode = canPing || currentRequested ? null : priority;
                    if (!canPing) priority = null;
                }
                if (startSweep) unavailableNode = null;
                void Emit(bool running, string? testingNode = null)
                {
                    var sweep = new DesktopServerSweep(batchDone, batchTotal, sweepStarted, unavailableNode);
                    PublishFacts(facts, samples, history.Values, serverResults, current, sweep,
                        running, pathChanged: samples.Count != 0 && testedGroups is null, testingNode);
                }
                if (Volatile.Read(ref queuedOrRunning) == 1)
                {
                    samples.Clear();
                    testedGroups = facts.Groups;
                    bool verified = false;
                    Emit(running: true);
                    await client.MeasureCurrentPathAsync(corePid, facts, sample =>
                    {
                        samples.Add(sample);
                        if (sample.Node is { } node)
                        {
                            var key = (node, sample.TargetId);
                            if (history.TryGetValue(key, out var window)) window.Add(sample);
                            else history[key] = new(sample);
                        }
                        if (!verified && (sample.Node is not null || sample.Direct))
                        {
                            current = sample;
                            verified = true;
                        }
                        Emit(running: true);
                    }, stop.Token).ConfigureAwait(false);
                    if (!verified) current = null;
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
                    sweepStarted = true;
                }
                if (priority is not null && batch.Contains(priority, StringComparer.Ordinal))
                    batch = new Queue<string>(batch.Where(name => name != priority).Prepend(priority));
                else if (priority is not null)
                {
                    Emit(running: false, testingNode: priority);
                    serverResults[priority] = await ServerTargets.MeasureAsync(serverMap[priority], stop.Token)
                        .ConfigureAwait(false);
                }
                Emit(running: false);
                while (Volatile.Read(ref nodeRequested) is null && Volatile.Read(ref queuedOrRunning) == 0 &&
                    batch.TryDequeue(out string? name))
                {
                    if (Array.BinarySearch(eligible, name, StringComparer.Ordinal) < 0)
                    {
                        batchTotal--;
                        continue;
                    }
                    Emit(running: false, testingNode: name);
                    serverResults[name] = await ServerTargets.MeasureAsync(serverMap[name], stop.Token)
                        .ConfigureAwait(false);
                    batchDone++;
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
        Dictionary<string, DesktopResult> serverResults, WebsiteProbe? current, DesktopServerSweep sweep,
        bool running, bool pathChanged, string? testingNode)
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
                Testing: node.Name == testingNode || isCurrent && running);
            int? cap = null;
            string? capTarget = null;
            double points = 0;
            int siteCount = 0, stableCount = 0;
            foreach (var window in byNode[node.Name])
            {
                var sample = window.Latest;
                var result = new DesktopResult(sample.TargetId, sample.ElapsedMs, sample.HttpStatus, sample.ObservedUtc,
                    "HTTPS", sample.HttpStatus switch
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
                int? score = siteCount == ReadOnlyHostClient.CurrentProbeTargetCount
                    ? Math.Min((int)Math.Round(points / siteCount), cap ?? 100) : null;
                QualityState state = score is >= 80 ? QualityState.Good : score is < 60 || cap.HasValue
                    ? QualityState.Poor : score.HasValue ? QualityState.Restricted : QualityState.Insufficient;
                row = row with { Quality = new(score, state, "HTTPS", cap, capTarget, siteCount, stableCount) };
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
