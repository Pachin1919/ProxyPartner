using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ProxyPartnerLite.Desktop;

internal static class ServerTargets
{
    private const int MaxBytes = 128 * 1024;

    internal static Dictionary<string, string> Load(string path)
    {
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxBytes) return addresses;
            byte[] bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return addresses;
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                string? host = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null;
                if (entry.Name.Length is 0 or > 1024 || host is null || host.Length > 253 ||
                    Uri.CheckHostName(host) == UriHostNameType.Unknown ||
                    addresses.Count >= 1000 || !addresses.TryAdd(entry.Name, host))
                {
                    addresses.Clear();
                    break;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            addresses.Clear();
        }
        return addresses;
    }

    internal static async Task<DesktopResult> MeasureAsync(string host, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        int? elapsed = null;
        var state = ResultState.Failed;
        string method = "ICMP";
        try
        {
            if (!IPAddress.TryParse(host, out var endpoint))
            {
                var candidates = await Dns.GetHostAddressesAsync(host, deadline.Token).ConfigureAwait(false);
                endpoint = candidates.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? candidates.FirstOrDefault();
            }
            if (endpoint is not null)
            {
                method = endpoint.AddressFamily == AddressFamily.InterNetwork ? "ICMP IPv4" : "ICMP IPv6";
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(endpoint, TimeSpan.FromMilliseconds(1500), [],
                    new PingOptions(64, false), deadline.Token).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    elapsed = (int)reply.RoundtripTime;
                    state = ResultState.Measured;
                }
                else if (reply.Status == IPStatus.TimedOut)
                    state = ResultState.TimedOut;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { state = ResultState.TimedOut; }
        catch (Exception error) when (error is PingException or SocketException or ArgumentException) { }
        return new("server_icmp", elapsed, null, DateTimeOffset.UtcNow, method, state);
    }
}
