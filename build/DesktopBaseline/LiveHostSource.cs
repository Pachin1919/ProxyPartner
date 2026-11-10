using ProxyPartnerLite.Desktop;
using ProxyPartnerLite.Host;

internal sealed class LiveHostSource : IDesktopSnapshotSource, IDesktopCurrentTestSource, IDisposable
{
    private static readonly DesktopResult Untested = new(null, null, null, null, "—", ResultState.Missing);
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim requested = new(0, 1);
    private readonly Task poll;
    private int queuedOrRunning;
    private DesktopSnapshot snapshot = new("Clash Verge", CollectionState.Connecting, null, null, []);

    internal LiveHostSource() => poll = Task.Run(PollAsync);

    public DesktopSnapshot GetSnapshot() => Volatile.Read(ref snapshot);
    public event Action? SnapshotChanged;

    public void RequestCurrentTest()
    {
        if (Interlocked.CompareExchange(ref queuedOrRunning, 1, 0) == 0)
            requested.Release();
    }

    private async Task PollAsync()
    {
        var client = new ReadOnlyHostClient();
        var samples = new List<WebsiteProbe>(ReadOnlyHostClient.CurrentProbeTargetCount);
        IReadOnlyList<HostGroup>? testedGroups = null;
        int? previousPid = null;
        bool pending = false;
        do
        {
            var read = await client.ReadAsync(cancellationToken: stop.Token).ConfigureAwait(false);
            if (stop.IsCancellationRequested) return;
            if (!pending) pending = requested.Wait(0);

            if (read.Facts is { } facts && read.CoreProcessId is int corePid)
            {
                if (previousPid != corePid)
                {
                    samples.Clear();
                    testedGroups = null;
                    previousPid = corePid;
                }
                if (pending)
                {
                    samples.Clear();
                    testedGroups = facts.Groups;
                    PublishFacts(facts, samples, running: true, pathChanged: false);
                    await client.MeasureCurrentPathAsync(corePid, facts, sample =>
                    {
                        samples.Add(sample);
                        PublishFacts(facts, samples, running: true, pathChanged: false);
                    }, stop.Token).ConfigureAwait(false);
                    pending = false;
                    Volatile.Write(ref queuedOrRunning, 0);
                    if (stop.IsCancellationRequested) return;
                }
                else if (testedGroups is not null && !testedGroups.Select(g => (g.Name, g.Selected))
                    .SequenceEqual(facts.Groups.Select(g => (g.Name, g.Selected))))
                {
                    testedGroups = null;
                }
                PublishFacts(facts, samples, running: false, pathChanged: samples.Count != 0 && testedGroups is null);
            }
            else if (GetSnapshot().Collection != CollectionState.Disconnected)
            {
                samples.Clear();
                testedGroups = null;
                previousPid = null;
                Publish(new("Clash Verge", CollectionState.Disconnected, null, null, []));
            }
            if (await requested.WaitAsync(TimeSpan.FromSeconds(30), stop.Token).ConfigureAwait(false))
                pending = true;
        } while (!stop.IsCancellationRequested);
    }

    private void PublishFacts(HostFacts facts, List<WebsiteProbe> samples, bool running, bool pathChanged)
    {
        WebsiteProbe? current = pathChanged ? null : samples.FirstOrDefault(s => s.Node is not null || s.Direct);
        var byNode = samples.Where(s => s.Node is not null).ToLookup(s => s.Node, StringComparer.Ordinal);
        var rows = new DesktopRow[facts.Nodes.Count + facts.Groups.Count];
        int index = 0;
        foreach (var node in facts.Nodes)
        {
            bool isCurrent = current?.Node == node.Name;
            var row = new DesktopRow("node:" + node.Name, node.Name, NodeKind.Leaf,
                isCurrent, Untested, Untested, Untested, Untested, null, CurrentTarget: isCurrent ? current?.TargetId : null);
            foreach (var sample in byNode[node.Name])
            {
                var result = new DesktopResult(sample.TargetId, sample.ElapsedMs, sample.HttpStatus, sample.ObservedUtc,
                    "HTTPS", sample.HttpStatus switch
                    {
                        401 => ResultState.AuthenticationRequired,
                        403 => ResultState.Restricted,
                        < 400 => ResultState.Measured,
                        null => ResultState.Failed,
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
                row = row with { LastObservedUtc = sample.ObservedUtc };
            }
            rows[index++] = row;
        }
        foreach (var group in facts.Groups)
            rows[index++] = new("group:" + group.Name, group.Name, NodeKind.Group, false,
                Untested, Untested, Untested, Untested, null, SelectedMember: group.Selected);

        var test = running || samples.Count != 0
            ? new DesktopCurrentTest(running, samples.Count, ReadOnlyHostClient.CurrentProbeTargetCount,
                samples.Count(s => s.Node is not null || s.Direct), samples.Any(s => s.Attempted), current?.TargetId,
                current?.Direct == true, pathChanged)
            : null;
        Publish(new("Mihomo " + facts.Version, CollectionState.Observing, current?.Node, facts.ObservedAtUtc, rows, test));
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
