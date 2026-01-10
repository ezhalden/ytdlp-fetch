using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace YtDlpFetch.Tests;

/// <summary>Serves a fake yt-dlp release: one asset plus a matching (or deliberately broken) SHA2-512SUMS.</summary>
internal sealed class FakeRelease : HttpMessageHandler
{
    public string Body { get; set; } = "#!/bin/sh\necho fake yt-dlp\n";
    public string Asset { get; set; } = YtDlpAsset.ForCurrentPlatform();
    public Dictionary<string, string>? ZipEntries { get; set; }

    public bool Offline { get; set; }
    public bool DropMidDownload { get; set; }
    public bool WrongHash { get; set; }
    public bool NoSums { get; set; }
    public bool HangOnConnect { get; set; }
    public bool HangMidBody { get; set; }
    public bool HangOnSums { get; set; }

    public List<string> Requests { get; } = [];

    public HttpClient Client() => new(this, disposeHandler: false);

    private byte[] Bytes => ZipEntries is null ? Encoding.UTF8.GetBytes(Body) : Zip(ZipEntries);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);

        if (Offline) throw new HttpRequestException("network is down");

        var isSums = url.EndsWith("SHA2-512SUMS", StringComparison.Ordinal);

        if (HangOnConnect || (HangOnSums && isSums))
            await Task.Delay(Timeout.Infinite, ct);

        if (isSums)
        {
            if (NoSums) return new HttpResponseMessage(HttpStatusCode.NotFound);

            var hash = WrongHash ? new string('0', 128) : Convert.ToHexStringLower(SHA512.HashData(Bytes));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {Asset}\n") };
        }

        HttpContent content =
            HangMidBody ? new StreamContent(new ScriptedStream(hangAfterFirstRead: true))
            : DropMidDownload ? new StreamContent(new ScriptedStream(hangAfterFirstRead: false))
            : new ByteArrayContent(Bytes);

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] Zip(Dictionary<string, string> entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(body);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Sends a few bytes, then either hangs until cancelled or drops the connection.</summary>
    private sealed class ScriptedStream(bool hangAfterFirstRead) : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_served)
            {
                _served = true;
                buffer.Span[..4].Fill((byte)'x');
                return 4;
            }

            if (!hangAfterFirstRead) throw new IOException("connection reset");

            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
