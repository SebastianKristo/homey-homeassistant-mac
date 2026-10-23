using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MacBridge;

sealed record HttpRequest(
    string Method,
    string Target, // as sent (path + query); this is what gets signed
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    string RemoteHost); // remote IP address, used for rate limiting

sealed class HttpResponse
{
    public int Status { get; init; }
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public Dictionary<string, string> Headers { get; } = new();

    public static HttpResponse Json(object value, int status = 200) =>
        new() { Status = status, Body = JsonSerializer.SerializeToUtf8Bytes(value) };

    public static HttpResponse Error(string message, int status) =>
        Json(new Dictionary<string, object> { ["error"] = message }, status);
}

/// <summary>
/// Minimal HTTP/1.1 server on a plain TCP socket (no HttpListener/URL ACLs, no admin rights).
///
/// Hardening, same as the Mac app: only accepts connections from the local network (private,
/// link-local and loopback addresses), limits concurrent connections, header/body size and how
/// long a client may take.
/// </summary>
sealed class HttpServer : IDisposable
{
    public const int MaxHeaderBytes = 8 * 1024;
    public const int MaxBodyBytes = 16 * 1024;
    public const int MaxConnections = 16;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly int port;
    private readonly Func<HttpRequest, HttpResponse> handler;
    private readonly CancellationTokenSource cts = new();
    private TcpListener? listener;
    private int activeConnections;

    public HttpServer(int port, Func<HttpRequest, HttpResponse> handler)
    {
        this.port = port;
        this.handler = handler;
    }

    /// <summary>Throws SocketException if the port cannot be opened.</summary>
    public void Start()
    {
        TcpListener l;
        if (Socket.OSSupportsIPv6)
        {
            l = new TcpListener(IPAddress.IPv6Any, port);
            l.Server.DualMode = true; // IPv4 and IPv6
        }
        else
        {
            l = new TcpListener(IPAddress.Any, port); // IPv6 turned off on this PC
        }
        l.Server.ExclusiveAddressUse = true;
        l.Start(64);
        listener = l;
        _ = AcceptLoop(l, cts.Token);
    }

    public void Dispose()
    {
        cts.Cancel();
        listener?.Stop();
        listener = null;
    }

    private async Task AcceptLoop(TcpListener l, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await l.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            var host = remote is null ? null : LocalHost(remote.Address);
            if (host is null)
            {
                Trace.WriteLine($"MacBridge: rejected non-local connection from {remote}");
                return;
            }
            if (Interlocked.Increment(ref activeConnections) > MaxConnections)
            {
                Interlocked.Decrement(ref activeConnections);
                return;
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                timeout.CancelAfter(RequestTimeout);
                var stream = client.GetStream();
                var buffer = new MemoryStream();
                var chunk = new byte[16384];
                while (true)
                {
                    var read = await stream.ReadAsync(chunk, timeout.Token);
                    if (read == 0) return;
                    buffer.Write(chunk, 0, read);
                    var result = Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), host);
                    if (result.Request is not null)
                    {
                        await Send(handler(result.Request), stream, timeout.Token);
                        return;
                    }
                    if (result.InvalidStatus != 0)
                    {
                        await Send(HttpResponse.Error("bad request", result.InvalidStatus), stream, timeout.Token);
                        return;
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // Client was too slow or went away.
            }
            finally
            {
                Interlocked.Decrement(ref activeConnections);
            }
        }
    }

    private readonly record struct ParseResult(HttpRequest? Request, int InvalidStatus);

    private static readonly ParseResult Incomplete = new(null, 0);

    /// <summary>Returns a request once headers and the full body have arrived.</summary>
    private static ParseResult Parse(ReadOnlySpan<byte> data, string host)
    {
        var headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0) return data.Length > MaxHeaderBytes ? new(null, 431) : Incomplete;
        if (headerEnd > MaxHeaderBytes) return new(null, 431);

        string head;
        try { head = StrictUtf8.GetString(data[..headerEnd]); }
        catch (DecoderFallbackException) { return new(null, 431); }

        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || (requestLine[0] != "GET" && requestLine[0] != "POST") || !requestLine[1].StartsWith('/'))
            return new(null, 400);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) return new(null, 400);
            var key = line[..colon].ToLowerInvariant();
            if (headers.ContainsKey(key)) return new(null, 400); // no duplicate headers
            headers[key] = line[(colon + 1)..].Trim(' ', '\t');
        }
        if (headers.ContainsKey("transfer-encoding")) return new(null, 400);

        var lengthText = headers.TryGetValue("content-length", out var value) ? value : "0";
        if (lengthText.Length == 0 || !lengthText.All(char.IsAsciiDigit)
            || !int.TryParse(lengthText, out var length) || length > MaxBodyBytes)
            return new(null, 413);

        var body = data[(headerEnd + 4)..];
        if (body.Length < length) return Incomplete;

        var target = requestLine[1];
        var path = target.Split('?')[0];
        return new(new HttpRequest(requestLine[0], target, path, headers, body[..length].ToArray(), host), 0);
    }

    private static async Task Send(HttpResponse response, NetworkStream stream, CancellationToken token)
    {
        var reason = response.Status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            413 => "Payload Too Large",
            429 => "Too Many Requests",
            431 => "Request Header Fields Too Large",
            _ => "Error",
        };
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {response.Status} {reason}\r\n");
        head.Append("Content-Type: application/json\r\n");
        head.Append($"Content-Length: {response.Body.Length}\r\n");
        head.Append("Cache-Control: no-store\r\n");
        foreach (var (key, val) in response.Headers.OrderBy(h => h.Key, StringComparer.Ordinal))
            head.Append($"{key}: {val}\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token);
        await stream.WriteAsync(response.Body, token);
        await stream.FlushAsync(token);
    }

    // Local network check

    /// <summary>The remote IP if it is on the local network, otherwise null.</summary>
    public static string? LocalHost(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsLocalIPv4(b) ? address.ToString() : null;
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || b.Length != 16) return null;
        var loopback = IPAddress.IPv6Loopback.Equals(address);
        var linkLocal = b[0] == 0xfe && (b[1] & 0xc0) == 0x80; // fe80::/10
        var uniqueLocal = (b[0] & 0xfe) == 0xfc; // fc00::/7
        return loopback || linkLocal || uniqueLocal ? address.ToString() : null;
    }

    private static bool IsLocalIPv4(byte[] b) =>
        b.Length == 4 && (
            b[0] == 10 // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168) // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254) // link-local
            || b[0] == 127); // loopback
}
