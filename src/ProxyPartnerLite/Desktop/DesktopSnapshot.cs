namespace ProxyPartnerLite.Desktop;

public enum NodeKind { Leaf, Group }
public enum CollectionState { Synthetic, Disconnected, Observing, Idle, SleepGap, SourceStopped, Connecting }
public enum ResultState { Missing, Measured, Stale, Incomparable, AuthenticationRequired, Restricted, Failed, HttpError, TimedOut }
public enum QualityState { Insufficient, Good, Poor, Restricted }

public sealed record DesktopQuality(int? Score, QualityState State, string Source,
    int? Cap = null, string? CapTarget = null, int TargetCount = 0, int StableTargetCount = 0);
public sealed record DesktopExitIp(string Address, DateTimeOffset ObservedUtc, int Changes,
    int Observers = 1, string? OtherAddress = null, string? LastVerifiedAddress = null,
    DateTimeOffset? ConsistentSince = null, int ConfirmedSamples = 0);
public sealed record DesktopProxyGroup(string Name, string? Selected, IReadOnlyList<string> Members);
public sealed record DesktopProxySelection(string Group, string Member, bool Confirmed);
public sealed record DesktopProxyControl(int CorePid, DateTimeOffset CoreStarted,
    IReadOnlyList<DesktopProxyGroup> Groups, DesktopProxySelection? LastSelection, string? Mode = null);

public sealed record DesktopResult(
    string? TargetId, int? ElapsedMs, int? HttpStatus, DateTimeOffset? ObservedUtc,
    string Source, ResultState State)
{
    public string DisplayText => Format(false);
    internal bool NoResponse => State is ResultState.Failed or ResultState.TimedOut ||
        State == ResultState.Stale && ElapsedMs is null;

    internal string Format(bool english) => State switch
    {
        ResultState.Missing => english ? "Untested" : "未测",
        ResultState.Stale when ElapsedMs is int ms => $"{ms} ms",
        ResultState.Stale or ResultState.Failed => english ? "No response" : "未响应",
        ResultState.Incomparable => english ? "Not comparable" : "不可比较",
        ResultState.AuthenticationRequired => ElapsedMs is >= 0 and int delay ? $"{delay} ms" : "—",
        ResultState.Restricted => (english ? $"{HttpStatus} Restricted" : $"{HttpStatus} 受限") + TimingSuffix,
        ResultState.HttpError => $"HTTP {HttpStatus}",
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
    string? CurrentTarget = null, DesktopResult? ServerPing = null, bool Testing = false,
    DesktopExitIp? ExitV4 = null, DesktopExitIp? ExitV6 = null, bool SelectionOnly = false);

public sealed record DesktopCurrentTest(bool Running, int Completed, int Total, int Verified, int TimedOut,
    bool Attempted, string? CurrentTarget, bool Direct, bool PathChanged);

public sealed record DesktopServerSweep(int Completed, int BatchTotal, bool Requested,
    string? UnavailableNode = null, int WebsiteCompleted = 0, int WebsiteTotal = 0);

public sealed record DesktopSnapshot(
    string Host, CollectionState Collection, string? VerifiedCurrentPath,
    DateTimeOffset? UpdatedUtc, IReadOnlyList<DesktopRow> Rows, DesktopCurrentTest? CurrentTest = null,
    DesktopServerSweep? ServerSweep = null, DesktopProxyControl? ProxyControl = null);

public interface IDesktopProxyControlSource
{
    void RequestProxySelection(DesktopProxyControl control, string group, string member);
}

public interface IDesktopCurrentTestSource
{
    void RequestCurrentTest();
    void RequestSweep();
    void RequestNodeTest(string node);
}

public interface IDesktopSnapshotSource
{
    // Each snapshot and its row collection must remain immutable after publication.
    DesktopSnapshot GetSnapshot();
    event Action? SnapshotChanged;
}
