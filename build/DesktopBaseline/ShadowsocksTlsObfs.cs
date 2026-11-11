using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using QuickProxyNet;

// Client-side wire format adapted from ginuerzh/gost v2.11.5, obfs.go (MIT).
// https://github.com/ginuerzh/gost/blob/v2.11.5/obfs.go
// Scope: clientHandshake, client-side Parse and Write. See Gost.LICENSE.txt.
internal sealed class ShadowsocksTlsObfs(Stream transport, string obfsHost) : Stream
{
    private const int MaxPayload = 16384;
    private readonly byte[] readHeader = new byte[5];
    private int readRemaining;
    private int serverRecord;
    private bool wroteHello;
    private bool disposed;

    internal static async ValueTask<Stream> ConnectAsync(ShadowsocksClient client, string obfsHost,
        EndPoint destination, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(client.Options.Host, client.Options.Port, token).ConfigureAwait(false);
            var obfs = new ShadowsocksTlsObfs(new NetworkStream(socket, ownsSocket: true), obfsHost);
            return await client.ConnectAsync(obfs, destination, token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty) return 0;
        while (readRemaining == 0)
        {
            if (await transport.ReadAsync(readHeader.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
                return 0;
            await transport.ReadExactlyAsync(readHeader.AsMemory(1), cancellationToken).ConfigureAwait(false);

            byte expectedType = serverRecord switch { 0 or 2 => 0x16, 1 => 0x14, _ => 0x17 };
            if (readHeader[0] != expectedType || readHeader[1] != 3 ||
                (serverRecord == 0 ? readHeader[2] is not (1 or 3) : readHeader[2] != 3))
                throw new IOException("Invalid TLS obfuscation record.");

            // The first two records have fixed body sizes in simple-obfs.
            if (serverRecord < 2)
            {
                var preamble = new byte[serverRecord == 0 ? 91 : 1];
                await transport.ReadExactlyAsync(preamble, cancellationToken).ConfigureAwait(false);
                serverRecord++;
                continue;
            }

            readRemaining = BinaryPrimitives.ReadUInt16BigEndian(readHeader.AsSpan(3));
            if (readRemaining > MaxPayload)
                throw new IOException("Invalid TLS obfuscation record length.");
            serverRecord = 3;
        }

        int count = await transport.ReadAsync(buffer[..Math.Min(buffer.Length, readRemaining)], cancellationToken)
            .ConfigureAwait(false);
        if (count == 0) throw new EndOfStreamException();
        readRemaining -= count;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        while (!buffer.IsEmpty)
        {
            int count = Math.Min(MaxPayload, buffer.Length);
            if (!wroteHello)
            {
                await transport.WriteAsync(CreateClientHello(buffer.Span[..count], obfsHost), cancellationToken)
                    .ConfigureAwait(false);
                wroteHello = true;
            }
            else
            {
                byte[] record = ArrayPool<byte>.Shared.Rent(count + 5);
                try
                {
                    record[0] = 0x17;
                    record[1] = 3;
                    record[2] = 3;
                    BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3), (ushort)count);
                    buffer[..count].CopyTo(record.AsMemory(5));
                    await transport.WriteAsync(record.AsMemory(0, count + 5), cancellationToken).ConfigureAwait(false);
                }
                finally { ArrayPool<byte>.Shared.Return(record); }
            }
            buffer = buffer[count..];
        }
    }

    private static byte[] CreateClientHello(ReadOnlySpan<byte> payload, string host)
    {
        int hostLength = Encoding.UTF8.GetByteCount(host);
        if (hostLength is 0 or > 253) throw new ArgumentException("Invalid TLS obfuscation host.", nameof(host));
        int variableLength = payload.Length + hostLength;
        byte[] record = new byte[217 + variableLength];

        record[0] = 0x16;
        record[1] = 3;
        record[2] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3), (ushort)(212 + variableLength));
        record[5] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(7), (ushort)(208 + variableLength));
        record[9] = 3;
        record[10] = 3;
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(11), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        RandomNumberGenerator.Fill(record.AsSpan(15, 28));
        record[43] = 32;
        RandomNumberGenerator.Fill(record.AsSpan(44, 32));

        // Cipher suite vector and the single null compression method.
        ReadOnlySpan<byte> clientOptions =
        [
            0x00, 0x38,
            0xc0, 0x2c, 0xc0, 0x30, 0x00, 0x9f, 0xcc, 0xa9, 0xcc, 0xa8, 0xcc, 0xaa, 0xc0, 0x2b, 0xc0, 0x2f,
            0x00, 0x9e, 0xc0, 0x24, 0xc0, 0x28, 0x00, 0x6b, 0xc0, 0x23, 0xc0, 0x27, 0x00, 0x67, 0xc0, 0x0a,
            0xc0, 0x14, 0x00, 0x39, 0xc0, 0x09, 0xc0, 0x13, 0x00, 0x33, 0x00, 0x9d, 0x00, 0x9c, 0x00, 0x3d,
            0x00, 0x3c, 0x00, 0x35, 0x00, 0x2f, 0x00, 0xff,
            0x01, 0x00
        ];
        clientOptions.CopyTo(record.AsSpan(76));
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(136), (ushort)(79 + variableLength));

        // The initial Shadowsocks ciphertext occupies the session ticket extension.
        record[139] = 0x23;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(140), (ushort)payload.Length);
        payload.CopyTo(record.AsSpan(142));
        int sniOffset = 142 + payload.Length;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(sniOffset + 2), (ushort)(hostLength + 5));
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(sniOffset + 4), (ushort)(hostLength + 3));
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(sniOffset + 7), (ushort)hostLength);
        Encoding.UTF8.GetBytes(host, record.AsSpan(sniOffset + 9, hostLength));

        // EC point formats, supported groups, signature algorithms and empty extensions.
        ReadOnlySpan<byte> extensions =
        [
            0x00, 0x0b, 0x00, 0x04, 0x03, 0x01, 0x00, 0x02,
            0x00, 0x0a, 0x00, 0x0a, 0x00, 0x08, 0x00, 0x1d, 0x00, 0x17, 0x00, 0x19, 0x00, 0x18,
            0x00, 0x0d, 0x00, 0x20, 0x00, 0x1e, 0x06, 0x01, 0x06, 0x02, 0x06, 0x03, 0x05, 0x01, 0x05, 0x02,
            0x05, 0x03, 0x04, 0x01, 0x04, 0x02, 0x04, 0x03, 0x03, 0x01, 0x03, 0x02, 0x03, 0x03, 0x02, 0x01,
            0x02, 0x02, 0x02, 0x03,
            0x00, 0x16, 0x00, 0x00, 0x00, 0x17, 0x00, 0x00
        ];
        extensions.CopyTo(record.AsSpan(151 + variableLength));
        return record;
    }

    public override bool CanRead => !disposed && transport.CanRead;
    public override bool CanWrite => !disposed && transport.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => transport.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => transport.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            transport.Dispose();
        }
        base.Dispose(disposing);
    }
}
