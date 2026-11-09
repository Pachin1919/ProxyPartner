using ProxyPartnerLite.Desktop;

internal sealed class BaselineSource : IDesktopSnapshotSource
{
    private static readonly DateTimeOffset Reference = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Cities = ["香港", "东京", "新加坡", "洛杉矶"];
    private DesktopSnapshot? snapshot;
    public event Action? SnapshotChanged { add { } remove { } }

    public DesktopSnapshot GetSnapshot()
    {
        if (snapshot is not null) return snapshot;
        var rows = new List<DesktopRow>(102);
        for (int i = 1; i <= 100; i++)
        {
            var name = i switch
            {
                12 or 13 => "同名节点 · 东京",
                100 => "尾行 · 香港一百号",
                _ => $"节点 {i:000} · {Cities[(i - 1) % Cities.Length]}"
            };
            var latest = Reference.AddMinutes(-i);
            var native = Project(i, 0, null, latest);
            rows.Add(new($"leaf-{i:000}", name, NodeKind.Leaf, false, native,
                Project(i, 1, "openai_api", latest), Project(i, 2, "chatgpt_web", latest),
                Project(i, 3, "github", latest), latest));
        }
        var absent = new DesktopResult(null, null, null, null, "合成", ResultState.Missing);
        rows.Add(new("group-01", "策略组 · 日常工作", NodeKind.Group, false, absent, absent, absent, absent, null));
        rows.Add(new("group-02", "策略组 · 备用线路", NodeKind.Group, false, absent, absent, absent, absent, null));
        return snapshot = new("未接入宿主", CollectionState.Synthetic, null, Reference, rows.AsReadOnly());
    }

    // Seven daily observations are projected to one display value. Historical samples
    // are generated deterministically and immediately discarded.
    private static DesktopResult Project(int node, int target, string? id, DateTimeOffset latest)
    {
        DesktopResult? result = null;
        for (int day = 6; day >= 0; day--)
        {
            int delay = (node * 37 + target * 41 + day * 13) % 421;
            ResultState state = target == 0 ? ResultState.Measured : ((node + target) % 13) switch
            {
                0 => ResultState.Missing,
                1 => ResultState.Stale,
                2 => ResultState.Incomparable,
                3 => ResultState.Failed,
                _ => target switch
                {
                    1 => ResultState.AuthenticationRequired,
                    2 when node % 4 == 0 => ResultState.Restricted,
                    _ => ResultState.Measured
                }
            };
            if (node == 1 && target == 0) state = ResultState.Incomparable;
            if (node == 1 && target == 3) delay = 0;
            int? status = state switch
            {
                ResultState.AuthenticationRequired => 401,
                ResultState.Restricted => 403,
                ResultState.Measured when target != 0 => 200,
                _ => null
            };
            result = new(id, state is ResultState.Missing or ResultState.Failed or ResultState.Incomparable
                ? null : delay, status,
                state == ResultState.Missing ? null : latest.AddDays(-day - (state == ResultState.Stale ? 1 : 0)),
                target == 0 ? "原生历史 · 合成" : "HTTPS · 合成", state);
        }
        return result!;
    }
}
