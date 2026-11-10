using ProxyPartnerLite.Desktop;
using ProxyPartnerLite.Host;

internal sealed class LiveHostSource : IDesktopSnapshotSource, IDisposable
{
    private static readonly DesktopResult Untested = new(null, null, null, null, "—", ResultState.Missing);
    private readonly CancellationTokenSource stop = new();
    private readonly Task poll;
    private DesktopSnapshot snapshot = new("Clash Verge", CollectionState.Connecting, null, null, []);

    internal LiveHostSource() => poll = Task.Run(PollAsync);

    public DesktopSnapshot GetSnapshot() => Volatile.Read(ref snapshot);
    public event Action? SnapshotChanged;

    private async Task PollAsync()
    {
        var client = new ReadOnlyHostClient();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            var read = await client.ReadAsync(cancellationToken: stop.Token).ConfigureAwait(false);
            if (stop.IsCancellationRequested) return;

            DesktopSnapshot next;
            if (read.Facts is { } facts)
            {
                var rows = new DesktopRow[facts.Nodes.Count + facts.Groups.Count];
                int index = 0;
                foreach (var node in facts.Nodes)
                    rows[index++] = new("node:" + node.Name, node.Name, NodeKind.Leaf, false,
                        Untested, Untested, Untested, Untested, null);
                foreach (var group in facts.Groups)
                    rows[index++] = new("group:" + group.Name, group.Name, NodeKind.Group, false,
                        Untested, Untested, Untested, Untested, null, SelectedMember: group.Selected);
                next = new("Mihomo " + facts.Version, CollectionState.Observing, null, facts.ObservedAtUtc, rows);
            }
            else
            {
                if (GetSnapshot().Collection == CollectionState.Disconnected) continue;
                next = new("Clash Verge", CollectionState.Disconnected, null, null, []);
            }
            Volatile.Write(ref snapshot, next);
            SnapshotChanged?.Invoke();
        } while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false));
    }

    public void Dispose()
    {
        stop.Cancel();
        try { poll.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { stop.Dispose(); }
    }
}
