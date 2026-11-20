using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

internal sealed partial class NativeNodeWebsiteProbe
{
    private static readonly Lazy<NativeNodeWebsiteProbe?> AnyTls = new(() => Load("AnyTls", "ProbeAnyTlsWebsite"));
    private static readonly Lazy<NativeNodeWebsiteProbe?> Mieru = new(() => Load("Mieru", "ProbeMieruWebsite"));
    private readonly nint module;
    private readonly Stop shutdown;
    private readonly AnyTlsProbe? anyTls;
    private readonly MieruProbe? mieru;

    private NativeNodeWebsiteProbe(nint module, Stop shutdown, AnyTlsProbe? anyTls, MieruProbe? mieru) =>
        (this.module, this.shutdown, this.anyTls, this.mieru) = (module, shutdown, anyTls, mieru);

    internal static NativeNodeWebsiteProbe? For(GoProbeRequest settings)
    {
        if (settings.Password.Contains('\0')) return null;
        if (settings.Type == "anytls" && settings.Alpn is null or { Count: 0 } &&
            string.IsNullOrEmpty(settings.ClientFingerprint) && string.IsNullOrEmpty(settings.Fingerprint) &&
            Uri.CheckHostName(string.IsNullOrEmpty(settings.Sni) ? settings.Server : settings.Sni) == UriHostNameType.Dns)
            return AnyTls.Value;
        if (settings.Type == "mieru" && settings.Transport == "TCP" &&
            settings.Username is { } username && !username.Contains('\0') &&
            Encoding.UTF8.GetByteCount(username) <= 255 && Encoding.UTF8.GetByteCount(settings.Password) <= 1024 &&
            string.IsNullOrEmpty(settings.Multiplexing) && string.IsNullOrEmpty(settings.HandshakeMode) &&
            string.IsNullOrEmpty(settings.TrafficPattern))
            return Mieru.Value;
        return null;
    }

    private static NativeNodeWebsiteProbe? Load(string name, string symbol)
    {
        nint module = LoadLibraryEx(Path.Combine(AppContext.BaseDirectory, $"ProxyPartner.{name}Probe.dll"),
            0, 0x00000100 | 0x00000800);
        if (module == 0) return null;
        if (NativeLibrary.TryGetExport(module, "ProbeInitialize", out var initialize) &&
            NativeLibrary.TryGetExport(module, "ProbeShutdown", out var stop) &&
            NativeLibrary.TryGetExport(module, symbol, out var probe) &&
            Marshal.GetDelegateForFunctionPointer<Initialize>(initialize)(Path.Combine(AppContext.BaseDirectory, "probe-ca-bundle.crt")) == 1)
            return new(module, Marshal.GetDelegateForFunctionPointer<Stop>(stop),
                name == "AnyTls" ? Marshal.GetDelegateForFunctionPointer<AnyTlsProbe>(probe) : null,
                name == "Mieru" ? Marshal.GetDelegateForFunctionPointer<MieruProbe>(probe) : null);
        NativeLibrary.Free(module);
        return null;
    }

    internal async Task<int> MeasureAsync(GoProbeRequest settings, Uri target, string headers,
        long deadline, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(settings.Server, token).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
            using var registration = token.UnsafeRegister(static state => ((EventWaitHandle)state!).Set(), signal);
            string port = settings.PortRange ?? settings.Port.ToString(CultureInfo.InvariantCulture);
            foreach (var address in addresses)
            {
                token.ThrowIfCancellationRequested();
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) break;
                int status, result;
                if (anyTls is { } probe)
                    result = probe(address.ToString(), port, settings.Password,
                        string.IsNullOrEmpty(settings.Sni) ? settings.Server : settings.Sni,
                        settings.EchConfig ?? "", target.IdnHost, target.PathAndQuery, headers,
                        (uint)remaining, signal.SafeWaitHandle.DangerousGetHandle(), out status, out _);
                else
                    result = mieru!(address.ToString(), port, settings.Username!, settings.Password,
                        target.IdnHost, target.PathAndQuery, headers, (uint)remaining,
                        signal.SafeWaitHandle.DangerousGetHandle(), out status);
                if (result != 0) return status;
            }
            return 0;
        }, token).ConfigureAwait(false);
    }

    internal static void Shutdown()
    {
        foreach (var backend in new[] { AnyTls, Mieru })
        {
            if (!backend.IsValueCreated || backend.Value is not { } loaded) continue;
            loaded.shutdown();
            NativeLibrary.Free(loaded.module);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Initialize([MarshalAs(UnmanagedType.LPUTF8Str)] string ca);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Stop();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AnyTlsProbe(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string address, [MarshalAs(UnmanagedType.LPUTF8Str)] string port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password, [MarshalAs(UnmanagedType.LPUTF8Str)] string sni,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ech, [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string headers,
        uint remaining, nint stop, out int status, out int echStatus);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MieruProbe(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string address, [MarshalAs(UnmanagedType.LPUTF8Str)] string port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username, [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string host, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string headers, uint remaining, nint stop, out int status);
    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string path, nint file, uint flags);
}
