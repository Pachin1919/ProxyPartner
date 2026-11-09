namespace ProxyPartnerLite.Host;

public enum HostStatus
{
    ConnectedPipe, ConnectedLoopback, HostUnavailable, IdentityRejected, AccessDenied,
    Ambiguous, FallbackAuthorizationRequired, FallbackConfigUnverified, FallbackAuthFailed,
    InvalidResponse, TransportError, TimedOut, Cancelled
}

public sealed record HostReadOptions(bool IncludeProviders = false, bool IncludeMode = false);

/// <summary>An explicit grant for a local controller-only file and a single running core.</summary>
public sealed record LoopbackFallbackAuthorization(
    int CoreProcessId, DateTimeOffset CoreStartedAtUtc, string FilePath, string ApprovedSha256);

public sealed record HostNode(string Name, string Type);
public sealed record HostGroup(string Name, string Type, string? Selected, IReadOnlyList<string> Members);
public sealed record HostProvider(string Name, IReadOnlyList<string> Members);
public sealed record HostFacts(
    DateTimeOffset ObservedAtUtc, string Version, string? Mode,
    IReadOnlyList<HostNode> Nodes, IReadOnlyList<HostGroup> Groups, IReadOnlyList<HostProvider>? Providers);

public sealed record HostRequestFact(string Path, int HttpStatus);
public sealed record HostReadResult(
    HostStatus Status, HostFacts? Facts, int? CoreProcessId, DateTimeOffset? CoreStartedAtUtc,
    int? VerifiedPeerProcessId, IReadOnlyList<HostRequestFact> Requests);

internal sealed class HostFailure(HostStatus status) : Exception
{
    internal HostStatus Status { get; } = status;
}
