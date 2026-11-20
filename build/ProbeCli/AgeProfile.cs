using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

internal static class AgeProfile
{
    internal static async Task<(ProbeCatalog? Catalog, string? Error)> OpenAsync(string path, string identity)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "age.exe");
        if (!File.Exists(executable)) return (null, "age_tool_missing");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["PATH"] = "";
        foreach (string arg in new[] { "--decrypt", "--identity", Path.GetFullPath(identity), "--", Path.GetFullPath(path) })
            start.ArgumentList.Add(arg);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[IndependentNodeProbe.MaxProfileBytes + 1];
        try
        {
            var (count, exit) = await ProbeChild.ReadAsync(start, buffer, deadline.Token);
            if (count == buffer.Length) return (null, "age_profile_too_large");
            return exit == 0 ? (IndependentNodeProbe.LoadDecryptedYaml(buffer, count), null) : (null, "age_decrypt_failed");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return (null, "age_timeout"); }
        catch (Exception error) when (error is Win32Exception or IOException) { return (null, "age_tool_failed"); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
