using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProxyPartnerLite.Host;
using QuickProxyNet;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

internal sealed record ProbeProfile(IProxyClient Client, string Revision);

internal static class IndependentNodeProbe
{
    private const int MaxProfileBytes = 1024 * 1024;
    internal static readonly Uri EchoUrl = new("https://checkip.amazonaws.com/");
    internal static readonly Uri CorroboratingEchoUrl = new("https://icanhazip.com/");
    private static readonly string[] ClashFields =
    [
        "name", "type", "server", "port", "uuid", "network", "tls", "servername", "client-fingerprint",
        "flow", "encryption", "reality-opts", "ws-opts", "alpn", "udp", "xudp", "packet-addr", "packet-encoding",
        "dialer-proxy", "interface-name", "ip-version", "routing-mark", "skip-cert-verify", "tfo", "mptcp",
        "alterId", "cipher"
    ];
    private static readonly string[] WsFields = ["path", "headers", "max-early-data", "v2ray-http-upgrade", "v2ray-http-upgrade-fast-open"];
    private static readonly string[] RealityFields = ["public-key", "short-id"];

    internal static Dictionary<string, ProbeProfile> LoadConfigured(bool verifiedPipe)
    {
        if (Environment.GetEnvironmentVariable("PROXYPARTNER_NODE_PROBES") is { } configured)
            return Load(configured);

        var pointer = new FileInfo(Path.Combine(AppContext.BaseDirectory, "ProbeProfilePath.txt"));
        if (verifiedPipe && pointer.Exists)
        {
            try
            {
                if (pointer.Length is > 0 and <= 4096)
                {
                    string path = File.ReadAllText(pointer.FullName).Trim();
                    if (Path.IsPathFullyQualified(path)) return Load(path);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new(StringComparer.Ordinal);
        }
        return Load(Path.Combine(AppContext.BaseDirectory, "ProbeNodes.json"));
    }

    internal static Dictionary<string, ProbeProfile> Load(string path)
    {
        var profiles = new Dictionary<string, ProbeProfile>(StringComparer.Ordinal);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > MaxProfileBytes) return profiles;
            byte[] bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            try
            {
                bool yaml = Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml";
                foreach (var (name, link) in yaml ? ReadClashLinks(bytes) : ReadLinks(bytes))
                {
                    if (profiles.Count == 1000) break;
                    if (name.Length is 0 or > 1024 || link.Length > 4096 ||
                        !link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
                        !Proxy.TryCreate(link, out var client, out _) || client is not VlessClient vless ||
                        (vless.Options.Security is not (VlessSecurity.Tls or VlessSecurity.Reality) &&
                            (!IPAddress.TryParse(vless.Options.Host, out var local) || !IPAddress.IsLoopback(local))))
                        continue;
                    string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link)));
                    profiles.TryAdd(name, new(vless, revision));
                }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or YamlException or ArgumentException)
        {
            profiles.Clear();
        }
        return profiles;
    }

    private static IEnumerable<(string Name, string Link)> ReadLinks(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) yield break;
        foreach (var entry in json.RootElement.EnumerateObject())
            if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { } link)
                yield return (entry.Name, link);
    }

    private static IEnumerable<(string Name, string Link)> ReadClashLinks(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), new UTF8Encoding(false, true));
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root ||
            Field(root, "proxies") is not YamlSequenceNode proxies)
            yield break;

        foreach (var entry in proxies.Children.OfType<YamlMappingNode>())
        {
            if (Text(entry, "type") != "vless" || !HasOnly(entry, ClashFields) || Text(entry, "name") is not { } name ||
                Text(entry, "server") is not { } server || Uri.CheckHostName(server) == UriHostNameType.Unknown ||
                !int.TryParse(Text(entry, "port"), out int port) || port is < 1 or > 65535 ||
                !Guid.TryParse(Text(entry, "uuid"), out Guid id) ||
                Text(entry, "alterId") is not (null or "0") ||
                Text(entry, "cipher") is not (null or "auto" or "none") ||
                Text(entry, "encryption") is not (null or "" or "none") ||
                Text(entry, "dialer-proxy") is not (null or "") ||
                Text(entry, "interface-name") is not (null or "") ||
                Text(entry, "ip-version") is not (null or "" or "dual") ||
                Text(entry, "routing-mark") is not (null or "" or "0") ||
                Text(entry, "skip-cert-verify")?.ToLowerInvariant() is not (null or "" or "false") ||
                Text(entry, "tfo")?.ToLowerInvariant() is not (null or "" or "false") ||
                Text(entry, "mptcp")?.ToLowerInvariant() is not (null or "" or "false"))
                continue;
            string network = Text(entry, "network") ?? "tcp";
            if (network is not ("tcp" or "ws" or "httpupgrade")) continue;
            var ws = Field(entry, "ws-opts") as YamlMappingNode;
            if (!HasOnly(ws, WsFields)) continue;
            var headers = Field(ws, "headers") as YamlMappingNode;
            string? host = Text(headers, "Host") ?? Text(headers, "host");
            if ((headers?.Children.Count ?? 0) > (host is null ? 0 : 1) ||
                Text(ws, "max-early-data") is not (null or "" or "0") ||
                Text(ws, "v2ray-http-upgrade-fast-open") == "true")
                continue;
            if (network == "ws" && Text(ws, "v2ray-http-upgrade") == "true") network = "httpupgrade";
            var reality = Field(entry, "reality-opts") as YamlMappingNode;
            if (!HasOnly(reality, RealityFields)) continue;
            string security = reality is not null ? "reality" :
                Text(entry, "tls")?.ToLowerInvariant() == "true" ? "tls" : "none";
            var parts = new List<string> { $"type={network}", $"security={security}", "encryption=none" };
            void Option(string key, string? value)
            {
                if (!string.IsNullOrEmpty(value)) parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
            Option("sni", Text(entry, "servername"));
            Option("fp", Text(entry, "client-fingerprint"));
            Option("flow", Text(entry, "flow"));
            Option("pbk", Text(reality, "public-key"));
            Option("sid", Text(reality, "short-id"));
            Option("path", Text(ws, "path"));
            Option("host", host);
            if (Field(entry, "alpn") is YamlSequenceNode alpn)
                Option("alpn", string.Join(',', alpn.Children.OfType<YamlScalarNode>().Select(n => n.Value)));
            var uri = new UriBuilder("vless", server, port) { UserName = id.ToString(), Query = string.Join('&', parts) };
            yield return (name, uri.Uri.AbsoluteUri);
        }
    }

    private static YamlNode? Field(YamlMappingNode? map, string name) =>
        map?.Children.TryGetValue(new YamlScalarNode(name), out var node) == true ? node : null;

    private static string? Text(YamlMappingNode? map, string name) =>
        (Field(map, name) as YamlScalarNode)?.Value;

    private static bool HasOnly(YamlMappingNode? map, string[] fields) =>
        map is null || map.Children.Keys.All(key =>
            key is YamlScalarNode { Value: { } name } && fields.Contains(name, StringComparer.Ordinal));

    internal static async Task<(IPAddress? First, IPAddress? Second)> MeasureAsync(string node, ProbeProfile profile,
        IReadOnlyList<(string Id, Uri Url)> targets, Uri echo, Uri corroboration,
        Action<WebsiteProbe> publish, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(7));
        var attempted = new ConcurrentDictionary<Uri, byte>();
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ActivityHeadersPropagator = null, MaxResponseDrainSize = 0, MaxResponseHeadersLength = 32,
            ConnectCallback = (context, token) =>
            {
                if (!attempted.TryAdd(context.InitialRequestMessage.RequestUri!, 0))
                    throw new HttpRequestException("Independent probe already dialed.");
                return profile.Client.ConnectAsync(context.DnsEndPoint, token);
            }
        };
        using var client = new HttpClient(handler);

        async Task<WebsiteProbe> MeasureSite((string Id, Uri Url) target)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
            if (target.Id == "anthropic_api")
                request.Headers.TryAddWithoutValidation("anthropic-version", "2026-10-21");
            long start = Stopwatch.GetTimestamp();
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token).ConfigureAwait(false);
                return new(target.Id, node, false, true, (int)response.StatusCode,
                    (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds, DateTimeOffset.UtcNow, Independent: true);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return new(target.Id, node, false, true, null, null, DateTimeOffset.UtcNow,
                    TimedOut: !stop.IsCancellationRequested, Independent: true);
            }
            catch (HttpRequestException)
            {
                return new(target.Id, node, false, true, null, null, DateTimeOffset.UtcNow, Independent: true);
            }
        }

        var pending = targets.Select(MeasureSite).ToList();
        var ip = MeasureExitAsync(client, echo, deadline.Token);
        var secondIp = MeasureExitAsync(client, corroboration, deadline.Token);
        while (pending.Count != 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            var sample = await finished.ConfigureAwait(false);
            if (!stop.IsCancellationRequested) publish(sample);
        }
        return (await ip.ConfigureAwait(false), await secondIp.ConfigureAwait(false));
    }

    private static async Task<IPAddress?> MeasureExitAsync(HttpClient client, Uri echo, CancellationToken token)
    {
        try
        {
            using var response = await client.GetAsync(echo, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[65];
            int count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, token).ConfigureAwait(false);
            if (count == buffer.Length ||
                !IPAddress.TryParse(Encoding.ASCII.GetString(buffer, 0, count).Trim(), out var address))
                return null;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            byte[] octets = address.GetAddressBytes();
            bool publicAddress = octets.Length == 4
                ? octets[0] is > 0 and < 224 and not (10 or 127) &&
                    !(octets[0] == 169 && octets[1] == 254 || octets[0] == 172 && octets[1] is >= 16 and <= 31 ||
                      octets[0] == 192 && (octets[1] == 168 || octets[1] == 0 && octets[2] is 0 or 2) ||
                      octets[0] == 100 && octets[1] is >= 64 and <= 127 ||
                      octets[0] == 198 && (octets[1] is 18 or 19 || octets[1] == 51 && octets[2] == 100) ||
                      octets[0] == 203 && octets[1] == 0 && octets[2] == 113)
                : (octets[0] & 0xe0) == 0x20 &&
                    !(octets[0] == 0x20 && octets[1] == 1 && octets[2] == 0x0d && octets[3] == 0xb8);
            return publicAddress ? address : null;
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException)
        {
            return null;
        }
    }
}
