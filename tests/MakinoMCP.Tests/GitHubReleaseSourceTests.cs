using System.Diagnostics;
using System.Net;
using System.Net.Http;
using MakinoMCP.Launcher;
using Xunit;

namespace MakinoMCP.Tests;

public class GitHubReleaseSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mmcp-gh-" + Guid.NewGuid().ToString("N"));

    public GitHubReleaseSourceTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<Uri> Seen = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    // A body that sends nothing until it is disposed: a proxy that stalls.
    private sealed class StalledStream : Stream
    {
        private readonly ManualResetEventSlim _closed = new();
        public override int Read(byte[] buffer, int offset, int count) { _closed.Wait(TimeSpan.FromSeconds(30)); return 0; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return 0;
        }
        protected override void Dispose(bool disposing) { _closed.Set(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private static GitHubReleaseSource Source(Handler h, int timeoutMs = 500) =>
        new(h, TimeSpan.FromMilliseconds(timeoutMs), TimeSpan.FromMilliseconds(timeoutMs));

    [Fact]
    public void Stalled_download_body_times_out()
    {
        using var src = Source(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }));
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => src.Download("https://github.com/o/r/releases/download/v1.0.0/x.zip", Path.Combine(_dir, "x.zip"), 1000));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Stalled_api_body_times_out()
    {
        using var src = Source(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }));
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => src.LatestJson("o/r"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Redirect_to_http_is_refused()
    {
        var h = new Handler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("http://objects.example.com/x.zip");
            return r;
        });
        using var src = Source(h, 5000);
        Assert.Throws<InvalidDataException>(() => src.Download("https://github.com/o/r/releases/download/v1.0.0/x.zip", Path.Combine(_dir, "x.zip"), 1000));
        Assert.Single(h.Seen);
    }

    [Fact]
    public void Https_redirect_is_followed()
    {
        var h = new Handler(req => req.RequestUri!.Host == "github.com"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://objects.example.com/x.zip") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var src = Source(h, 5000);
        var path = Path.Combine(_dir, "x.zip");
        src.Download("https://github.com/o/r/releases/download/v1.0.0/x.zip", path, 1000);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Oversized_download_is_refused()
    {
        using var src = Source(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5000]) }), 5000);
        Assert.Throws<InvalidDataException>(() => src.Download("https://github.com/o/r/releases/download/v1.0.0/x.zip", Path.Combine(_dir, "x.zip"), 1000));
        Assert.Throws<InvalidDataException>(() => src.DownloadText("https://github.com/o/r/releases/download/v1.0.0/x.sha256", 100));
    }

    [Fact]
    public void Error_status_becomes_an_exception()
    {
        using var src = Source(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "rate limit exceeded" }), 5000);
        var ex = Assert.Throws<HttpRequestException>(() => src.LatestJson("o/r"));
        Assert.Contains("403", ex.Message);
    }
}
