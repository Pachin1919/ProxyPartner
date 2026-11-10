using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxyPartnerLite.Discovery;
using ProxyPartnerLite.Host;

if (args is ["--fixture", var port])
    return await FixtureOwnerAsync(int.Parse(port));
if (args.Length != 1)
    return 1;

try
{
    switch (args[0])
    {
        case "pipe-handshake":
            var client = new ReadOnlyHostClient();
            var basic = await client.ReadAsync();
            Console.WriteLine($"default_status={basic.Status}; default_routes={Routes(basic)}");
            if (basic.Status is HostStatus.HostUnavailable or HostStatus.AccessDenied or HostStatus.FallbackAuthorizationRequired)
                return 2;
            Require(basic.Status == HostStatus.ConnectedPipe && basic.CoreProcessId == basic.VerifiedPeerProcessId &&
                Routes(basic) == "/version:200,/proxies:200" && basic.Facts is { Providers: null },
                "default real pipe identity and routes");
            var live = await client.ReadAsync(new HostReadOptions(IncludeProviders: true));
            Console.WriteLine($"status={live.Status}; core_pid={live.CoreProcessId}; verified_peer_pid={live.VerifiedPeerProcessId}");
            Console.WriteLine("routes=" + Routes(live));
            if (live.Status is HostStatus.HostUnavailable or HostStatus.AccessDenied or HostStatus.FallbackAuthorizationRequired)
                return 2;
            Require(live.Status == HostStatus.ConnectedPipe && live.CoreProcessId == live.VerifiedPeerProcessId, "real pipe identity");
            Require(Routes(live) == "/version:200,/proxies:200,/providers/proxies:200", "real pipe allowlist");
            Require(live.Facts is { Nodes.Count: > 0, Groups.Count: > 0, Version: "v1.19.21" } &&
                live.Facts.Groups.Any(g => g.Selected is not null), "real typed facts and current selection");
            Console.WriteLine($"version={live.Facts!.Version}; nodes={live.Facts.Nodes.Count}; groups={live.Facts.Groups.Count}; selected_groups={live.Facts.Groups.Count(g => g.Selected is not null)}; providers={live.Facts.Providers?.Count}");
            break;
        case "fallback-auth":
            await FallbackAsync();
            break;
        case "readonly-routes":
            await ReadOnlyRoutesAsync();
            break;
        case "no-secrets":
            await NoSecretsAsync();
            break;
        default:
            return 1;
    }
    Console.WriteLine("PASS " + args[0]);
    return 0;
}
catch (Exception error)
{
    // Fixture input and runtime exceptions can contain synthetic credentials. Print only fixed assertion labels.
    Console.WriteLine("FAIL " + (error is CheckFailed ? error.Message : error.GetType().Name));
    return 1;
}

static string Routes(HostReadResult result) => string.Join(',', result.Requests.Select(r => $"{r.Path}:{r.HttpStatus}"));
static void Require(bool condition, string label)
{
    if (!condition) throw new CheckFailed(label);
}

static async Task FallbackAsync()
{
    var core = WindowsPeer.Inspect(Environment.ProcessId);
    string missingPipe = "ProxyPartner-absent-" + Guid.NewGuid().ToString("N");
    string path = Path.Combine(Environment.CurrentDirectory, "controller.fixture.yaml");
    string secret = Guid.NewGuid().ToString("N");
    try
    {
        using var server = new HttpFixture(secret);
        var none = await ReadOnlyHostClient.ReadForCoreAsync(core, missingPipe, new(), null);
        Require(none.Status == HostStatus.FallbackAuthorizationRequired && server.Requests == 0 && !File.Exists(path),
            "fallback without consent");
        var grant = Grant(core, path, $"127.0.0.1:{server.Port}", secret);
        var good = await ReadOnlyHostClient.ReadForCoreAsync(core, missingPipe, new(), grant);
        Require(good.Status == HostStatus.ConnectedLoopback && server.Requests == 2 && server.Authenticated == 2 &&
            Routes(good) == "/version:200,/proxies:200" && good.Facts is { Providers: null },
            "verified Windows owner, bearer and default routes");
        Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == grant.ApprovedSha256,
            "fixture source was read without modification");

        var wrong = Grant(core, path, $"127.0.0.1:{server.Port}", Guid.NewGuid().ToString("N"));
        var denied = await ReadOnlyHostClient.ReadForCoreAsync(core, missingPipe, new(), wrong);
        Require(denied.Status == HostStatus.FallbackAuthFailed && server.Requests == 3 && Routes(denied) == "/version:401",
            "401 stops the batch");
        var replacedFile = await ReadOnlyHostClient.ReadForCoreAsync(core, missingPipe, new(), grant);
        Require(replacedFile.Status == HostStatus.FallbackConfigUnverified && server.Requests == 3, "approved file content replaced");

        var remote = Grant(core, path, "192.0.2.1:54321", secret);
        var rejected = await ReadOnlyHostClient.ReadForCoreAsync(core, missingPipe, new(), remote);
        Require(rejected.Status == HostStatus.FallbackConfigUnverified && rejected.Requests.Count == 0, "non-loopback rejected");

        int formerPort;
        using (var vacant = new TcpListener(IPAddress.Loopback, 0))
        {
            vacant.Start();
            formerPort = ((IPEndPoint)vacant.LocalEndpoint).Port;
        }
        var previousGrant = Grant(core, path, $"127.0.0.1:{formerPort}", secret);
        using var fake = StartFixture(formerPort);
        try
        {
            Require(await ReadLineAsync(fake) == "ready", "replacement endpoint ready");
            var moved = await ReadOnlyHostClient.ReadForCoreAsync(core, missingPipe, new(), previousGrant);
            Require(moved.Status == HostStatus.IdentityRejected && moved.Requests.Count == 0, "replacement listener owner rejected");
            await fake.StandardInput.WriteLineAsync("done");
            Require(await ReadLineAsync(fake) == "accepted=0", "wrong owner received no connection or bearer");
            await fake.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
        }
        finally { if (!fake.HasExited) { fake.Kill(); await fake.WaitForExitAsync(); } }

        using var redirect = new HttpFixture(secret, redirect: true);
        var hop = await ReadOnlyHostClient.ReadForCoreAsync(core, missingPipe, new(), Grant(core, path, $"127.0.0.1:{redirect.Port}", secret));
        Require(hop.Status == HostStatus.InvalidResponse && redirect.Requests == 1 && Routes(hop) == "/version:302",
            "302 not followed");
        Console.WriteLine("verified_owner=pass; auth_401=stop; source_replacement=reject; non_loopback=reject; wrong_owner_connections=0; redirect=stop");
    }
    finally { File.Delete(path); }
}

static async Task ReadOnlyRoutesAsync()
{
    foreach (var method in new[] { "PUT", "PATCH", "POST", "DELETE", "HEAD", "get" })
        MustReject(method, "/proxies");
    foreach (var path in new[] { "/configs", "/configs/", "/configs?mode=global", "/%63onfigs", "/proxies/node/delay", "/group/group/delay",
        "/providers/proxies/p/healthcheck", "/providers/proxies/p/n/healthcheck", "/restart",
        "/upgrade", "/cache", "/connections", "/Proxies", "/%70roxies", "/proxies/",
        "//proxies", "/x/../proxies", "/proxies?anything=1", "/proxies#fragment", "http://127.0.0.1/proxies" })
        MustReject("GET", path);
    foreach (var path in new[] { "/version", "/proxies", "/providers/proxies" })
        Require(ReadOnlyHostClient.CheckRoute("GET", path) == path, "exact GET route");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var stopped = await new ReadOnlyHostClient().ReadAsync(cancellationToken: cancelled.Token);
    Require(stopped.Status == HostStatus.Cancelled && stopped.Requests.Count == 0, "cancelled without request");
    using var body = new LimitedBody(new MemoryStream([1, 2, 3, 4]), 3);
    try { await body.ReadExactlyAsync(new byte[4]); throw new CheckFailed("response limit not enforced"); }
    catch (HostFailure failure) { Require(failure.Status == HostStatus.InvalidResponse, "bounded response"); }
    var pressure = Enumerable.Range(0, 1000).ToDictionary(i => $"n{i}", i => (ProxyWire?)new($"n{i}", "Shadowsocks", null, null));
    pressure.Add("group", new("group", "Selector", "n0", ["n0"]));
    foreach (string builtin in new[] { "Direct", "Reject", "RejectDrop", "Pass", "Compatible" })
        pressure.Add(builtin, new(builtin, builtin, null, null));
    var typed = HostProjection.Proxies(new(pressure));
    Require(typed.Nodes.Length == 1000 && typed.Groups.Length == 1, "leaf pressure with groups and builtins");
    pressure.Add("too-many", new("too-many", "Shadowsocks", null, null));
    try { HostProjection.Proxies(new(pressure)); throw new CheckFailed("leaf limit not enforced"); }
    catch (HostFailure failure) { Require(failure.Status == HostStatus.InvalidResponse, "bounded leaves"); }
    var roster = Enumerable.Range(0, 1000).Select(i => (ProviderMemberWire?)new($"n{i}")).ToArray();
    var providerFacts = HostProjection.Providers(new(new()
    {
        ["provider1"] = new("HTTP", roster), ["provider2"] = new("File", roster),
        ["synthetic-group-bucket"] = new("Compatible", roster)
    }));
    Require(providerFacts.Length == 2 && providerFacts.All(p => p.Members.Count == 1000), "overlapping provider rosters");
    Console.WriteLine("forbidden_routes=reject_at_send_guard; cancelled_requests=0; response_overflow=reject; pressure_leaves=1000");
}

static void MustReject(string method, string path)
{
    try { ReadOnlyHostClient.CheckRoute(method, path); throw new CheckFailed("forbidden request reached allowlist"); }
    catch (HostFailure) { }
}

static async Task NoSecretsAsync()
{
    var core = WindowsPeer.Inspect(Environment.ProcessId);
    string missingPipe = "ProxyPartner-absent-" + Guid.NewGuid().ToString("N");
    string path = Path.Combine(Environment.CurrentDirectory, "controller.fixture.yaml");
    string secret = Guid.NewGuid().ToString("N");
    string password = Guid.NewGuid().ToString("N");
    string url = $"https://fixture.invalid/{Guid.NewGuid():N}";
    try
    {
        using var server = new HttpFixture(secret, password, url);
        var grant = Grant(core, path, $"127.0.0.1:{server.Port}", secret);
        var result = await ReadOnlyHostClient.ReadForCoreAsync(core, missingPipe, new(IncludeProviders: true), grant);
        Require(result.Status == HostStatus.ConnectedLoopback && Routes(result) ==
            "/version:200,/proxies:200,/providers/proxies:200" && server.Authenticated == 3,
            "three exact approved routes");
        Require(result.Facts is { Nodes.Count: 1, Groups.Count: 1, Providers.Count: 1 }, "typed projection");
        var encoded = JsonSerializer.Serialize(result, FixtureJson.Default.HostReadResult);
        Require(!encoded.Contains(secret) && !encoded.Contains(password) && !encoded.Contains(url), "projected result excludes sensitive fields");
        var denied = await ReadOnlyHostClient.ReadForCoreAsync(core, missingPipe, new(),
            Grant(core, path, $"127.0.0.1:{server.Port}", Guid.NewGuid().ToString("N")));
        Require(denied.Status == HostStatus.FallbackAuthFailed &&
            !JsonSerializer.Serialize(denied, FixtureJson.Default.HostReadResult).Contains(secret),
            "error result excludes response secret");

        string fakePipe = "ProxyPartner-fake-" + Guid.NewGuid().ToString("N");
        using var serverPipe = new NamedPipeServerStream(fakePipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var owner = StartFixture(0);
        try
        {
            Require(await ReadLineAsync(owner) == "ready", "other process ready");
            var accepted = serverPipe.WaitForConnectionAsync();
            var impostor = await ReadOnlyHostClient.ReadForCoreAsync(WindowsPeer.Inspect(owner.Id), fakePipe, new(), grant);
            Require(impostor.Status == HostStatus.IdentityRejected && impostor.Requests.Count == 0, "wrong pipe identity rejected");
            await accepted.WaitAsync(TimeSpan.FromSeconds(4));
            byte[] byteProbe = new byte[1];
            Require(await serverPipe.ReadAsync(byteProbe).AsTask().WaitAsync(TimeSpan.FromSeconds(4)) == 0,
                "fake pipe receives no HTTP bytes");
            await owner.StandardInput.WriteLineAsync("done");
            Require(await ReadLineAsync(owner) == "accepted=0", "no unauthorized fallback");
        }
        finally { if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); } }
        Console.WriteLine($"fixture_bearer_seen_by_verified_owner={server.Authenticated}; projected_secrets=0; fake_pipe_http_bytes=0");
    }
    finally { File.Delete(path); }
}

static LoopbackFallbackAuthorization Grant(CoreIdentity core, string path, string endpoint, string secret)
{
    byte[] yaml = Encoding.UTF8.GetBytes($"external-controller: {endpoint}\nsecret: {secret}\n");
    File.WriteAllBytes(path, yaml);
    return new(core.Pid, core.StartedAtUtc ?? throw new CheckFailed("fixture process start time"), path,
        Convert.ToHexString(SHA256.HashData(yaml)));
}

static Process StartFixture(int port)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true
    };
    start.ArgumentList.Add("--fixture");
    start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return Process.Start(start)!;
}

static async Task<string?> ReadLineAsync(Process process) =>
    await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));

static async Task<int> FixtureOwnerAsync(int port)
{
    using var listener = new TcpListener(IPAddress.Loopback, port);
    listener.ExclusiveAddressUse = true;
    listener.Start();
    Console.WriteLine("ready");
    await Console.In.ReadLineAsync();
    Console.WriteLine(listener.Pending() ? "accepted=1" : "accepted=0");
    return 0;
}

sealed class CheckFailed(string label) : Exception(label);

sealed class HttpFixture : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private int requests;
    private int authenticated;
    internal int Requests => Volatile.Read(ref requests);
    internal int Authenticated => Volatile.Read(ref authenticated);
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    internal HttpFixture(string token, string password = "fixture", string url = "https://fixture.invalid/", bool redirect = false)
    {
        listener.Start();
        loop = ServeAsync(token, password, url, redirect);
    }

    private async Task ServeAsync(string token, string password, string url, bool redirect)
    {
        while (!stop.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = await reader.ReadLineAsync(stop.Token);
            if (request is null) continue;
            Interlocked.Increment(ref requests);
            bool authorized = false;
            while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                authorized |= line.Equals("Authorization: Bearer " + token, StringComparison.Ordinal);
            if (authorized) Interlocked.Increment(ref authenticated);
            string path = request.Split(' ')[1];
            string data = path switch
            {
                "/version" => "{\"version\":\"v1.19.21\"}",
                "/proxies" => "{\"proxies\":{\"n\":{\"name\":\"n\",\"type\":\"Shadowsocks\",\"password\":\"" +
                    password + "\"},\"g\":{\"name\":\"g\",\"type\":\"Selector\",\"now\":\"n\",\"all\":[\"n\"]}}}",
                "/providers/proxies" => "{\"providers\":{\"p\":{\"vehicleType\":\"HTTP\",\"url\":\"" + url +
                    "\",\"proxies\":[{\"name\":\"n\"}]}}}",
                _ => throw new CheckFailed("fixture received unexpected path")
            };
            var response = Encoding.UTF8.GetBytes(authorized ? data : "{\"error\":\"" + token + "\"}");
            string status = !authorized ? "401 Unauthorized" : redirect ? "302 Found" : "200 OK";
            string location = redirect ? "Location: http://127.0.0.1:1/version\r\n" : "";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\n{location}Content-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
            await stream.WriteAsync(response, stop.Token);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { loop.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); stop.Dispose(); }
    }
}

[JsonSerializable(typeof(HostReadResult))]
internal sealed partial class FixtureJson : JsonSerializerContext { }
