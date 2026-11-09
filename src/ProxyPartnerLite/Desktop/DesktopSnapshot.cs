namespace ProxyPartnerLite.Desktop;

public enum NodeKind { Leaf, Group }
public enum CollectionState { Synthetic, Disconnected, Observing, Idle, SleepGap, SourceStopped }
public enum ResultState { Missing, Measured, Stale, Incomparable, AuthenticationRequired, Restricted, Failed }

public sealed record DesktopResult(
    string? TargetId, int? ElapsedMs, int? HttpStatus, DateTimeOffset? ObservedUtc,
    string Source, ResultState State)
{
    public string DisplayText => State switch
    {
        ResultState.Missing => "未测",
        ResultState.Stale => "已过期",
        ResultState.Incomparable => "不可比较",
        ResultState.AuthenticationRequired => $"{HttpStatus} 鉴权响应",
        ResultState.Restricted => $"{HttpStatus} 受限",
        ResultState.Failed => "测量失败",
        _ => ElapsedMs is int ms ? $"{ms} ms" : "无耗时"
    };
}

public sealed record DesktopRow(
    string Id, string Name, NodeKind Kind, bool IsCurrent,
    DesktopResult Native, DesktopResult OpenAi, DesktopResult ChatGpt, DesktopResult GitHub,
    DateTimeOffset? LastObservedUtc);

public sealed record DesktopSnapshot(
    string Host, CollectionState Collection, string? VerifiedCurrentPath,
    DateTimeOffset? UpdatedUtc, IReadOnlyList<DesktopRow> Rows);

public interface IDesktopSnapshotSource
{
    // Each snapshot and its row collection must remain immutable after publication.
    DesktopSnapshot GetSnapshot();
    event Action? SnapshotChanged;
}
