using System.ComponentModel;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ProxyPartnerLite.Discovery;

namespace ProxyPartnerLite.Host;

/// <summary>Reads typed, transient facts from the selected local Clash Verge core.</summary>
public sealed partial class ReadOnlyHostClient
{
    public async Task<HostReadResult> ReadAsync(HostReadOptions? options = null,
        LoopbackFallbackAuthorization? fallback = null, CancellationToken cancellationToken = default)
    {
        CoreIdentity core;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            core = WindowsPeer.Discover();
        }
        catch (Exception error) when (IsBoundaryError(error))
        {
            return new HostReadResult(Classify(error, cancellationToken), null, null, null, null, []);
        }
        return await ReadForCoreAsync(core, "verge-mihomo", options ?? new HostReadOptions(), fallback,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<HostReadResult> ReadForCoreAsync(CoreIdentity core, string pipe,
        HostReadOptions options, LoopbackFallbackAuthorization? fallback, CancellationToken cancellationToken = default)
    {
        var read = new Session(core, pipe, options);
        try
        {
            try
            {
                var facts = await read.ReadAsync(null, cancellationToken).ConfigureAwait(false);
                return read.Result(HostStatus.ConnectedPipe, facts);
            }
            catch (HostFailure failure) when (failure.Status == HostStatus.HostUnavailable && read.Requests.Count == 0)
            {
                if (core.Service is not null)
                    return read.Result(HostStatus.HostUnavailable);
                if (fallback is null)
                    return read.Result(HostStatus.FallbackAuthorizationRequired);
            }

            WindowsPeer.Verify(core, core.Pid);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var settings = await LoopbackSettings.ReadAsync(fallback!, core, deadline.Token).ConfigureAwait(false);
            var loopbackFacts = await read.ReadAsync(settings, cancellationToken).ConfigureAwait(false);
            return read.Result(HostStatus.ConnectedLoopback, loopbackFacts);
        }
        catch (Exception error) when (IsBoundaryError(error))
        {
            return read.Result(Classify(error, cancellationToken));
        }
    }

    internal static string CheckRoute(string method, string path)
    {
        if (method == "GET" && (path is "/version" or "/proxies" or "/providers/proxies"))
            return path;
        throw new HostFailure(HostStatus.IdentityRejected);
    }

    public async Task<bool> SelectMemberAsync(int expectedPid, DateTimeOffset expectedStart,
        string group, string member, CancellationToken token)
    {
        try
        {
            var core = WindowsPeer.Discover();
            if (core.Pid != expectedPid || (core.StartedAtUtc ?? core.Service?.Ui.StartedAtUtc) != expectedStart) return false;
            return await SelectMemberForCoreAsync(core, "verge-mihomo", group, member, token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsBoundaryError(error)) { return false; }
    }

    internal static async Task<bool> SelectMemberForCoreAsync(CoreIdentity core, string pipe, string group, string member,
        CancellationToken token)
    {
        if (group is "." or ".." || token.IsCancellationRequested) return false;
        try
        {
            var session = new Session(core, pipe, new());
            var facts = await session.ReadAsync(null, token).ConfigureAwait(false);
            var selector = facts.Groups.FirstOrDefault(item => item.Name == group && item.Type == "Selector");
            if (selector is null || !selector.Members.Contains(member, StringComparer.Ordinal)) return false;
            if (selector.Selected == member) return true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await using var stream = await OpenVerifiedPipeAsync(core, pipe, deadline.Token).ConfigureAwait(false);
            using var client = new HttpClient(UseVerifiedStream(stream));
            using var request = new HttpRequestMessage(HttpMethod.Put,
                "http://127.0.0.1/proxies/" + Uri.EscapeDataString(group))
            {
                Content = new StringContent("{\"name\":\"" + JsonEncodedText.Encode(member) + "\"}", Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent) return false;
            var updated = await session.ReadAsync(null, deadline.Token).ConfigureAwait(false);
            return updated.Groups.Any(item => item.Name == group && item.Selected == member);
        }
        catch (Exception error) when (IsBoundaryError(error)) { return false; }
    }

    private static SocketsHttpHandler UseVerifiedStream(Stream stream)
    {
        bool streamTaken = false;
        return new()
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ActivityHeadersPropagator = null, MaxResponseHeadersLength = 16,
            ConnectCallback = (_, _) =>
            {
                if (streamTaken) throw new IOException("The verified connection was already used.");
                streamTaken = true;
                return ValueTask.FromResult(stream);
            }
        };
    }

    private static bool IsBoundaryError(Exception error) => error is HostFailure or UnauthorizedAccessException
        or OperationCanceledException or Win32Exception or IOException or HttpRequestException or SocketException
        or InvalidOperationException or JsonException or ArgumentException;

    private static HostStatus Classify(Exception error, CancellationToken cancellation) => error switch
    {
        HostFailure failure => failure.Status,
        UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } => HostStatus.AccessDenied,
        OperationCanceledException => cancellation.IsCancellationRequested ? HostStatus.Cancelled : HostStatus.TimedOut,
        JsonException => HostStatus.InvalidResponse,
        _ => HostStatus.TransportError
    };

    private static async Task<Stream> OpenVerifiedPipeAsync(CoreIdentity core, string name, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            try { await pipe.ConnectAsync(1200, token).ConfigureAwait(false); }
            catch (TimeoutException) { throw new HostFailure(HostStatus.HostUnavailable); }
            WindowsPeer.VerifyPipe(pipe.SafePipeHandle, core);
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    private sealed class Session(CoreIdentity core, string pipeName, HostReadOptions options)
    {
        private int? verifiedPeer;
        internal List<HostRequestFact> Requests { get; } = new(4);

        internal HostReadResult Result(HostStatus status, HostFacts? facts = null) =>
            new(status, facts, core.Pid, core.StartedAtUtc, verifiedPeer, Requests.ToArray(),
                core.StartedAtUtc ?? core.Service?.Ui.StartedAtUtc);

        internal async Task<HostFacts> ReadAsync(LoopbackSettings? fallback, CancellationToken token)
        {
            var version = HostProjection.Version(await GetAsync("/version", HostJson.Default.VersionWire, fallback, token)
                .ConfigureAwait(false));
            var (nodes, groups) = HostProjection.Proxies(await GetAsync("/proxies", HostJson.Default.ProxiesWire, fallback, token)
                .ConfigureAwait(false));
            HostProvider[]? providers = options.IncludeProviders
                ? HostProjection.Providers(await GetAsync("/providers/proxies", HostJson.Default.ProvidersWire, fallback, token)
                    .ConfigureAwait(false))
                : null;
            string? mode = null;
            if (options.IncludeMode && fallback is null)
            {
                try
                {
                    mode = (await GetAsync("/configs", HostJson.Default.ModeWire, null, token)
                        .ConfigureAwait(false)).Mode;
                }
                catch (Exception error) when (IsBoundaryError(error) && !token.IsCancellationRequested) { }
            }
            return new HostFacts(DateTimeOffset.UtcNow, version, nodes, groups, providers, mode);
        }

        private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, LoopbackSettings? fallback, CancellationToken token)
        {
            string allowedPath = path == "/configs" && options.IncludeMode && fallback is null ? path : CheckRoute("GET", path);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var cancellation = deadline.Token;
            await using var stream = fallback is null
                ? await OpenVerifiedPipeAsync(core, pipeName, cancellation).ConfigureAwait(false)
                : await OpenLoopbackAsync(fallback.Endpoint, cancellation).ConfigureAwait(false);
            verifiedPeer = core.Pid;

            using var client = new HttpClient(UseVerifiedStream(stream));
            var address = new UriBuilder("http", "127.0.0.1", fallback?.Endpoint.Port ?? 80, allowedPath).Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, address)
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.ConnectionClose = true;
            if (fallback is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fallback.Secret);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            Requests.Add(new HostRequestFact(allowedPath, (int)response.StatusCode));
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new HostFailure(fallback is null ? HostStatus.AccessDenied : HostStatus.FallbackAuthFailed);

            int limit = path == "/version" ? 4096 : 8 * 1024 * 1024;
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > limit ||
                response.Content.Headers.ContentEncoding.Count != 0)
                throw new HostFailure(HostStatus.InvalidResponse);
            await using var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var limited = new LimitedBody(body, limit);
            return await JsonSerializer.DeserializeAsync(limited, type, cancellation).ConfigureAwait(false)
                ?? throw new HostFailure(HostStatus.InvalidResponse);
        }

        private async Task<Stream> OpenLoopbackAsync(IPEndPoint endpoint, CancellationToken token)
        {
            WindowsPeer.VerifyTcp(core, endpoint, null);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
                WindowsPeer.VerifyTcp(core, endpoint, (IPEndPoint)socket.LocalEndPoint!);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }
}
