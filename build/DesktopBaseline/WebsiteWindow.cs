using ProxyPartnerLite.Host;

internal sealed class WebsiteWindow
{
    internal static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);
    private const int Capacity = 6;
    private readonly Queue<WebsiteProbe> samples = new(Capacity);

    internal WebsiteWindow(WebsiteProbe first)
    {
        Latest = first;
        samples.Enqueue(first);
    }

    internal WebsiteProbe Latest { get; private set; }

    internal void Add(WebsiteProbe sample)
    {
        if (samples.Count == Capacity) samples.Dequeue();
        samples.Enqueue(sample);
        Latest = sample;
    }

    internal (double Score, bool HasStability)? Score(DateTimeOffset now)
    {
        int attempts = 0, replies = 0;
        double mean = 0, squaredDeviation = 0;
        foreach (var sample in samples)
        {
            if (now - sample.ObservedUtc > Freshness) continue;
            attempts++;
            if (sample.HttpStatus is null || sample.ElapsedMs is not (>= 0 and int ms)) continue;
            replies++;
            double delta = ms - mean;
            mean += delta / replies;
            squaredDeviation += delta * (ms - mean);
        }
        if (attempts == 0) return null;
        if (replies == 0) return (0, false);

        double availability = 50.0 * replies / attempts;
        double speed = 35 * Math.Clamp((3000 - mean) / 2800, 0, 1);
        if (replies == 1) return ((availability + speed) / 0.85, false);
        double stability = 15 * Math.Clamp(1 - Math.Sqrt(Math.Max(0, squaredDeviation / replies)) / 1000, 0, 1);
        return (availability + speed + stability, true);
    }
}
