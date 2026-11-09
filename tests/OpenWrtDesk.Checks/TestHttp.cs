using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;

// User-mode HTTP fixture: no Windows HTTP.sys registration or administrator rights.
sealed class TestHttpListener
{
    private TcpListener? socket;
    public List<string> Prefixes { get; } = [];
    public void Start() { socket = new TcpListener(IPAddress.Loopback, new Uri(Prefixes.Single()).Port); socket.Start(); }
    public void Stop() => socket?.Stop();
    public void Close() => Stop();
    public async Task<TestHttpContext> GetContextAsync()
    {
        var client = await socket!.AcceptTcpClientAsync(); var stream = client.GetStream();
        var bytes = new List<byte>(); var one = new byte[1];
        while (bytes.Count < 65536)
        {
            if (await stream.ReadAsync(one) == 0) throw new IOException("Client disconnected");
            bytes.Add(one[0]); if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n"); var first = lines[0].Split(' ');
        var headers = new NameValueCollection(); foreach (var line in lines.Skip(1)) { var pos = line.IndexOf(':'); if (pos > 0) headers[line[..pos]] = line[(pos + 1)..].Trim(); }
        var length = int.TryParse(headers["Content-Length"], out var n) ? n : 0; var body = new byte[length];
        if (length > 0) await stream.ReadExactlyAsync(body);
        return new(new TestHttpRequest(new Uri(Prefixes.Single().TrimEnd('/') + first[1]), first[0], headers, new MemoryStream(body)), new TestHttpResponse(client));
    }
}
sealed record TestHttpContext(TestHttpRequest Request, TestHttpResponse Response);
sealed record TestHttpRequest(Uri Url, string HttpMethod, NameValueCollection Headers, Stream InputStream);
sealed class TestHttpResponse
{
    private readonly TcpClient client; private bool headersSent;
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "application/json";
    public long ContentLength64 { get; set; } = -1;
    public bool SendChunked { get; set; }
    public NameValueCollection Headers { get; } = new();
    public Stream OutputStream { get; }
    public TestHttpResponse(TcpClient socket) { client = socket; OutputStream = new Output(this); }
    private async Task SendHeaders()
    {
        if (headersSent) return; headersSent = true;
        var header = $"HTTP/1.1 {StatusCode} OK\r\nConnection: close\r\nContent-Type: {ContentType}\r\n";
        if (ContentLength64 >= 0) header += $"Content-Length: {ContentLength64}\r\n";
        foreach (var key in Headers.AllKeys) header += key + ": " + Headers[key] + "\r\n";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(header + "\r\n"));
    }
    public void Close() { try { client.Dispose(); } catch { } }
    private sealed class Output(TestHttpResponse response) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => response.client.GetStream().Flush();
        public override Task FlushAsync(CancellationToken ct) => response.client.GetStream().FlushAsync(ct);
        public override void Write(byte[] buffer, int offset, int count) { response.SendHeaders().GetAwaiter().GetResult(); response.client.GetStream().Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { await response.SendHeaders(); await response.client.GetStream().WriteAsync(buffer, ct); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
