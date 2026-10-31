using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>Counters for one measured run against the range server.</summary>
internal sealed class ServerCounters
{
    public long Requests;
    public long RangeRequests;
    public long BodyBytes;

    internal void Reset()
    {
        Requests = 0;
        RangeRequests = 0;
        BodyBytes = 0;
    }
}

/// <summary>
/// A local HTTP/1.1 server for files under one directory, with single-range support, that counts requests and
/// body bytes written. Bytes are counted as written to the socket, so a client that closes early may have read
/// slightly less than counted (by about one socket buffer); the send buffer is kept small to limit that.
/// </summary>
internal sealed class RangeServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _root;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private ServerCounters _counters = new();

    public RangeServer(string root, int port = 0)
    {
        _root = Path.GetFullPath(root);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public string UrlFor(string relativePath) => $"http://127.0.0.1:{Port}/{Uri.EscapeDataString(relativePath).Replace("%2F", "/", StringComparison.Ordinal)}";

    /// <summary>Starts a fresh set of counters and returns them.</summary>
    public ServerCounters Begin()
    {
        _counters = new ServerCounters();
        return _counters;
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            client.SendBufferSize = 16 * 1024;
            client.NoDelay = true;
            try
            {
                using NetworkStream net = client.GetStream();
                using StreamReader reader = new(net, Encoding.ASCII, false, 4096, leaveOpen: true);
                while (true)
                {
                    string? requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (requestLine is null)
                    {
                        return;
                    }

                    string[] parts = requestLine.Split(' ');
                    string? range = null;
                    string? header;
                    bool close = false;
                    while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync().ConfigureAwait(false)))
                    {
                        if (header.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        {
                            range = header[6..].Trim();
                        }
                        else if (header.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) && header.Contains("close", StringComparison.OrdinalIgnoreCase))
                        {
                            close = true;
                        }
                    }

                    ServerCounters counters = _counters;
                    Interlocked.Increment(ref counters.Requests);
                    if (!await Respond(net, counters, parts[0], WebUtility.UrlDecode(parts[1].TrimStart('/')), range).ConfigureAwait(false) || close)
                    {
                        return;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task<bool> Respond(NetworkStream net, ServerCounters counters, string method, string relative, string? range)
    {
        string path = Path.GetFullPath(Path.Combine(_root, relative));
        if (!path.StartsWith(_root, StringComparison.Ordinal) || !File.Exists(path))
        {
            await WriteHeader(net, "404 Not Found", 0, null).ConfigureAwait(false);
            return true;
        }

        long length = new FileInfo(path).Length;
        long start = 0, end = length - 1;
        string status = "200 OK";
        string? contentRange = null;
        if (range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            // Single range only: "a-b", "a-" or "-n".
            string spec = range[6..].Split(',')[0];
            int dash = spec.IndexOf('-', StringComparison.Ordinal);
            string a = spec[..dash], b = spec[(dash + 1)..];
            if (a.Length == 0)
            {
                start = Math.Max(0, length - long.Parse(b, CultureInfo.InvariantCulture));
            }
            else
            {
                start = long.Parse(a, CultureInfo.InvariantCulture);
                end = b.Length == 0 ? length - 1 : Math.Min(length - 1, long.Parse(b, CultureInfo.InvariantCulture));
            }

            if (start >= length || start > end)
            {
                await WriteHeader(net, "416 Range Not Satisfiable", 0, $"bytes */{length}").ConfigureAwait(false);
                return true;
            }

            status = "206 Partial Content";
            contentRange = $"bytes {start}-{end}/{length}";
            Interlocked.Increment(ref counters.RangeRequests);
        }

        long count = end - start + 1;
        await WriteHeader(net, status, count, contentRange).ConfigureAwait(false);
        if (method == "HEAD")
        {
            return true;
        }

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        fs.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[16 * 1024];
        long left = count;
        while (left > 0)
        {
            int n = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left))).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            await net.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
            Interlocked.Add(ref counters.BodyBytes, n);
            left -= n;
        }

        return true;
    }

    private static async Task WriteHeader(NetworkStream net, string status, long length, string? contentRange)
    {
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status}\r\nAccept-Ranges: bytes\r\nContent-Type: application/octet-stream\r\nContent-Length: {length}\r\n");
        if (contentRange is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Content-Range: {contentRange}\r\n");
        }

        sb.Append("\r\n");
        await net.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}
