using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

internal sealed record NativeShadowsocksProfile(string Server, int Port, string Password, string ObfsHost);

internal sealed partial class NativeWebsiteProbe
{
    private const uint SearchDllDirectoryAndSystem = 0x00000100 | 0x00000800;
    private static readonly Lazy<NativeWebsiteProbe?> Backend = new(Load);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Initialize([MarshalAs(UnmanagedType.LPUTF8Str)] string caBundle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Probe(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string address,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string obfsHost,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string headers,
        uint remaining, nint stop, out int status);

    private readonly nint module;
    private readonly Probe probe;
    private readonly Shutdown shutdown;

    private NativeWebsiteProbe(nint module, Probe probe, Shutdown shutdown) =>
        (this.module, this.probe, this.shutdown) = (module, probe, shutdown);

    internal static NativeWebsiteProbe? Get() => Backend.Value;

    private static NativeWebsiteProbe? Load()
    {
        var module = LoadLibraryEx(Path.Combine(AppContext.BaseDirectory, "ProxyPartner.NativeProbe.dll"),
            0, SearchDllDirectoryAndSystem);
        if (module == 0) return null;
        if (NativeLibrary.TryGetExport(module, "ProbeInitialize", out var init) &&
            NativeLibrary.TryGetExport(module, "ProbeWebsite", out var probe) &&
            NativeLibrary.TryGetExport(module, "ProbeShutdown", out var stop) &&
            Marshal.GetDelegateForFunctionPointer<Initialize>(init)(Path.Combine(AppContext.BaseDirectory, "probe-ca-bundle.crt")) == 1)
            return new(module, Marshal.GetDelegateForFunctionPointer<Probe>(probe),
                Marshal.GetDelegateForFunctionPointer<Shutdown>(stop));
        NativeLibrary.Free(module);
        return null;
    }

    internal static void Stop()
    {
        if (!Backend.IsValueCreated || Backend.Value is not { } backend) return;
        backend.shutdown();
        NativeLibrary.Free(backend.module);
    }

    internal async Task<int> MeasureAsync(NativeShadowsocksProfile profile, Uri target, string headers,
        long deadline, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(profile.Server, token).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
            using var registration = token.UnsafeRegister(static state => ((EventWaitHandle)state!).Set(), signal);
            string port = profile.Port.ToString(CultureInfo.InvariantCulture);
            foreach (var address in addresses)
            {
                token.ThrowIfCancellationRequested();
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) break;
                if (probe(address.ToString(), port, profile.Password, profile.ObfsHost, target.IdnHost,
                    target.PathAndQuery, headers, (uint)remaining, signal.SafeWaitHandle.DangerousGetHandle(), out int status) != 0)
                    return status;
            }
            return 0;
        }, token).ConfigureAwait(false);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string path, nint file, uint flags);
}
