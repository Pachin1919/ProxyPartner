using System.Diagnostics;
using System.Text;

internal static class ProbeChild
{
    internal static async Task<(int Count, int ExitCode)> ReadAsync(ProcessStartInfo start, byte[] buffer,
        CancellationToken stop, Func<Stream, Task>? writeInput = null)
    {
        start.StandardInputEncoding = new UTF8Encoding(false);
        using var process = new Process { StartInfo = start };
        process.Start();
        var discard = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            if (writeInput is not null) await writeInput(process.StandardInput.BaseStream).ConfigureAwait(false);
            process.StandardInput.Close();
            int count = await process.StandardOutput.BaseStream.ReadAtLeastAsync(buffer, buffer.Length, false, stop)
                .ConfigureAwait(false);
            if (count == buffer.Length) return (count, -1);
            await process.WaitForExitAsync(stop).ConfigureAwait(false);
            return (count, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            await discard.ConfigureAwait(false);
        }
    }
}
