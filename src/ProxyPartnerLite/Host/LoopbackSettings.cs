using System.Net;
using System.Security.Cryptography;
using System.Text;
using ProxyPartnerLite.Discovery;

namespace ProxyPartnerLite.Host;

internal sealed class LoopbackSettings(IPEndPoint endpoint, string secret)
{
    internal IPEndPoint Endpoint { get; } = endpoint;
    internal string Secret { get; } = secret;

    internal static async Task<LoopbackSettings> ReadAsync(
        LoopbackFallbackAuthorization grant, CoreIdentity core, CancellationToken cancellation)
    {
        if (grant.CoreProcessId != core.Pid || grant.CoreStartedAtUtc != core.StartedAtUtc ||
            !Path.IsPathFullyQualified(grant.FilePath) || grant.ApprovedSha256.Length != 64)
            throw new HostFailure(HostStatus.FallbackConfigUnverified);

        string path = Path.GetFullPath(grant.FilePath);
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path[2] != '\\' || path.AsSpan(2).Contains(':'))
            throw new HostFailure(HostStatus.FallbackConfigUnverified);

        byte[] bytes = new byte[4097];
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (!WindowsPeer.OpenedFilePath(file.SafeFileHandle).Equals(@"\\?\" + path, StringComparison.OrdinalIgnoreCase))
                throw new HostFailure(HostStatus.FallbackConfigUnverified);
            int length = await file.ReadAtLeastAsync(bytes, bytes.Length, false, cancellation).ConfigureAwait(false);
            if (length == bytes.Length ||
                !Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, length))).Equals(
                    grant.ApprovedSha256, StringComparison.OrdinalIgnoreCase))
                throw new HostFailure(HostStatus.FallbackConfigUnverified);

            // Deliberately accept a controller-only YAML subset, not a general Verge profile.
            var (controller, token) = ReadScalars(new UTF8Encoding(false, true).GetString(bytes, 0, length));
            if (!controller.StartsWith("127.0.0.1:", StringComparison.Ordinal) ||
                !int.TryParse(controller.AsSpan(10), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int port) ||
                port is < 1 or > 65535 || token.Length is < 1 or > 512 ||
                token.Any(c => !char.IsAsciiLetterOrDigit(c) && !"-._~+/=".Contains(c)))
                throw new HostFailure(HostStatus.FallbackConfigUnverified);
            return new LoopbackSettings(new IPEndPoint(IPAddress.Loopback, port), token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new HostFailure(HostStatus.FallbackConfigUnverified);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static (string Controller, string Token) ReadScalars(string text)
    {
        const string endpointKey = "external-controller: ";
        const string secretKey = "secret: ";
        string? endpoint = null;
        string? secret = null;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (line.Trim().IsEmpty || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            if (endpoint is null && line.StartsWith(endpointKey, StringComparison.Ordinal))
                endpoint = line[endpointKey.Length..].ToString();
            else if (secret is null && line.StartsWith(secretKey, StringComparison.Ordinal))
                secret = line[secretKey.Length..].ToString();
            else
                throw new HostFailure(HostStatus.FallbackConfigUnverified);
        }
        if (endpoint is null || secret is null)
            throw new HostFailure(HostStatus.FallbackConfigUnverified);
        return (endpoint, secret);
    }
}
