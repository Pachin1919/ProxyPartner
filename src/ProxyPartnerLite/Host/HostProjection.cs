using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyPartnerLite.Host;

internal sealed record VersionWire(string? Version);
internal sealed record NativeHistoryWire(string? Time, int? Delay);
internal sealed record ProxyWire(string? Name, string? Type, string? Now, string[]? All, NativeHistoryWire?[]? History = null);
internal sealed record ProxiesWire(Dictionary<string, ProxyWire?>? Proxies);
internal sealed record ProviderMemberWire(string? Name);
internal sealed record ProviderWire(string? VehicleType, ProviderMemberWire?[]? Proxies);
internal sealed record ProvidersWire(Dictionary<string, ProviderWire?>? Providers);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 32)]
[JsonSerializable(typeof(VersionWire))]
[JsonSerializable(typeof(ProxiesWire))]
[JsonSerializable(typeof(ProvidersWire))]
internal sealed partial class HostJson : JsonSerializerContext { }

internal static class HostProjection
{
    internal static string Version(VersionWire wire)
    {
        string version = Text(wire.Version);
        string number = version.StartsWith('v') ? version[1..] : version;
        if (version.Length > 64 || !version.All(c => char.IsAsciiLetterOrDigit(c) || ".-+".Contains(c)) ||
            !System.Version.TryParse(number.Split('-', '+')[0], out var parsed) || parsed.Build < 0)
            throw new HostFailure(HostStatus.InvalidResponse);
        return version;
    }

    internal static (HostNode[] Nodes, HostGroup[] Groups) Proxies(ProxiesWire wire)
    {
        if (wire.Proxies is null || wire.Proxies.Count > 2005)
            throw new HostFailure(HostStatus.InvalidResponse);
        var nodes = new List<HostNode>();
        var groups = new List<HostGroup>();
        foreach (var (name, proxy) in wire.Proxies)
        {
            Text(name);
            string type = Text(proxy?.Type);
            if (proxy!.All is { } members)
            {
                if (members.Length > 1000)
                    throw new HostFailure(HostStatus.InvalidResponse);
                foreach (string member in members)
                    Text(member);
                string? selected = string.IsNullOrEmpty(proxy.Now) ? null : Text(proxy.Now);
                groups.Add(new HostGroup(name, type, selected, members));
            }
            else if (type is not ("Direct" or "Reject" or "RejectDrop" or "Pass" or "Compatible"))
            {
                HostNativeSample? native = null;
                if (proxy.History is [.., { Time: { } time, Delay: >= 0 and < 65535 } sample] &&
                    DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var observed) &&
                    observed > DateTimeOffset.UnixEpoch && observed <= DateTimeOffset.UtcNow.AddMinutes(1))
                    native = new HostNativeSample(observed, sample.Delay is > 0 ? sample.Delay : null);
                nodes.Add(new HostNode(name, type, native));
            }
        }
        if (nodes.Count > 1000 || groups.Count > 1000)
            throw new HostFailure(HostStatus.InvalidResponse);
        return (nodes.ToArray(), groups.ToArray());
    }

    internal static HostProvider[] Providers(ProvidersWire wire)
    {
        if (wire.Providers is null || wire.Providers.Count > 1000)
            throw new HostFailure(HostStatus.InvalidResponse);
        var providers = new List<HostProvider>();
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, provider) in wire.Providers)
        {
            // Compatible providers can each repeat the same nodes, groups, and built-ins.
            if (provider is not { VehicleType: "HTTP" or "File" or "Inline" })
                continue;
            if (provider.Proxies is not { } members || members.Length > 2005)
                throw new HostFailure(HostStatus.InvalidResponse);
            string[] roster = members.Select(p => Text(p?.Name)).ToArray();
            uniqueNames.UnionWith(roster);
            if (uniqueNames.Count > 1000)
                throw new HostFailure(HostStatus.InvalidResponse);
            providers.Add(new HostProvider(Text(name), roster));
        }
        return providers.ToArray();
    }

    private static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) > 4096)
            throw new HostFailure(HostStatus.InvalidResponse);
        return value;
    }
}

// The serializer consumes this stream directly. One extra byte distinguishes an exact limit from overflow.
internal sealed class LimitedBody(Stream source, int limit) : Stream
{
    private int remaining = limit;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int count = await source.ReadAsync(buffer[..Math.Min(buffer.Length, remaining + 1)], cancellationToken).ConfigureAwait(false);
        remaining -= count;
        if (remaining < 0)
            throw new HostFailure(HostStatus.InvalidResponse);
        return count;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
