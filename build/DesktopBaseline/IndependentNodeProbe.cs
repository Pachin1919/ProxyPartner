using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProxyPartnerLite.Host;
using QuickProxyNet;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

internal sealed record ProbeProfile(string Type, string Revision,
    Func<EndPoint, CancellationToken, ValueTask<Stream>> Connect);
internal sealed record ProbeCatalog(Dictionary<string, ProbeProfile> Profiles, HashSet<string> Configured);
internal readonly record struct IndependentResult(WebsiteProbe? Site = null, IPAddress? Exit = null);

internal static class IndependentNodeProbe
{
    private const int MaxProfileBytes = 1024 * 1024;
    internal static readonly Uri EchoUrl = new("https://ipv4.icanhazip.com/");
    internal static readonly Uri CorroboratingEchoUrl = new("https://api.ipify.org/");
    private static readonly string[] ClashFields =
    [
        "name", "type", "server", "port", "uuid", "network", "tls", "servername", "client-fingerprint",
        "flow", "encryption", "reality-opts", "ws-opts", "alpn", "udp", "xudp", "packet-addr", "packet-encoding",
        "dialer-proxy", "interface-name", "ip-version", "routing-mark", "skip-cert-verify", "tfo", "mptcp",
        "alterId", "cipher"
    ];
    private static readonly string[] TrojanFields =
    [
        "name", "type", "server", "port", "password", "tls", "sni", "network", "ws-opts", "alpn",
        "skip-cert-verify", "udp"
    ];
    private static readonly string[] VmessFields =
    [
        "name", "type", "server", "port", "uuid", "alterId", "cipher", "tls", "servername", "network",
        "ws-opts", "alpn", "skip-cert-verify", "udp", "xudp", "packet-addr", "packet-encoding",
        "global-padding", "authenticated-length"
    ];
    private static readonly string[] WsFields = ["path", "headers", "max-early-data", "v2ray-http-upgrade", "v2ray-http-upgrade-fast-open"];
    private static readonly string[] RealityFields = ["public-key", "short-id"];

    internal static string? MethodLabel(string type) => type switch
    {
        "Vless" => "VLESS · HTTPS",
        "Shadowsocks" => "Shadowsocks · HTTPS",
        "Trojan" => "Trojan · HTTPS",
        "Vmess" => "VMess · HTTPS",
        _ => null
    };

    internal static string? ConfiguredPath(bool verifiedPipe)
    {
        if (Environment.GetEnvironmentVariable("PROXYPARTNER_NODE_PROBES") is { Length: > 0 } configured)
            return configured;

        var pointer = new FileInfo(Path.Combine(AppContext.BaseDirectory, "ProbeProfilePath.txt"));
        if (!verifiedPipe || !pointer.Exists)
            return Path.Combine(AppContext.BaseDirectory, "ProbeNodes.json");
        try
        {
            if (pointer.Length is > 0 and <= 4096)
            {
                string path = File.ReadAllText(pointer.FullName).Trim();
                if (Path.IsPathFullyQualified(path)) return path;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return null;
    }

    internal static ProbeCatalog Load(string? path)
    {
        var profiles = new Dictionary<string, ProbeProfile>(StringComparer.Ordinal);
        var configured = new HashSet<string>(StringComparer.Ordinal);
        if (path is null) return new(profiles, configured);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > MaxProfileBytes) return new(profiles, configured);
            byte[] bytes = new byte[(int)file.Length];
            try
            {
                file.ReadExactly(bytes);
                bool yaml = Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml";
                foreach (var (name, profile) in yaml ? ReadClashProfiles(bytes) : ReadLinks(bytes))
                {
                    if (configured.Count == 1000) break;
                    if (name.Length is 0 or > 1024) continue;
                    if (!configured.Add(name)) profiles.Remove(name);
                    else if (profile is not null) profiles.Add(name, profile);
                }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or YamlException or ArgumentException)
        {
            profiles.Clear();
            configured.Clear();
        }
        return new(profiles, configured);
    }

    private static ProbeProfile? FromLink(string link)
    {
        if (link.Length > 4096 || !Proxy.TryCreate(link, out var client, out _))
            return null;
        return client switch
        {
            VlessClient vless when SupportedTransport(vless.Options.Transport) &&
                (vless.Options.Security is VlessSecurity.Tls or VlessSecurity.Reality ||
                 IPAddress.TryParse(vless.Options.Host, out var local) && IPAddress.IsLoopback(local))
                => new("Vless", Revision(link), vless.ConnectAsync),
            ShadowsocksClient ss => new("Shadowsocks", Revision(link), ss.ConnectAsync),
            TrojanClient trojan when !trojan.Options.AllowInsecure && SupportedTransport(trojan.Options.Transport)
                => new("Trojan", Revision(link), trojan.ConnectAsync),
            VmessClient vmess when !vmess.Options.AllowInsecure && SupportedTransport(vmess.Options.Transport) &&
                (vmess.Options.Security != VmessSecurityKind.ChaCha20Poly1305 || ChaCha20Poly1305.IsSupported)
                => new("Vmess", Revision(link), vmess.ConnectAsync),
            _ => null
        };
    }

    private static bool SupportedTransport(string? transport) => transport is
        null or "" or "tcp" or "raw" or "ws" or "websocket" or "httpupgrade";

    private static string Revision(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ProbeProfile? ReadShadowsocks(YamlMappingNode entry, string server, int port)
    {
        if (!HasOnly(entry, ["name", "type", "server", "port", "cipher", "password",
            "plugin", "plugin-opts", "udp", "client-fingerprint"]) ||
            Text(entry, "cipher") is not { } cipher ||
            cipher is not ("aes-128-gcm" or "aes-192-gcm" or "aes-256-gcm" or "chacha20-ietf-poly1305") ||
            Text(entry, "password") is not { Length: > 0 and <= 4096 } password)
            return null;
        if (cipher == "chacha20-ietf-poly1305" && !ChaCha20Poly1305.IsSupported) return null;

        string? obfsHost = null;
        if (Field(entry, "plugin") is { } plugin)
        {
            if (plugin is not YamlScalarNode { Value: "obfs" } ||
                Field(entry, "plugin-opts") is not YamlMappingNode options ||
                !HasOnly(options, ["mode", "host"]) || Text(options, "mode") != "tls")
                return null;
            obfsHost = Text(options, "host") ?? "bing.com";
            // Mihomo passes this label through, including a configured port suffix.
            if (obfsHost.Length is 0 or > 253 || obfsHost.Any(c => c is < '!' or > '~'))
                return null;
        }
        else if (Field(entry, "plugin-opts") is not null) return null;

        var client = new ShadowsocksClient(new() { Method = cipher, Password = password, Host = server, Port = port });
        string revision = Revision($"{server}\n{port}\n{cipher}\n{obfsHost}\n{password}");
        return new("Shadowsocks", revision, obfsHost is { } host
            ? (destination, token) => ShadowsocksTlsObfs.ConnectAsync(client, host, destination, token)
            : client.ConnectAsync);
    }

    private static IEnumerable<(string Name, ProbeProfile? Profile)> ReadLinks(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) yield break;
        foreach (var entry in json.RootElement.EnumerateObject())
            yield return (entry.Name, entry.Value.ValueKind == JsonValueKind.String &&
                entry.Value.GetString() is { } link ? FromLink(link) : null);
    }

    private static IEnumerable<(string Name, ProbeProfile? Profile)> ReadClashProfiles(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), new UTF8Encoding(false, true));
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root ||
            Field(root, "proxies") is not YamlSequenceNode proxies)
            yield break;

        foreach (var entry in proxies.Children.OfType<YamlMappingNode>())
        {
            if (Text(entry, "name") is not { } name) continue;
            ProbeProfile? profile = null;
            if (Text(entry, "server") is { } server && Uri.CheckHostName(server) != UriHostNameType.Unknown &&
                int.TryParse(Text(entry, "port"), out int port) && port is >= 1 and <= 65535)
            {
                profile = Text(entry, "type") switch
                {
                    "vless" => ReadVless(entry, server, port),
                    "ss" => ReadShadowsocks(entry, server, port),
                    "trojan" or "vmess" => ReadTrojanOrVmess(entry, server, port),
                    _ => null
                };
            }
            yield return (name, profile);
        }
    }

    private static ProbeProfile? ReadTrojanOrVmess(YamlMappingNode entry, string server, int port)
    {
        bool trojan = Text(entry, "type") == "trojan";
        if (!HasOnly(entry, trojan ? TrojanFields : VmessFields) ||
            Text(entry, "network") is not (null or "tcp" or "ws") ||
            Text(entry, "skip-cert-verify")?.ToLowerInvariant() is not (null or "" or "false") ||
            !TryReadWebTransport(entry, out string network, out string? path, out string? host))
            return null;

        string? tls = Text(entry, "tls")?.ToLowerInvariant();
        if (tls is not (null or "" or "true" or "false")) return null;

        string? sni = Text(entry, trojan ? "sni" : "servername");
        if (sni is { Length: > 0 } && Uri.CheckHostName(sni) == UriHostNameType.Unknown) return null;

        IReadOnlyList<string>? alpn = null;
        if (Field(entry, "alpn") is { } field)
        {
            if (field is not YamlSequenceNode sequence ||
                sequence.Children.Any(value => value is not YamlScalarNode { Value: { Length: > 0 and <= 255 } protocol }
                    || protocol.Any(c => c is < '!' or > '~')))
                return null;
            alpn = sequence.Children.Cast<YamlScalarNode>().Select(value => value.Value!).ToArray();
        }
        string transportRevision = $"{server}\n{port}\n{network}\n{sni}\n{path}\n{host}\n{string.Join('\n', alpn ?? [])}";
        if (trojan)
        {
            if (tls == "false" || Text(entry, "password") is not { Length: > 0 and <= 4096 } password)
                return null;
            var client = new TrojanClient(new()
            {
                Host = server, Port = port, Password = password, Transport = network,
                Path = path, HostHeader = host, Sni = string.IsNullOrEmpty(sni) ? server : sni,
                Alpn = alpn ?? (network == "tcp" ? ["h2", "http/1.1"] : ["http/1.1"])
            });
            return new("Trojan", Revision($"{transportRevision}\n{password}"), client.ConnectAsync);
        }

        if (!Guid.TryParse(Text(entry, "uuid"), out var id) ||
            Text(entry, "alterId") is not (null or "0") ||
            Text(entry, "global-padding")?.ToLowerInvariant() is not (null or "" or "false") ||
            Text(entry, "authenticated-length")?.ToLowerInvariant() is not (null or "" or "false"))
            return null;

        VmessSecurityKind? cipher = Text(entry, "cipher") switch
        {
            null or "" or "auto" => ChaCha20Poly1305.IsSupported ? VmessSecurityKind.Auto : VmessSecurityKind.Aes128Gcm,
            "aes-128-gcm" => VmessSecurityKind.Aes128Gcm,
            "chacha20-poly1305" when ChaCha20Poly1305.IsSupported => VmessSecurityKind.ChaCha20Poly1305,
            _ => null
        };
        if (cipher is not { } security) return null;
        var vmess = new VmessClient(new()
        {
            Host = server, Port = port, Id = id.ToString(), Security = security, UseTls = tls == "true",
            Transport = network, Path = path, HostHeader = host, Sni = sni,
            Alpn = network == "tcp" ? alpn : ["http/1.1"]
        });
        return new("Vmess", Revision($"{transportRevision}\n{security}\n{tls}\n{id}"), vmess.ConnectAsync);
    }

    private static bool TryReadWebTransport(YamlMappingNode entry, out string network,
        out string? path, out string? host)
    {
        network = Text(entry, "network") ?? "tcp";
        path = host = null;
        if (network is not ("tcp" or "ws" or "httpupgrade")) return false;
        if (Field(entry, "ws-opts") is not { } field) return true;
        if (field is not YamlMappingNode ws || !HasOnly(ws, WsFields)) return false;

        path = Text(ws, "path");
        if (Field(ws, "headers") is { } value)
        {
            if (value is not YamlMappingNode headers) return false;
            host = Text(headers, "Host") ?? Text(headers, "host");
            if (headers.Children.Count > (host is null ? 0 : 1)) return false;
        }
        if (path is { Length: > 0 } && (path[0] != '/' || path.Any(c => c == ' ' || char.IsControl(c))) ||
            host?.Any(c => c == ' ' || char.IsControl(c)) == true ||
            Text(ws, "max-early-data") is not (null or "" or "0") ||
            Text(ws, "v2ray-http-upgrade-fast-open")?.ToLowerInvariant() is not (null or "" or "false"))
            return false;

        if (network == "ws" && Text(ws, "v2ray-http-upgrade")?.ToLowerInvariant() == "true")
            network = "httpupgrade";
        return true;
    }

    private static ProbeProfile? ReadVless(YamlMappingNode entry, string server, int port)
    {
        if (!HasOnly(entry, ClashFields) || !Guid.TryParse(Text(entry, "uuid"), out Guid id) ||
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
            return null;
        if (!TryReadWebTransport(entry, out string network, out string? path, out string? host))
            return null;
        var reality = Field(entry, "reality-opts") as YamlMappingNode;
        if (!HasOnly(reality, RealityFields)) return null;
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
        Option("path", path);
        Option("host", host);
        if (Field(entry, "alpn") is YamlSequenceNode alpn)
            Option("alpn", string.Join(',', alpn.Children.OfType<YamlScalarNode>().Select(n => n.Value)));
        var uri = new UriBuilder("vless", server, port) { UserName = id.ToString(), Query = string.Join('&', parts) };
        return FromLink(uri.Uri.AbsoluteUri);
    }

    private static YamlNode? Field(YamlMappingNode? map, string name) =>
        map?.Children.TryGetValue(new YamlScalarNode(name), out var node) == true ? node : null;

    private static string? Text(YamlMappingNode? map, string name) =>
        (Field(map, name) as YamlScalarNode)?.Value;

    private static bool HasOnly(YamlMappingNode? map, string[] fields) =>
        map is null || map.Children.Keys.All(key =>
            key is YamlScalarNode { Value: { } name } && fields.Contains(name, StringComparer.Ordinal));

    internal sealed class Session : IDisposable
    {
        private static readonly HttpRequestOptionsKey<DialAttempt> AttemptKey = new("ProbeDialAttempt");
        private sealed class DialAttempt { internal int Count; }
        private readonly HttpClient client;

        internal Session(ProbeProfile profile)
        {
            client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
                ActivityHeadersPropagator = null, MaxResponseDrainSize = 0, MaxResponseHeadersLength = 32,
                ConnectCallback = (context, token) =>
                {
                    if (!context.InitialRequestMessage.Options.TryGetValue(AttemptKey, out var attempt) ||
                        Interlocked.Increment(ref attempt.Count) != 1)
                        throw new HttpRequestException("Independent probe already dialed.");
                    return profile.Connect(context.DnsEndPoint, token);
                }
            });
        }

        internal async Task<IndependentResult> MeasureAsync(string node, int index, CancellationToken stop)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromSeconds(7));
            var targets = ReadOnlyHostClient.WebsiteTargets;
            bool site = index < targets.Count;
            var (id, uri) = site ? targets[index] : (null, index == targets.Count ? EchoUrl : CorroboratingEchoUrl);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Options.Set(AttemptKey, new DialAttempt());
            if (id == "anthropic_api")
                request.Headers.TryAddWithoutValidation("anthropic-version", "2026-10-21");
            long start = Stopwatch.GetTimestamp();
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token).ConfigureAwait(false);
                if (id is null)
                    return new(Exit: await ReadExitAsync(response, deadline.Token).ConfigureAwait(false));
                return new(new(id, node, false, true, (int)response.StatusCode,
                    (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds, DateTimeOffset.UtcNow, Independent: true));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return id is null ? default : new(new(id, node, false, true, null, null, DateTimeOffset.UtcNow,
                    TimedOut: !stop.IsCancellationRequested, Independent: true));
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                return id is null ? default : new(new(id, node, false, true, null, null,
                    DateTimeOffset.UtcNow, Independent: true));
            }
        }

        public void Dispose() => client.Dispose();
    }

    private static async Task<IPAddress?> ReadExitAsync(HttpResponseMessage response, CancellationToken token)
    {
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
}
