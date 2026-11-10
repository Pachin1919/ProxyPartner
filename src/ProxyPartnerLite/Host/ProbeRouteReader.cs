using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace ProxyPartnerLite.Host;

internal sealed record MatchedProbeRoute(string? Node, bool Direct);

internal static class ProbeRouteReader
{
    internal static async Task<MatchedProbeRoute?> MatchAsync(Stream source, string host, int port, int sourcePort,
        HashSet<string> nodes, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            int buffered = 0;
            bool pendingArray = false, inArray = false;
            var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 32 });
            while (true)
            {
                if (buffered == buffer.Length)
                    throw new HostFailure(HostStatus.InvalidResponse);
                int count = await source.ReadAsync(buffer.AsMemory(buffered), token).ConfigureAwait(false);
                buffered += count;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), count == 0, state);
                while (true)
                {
                    var before = reader;
                    if (!reader.Read()) break;
                    if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName)
                        pendingArray = reader.ValueTextEquals("connections"u8);
                    else if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.StartArray)
                        inArray = pendingArray;
                    else if (inArray && reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.EndArray)
                        return null;
                    else if (inArray && reader.CurrentDepth == 2 && reader.TokenType == JsonTokenType.StartObject)
                    {
                        if (!JsonDocument.TryParseValue(ref reader, out var entry))
                        {
                            reader = before;
                            break;
                        }
                        using (entry)
                        {
                            var connection = entry!.RootElement;
                            if (!connection.TryGetProperty("metadata", out var metadata) ||
                                metadata.ValueKind != JsonValueKind.Object ||
                                !Is(metadata, "network", "tcp") || !Is(metadata, "sourceIP", "127.0.0.1") ||
                                !Is(metadata, "sourcePort", sourcePort.ToString(CultureInfo.InvariantCulture)) ||
                                !Is(metadata, "host", host) ||
                                !Is(metadata, "destinationPort", port.ToString(CultureInfo.InvariantCulture)))
                                continue;
                            if (!connection.TryGetProperty("chains", out var chains) || chains.ValueKind != JsonValueKind.Array)
                                return null;
                            string? leaf = null;
                            bool direct = false;
                            foreach (var link in chains.EnumerateArray())
                            {
                                if (link.ValueKind != JsonValueKind.String) return null;
                                string name = link.GetString()!;
                                if (name == "DIRECT") direct = true;
                                if (!nodes.Contains(name)) continue;
                                if (leaf is not null && leaf != name) return null;
                                leaf = name;
                            }
                            return leaf is not null ? new(leaf, false) : direct ? new(null, true) : null;
                        }
                    }
                }
                if (count == 0)
                    return null;
                state = reader.CurrentState;
                int consumed = (int)reader.BytesConsumed;
                buffered -= consumed;
                buffer.AsSpan(consumed, buffered).CopyTo(buffer);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static bool Is(JsonElement value, string property, string expected) =>
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String &&
        field.ValueEquals(expected);
}
