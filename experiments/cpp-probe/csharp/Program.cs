using System.IO.Pipes;
using System.Text;
using ProxyPartnerLite.Host;
using QuickProxyNet;

if (args.Length != 3 || !args[0].StartsWith(@"\\.\pipe\", StringComparison.Ordinal)) return 2;
using var pipe = new NamedPipeClientStream(".", args[0][9..], PipeDirection.In);
await pipe.ConnectAsync(10000);
using var reader = new StreamReader(pipe);
var profiles = new List<ProbeProfile>();
while (await reader.ReadLineAsync() is { } line)
{
    var fields = line.Split('\t');
    var client = new ShadowsocksClient(new()
    {
        Host = fields[0], Port = int.Parse(fields[1]), Password = fields[2], Method = "chacha20-ietf-poly1305"
    });
    var obfs = fields[3];
    profiles.Add(new("Shadowsocks", "", (endpoint, token) => ShadowsocksTlsObfs.ConnectAsync(client, obfs, endpoint, token)));
}
if (profiles.Count == 0) return 2;
const int siteCount = ReadOnlyHostClient.CurrentProbeTargetCount;
string[] names = ["GPT", "OPENAI", "GO", "YT", "ANT"];
var results = new WebsiteProbe[profiles.Count, siteCount];
var options = new ParallelOptions { MaxDegreeOfParallelism = 8 };
using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
using var report = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true };
for (int round = 1; round <= 3; round++)
{
    report.WriteLine($"sweep\t{round}");
    await Parallel.ForEachAsync(Enumerable.Range(0, profiles.Count), options, async (node, stop) =>
    {
        using var session = new IndependentNodeProbe.Session(profiles[node], stop);
        for (int site = 0; site < siteCount; site++)
            results[node, site] = (await session.MeasureAsync("", site)).Site!;
    });
    for (int node = 0; node < profiles.Count; node++)
        for (int site = 0; site < siteCount; site++)
        {
            var result = results[node, site];
            report.WriteLine($"{names[site]}\t{round}\t{node}\t{result.HttpStatus ?? 0}\t{result.ElapsedMs ?? 0}\t{(result.TimedOut ? 1 : 0)}");
        }
    report.WriteLine($"idle\t{round}");
    await Task.Delay(30000);
}
return 0;
