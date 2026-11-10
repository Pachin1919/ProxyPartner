using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProxyPartnerLite.Host;
using QuickProxyNet;

internal sealed record ProbeProfile(IProxyClient Client, string Revision);

internal static class IndependentNodeProbe
{
    private const int MaxProfileBytes = 1024 * 1024;
    internal static readonly Uri EchoUrl = new("https://api64.ipify.org/");

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
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
                if (json.RootElement.ValueKind != JsonValueKind.Object) return profiles;
                foreach (var entry in json.RootElement.EnumerateObject())
                {
                    if (profiles.Count == 1000) break;
                    if (entry.Name.Length is 0 or > 1024 || entry.Value.ValueKind != JsonValueKind.String ||
                        entry.Value.GetString() is not { Length: <= 4096 } link ||
                        !link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
                        !Proxy.TryCreate(link, out var client, out _) || client is not VlessClient vless ||
                        (vless.Options.Security is not (VlessSecurity.Tls or VlessSecurity.Reality) &&
                            (!IPAddress.TryParse(vless.Options.Host, out var local) || !IPAddress.IsLoopback(local))))
                        continue;
                    string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link)));
                    profiles.TryAdd(entry.Name, new(vless, revision));
                }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            profiles.Clear();
        }
        return profiles;
    }

    internal static async Task<IPAddress?> MeasureAsync(string node, ProbeProfile profile,
        IReadOnlyList<(string Id, Uri Url)> targets, Uri echo, Action<WebsiteProbe> publish, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(7));
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ActivityHeadersPropagator = null, MaxResponseDrainSize = 0, MaxResponseHeadersLength = 32,
            ConnectCallback = (context, token) => profile.Client.ConnectAsync(context.DnsEndPoint, token)
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
        while (pending.Count != 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            var sample = await finished.ConfigureAwait(false);
            if (!stop.IsCancellationRequested) publish(sample);
        }
        return await ip.ConfigureAwait(false);
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
            return IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
                ? null : address;
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException)
        {
            return null;
        }
    }
}
