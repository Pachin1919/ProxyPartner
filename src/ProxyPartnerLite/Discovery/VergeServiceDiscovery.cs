using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ProxyPartnerLite.Host;

namespace ProxyPartnerLite.Discovery;

internal static partial class WindowsPeer
{
    private static unsafe CoreIdentity DiscoverServiceCore(CoreIdentity ui)
    {
        using var manager = OpenSCManager(0, 0, 1); // SC_MANAGER_CONNECT
        if (manager.IsInvalid)
            throw new HostFailure(HostStatus.AccessDenied);
        using var service = OpenService(manager, "clash_verge_service", 5); // QUERY_CONFIG | QUERY_STATUS
        if (service.IsInvalid)
            throw new HostFailure(Marshal.GetLastPInvokeError() == 5 ? HostStatus.AccessDenied : HostStatus.HostUnavailable);

        var status = new ServiceProcessStatus();
        if (QueryServiceStatusEx(service, 0, &status, (uint)sizeof(ServiceProcessStatus), out _) == 0)
            throw new HostFailure(HostStatus.AccessDenied);
        if (status.State != 4 || status.Pid <= 0) // SERVICE_RUNNING
            throw new HostFailure(HostStatus.HostUnavailable);
        if (status.Type != 0x10) // SERVICE_WIN32_OWN_PROCESS
            throw new HostFailure(HostStatus.IdentityRejected);

        const int MaxConfigBytes = 8192;
        byte* buffer = stackalloc byte[MaxConfigBytes];
        if (QueryServiceConfig(service, (ServiceConfig*)buffer, MaxConfigBytes, out _) == 0)
            throw new HostFailure(HostStatus.AccessDenied);
        var config = (ServiceConfig*)buffer;
        string expectedImage = Path.Combine(Path.GetDirectoryName(ui.Image)!, "resources", "clash-verge-service.exe");
        if (!string.Equals(Marshal.PtrToStringUni(config->Image)?.Trim('"'), expectedImage, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Marshal.PtrToStringUni(config->Account), "LocalSystem", StringComparison.OrdinalIgnoreCase))
            throw new HostFailure(HostStatus.IdentityRejected);

        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
            throw new HostFailure(HostStatus.AccessDenied);
        var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
        bool serviceFound = false;
        int child = 0;
        for (int found = Process32First(snapshot, ref entry); found != 0; found = Process32Next(snapshot, ref entry))
        {
            if (entry.Pid != status.Pid && entry.ParentPid != status.Pid)
                continue;
            var image = new ReadOnlySpan<char>(entry.Image, 260);
            image = image[..image.IndexOf('\0')];
            if (entry.Pid == status.Pid && image.Equals("clash-verge-service.exe", StringComparison.OrdinalIgnoreCase))
                serviceFound = InServiceSession(entry.Pid);
            else if (entry.ParentPid == status.Pid && image.Equals("verge-mihomo.exe", StringComparison.OrdinalIgnoreCase) &&
                InServiceSession(entry.Pid))
            {
                if (child != 0)
                    throw new HostFailure(HostStatus.Ambiguous);
                child = entry.Pid;
            }
        }
        if (!serviceFound || child == 0)
            throw new HostFailure(HostStatus.HostUnavailable);

        // A normal desktop user cannot inspect the LocalSystem child's image or token.
        // Bind pipe requests to the registered service, its Session 0 child and the pipe server PID.
        return new CoreIdentity(child, null, null, null, 0, new VergeServicePeer(ui, status.Pid));
    }

    private static bool InServiceSession(int pid)
    {
        // Process.SessionId uses process-table metadata, available without a handle to a LocalSystem process.
        using var process = Process.GetProcessById(pid);
        return process.SessionId == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceProcessStatus
    {
        internal uint Type, State, Controls, ExitCode, ServiceExitCode, CheckPoint, WaitHint;
        internal int Pid;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        internal uint Type, Start, ErrorControl;
        internal nint Image, Group;
        internal uint Tag;
        internal nint Dependencies, Account, DisplayName;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle) != 0;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true)]
    private static partial ServiceHandle OpenSCManager(nint machine, nint database, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static unsafe partial int QueryServiceStatusEx(ServiceHandle service, int level, ServiceProcessStatus* status,
        uint size, out uint needed);
    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    private static unsafe partial int QueryServiceConfig(ServiceHandle service, ServiceConfig* config, uint size, out uint needed);
    [LibraryImport("advapi32.dll")]
    private static partial int CloseServiceHandle(nint service);
}
