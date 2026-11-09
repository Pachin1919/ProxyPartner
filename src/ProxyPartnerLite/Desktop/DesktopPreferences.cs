using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyPartnerLite.Desktop;

internal sealed record CustomTarget(string Url, string ShortName, string Symbol)
{
    public static bool IsValid(CustomTarget? target) => target is
        { Url.Length: > 0 and <= 2048, ShortName.Length: > 0 and <= 12, Symbol.Length: > 0 and <= 8 } &&
        !target.Url.Any(char.IsWhiteSpace) && !target.Url.Any(char.IsControl) && !target.Url.Contains('\\') &&
        Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 &&
        !target.ShortName.Any(char.IsControl) && !target.Symbol.Any(char.IsControl);
}

internal sealed record WeightSet(int First, int Second, int Third)
{
    [JsonIgnore]
    public bool IsValid => First is >= 0 and <= 100 && Second is >= 0 and <= 100 &&
        Third is >= 0 and <= 100 && First + Second + Third == 100;

    public double Apply(double first, double second, double third) =>
        (First * first + Second * second + Third * third) / 100;
}

internal sealed record DesktopPreferences(bool English, bool Light, WeightSet Components,
    WeightSet Targets, CustomTarget[] CustomTargets)
{
    internal const int MaxTargets = 16;
    private const int MaxBytes = 64 * 1024;
    public static DesktopPreferences Defaults { get; } = new(false, false, new(50, 35, 15), new(60, 30, 10), []);

    public static DesktopPreferences Load(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length is <= 0 or > MaxBytes) return Defaults;
            byte[] bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            var value = JsonSerializer.Deserialize(bytes, PreferenceJson.Default.DesktopPreferences);
            if (value is { Components.IsValid: true, Targets.IsValid: true, CustomTargets.Length: <= MaxTargets } &&
                value.CustomTargets.All(CustomTarget.IsValid)) return value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return Defaults;
    }

    public void Save(string path)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, PreferenceJson.Default.DesktopPreferences);
        if (bytes.Length > MaxBytes) throw new IOException("Desktop preferences exceed the size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

[JsonSerializable(typeof(DesktopPreferences))]
internal sealed partial class PreferenceJson : JsonSerializerContext { }
