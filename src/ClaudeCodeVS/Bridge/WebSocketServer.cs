using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Bridge
{
    /// <summary>
    /// Minimal loopback-only WebSocket (RFC 6455) text server. Rejects any connection that lacks the lock-file
    /// auth token or that carries an Origin header (i.e. comes from a browser page).
    /// </summary>
    internal sealed class WebSocketServer : IDisposable
    {
        private const string AuthHeader = "x-claude-code-ide-authorization";
        private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxMessageBytes = 32 * 1024 * 1024;

        private readonly TcpListener _listener;
        private readonly string _authToken;
        private readonly Func<string, Task<string>> _onMessage;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<WsConnection, byte> _connections = new ConcurrentDictionary<WsConnection, byte>();

        public int Port { get; }

        /// <summary>Raised for per-connection failures (never contains the auth token).</summary>
        public event Action<Exception> Error;

        public WebSocketServer(string authToken, Func<string, Task<string>> onMessage)
        {
            _authToken = authToken;
            _onMessage = onMessage;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Start() => _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

        public async Task BroadcastAsync(string message)
        {
            foreach (var c in _connections.Keys)
            {
                try { await c.SendTextAsync(message, _cts.Token).ConfigureAwait(false); }
                catch (Exception) { /* connection loop cleans up */ }
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    var headers = await ReadHeadersAsync(stream, ct).ConfigureAwait(false);
                    if (headers == null) return;

                    if (!headers.TryGetValue("upgrade", out var up) || !up.Equals("websocket", StringComparison.OrdinalIgnoreCase)
                        || !headers.TryGetValue("sec-websocket-key", out var key))
                    { await RejectAsync(stream, "400 Bad Request").ConfigureAwait(false); return; }
                    if (headers.ContainsKey("origin"))
                    { await RejectAsync(stream, "403 Forbidden").ConfigureAwait(false); return; }
                    if (!headers.TryGetValue(AuthHeader, out var token) || !FixedTimeEquals(token, _authToken))
                    { await RejectAsync(stream, "401 Unauthorized").ConfigureAwait(false); return; }

                    string subProtocol = null;
                    if (headers.TryGetValue("sec-websocket-protocol", out var protos))
                        subProtocol = protos.Split(',')[0].Trim();

                    string accept;
                    using (var sha1 = SHA1.Create())
                        accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WsGuid)));
                    var resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "
                               + accept + "\r\n" + (subProtocol != null ? "Sec-WebSocket-Protocol: " + subProtocol + "\r\n" : "") + "\r\n";
                    var bytes = Encoding.ASCII.GetBytes(resp);
                    await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);

                    var conn = new WsConnection(stream);
                    _connections[conn] = 0;
                    try { await ReceiveLoopAsync(conn, ct).ConfigureAwait(false); }
                    finally { _connections.TryRemove(conn, out _); }
                }
                catch (Exception ex) { Error?.Invoke(ex); /* drop the connection */ }
            }
        }

        private async Task ReceiveLoopAsync(WsConnection conn, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var text = await conn.ReceiveTextAsync(ct).ConfigureAwait(false);
                if (text == null) return; // closed
                // Dispatch without blocking the receive loop: openDiff can block for a long time.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var reply = await _onMessage(text).ConfigureAwait(false);
                        if (reply != null) await conn.SendTextAsync(reply, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) { Error?.Invoke(ex); }
                });
            }
        }

        private static async Task<Dictionary<string, string>> ReadHeadersAsync(Stream s, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (sb.Length < 16 * 1024)
            {
                if (await s.ReadAsync(one, 0, 1, ct).ConfigureAwait(false) == 0) return null;
                sb.Append((char)one[0]);
                if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb[sb.Length - 2] == '\r'
                    && sb[sb.Length - 3] == '\n' && sb[sb.Length - 4] == '\r')
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var lines = sb.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var idx = lines[i].IndexOf(':');
                        if (idx > 0) d[lines[i].Substring(0, idx).Trim()] = lines[i].Substring(idx + 1).Trim();
                    }
                    return d;
                }
            }
            return null; // header block too large
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static async Task RejectAsync(Stream s, string status)
        {
            var b = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(b, 0, b.Length).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
        }

        /// <summary>One upgraded connection: reads masked client frames, writes unmasked server frames.</summary>
        private sealed class WsConnection
        {
            private readonly Stream _s;
            private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

            public WsConnection(Stream s) => _s = s;

            /// <summary>Next complete text message, or null once the peer closes.</summary>
            public async Task<string> ReceiveTextAsync(CancellationToken ct)
            {
                var message = new MemoryStream();
                while (true)
                {
                    var hdr = new byte[2];
                    if (!await ReadExactAsync(hdr, 2, ct).ConfigureAwait(false)) return null;
                    bool fin = (hdr[0] & 0x80) != 0;
                    int opcode = hdr[0] & 0x0F;
                    bool masked = (hdr[1] & 0x80) != 0;
                    long len = hdr[1] & 0x7F;
                    if (!masked) return null; // clients MUST mask (RFC 6455 5.1)
                    if (len == 126)
                    {
                        var e = new byte[2];
                        if (!await ReadExactAsync(e, 2, ct).ConfigureAwait(false)) return null;
                        len = (e[0] << 8) | e[1];
                    }
                    else if (len == 127)
                    {
                        var e = new byte[8];
                        if (!await ReadExactAsync(e, 8, ct).ConfigureAwait(false)) return null;
                        len = 0;
                        for (int i = 0; i < 8; i++) len = (len << 8) | e[i];
                    }
                    if (len < 0 || message.Length + len > MaxMessageBytes) return null;

                    var mask = new byte[4];
                    if (!await ReadExactAsync(mask, 4, ct).ConfigureAwait(false)) return null;
                    var payload = new byte[len];
                    if (!await ReadExactAsync(payload, (int)len, ct).ConfigureAwait(false)) return null;
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                    switch (opcode)
                    {
                        case 0x8: // close: echo and finish
                            await WriteFrameAsync(0x8, payload, ct).ConfigureAwait(false);
                            return null;
                        case 0x9: // ping
                            await WriteFrameAsync(0xA, payload, ct).ConfigureAwait(false);
                            continue;
                        case 0xA: // pong
                            continue;
                        case 0x0: // continuation
                        case 0x1: // text
                        case 0x2: // binary (treated as UTF-8 text)
                            message.Write(payload, 0, payload.Length);
                            if (fin) return Encoding.UTF8.GetString(message.ToArray());
                            continue;
                        default:
                            return null;
                    }
                }
            }

            public Task SendTextAsync(string text, CancellationToken ct) => WriteFrameAsync(0x1, Encoding.UTF8.GetBytes(text), ct);

            private async Task WriteFrameAsync(int opcode, byte[] payload, CancellationToken ct)
            {
                var header = new List<byte> { (byte)(0x80 | opcode) };
                if (payload.Length < 126) header.Add((byte)payload.Length);
                else if (payload.Length <= ushort.MaxValue) { header.Add(126); header.Add((byte)(payload.Length >> 8)); header.Add((byte)payload.Length); }
                else
                {
                    header.Add(127);
                    for (int i = 7; i >= 0; i--) header.Add((byte)((long)payload.Length >> (8 * i)));
                }
                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var h = header.ToArray();
                    await _s.WriteAsync(h, 0, h.Length, ct).ConfigureAwait(false);
                    await _s.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                    await _s.FlushAsync(ct).ConfigureAwait(false);
                }
                finally { _sendLock.Release(); }
            }

            private async Task<bool> ReadExactAsync(byte[] buf, int count, CancellationToken ct)
            {
                int got = 0;
                while (got < count)
                {
                    int n = await _s.ReadAsync(buf, got, count - got, ct).ConfigureAwait(false);
                    if (n == 0) return false;
                    got += n;
                }
                return true;
            }
        }
    }
}
