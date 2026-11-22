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
    Console.WriteLine("ProxyPartner.Cli probe --profile <file> [--age-identity <key-file>] (--all | --node <name>) [--rounds 1-6] [--min-parallel 1-12 --max-parallel 1-12 | --parallel 1-12] [--node-timeout-ms 1-6000] [--kernel-latency]");
    return 0;
}
string command = args[0];
string? path = null, requested = null, identity = null;
bool all = false, kernel = false;
int rounds = 1, minParallel = 4, maxParallel = 8, fixedParallel = 0, fixedTimeout = 0;
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
        case "--min-parallel" when i + 1 < args.Length && int.TryParse(args[++i], out minParallel) && minParallel is >= 1 and <= 12: break;
        case "--max-parallel" when i + 1 < args.Length && int.TryParse(args[++i], out maxParallel) && maxParallel is >= 1 and <= 12: break;
        case "--parallel" when i + 1 < args.Length && int.TryParse(args[++i], out fixedParallel) && fixedParallel is >= 1 and <= 12: break;
        case "--node-timeout-ms" when i + 1 < args.Length && int.TryParse(args[++i], out fixedTimeout) && fixedTimeout is >= 1 and <= 6000: break;
        default: return Usage();
    }
}
if (minParallel > maxParallel || fixedParallel != 0 && (seen.Contains("--min-parallel") || seen.Contains("--max-parallel")) ||
    string.IsNullOrWhiteSpace(path) || identity is not null && string.IsNullOrWhiteSpace(identity) ||
    command == "inspect" && seen.Count != (identity is null ? 1 : 2) || command == "probe" && all == (requested is not null) ||
    command is not ("inspect" or "probe")) return Usage();
if (fixedParallel != 0) minParallel = maxParallel = fixedParallel;

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
        int scored = 0, replies = 0, attempted = 0, completed = 0, dnsRetries = 0;
        var nativeBudget = new NodeBudget(fixedTimeout);
        var workerBudget = new NodeBudget(fixedTimeout);
        var dns = new ConcurrentDictionary<string, Lazy<Task<string?>>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<(string, string), HostNativeSample?> native = [];
        if (kernel)
        {
            var read = await new ReadOnlyHostClient().ReadAsync(cancellationToken: stop.Token);
            Write(new("kernel", State: read.Status.ToString()));
            if (read.Facts is { } host) native = host.Nodes.ToDictionary(n => (n.Name, n.Type), n => n.Native);
        }

        async Task<bool> ProbeNodeAsync((string Name, ProbeProfile? Profile, string[]? Endpoints, string State) n,
            CancellationToken token)
        {
            if (n.State != "ready")
            {
                Write(new("node", n.Name, n.Profile?.Type, n.State));
                return false;
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
                    return false;
                }
                string? sni = settings.Type == "anytls" && string.IsNullOrEmpty(settings.Sni) ? settings.Server : settings.Sni;
                profile = profile with { WorkerProfile = settings with { Server = address, Sni = sni } };
            }

            var node = new HostNode(n.Name, profile.Type, native.GetValueOrDefault((n.Name, profile.Type)));
            var history = new Dictionary<(string, string), WebsiteWindow>();
            var latencies = targets.Select(_ => new List<int>(rounds)).ToArray();
            using var scorer = new LiveHostSource();
            NodeBudget budget = profile.RequiresWorker ? workerBudget : nativeBudget;
            bool allAnswered = true;
            for (int round = 1; round <= rounds; round++)
            {
                int deadlineMs = budget.DeadlineMs;
                var (samples, elapsedMs, expired) = await MeasureRoundAsync(profile, n.Name, deadlineMs, token);
                token.ThrowIfCancellationRequested();
                bool answered = samples.All(sample => sample?.HttpStatus is not null);
                allAnswered &= answered;
                if (answered && elapsedMs is int measuredRound) budget.Observe(measuredRound);
                var observed = DateTimeOffset.UtcNow;
                var sites = new CliSite[targets.Count];
                var usable = new List<WebsiteProbe>(targets.Count);
                for (int i = 0; i < targets.Count; i++)
                {
                    string id = targets[i].Id;
                    var sample = samples[i];
                    if (sample is { Attempted: true })
                    {
                        usable.Add(sample);
                        if (history.TryGetValue((n.Name, id), out var window)) window.Add(sample);
                        else history.Add((n.Name, id), new(sample));
                    }
                    var measured = latencies[i];
                    if (sample?.ElapsedMs is int elapsed) measured.Add(elapsed);
                    double? mean = measured.Count == 0 ? null : measured.Average();
                    double? deviation = measured.Count < 2 ? null : Math.Sqrt(measured.Average(ms => Math.Pow(ms - mean!.Value, 2)));
                    sites[i] = new(id, sample?.Attempted == true, sample?.HttpStatus, sample?.ElapsedMs,
                        sample?.TimedOut == true, measured.Count, mean, deviation, sample?.NativeTransport, false);
                }

                scorer.PublishFacts(new(observed, "", [node], [], null), usable, history, [], [], [], [], [], catalog,
                    null, new(0, 0, false), false, false, new HashSet<string>(), new HashSet<string>(),
                    new Dictionary<string, uint>());
                var quality = scorer.GetSnapshot().Rows[0].Quality;
                Write(new("round", n.Name, node.Type, Round: round, Score: quality?.Score,
                    StableTargets: quality?.StableTargetCount, KernelLatencyMs: node.Native?.ElapsedMs,
                    KernelObservedUtc: node.Native?.ObservedUtc, Sites: sites,
                    NodeElapsedMs: elapsedMs, NodeDeadlineMs: deadlineMs, DeadlineReached: expired));
                Interlocked.Add(ref replies, sites.Count(site => site.HttpStatus is not null));
                Interlocked.Add(ref attempted, usable.Count);
                if (round == rounds)
                {
                    Interlocked.Increment(ref completed);
                    if (quality?.Score is not null) Interlocked.Increment(ref scored);
                }
            }
            return allAnswered;
        }

        // Interleave the transports so queued Go work cannot occupy all node slots first.
        var queue = new Queue<(string Name, ProbeProfile? Profile, string[]? Endpoints, string State)>(
            nodes.GroupBy(n => n.Profile?.RequiresWorker == true)
                .SelectMany(group => group.Select((node, index) => (node, index))).OrderBy(item => item.index).Select(item => item.node));
        var active = new List<Task<bool>>(maxParallel);
        int limit = minParallel, peakParallel = 0;
        try
        {
            while (queue.Count != 0 || active.Count != 0)
            {
                stop.Token.ThrowIfCancellationRequested();
                while (active.Count < limit && queue.TryDequeue(out var node)) active.Add(ProbeNodeAsync(node, stop.Token));
                peakParallel = Math.Max(peakParallel, active.Count);
                var finished = await Task.WhenAny(active);
                active.Remove(finished);
                limit = Math.Clamp(limit + (await finished ? 1 : -1), minParallel, maxParallel);
            }
        }
        finally
        {
            if (active.Count != 0)
            {
                stop.Cancel();
                try { await Task.WhenAll(active); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
        Write(new("summary", Nodes: nodes.Length, Completed: completed, Scored: scored, Replies: replies,
            Attempts: attempted, Retries: 0, DnsRetries: dnsRetries, WallSeconds: watch.Elapsed.TotalSeconds, Parallel: peakParallel,
            MinParallel: minParallel, MaxParallel: maxParallel, NativeDeadlineMs: nativeBudget.DeadlineMs, WorkerDeadlineMs: workerBudget.DeadlineMs));
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

static async Task<(WebsiteProbe?[] Sites, int? ElapsedMs, bool DeadlineReached)> MeasureRoundAsync(
    ProbeProfile profile, string node, int deadlineMs, CancellationToken stop)
{
    var targets = ReadOnlyHostClient.WebsiteTargets;
    if (profile.RequiresWorker)
    {
        var (round, expired) = await GoWebsiteProbe.MeasureRoundAsync(profile.WorkerProfile!, deadlineMs, stop);
        if (round?.Sites is not { } replies) return (new WebsiteProbe?[targets.Count], null, expired);
        var now = DateTimeOffset.UtcNow;
        var sites = targets.Select(target => replies.TryGetValue(target.Url.AbsoluteUri, out var reply)
            ? new WebsiteProbe(target.Id, node, false, true, reply.HttpStatus, reply.HttpStatus is null ? null : reply.ElapsedMs,
                now, TimedOut: reply.Status == "timeout", Independent: true) : null).ToArray();
        return (sites, round.ElapsedMs, sites.Any(site => site?.TimedOut == true));
    }

    using var cutoff = CancellationTokenSource.CreateLinkedTokenSource(stop);
    using var session = new IndependentNodeProbe.Session(profile, cutoff.Token);
    var watch = Stopwatch.StartNew();
    cutoff.CancelAfter(deadlineMs);
    var samples = await Task.WhenAll(Enumerable.Range(0, targets.Count).Select(async i =>
    {
        var site = (await session.MeasureAsync(node, i)).Site;
        return cutoff.IsCancellationRequested && site is { Attempted: true, HttpStatus: null }
            ? site with { TimedOut = true } : site;
    }));
    return (samples, (int)watch.ElapsedMilliseconds, samples.Any(site => site?.TimedOut == true));
}

internal sealed class NodeBudget(int fixedTimeout)
{
    private readonly object gate = new();
    private int total, count;

    internal int DeadlineMs
    {
        get { lock (gate) return fixedTimeout != 0 ? fixedTimeout : count < 4 ? 6000 : (total + count - 1) / count; }
    }

    internal void Observe(int elapsedMs)
    {
        lock (gate)
        {
            if (count == 4 || fixedTimeout != 0) return;
            total += Math.Clamp(elapsedMs, 1, 6000);
            count++;
        }
    }
}

internal sealed record CliSite(string Target, bool Attempted, int? HttpStatus, int? LatencyMs, bool TimedOut,
    int Replies, double? MeanMs, double? StandardDeviationMs, bool? NativeTransport, bool Retried);
internal sealed record CliEvent(string Event, string? Node = null, string? Protocol = null, string? State = null,
    string? Format = null, int? Nodes = null, int? Eligible = null, int? Round = null, int? Score = null,
    int? StableTargets = null, int? KernelLatencyMs = null, DateTimeOffset? KernelObservedUtc = null,
    CliSite[]? Sites = null, int? Completed = null, int? Scored = null, int? Replies = null, int? Attempts = null,
    int? Retries = null, int? DnsRetries = null, double? WallSeconds = null, int? Parallel = null,
    int? MinParallel = null, int? MaxParallel = null, int? NodeElapsedMs = null, int? NodeDeadlineMs = null,
    bool? DeadlineReached = null, int? NativeDeadlineMs = null, int? WorkerDeadlineMs = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliEvent))]
internal sealed partial class CliJson : JsonSerializerContext { }
