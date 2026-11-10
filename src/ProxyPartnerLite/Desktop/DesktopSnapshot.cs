namespace ProxyPartnerLite.Desktop;

public enum NodeKind { Leaf, Group }
public enum CollectionState { Synthetic, Disconnected, Observing, Idle, SleepGap, SourceStopped, Connecting }
public enum ResultState { Missing, Measured, Stale, Incomparable, AuthenticationRequired, Restricted, Failed, HttpError }
public enum QualityState { Insufficient, Good, Poor, Restricted }

public sealed record DesktopQuality(int? Score, QualityState State, string Source);

public sealed record DesktopResult(
    string? TargetId, int? ElapsedMs, int? HttpStatus, DateTimeOffset? ObservedUtc,
    string Source, ResultState State)
{
    public string DisplayText => Format(false);

    internal string Format(bool english) => State switch
    {
        ResultState.Missing => english ? "Untested" : "未测",
        ResultState.Stale => english ? "Stale" : "已过期",
        ResultState.Incomparable => english ? "Not comparable" : "不可比较",
        ResultState.AuthenticationRequired => english ? $"{HttpStatus} Auth response" : $"{HttpStatus} 鉴权响应",
        ResultState.Restricted => english ? $"{HttpStatus} Restricted" : $"{HttpStatus} 受限",
        ResultState.HttpError => $"HTTP {HttpStatus}",
        ResultState.Failed => english ? "Measurement failed" : "测量失败",
        _ => ElapsedMs is int ms ? $"{ms} ms" : english ? "No timing" : "无耗时"
    };
}

public sealed record DesktopRow(
    string Id, string Name, NodeKind Kind, bool IsCurrent,
    DesktopResult Native, DesktopResult OpenAi, DesktopResult ChatGpt, DesktopResult GitHub,
    DateTimeOffset? LastObservedUtc, DesktopQuality? Quality = null, string? SelectedMember = null,
    DesktopResult? Google = null, DesktopResult? YouTube = null, DesktopResult? Anthropic = null,
    string? CurrentTarget = null);

public sealed record DesktopCurrentTest(bool Running, int Completed, int Total, int Verified,
    bool Attempted, string? CurrentTarget, bool Direct, bool PathChanged);

public sealed record DesktopSnapshot(
    string Host, CollectionState Collection, string? VerifiedCurrentPath,
    DateTimeOffset? UpdatedUtc, IReadOnlyList<DesktopRow> Rows, DesktopCurrentTest? CurrentTest = null);

public interface IDesktopCurrentTestSource
{
    void RequestCurrentTest();
}

public interface IDesktopSnapshotSource
{
    // Each snapshot and its row collection must remain immutable after publication.
    DesktopSnapshot GetSnapshot();
    event Action? SnapshotChanged;
}
