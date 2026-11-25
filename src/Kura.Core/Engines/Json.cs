using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Kura.Core.Engines;

/// <summary>Lenient readers for loosely typed APIs (Gelbooru returns numbers and booleans as strings, nulls as "").</summary>
internal static class Lenient
{
    public static string? Str(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static long Long(this JsonElement e, string name) =>
        long.TryParse(e.Str(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static int Int(this JsonElement e, string name) => (int)Math.Clamp(e.Long(name), int.MinValue, int.MaxValue);

    public static bool Bool(this JsonElement e, string name) => e.Str(name) is { } s && (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase));

    public static string? Html(string? s) => s is null ? null : WebUtility.HtmlDecode(s);

    /// <summary>Peeks the first non-whitespace byte without losing it, returning a stream that replays it.</summary>
    public static async Task<(byte First, Stream Stream)> PeekAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[256];
        var read = 0;
        while (true)
        {
            var n = await s.ReadAsync(buf.AsMemory(read, buf.Length - read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
            for (var i = 0; i < read; i++)
            {
                var b = buf[i];
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF) continue;
                return (b, new PrefixStream(buf.AsMemory(0, read), s));
            }
            if (read == buf.Length) read = 0; // all whitespace so far; keep scanning
        }
        return (0, new PrefixStream(buf.AsMemory(0, read), s));
    }

    private sealed class PrefixStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefix.Length > 0)
            {
                var n = Math.Min(buffer.Length, _prefix.Length);
                _prefix.Span[..n].CopyTo(buffer);
                _prefix = _prefix[n..];
                return n;
            }
            return inner.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_prefix.Length > 0) return Read(buffer.Span);
            return await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
