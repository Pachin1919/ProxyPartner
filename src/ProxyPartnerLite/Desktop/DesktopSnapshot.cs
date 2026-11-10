namespace ProxyPartnerLite.Desktop;

public enum NodeKind { Leaf, Group }
public enum CollectionState { Synthetic, Disconnected, Observing, Idle, SleepGap, SourceStopped, Connecting }
public enum ResultState { Missing, Measured, Stale, Incomparable, AuthenticationRequired, Restricted, Failed, HttpError, TimedOut }
public enum QualityState { Insufficient, Good, Poor, Restricted }

public sealed record DesktopQuality(int? Score, QualityState State, string Source,
    int? Cap = null, string? CapTarget = null);

public sealed record DesktopResult(
    string? TargetId, int? ElapsedMs, int? HttpStatus, DateTimeOffset? ObservedUtc,
    string Source, ResultState State)
{
    public string DisplayText => Format(false);

    internal string Format(bool english) => State switch
    {
        ResultState.Missing => english ? "Untested" : "未测",
        ResultState.Stale when ElapsedMs is > 0 => english ? $"{ElapsedMs} ms · Stale" : $"{ElapsedMs} ms · 已过期",
        ResultState.Stale => english ? "Stale" : "已过期",
        ResultState.Incomparable => english ? "Not comparable" : "不可比较",
        ResultState.AuthenticationRequired => (TargetId is "openai_api" or "anthropic_api"
            ? english ? $"{HttpStatus} API key required" : $"{HttpStatus} 需 API 密钥"
            : english ? $"{HttpStatus} Authentication required" : $"{HttpStatus} 需认证") + TimingSuffix,
        ResultState.Restricted => (english ? $"{HttpStatus} Restricted" : $"{HttpStatus} 受限") + TimingSuffix,
        ResultState.HttpError => $"HTTP {HttpStatus}",
        ResultState.Failed => english ? "Measurement failed" : "测量失败",
        ResultState.TimedOut => english ? "Timed out" : "超时",
        _ => ElapsedMs is int ms ? $"{ms} ms" : english ? "No timing" : "无耗时"
    };

    private string TimingSuffix => ElapsedMs is int ms ? $" · {ms} ms" : "";
}

public sealed record DesktopRow(
    string Id, string Name, NodeKind Kind, bool IsCurrent,
    DesktopResult Native, DesktopResult OpenAi, DesktopResult ChatGpt, DesktopResult GitHub,
    DateTimeOffset? LastObservedUtc, DesktopQuality? Quality = null, string? SelectedMember = null,
    DesktopResult? Google = null, DesktopResult? YouTube = null, DesktopResult? Anthropic = null,
    string? CurrentTarget = null, DesktopResult? ServerPing = null);

public sealed record DesktopCurrentTest(bool Running, int Completed, int Total, int Verified, int TimedOut,
    bool Attempted, string? CurrentTarget, bool Direct, bool PathChanged);

public sealed record DesktopServerSweep(int Eligible, int Observed, int Completed, int BatchTotal, bool Requested);

public sealed record DesktopSnapshot(
    string Host, CollectionState Collection, string? VerifiedCurrentPath,
    DateTimeOffset? UpdatedUtc, IReadOnlyList<DesktopRow> Rows, DesktopCurrentTest? CurrentTest = null,
    DesktopServerSweep? ServerSweep = null);

public interface IDesktopCurrentTestSource
{
    void RequestCurrentTest();
    void RequestServerSweep();
}

public interface IDesktopSnapshotSource
{
    // Each snapshot and its row collection must remain immutable after publication.
    DesktopSnapshot GetSnapshot();
    event Action? SnapshotChanged;
}
