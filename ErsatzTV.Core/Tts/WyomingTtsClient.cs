using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ErsatzTV.Core.Tts;

/// <summary>
///     Minimal Wyoming protocol client for TTS (e.g. wyoming-piper). Speaks just enough
///     of the protocol to synthesize: send a synthesize event, collect audio-chunk PCM
///     payloads until audio-stop, and wrap the result in a RIFF/WAV header.
///     Protocol: one JSON header line per event (optionally followed by data_length bytes
///     of JSON data for legacy peers, then payload_length bytes of binary payload).
/// </summary>
public static class WyomingTtsClient
{
    private const int MaxAudioBytes = 64 * 1024 * 1024; // 64 MB guard

    public static async Task<byte[]> Synthesize(
        string host,
        int port,
        string text,
        Option<string> voice,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        CancellationToken ct = timeoutCts.Token;

        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        NetworkStream stream = client.GetStream();

        // synthesize event with inline data (modern wyoming)
        var data = new Dictionary<string, object> { ["text"] = text };
        foreach (string voiceName in voice.Filter(v => !string.IsNullOrWhiteSpace(v)))
        {
            data["voice"] = new Dictionary<string, object> { ["name"] = voiceName };
        }

        var header = new Dictionary<string, object>
        {
            ["type"] = "synthesize",
            ["data"] = data
        };

        byte[] headerBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header) + "\n");
        await stream.WriteAsync(headerBytes, ct);
        await stream.FlushAsync(ct);

        int rate = 22050;
        int width = 2;
        int channels = 1;
        using var pcm = new MemoryStream();

        while (true)
        {
            (JsonDocument eventHeader, byte[] payload) = await ReadEvent(stream, ct);
            using JsonDocument doc = eventHeader;

            string type = doc.RootElement.GetProperty("type").GetString() ?? string.Empty;

            switch (type)
            {
                case "audio-start":
                case "audio-chunk":
                    if (doc.RootElement.TryGetProperty("data", out JsonElement audioData))
                    {
                        if (audioData.TryGetProperty("rate", out JsonElement r))
                        {
                            rate = r.GetInt32();
                        }

                        if (audioData.TryGetProperty("width", out JsonElement w))
                        {
                            width = w.GetInt32();
                        }

                        if (audioData.TryGetProperty("channels", out JsonElement ch))
                        {
                            channels = ch.GetInt32();
                        }
                    }

                    if (type == "audio-chunk" && payload.Length > 0)
                    {
                        if (pcm.Length + payload.Length > MaxAudioBytes)
                        {
                            throw new InvalidOperationException("Wyoming TTS response exceeded 64 MB");
                        }

                        pcm.Write(payload);
                    }

                    break;
                case "audio-stop":
                    return BuildWav(pcm.ToArray(), rate, width, channels);
                case "error":
                    string message = doc.RootElement.TryGetProperty("data", out JsonElement errorData) &&
                                     errorData.TryGetProperty("text", out JsonElement errorText)
                        ? errorText.GetString()
                        : "unknown error";

                    throw new InvalidOperationException($"Wyoming TTS error: {message}");
            }
        }
    }

    private static async Task<(JsonDocument Header, byte[] Payload)> ReadEvent(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        string line = await ReadLine(stream, cancellationToken);
        var header = JsonDocument.Parse(line);

        // legacy peers put data in a separate block after the header line
        if (header.RootElement.TryGetProperty("data_length", out JsonElement dataLength))
        {
            byte[] dataBytes = await ReadExactly(stream, dataLength.GetInt32(), cancellationToken);

            // merge legacy data block into a combined document
            var combined = new Dictionary<string, JsonElement>();
            foreach (JsonProperty property in header.RootElement.EnumerateObject())
            {
                combined[property.Name] = property.Value.Clone();
            }

            using var dataDoc = JsonDocument.Parse(dataBytes);
            combined["data"] = dataDoc.RootElement.Clone();

            JsonDocument merged = JsonDocument.Parse(JsonSerializer.Serialize(combined));
            header.Dispose();
            header = merged;
        }

        byte[] payload = [];
        if (header.RootElement.TryGetProperty("payload_length", out JsonElement payloadLength) &&
            payloadLength.ValueKind == JsonValueKind.Number)
        {
            payload = await ReadExactly(stream, payloadLength.GetInt32(), cancellationToken);
        }

        return (header, payload);
    }

    private static async Task<string> ReadLine(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];

        while (true)
        {
            int read = await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Wyoming connection closed unexpectedly");
            }

            if (single[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (buffer.Length > 1024 * 1024)
            {
                throw new InvalidOperationException("Wyoming event header exceeded 1 MB");
            }

            buffer.WriteByte(single[0]);
        }
    }

    private static async Task<byte[]> ReadExactly(
        NetworkStream stream,
        int count,
        CancellationToken cancellationToken)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(result.AsMemory(offset, count - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Wyoming connection closed unexpectedly");
            }

            offset += read;
        }

        return result;
    }

    private static byte[] BuildWav(byte[] pcm, int rate, int width, int channels)
    {
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1); // pcm
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), rate * channels * width);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), (short)(channels * width));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), (short)(width * 8));
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), pcm.Length);

        var wav = new byte[44 + pcm.Length];
        header.CopyTo(wav, 0);
        pcm.CopyTo(wav, 44);
        return wav;
    }
}
