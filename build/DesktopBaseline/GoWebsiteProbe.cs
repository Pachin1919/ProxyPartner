using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

internal sealed record GoProbeRequest(string Type, string Server, int Port, string Password,
    string? Username = null, string? Sni = null, string? Transport = null, string? Multiplexing = null,
    string? HandshakeMode = null, string? TrafficPattern = null, List<string>? Alpn = null,
    string? ClientFingerprint = null, string? Fingerprint = null, string? EchConfig = null,
    string? TargetUrl = null, int TimeoutMs = 6000, string? PortRange = null);

internal sealed record GoProbeReply(string Status, int? HttpStatus, int? ElapsedMs, string? ExitIp);
internal sealed record GoDnsRequest(string Host, string[] Endpoints);
internal sealed record GoDnsReply(string? Address);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GoProbeRequest))]
[JsonSerializable(typeof(GoProbeReply))]
[JsonSerializable(typeof(GoDnsRequest))]
[JsonSerializable(typeof(GoDnsReply))]
internal sealed partial class GoProbeJson : JsonSerializerContext { }

internal static class GoWebsiteProbe
{
    private const int ReplyLimit = 512;
    private static readonly SemaphoreSlim Worker = new(1, 1);
    private static readonly string WorkerPath = Path.Combine(AppContext.BaseDirectory, "ProxyPartner.NodeProbe.exe");

    internal static bool IsInstalled { get; } = File.Exists(WorkerPath);

    internal static async Task<IndependentResult> MeasureAsync(GoProbeRequest settings,
        string node, string? target, Uri url, CancellationToken stop)
    {
        var (reply, timedOut) = await RunAsync(null, settings with { TargetUrl = url.AbsoluteUri },
            GoProbeJson.Default.GoProbeRequest, GoProbeJson.Default.GoProbeReply, TimeSpan.FromSeconds(8), stop);
        if (timedOut) reply = new("timeout", null, null, null);
        if (stop.IsCancellationRequested || reply is null) return default;
        if (target is null)
            return reply is { Status: "ok", HttpStatus: 200 }
                ? new(Exit: IndependentNodeProbe.ParseExitAddress(reply.ExitIp)) : default;
        if (reply is { Status: "ok", HttpStatus: >= 100 and <= 599, ElapsedMs: >= 0 and <= 8000 })
            return new(new(target, node, false, true, reply.HttpStatus, reply.ElapsedMs,
                DateTimeOffset.UtcNow, Independent: true));
        return reply.Status is "timeout" or "request_failed" or "server_dns_failed"
            ? new(new(target, node, false, true, null, null, DateTimeOffset.UtcNow,
                TimedOut: reply.Status == "timeout", Independent: true)) : default;
    }

    internal static async Task<string?> ResolveAsync(string host, string[] endpoints, CancellationToken stop)
    {
        var (reply, _) = await RunAsync("--resolve", new GoDnsRequest(host, endpoints), GoProbeJson.Default.GoDnsRequest,
            GoProbeJson.Default.GoDnsReply, TimeSpan.FromSeconds(11), stop);
        return reply?.Address;
    }

    private static async Task<(TReply? Reply, bool TimedOut)> RunAsync<TRequest, TReply>(string? argument,
        TRequest request, JsonTypeInfo<TRequest> input, JsonTypeInfo<TReply> output, TimeSpan timeout, CancellationToken stop)
        where TReply : class
    {
        if (!IsInstalled) return default;
        await Worker.WaitAsync(stop).ConfigureAwait(false);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
        budget.CancelAfter(timeout);
        try
        {
            var start = new ProcessStartInfo(WorkerPath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (argument is not null) start.ArgumentList.Add(argument);
            byte[] response = new byte[ReplyLimit];
            var (count, exit) = await ProbeChild.ReadAsync(start, response, budget.Token,
                stream => JsonSerializer.SerializeAsync(stream, request, input, budget.Token)).ConfigureAwait(false);
            if (count is 0 or ReplyLimit) return default;
            var reply = JsonSerializer.Deserialize(response.AsSpan(0, count), output);
            return exit != 0 && (reply is not GoProbeReply probe || probe.Status == "ok") ? default : (reply, false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !stop.IsCancellationRequested)
        {
            return (null, true);
        }
        catch (Exception error) when (error is Win32Exception or IOException or JsonException) { }
        finally { Worker.Release(); }

        return default;
    }
}
