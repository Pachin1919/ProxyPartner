using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxyPartnerLite.Host;
using YamlDotNet.RepresentationModel;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("ProxyPartner.Cli inspect --profile <file> [--age-identity <key-file>]");
    Console.WriteLine("ProxyPartner.Cli probe --profile <file> [--age-identity <key-file>] (--all | --node <name>) [--rounds 1-6] [--parallel 1-12] [--kernel-latency]");
    return 0;
}
string command = args[0];
string? path = null, requested = null, identity = null;
bool all = false, kernel = false;
int rounds = 1, parallel = 4;
var seen = new HashSet<string>(StringComparer.Ordinal);
for (int i = 1; i < args.Length; i++)
{
    string arg = args[i];
    if (!seen.Add(arg)) return Usage();
    switch (arg)
    {
        case "--profile" when i + 1 < args.Length: path = args[++i]; break;
        case "--age-identity" when i + 1 < args.Length: identity = args[++i]; break;
        case "--node" when i + 1 < args.Length: requested = args[++i]; break;
        case "--all": all = true; break;
        case "--kernel-latency": kernel = true; break;
        case "--rounds" when i + 1 < args.Length && int.TryParse(args[++i], out rounds) && rounds is >= 1 and <= 6: break;
        case "--parallel" when i + 1 < args.Length && int.TryParse(args[++i], out parallel) && parallel is >= 1 and <= 12: break;
        default: return Usage();
    }
}
if (string.IsNullOrWhiteSpace(path) || identity is not null && string.IsNullOrWhiteSpace(identity) ||
    command == "inspect" && seen.Count != (identity is null ? 1 : 2) || command == "probe" && all == (requested is not null) ||
    command is not ("inspect" or "probe")) return Usage();

var outputLock = new object();
void Write(CliEvent value)
{
    string line = JsonSerializer.Serialize(value, CliJson.Default.CliEvent);
    lock (outputLock) Console.WriteLine(line);
}
var catalog = IndependentNodeProbe.Load(path, includeDns: true);
try
{
    if (catalog.Container == "age")
    {
        if (identity is null)
        {
            Write(new("error", State: "age_identity_required", Format: "age"));
            return 2;
        }
        var (decrypted, error) = await AgeProfile.OpenAsync(path, identity);
        if (decrypted is null)
        {
            Write(new("error", State: error, Format: "age"));
            return 2;
        }
        catalog = decrypted;
    }
    if (catalog.Configured.Count == 0)
    {
        Write(new("error", State: catalog.Container is null ? "missing_or_invalid_yaml_proxies" : "encrypted_profile",
            Format: catalog.Container));
        return 2;
    }
    string[] names = requested is null ? catalog.Configured.ToArray() :
        catalog.Configured.Contains(requested) ? [requested] : [];
    if (names.Length == 0)
    {
        Write(new("error", State: "node_not_found"));
        return 2;
    }

    var policy = catalog.DnsPolicy?.Children;
    string[]? Endpoints(string? host)
    {
        if (policy is null || host is null || IPAddress.TryParse(host, out _)) return null;
        host = host.TrimEnd('.');
        YamlNode? matched = null;
        int priority = 0;
        foreach (var (key, value) in policy)
        {
            if (key is not YamlScalarNode { Value: { } pattern }) continue;
            if (host.Equals(pattern, StringComparison.OrdinalIgnoreCase)) { matched = value; break; }
            if (!pattern.StartsWith("+.", StringComparison.Ordinal) || pattern.Length <= Math.Max(2, priority) ||
                !(host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase) ||
                  host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))) continue;
            matched = value;
            priority = pattern.Length;
        }
        if (matched is null) return null;
        IEnumerable<YamlNode> values = matched is YamlSequenceNode list ? list.Children : [matched];
        return values.OfType<YamlScalarNode>()
            .Select(value => Uri.TryCreate(value.Value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
                uri.Fragment.TrimStart('#').Split('&').All(flag => flag is "" or "h3=true" or "skip-cert-verify=true")
                    ? uri.GetLeftPart(UriPartial.Path) : null)
            .OfType<string>().Distinct(StringComparer.Ordinal).Take(8).ToArray();
    }

    string State(ProbeProfile? profile, string[]? endpoints) => profile is null ? "unsupported_node" :
        endpoints is { Length: 0 } ? "unsupported_dns_policy" :
        (profile.RequiresWorker || endpoints is not null) && !GoWebsiteProbe.IsInstalled ? "worker_missing" : "ready";

    var nodes = names.Select(name =>
    {
        var profile = catalog.Profiles.GetValueOrDefault(name);
        var endpoints = Endpoints(profile?.WorkerProfile?.Server);
        return (Name: name, Profile: profile, Endpoints: endpoints, State: State(profile, endpoints));
    }).ToArray();
    Write(new("catalog", Nodes: nodes.Length, Eligible: nodes.Count(n => n.State == "ready")));
    if (command == "inspect")
    {
        foreach (var n in nodes) Write(new("node", n.Name, n.Profile?.Type, n.State));
        return 0;
    }

    using var stop = new CancellationTokenSource();
    void Cancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; stop.Cancel(); }
    Console.CancelKeyPress += Cancel;
    try
    {
        var targets = ReadOnlyHostClient.WebsiteTargets;
        var watch = Stopwatch.StartNew();
        int scored = 0, replies = 0, attempted = 0, completed = 0, retries = 0, dnsRetries = 0;
        var dns = new ConcurrentDictionary<string, Lazy<Task<string?>>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<(string, string), HostNativeSample?> native = [];
        if (kernel)
        {
            var read = await new ReadOnlyHostClient().ReadAsync(cancellationToken: stop.Token);
            Write(new("kernel", State: read.Status.ToString()));
            if (read.Facts is { } host) native = host.Nodes.ToDictionary(n => (n.Name, n.Type), n => n.Native);
        }

        // Each node keeps its own scoring window while a bounded number of nodes are in flight.
        await Parallel.ForEachAsync(nodes, new ParallelOptions { CancellationToken = stop.Token, MaxDegreeOfParallelism = parallel },
            async (n, token) =>
            {
                if (n.State != "ready")
                {
                    Write(new("node", n.Name, n.Profile?.Type, n.State));
                    return;
                }
                var profile = n.Profile!;
                if (n.Endpoints is { } endpoints && profile.WorkerProfile is { } settings)
                {
                    var address = await dns.GetOrAdd(settings.Server, host => new Lazy<Task<string?>>(async () =>
                    {
                        var first = await GoWebsiteProbe.ResolveAsync(host, endpoints, stop.Token);
                        if (first is not null) return first;
                        Interlocked.Increment(ref dnsRetries);
                        return await GoWebsiteProbe.ResolveAsync(host, endpoints, stop.Token);
                    })).Value;
                    if (address is null)
                    {
                        Write(new("node", n.Name, profile.Type, "entry_dns_failed"));
                        return;
                    }
                    string? sni = settings.Type == "anytls" && string.IsNullOrEmpty(settings.Sni) ? settings.Server : settings.Sni;
                    profile = profile with { WorkerProfile = settings with { Server = address, Sni = sni } };
                }

                var node = new HostNode(n.Name, profile.Type, native.GetValueOrDefault((n.Name, profile.Type)));
                var history = new Dictionary<(string, string), WebsiteWindow>();
                var latencies = targets.Select(_ => new List<int>(rounds)).ToArray();
                using var scorer = new LiveHostSource();
                using var session = new IndependentNodeProbe.Session(profile, token);
                for (int round = 1; round <= rounds; round++)
                {
                    var samples = await Task.WhenAll(Enumerable.Range(0, targets.Count).Select(async i =>
                    {
                        var first = (await session.MeasureAsync(n.Name, i)).Site;
                        if (first?.HttpStatus is not null) return (First: first, Retry: (WebsiteProbe?)null, Retried: false);
                        Interlocked.Increment(ref retries);
                        var retry = (await session.MeasureAsync(n.Name, i)).Site;
                        return (First: first, Retry: retry, Retried: true);
                    }));
                    token.ThrowIfCancellationRequested();
                    var observed = DateTimeOffset.UtcNow;
                    var sites = new CliSite[targets.Count];
                    var usable = new List<WebsiteProbe>(targets.Count);
                    for (int i = 0; i < targets.Count; i++)
                    {
                        string id = targets[i].Id;
                        var (first, retry, retried) = samples[i];
                        var sample = retry ?? first;
                        if (sample is { Attempted: true })
                        {
                            usable.Add(sample);
                            if (first is { Attempted: true } && retry is { Attempted: true }) AddSample(first);
                            AddSample(sample);
                        }
                        var measured = latencies[i];
                        if (sample?.ElapsedMs is int elapsed) measured.Add(elapsed);
                        double? mean = measured.Count == 0 ? null : measured.Average();
                        double? deviation = measured.Count < 2 ? null : Math.Sqrt(measured.Average(ms => Math.Pow(ms - mean!.Value, 2)));
                        sites[i] = new(id, sample?.Attempted == true, sample?.HttpStatus, sample?.ElapsedMs,
                            sample?.TimedOut == true, measured.Count, mean, deviation, sample?.NativeTransport, retried);

                        void AddSample(WebsiteProbe value)
                        {
                            if (history.TryGetValue((n.Name, id), out var window)) window.Add(value);
                            else history.Add((n.Name, id), new(value));
                        }
                    }

                    scorer.PublishFacts(new(observed, "", [node], [], null), usable, history, [], [], [], [], [], catalog,
                        null, new(0, 0, false), false, false, new HashSet<string>(), new HashSet<string>(),
                        new Dictionary<string, uint>());
                    var quality = scorer.GetSnapshot().Rows[0].Quality;
                    Write(new("round", n.Name, node.Type, Round: round, Score: quality?.Score,
                        StableTargets: quality?.StableTargetCount, KernelLatencyMs: node.Native?.ElapsedMs,
                        KernelObservedUtc: node.Native?.ObservedUtc, Sites: sites));
                    Interlocked.Add(ref replies, sites.Count(site => site.HttpStatus is not null));
                    Interlocked.Add(ref attempted, usable.Count);
                    if (round == rounds)
                    {
                        Interlocked.Increment(ref completed);
                        if (quality?.Score is not null) Interlocked.Increment(ref scored);
                    }
                }
            });
        Write(new("summary", Nodes: nodes.Length, Completed: completed, Scored: scored, Replies: replies,
            Attempts: attempted, Retries: retries, DnsRetries: dnsRetries, WallSeconds: watch.Elapsed.TotalSeconds, Parallel: parallel));
        return scored == nodes.Length && replies == nodes.Length * rounds * targets.Count ? 0 : 1;
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        Write(new("error", State: "cancelled"));
        return 130;
    }
    finally { Console.CancelKeyPress -= Cancel; }
}
finally
{
    NativeNodeWebsiteProbe.Shutdown();
    NativeWebsiteProbe.Stop();
}

static int Usage()
{
    Console.Error.WriteLine("Invalid arguments. Run ProxyPartner.Cli --help.");
    return 2;
}

internal sealed record CliSite(string Target, bool Attempted, int? HttpStatus, int? LatencyMs, bool TimedOut,
    int Replies, double? MeanMs, double? StandardDeviationMs, bool? NativeTransport, bool Retried);
internal sealed record CliEvent(string Event, string? Node = null, string? Protocol = null, string? State = null,
    string? Format = null, int? Nodes = null, int? Eligible = null, int? Round = null, int? Score = null,
    int? StableTargets = null, int? KernelLatencyMs = null, DateTimeOffset? KernelObservedUtc = null,
    CliSite[]? Sites = null, int? Completed = null, int? Scored = null, int? Replies = null, int? Attempts = null,
    int? Retries = null, int? DnsRetries = null, double? WallSeconds = null, int? Parallel = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliEvent))]
internal sealed partial class CliJson : JsonSerializerContext { }
