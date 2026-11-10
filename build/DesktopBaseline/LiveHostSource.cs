using ProxyPartnerLite.Desktop;
using ProxyPartnerLite.Host;

internal sealed class LiveHostSource : IDesktopSnapshotSource, IDesktopCurrentTestSource, IDisposable
{
    private static readonly DesktopResult Untested = new(null, null, null, null, "—", ResultState.Missing);
    private static readonly TimeSpan NativeFreshness = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ServerInterval = TimeSpan.FromSeconds(10);
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim requested = new(0, 1);
    private readonly Task poll;
    private readonly object wakeGate = new();
    private int queuedOrRunning;
    private int sweepRequested;
    private DesktopSnapshot snapshot = new("Clash Verge", CollectionState.Connecting, null, null, []);

    internal LiveHostSource() => poll = Task.Run(PollAsync);

    public DesktopSnapshot GetSnapshot() => Volatile.Read(ref snapshot);
    public event Action? SnapshotChanged;

    public void RequestCurrentTest()
    {
        if (Interlocked.CompareExchange(ref queuedOrRunning, 1, 0) == 0)
            Wake();
    }

    public void RequestServerSweep()
    {
        Interlocked.Exchange(ref sweepRequested, 1);
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
        var latest = new Dictionary<(string Node, string Target), WebsiteProbe>();
        var serverResults = new Dictionary<string, DesktopResult>(StringComparer.Ordinal);
        string serverPath = Environment.GetEnvironmentVariable("PROXYPARTNER_SERVER_TARGETS") ??
            Path.Combine(AppContext.BaseDirectory, "ServerTargets.json");
        var serverMap = ServerTargets.Load(serverPath);
        var manual = new Queue<string>();
        int batchTotal = 0, batchDone = 0;
        bool manualRequested = false;
        string? lastServer = null;
        DateTimeOffset nextServer = DateTimeOffset.MinValue;
        WebsiteProbe? current = null;
        IReadOnlyList<HostGroup>? testedGroups = null;
        int? previousPid = null;
        do
        {
            var read = await client.ReadAsync(cancellationToken: stop.Token).ConfigureAwait(false);
            if (stop.IsCancellationRequested) return;
            TimeSpan wait = TimeSpan.FromSeconds(30);

            if (read.Facts is { } facts && read.CoreProcessId is int corePid)
            {
                if (previousPid != corePid)
                {
                    samples.Clear();
                    latest.Clear();
                    serverResults.Clear();
                    manual.Clear();
                    batchTotal = batchDone = 0;
                    manualRequested = false;
                    lastServer = null;
                    nextServer = DateTimeOffset.MinValue;
                    current = null;
                    testedGroups = null;
                    previousPid = corePid;
                }
                bool startSweep = Interlocked.Exchange(ref sweepRequested, 0) == 1 && manual.Count == 0;
                if (startSweep)
                {
                    var updatedMap = ServerTargets.Load(serverPath);
                    foreach (string name in serverResults.Keys.ToArray())
                        if (!updatedMap.TryGetValue(name, out string? host) || host != serverMap[name])
                            serverResults.Remove(name);
                    serverMap = updatedMap;
                }
                var eligible = facts.Nodes.Select(n => n.Name).Where(serverMap.ContainsKey)
                    .Order(StringComparer.Ordinal).ToArray();
                if (startSweep)
                {
                    manual = new Queue<string>(eligible);
                    batchTotal = eligible.Length;
                    batchDone = 0;
                    manualRequested = true;
                }
                void Emit(bool running)
                {
                    var sweep = new DesktopServerSweep(eligible.Length,
                        eligible.Count(serverResults.ContainsKey), batchDone, batchTotal, manualRequested);
                    PublishFacts(facts, samples, latest.Values, serverResults, current, sweep,
                        running, pathChanged: samples.Count != 0 && testedGroups is null);
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
                        if (sample.Node is { } node) latest[(node, sample.TargetId)] = sample;
                        if (!verified && (sample.Node is not null || sample.Direct))
                        {
                            current = sample;
                            verified = true;
                        }
                        Emit(running: true);
                    }, stop.Token).ConfigureAwait(false);
                    if (!verified) current = null;
                    var names = facts.Nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (var key in latest.Keys.ToArray())
                        if (!names.Contains(key.Node)) latest.Remove(key);
                    Volatile.Write(ref queuedOrRunning, 0);
                    if (stop.IsCancellationRequested) return;
                }
                else if (testedGroups is not null && !testedGroups.Select(g => (g.Name, g.Selected))
                    .SequenceEqual(facts.Groups.Select(g => (g.Name, g.Selected))))
                {
                    testedGroups = null;
                    current = null;
                }
                Emit(running: false);
                if (eligible.Length != 0)
                {
                    if (DateTimeOffset.UtcNow >= nextServer)
                    {
                        while (manual.TryPeek(out string? skipped) &&
                            Array.BinarySearch(eligible, skipped, StringComparer.Ordinal) < 0)
                        {
                            manual.Dequeue();
                            batchTotal--;
                        }
                        bool manualStep = manual.TryDequeue(out string? name);
                        name ??= eligible.FirstOrDefault(n => StringComparer.Ordinal.Compare(n, lastServer) > 0)
                            ?? eligible[0];
                        lastServer = name;
                        nextServer = DateTimeOffset.UtcNow + ServerInterval;
                        serverResults[name] = await ServerTargets.MeasureAsync(serverMap[name], stop.Token)
                            .ConfigureAwait(false);
                        if (manualStep) batchDone++;
                        Emit(running: false);
                    }
                    wait = nextServer - DateTimeOffset.UtcNow;
                    if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
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
            await requested.WaitAsync(wait, stop.Token).ConfigureAwait(false);
        } while (!stop.IsCancellationRequested);
    }

    private void PublishFacts(HostFacts facts, List<WebsiteProbe> samples, IEnumerable<WebsiteProbe> latest,
        Dictionary<string, DesktopResult> serverResults, WebsiteProbe? current, DesktopServerSweep sweep,
        bool running, bool pathChanged)
    {
        var byNode = latest.ToLookup(s => s.Node, StringComparer.Ordinal);
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
                CurrentTarget: isCurrent ? current?.TargetId : null, ServerPing: server);
            int? cap = null;
            string? capTarget = null;
            foreach (var sample in byNode[node.Name])
            {
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
                if (sample.TargetId is "openai_api" or "chatgpt_web" or "anthropic_api" &&
                    facts.ObservedAtUtc - sample.ObservedUtc <= NativeFreshness)
                {
                    int? targetCap = sample.TimedOut ? 20 : sample.ElapsedMs > 2500 ? 40 : null;
                    if (targetCap.HasValue && (cap is null || targetCap < cap))
                    {
                        cap = targetCap;
                        capTarget = sample.TargetId;
                    }
                }
            }
            if (cap.HasValue)
                row = row with { Quality = new(null, QualityState.Poor, "HTTPS", cap, capTarget) };
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
