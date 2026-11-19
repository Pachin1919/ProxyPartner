using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record GoProbeRequest(string Type, string Server, int Port, string Password,
    string? Username = null, string? Sni = null, string? Transport = null, string? Multiplexing = null,
    string? HandshakeMode = null, string? TrafficPattern = null, List<string>? Alpn = null,
    string? ClientFingerprint = null, string? Fingerprint = null, string? EchConfig = null,
    string? TargetUrl = null, int TimeoutMs = 6000);

internal sealed record GoProbeReply(string Status, int? HttpStatus, int? ElapsedMs, string? ExitIp);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GoProbeRequest))]
[JsonSerializable(typeof(GoProbeReply))]
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
        if (!IsInstalled) return default;
        await Worker.WaitAsync(stop).ConfigureAwait(false);
        GoProbeReply? reply = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var process = new Process
            {
                StartInfo = new(WorkerPath)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false)
                }
            };
            if (!process.Start()) return default;
            var discard = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream,
                    settings with { TargetUrl = url.AbsoluteUri }, GoProbeJson.Default.GoProbeRequest,
                    budget.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                byte[] response = new byte[ReplyLimit];
                int count = await process.StandardOutput.BaseStream.ReadAtLeastAsync(response, response.Length,
                    false, budget.Token).ConfigureAwait(false);
                if (count is > 0 and < ReplyLimit)
                {
                    await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                    reply = JsonSerializer.Deserialize(response.AsSpan(0, count), GoProbeJson.Default.GoProbeReply);
                    if (reply?.Status == "ok" && process.ExitCode != 0) reply = null;
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                await discard.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !stop.IsCancellationRequested)
        {
            reply = new("timeout", null, null, null);
        }
        catch (Exception error) when (error is Win32Exception or IOException or JsonException) { }
        finally { Worker.Release(); }

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
}
