using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ProxyPartnerLite.Host;

namespace ProxyPartnerLite.Discovery;

internal sealed record CoreIdentity(int Pid, DateTimeOffset? StartedAtUtc, string? Image, string? Owner, int Session,
    VergeServicePeer? Service = null);
internal sealed record VergeServicePeer(CoreIdentity Ui, int Pid);

internal static partial class WindowsPeer
{
    internal static CoreIdentity Discover()
    {
        using var user = WindowsIdentity.GetCurrent();
        using var self = Process.GetCurrentProcess();
        string owner = user.User?.Value ?? throw new HostFailure(HostStatus.AccessDenied);
        var ui = CurrentImages("clash-verge", self.SessionId, owner);
        var cores = CurrentImages("verge-mihomo", self.SessionId, owner);
        if (ui.Count == 0)
            throw new HostFailure(HostStatus.HostUnavailable);
        if (ui.Count != 1 || cores.Count > 1)
            throw new HostFailure(HostStatus.Ambiguous);
        if (cores.Count == 0)
            return DiscoverServiceCore(ui[0]);
        if (!Path.GetDirectoryName(ui[0].Image)!.Equals(Path.GetDirectoryName(cores[0].Image),
            StringComparison.OrdinalIgnoreCase) || cores[0].StartedAtUtc < ui[0].StartedAtUtc ||
            ParentOf(cores[0].Pid) != ui[0].Pid)
            throw new HostFailure(HostStatus.IdentityRejected);
        return cores[0];
    }

    private static List<CoreIdentity> CurrentImages(string name, int session, string owner)
    {
        var selected = new List<CoreIdentity>();
        foreach (Process process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (process.SessionId != session)
                    continue;
                var image = Inspect(process.Id);
                if (image.Owner != owner)
                    continue;
                if (!string.Equals(Path.GetFileName(image.Image), name + ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new HostFailure(HostStatus.IdentityRejected);
                selected.Add(image);
            }
        }
        return selected;
    }

    internal static unsafe CoreIdentity Inspect(int pid)
    {
        using var process = OpenProcess(0x1000, 0, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid)
            throw new HostFailure(Marshal.GetLastPInvokeError() == 5 ? HostStatus.AccessDenied : HostStatus.HostUnavailable);
        Span<char> path = stackalloc char[4096];
        uint length = (uint)path.Length;
        fixed (char* buffer = path)
        {
            if (QueryFullProcessImageName(process, 0, buffer, ref length) == 0 ||
                GetProcessTimes(process, out long created, out _, out _, out _) == 0 ||
                ProcessIdToSessionId(pid, out int session) == 0 ||
                OpenProcessToken(process, 8, out var token) == 0)
                throw new HostFailure(HostStatus.AccessDenied);
            using (token)
            using (var user = new WindowsIdentity(token.DangerousGetHandle()))
                return new CoreIdentity(pid, DateTimeOffset.FromFileTime(created).ToUniversalTime(),
                    Path.GetFullPath(new string(path[..(int)length])),
                    user.User?.Value ?? throw new HostFailure(HostStatus.AccessDenied), session);
        }
    }

    internal static void Verify(CoreIdentity expected, int actualPid)
    {
        if (actualPid != expected.Pid || Inspect(actualPid) != expected)
            throw new HostFailure(HostStatus.IdentityRejected);
    }

    private static unsafe int ParentOf(int pid)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid)
            throw new HostFailure(HostStatus.AccessDenied);
        var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
        for (int found = Process32First(snapshot, ref entry); found != 0; found = Process32Next(snapshot, ref entry))
        {
            if (entry.Pid == pid)
                return entry.ParentPid;
        }
        throw new HostFailure(HostStatus.IdentityRejected);
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry
    {
        internal uint Size;
        internal uint Usage;
        internal int Pid;
        internal nuint Heap;
        internal uint Module;
        internal uint Threads;
        internal int ParentPid;
        internal int Priority;
        internal uint Flags;
        internal fixed char Image[260];
    }

    internal static void VerifyPipe(SafePipeHandle pipe, CoreIdentity expected)
    {
        if (GetNamedPipeServerProcessId(pipe, out int pid) == 0)
            throw new HostFailure(HostStatus.IdentityRejected);
        if (expected.Service is not { } service)
        {
            Verify(expected, pid);
            return;
        }
        if (pid != expected.Pid || Inspect(service.Ui.Pid) != service.Ui || DiscoverServiceCore(service.Ui) != expected)
            throw new HostFailure(HostStatus.IdentityRejected);
    }

    internal static unsafe string OpenedFilePath(SafeFileHandle file)
    {
        Span<char> path = stackalloc char[4096];
        fixed (char* buffer = path)
        {
            uint length = GetFinalPathNameByHandle(file, buffer, (uint)path.Length, 0);
            if (length == 0 || length >= path.Length)
                throw new HostFailure(HostStatus.FallbackConfigUnverified);
            return new string(path[..(int)length]);
        }
    }

    internal static void VerifyTcp(CoreIdentity expected, IPEndPoint server, IPEndPoint? client)
    {
        Verify(expected, expected.Pid);
        // OWNER_PID_LISTENER before dialing, OWNER_PID_CONNECTIONS for the exact connected tuple.
        int table = client is null ? 3 : 4;
        uint size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, 0, 2, table, 0) != 122 ||
            size is < 4 or > 4 * 1024 * 1024)
            throw new HostFailure(HostStatus.IdentityRejected);
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, 0, 2, table, 0) != 0)
                throw new HostFailure(HostStatus.IdentityRejected);
            int count = Marshal.ReadInt32(buffer);
            if (count < 0 || count > (size - 4) / 24)
                throw new HostFailure(HostStatus.IdentityRejected);
            int serverAddress = BitConverter.ToInt32(server.Address.GetAddressBytes());
            int clientAddress = client is null ? 0 : BitConverter.ToInt32(client.Address.GetAddressBytes());
            int matches = 0;
            for (int i = 0; i < count; i++)
            {
                IntPtr row = buffer + 4 + i * 24;
                if (Marshal.ReadInt32(row, 4) != serverAddress ||
                    BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt32(row, 8)) != server.Port)
                    continue;
                if (client is not null && (Marshal.ReadInt32(row) != 5 ||
                    Marshal.ReadInt32(row, 12) != clientAddress ||
                    BinaryPrimitives.ReverseEndianness((ushort)Marshal.ReadInt32(row, 16)) != client.Port))
                    continue;
                if (Marshal.ReadInt32(row, 20) != expected.Pid)
                    throw new HostFailure(HostStatus.IdentityRejected);
                matches++;
            }
            if (matches != 1)
                throw new HostFailure(HostStatus.IdentityRejected);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        Verify(expected, expected.Pid);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, int inherit, int pid);
    [LibraryImport("kernel32.dll")]
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, int pid);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW")]
    private static partial int Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW")]
    private static partial int Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    private static unsafe partial int QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* path, ref uint length);
    [LibraryImport("kernel32.dll")]
    private static partial int GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [LibraryImport("kernel32.dll")]
    private static partial int ProcessIdToSessionId(int pid, out int session);
    [LibraryImport("advapi32.dll")]
    private static partial int OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [LibraryImport("kernel32.dll")]
    private static partial int GetNamedPipeServerProcessId(SafePipeHandle pipe, out int pid);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW")]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);
    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref uint size, int sort, uint family, int type, uint reserved);
}
