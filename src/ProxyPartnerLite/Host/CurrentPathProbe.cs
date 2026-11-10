using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ProxyPartnerLite.Discovery;

namespace ProxyPartnerLite.Host;

public sealed record WebsiteProbe(
    string TargetId, string? Node, bool Direct, bool Attempted, int? HttpStatus, int? ElapsedMs, DateTimeOffset ObservedUtc,
    bool TimedOut = false, bool Independent = false);

public sealed partial class ReadOnlyHostClient
{
    private static readonly (string Id, Uri Url)[] ProbeTargets =
    [
        ("chatgpt_web", new("https://chatgpt.com/")),
        ("openai_api", new("https://api.openai.com/v1/models")),
        ("google", new("https://www.google.com/")),
        ("youtube", new("https://www.youtube.com/")),
        ("anthropic_api", new("https://api.anthropic.com/v1/models"))
    ];

    public const int CurrentProbeTargetCount = 5;
    public static IReadOnlyList<(string Id, Uri Url)> WebsiteTargets { get; } = Array.AsReadOnly(ProbeTargets);

    /// <summary>Checks the built-in targets concurrently and returns the first verified route in target order.</summary>
    public async Task<WebsiteProbe?> MeasureCurrentPathAsync(int corePid, HostFacts facts, Action<WebsiteProbe> publish, CancellationToken token)
    {
        CoreIdentity core;
        try { core = WindowsPeer.Discover(); }
        catch (Exception error) when (IsBoundaryError(error)) { return null; }
        if (core.Pid != corePid || token.IsCancellationRequested)
            return null;

        var nodes = facts.Nodes.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        var probes = ProbeTargets.Select(target => MeasureWebsiteAsync(core, target, nodes, token)).ToArray();
        var pending = probes.ToList();
        WebsiteProbe? anchor = null;
        int anchorIndex = probes.Length;
        while (pending.Count != 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            var sample = await finished.ConfigureAwait(false);
            if (token.IsCancellationRequested) continue;
            publish(sample);
            if (sample.Node is not null || sample.Direct)
            {
                int index = Array.IndexOf(probes, finished);
                if (index < anchorIndex)
                {
                    anchor = sample;
                    anchorIndex = index;
                }
            }
        }
        return anchor;
    }

    private static async Task<WebsiteProbe> MeasureWebsiteAsync(CoreIdentity core, (string Id, Uri Url) target,
        HashSet<string> nodes, CancellationToken token)
    {
        bool attempted = false;
        Task<MatchedProbeRoute?>? route = null;
        int? status = null, elapsed = null;
        try
        {
            var proxy = HttpClient.DefaultProxy;
            Uri? address = proxy.IsBypassed(target.Url) ? null : proxy.GetProxy(target.Url);
            if (address is not { Scheme: "http", Host: "127.0.0.1", UserInfo.Length: 0 } || address.Port <= 0)
                return new(target.Id, null, false, false, null, null, DateTimeOffset.UtcNow);

            var endpoint = new IPEndPoint(IPAddress.Loopback, address.Port);
            int localPort = 0, connectionCount = 0;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(7));
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(address), UseCookies = false, AllowAutoRedirect = false,
                ActivityHeadersPropagator = null, MaxResponseHeadersLength = 32, MaxResponseDrainSize = 0,
                ConnectCallback = async (context, cancellation) =>
                {
                    if (Interlocked.Increment(ref connectionCount) != 1 ||
                        context.DnsEndPoint.Host != "127.0.0.1" || context.DnsEndPoint.Port != endpoint.Port)
                        throw new HostFailure(HostStatus.IdentityRejected);
                    WindowsPeer.VerifyTcp(core, endpoint, null);
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(endpoint, cancellation).ConfigureAwait(false);
                        var local = (IPEndPoint)socket.LocalEndPoint!;
                        WindowsPeer.VerifyTcp(core, endpoint, local);
                        localPort = local.Port;
                        attempted = true;
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                },
                PlaintextStreamFilter = (context, _) =>
                {
                    // Start the one-shot lookup while the TLS tunnel is open.
                    if (context.InitialRequestMessage.Method == HttpMethod.Get && context.InitialRequestMessage.RequestUri == target.Url)
                        route ??= FindProbeRouteAsync(core, target.Url, localPort, nodes, deadline.Token);
                    return ValueTask.FromResult(context.PlaintextStream);
                }
            };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url)
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            if (target.Id == "anthropic_api")
                request.Headers.TryAddWithoutValidation("anthropic-version", "2026-10-21");
            long started = Stopwatch.GetTimestamp();
            HttpResponseMessage? response = null;
            bool timedOut = false;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);
                status = (int)response.StatusCode;
                elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (Exception error) when (IsBoundaryError(error)) { }

            using (response)
            {
                MatchedProbeRoute? matched = route is null ? null : await route.ConfigureAwait(false);
                return new(target.Id, matched?.Node, matched?.Direct ?? false, attempted, status, elapsed,
                    DateTimeOffset.UtcNow, timedOut);
            }
        }
        catch (Exception error) when (IsBoundaryError(error))
        {
            return new(target.Id, null, false, attempted, status, elapsed, DateTimeOffset.UtcNow);
        }
    }

    private static async Task<MatchedProbeRoute?> FindProbeRouteAsync(CoreIdentity core, Uri target, int localPort,
        HashSet<string> nodes, CancellationToken token)
    {
        if (localPort == 0)
            return null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var cancellation = deadline.Token;
            await using var pipe = await OpenVerifiedPipeAsync(core, "verge-mihomo", cancellation).ConfigureAwait(false);
            bool used = false;
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false, UseCookies = false, AllowAutoRedirect = false, ActivityHeadersPropagator = null,
                MaxResponseHeadersLength = 16, MaxResponseDrainSize = 0,
                ConnectCallback = (_, _) =>
                {
                    if (used) throw new IOException("Verified pipe already used.");
                    used = true;
                    return ValueTask.FromResult(pipe);
                }
            };
            using var client = new HttpClient(handler);
            // Separate from the polling allowlist. Only a live, owned probe calls this one-shot lookup.
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/connections");
            request.Headers.ConnectionClose = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0)
                return null;
            await using var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var limited = new LimitedBody(body, 2 * 1024 * 1024);
            return await ProbeRouteReader.MatchAsync(limited, target.IdnHost, target.Port, localPort, nodes, cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (IsBoundaryError(error)) { return null; }
    }
}
