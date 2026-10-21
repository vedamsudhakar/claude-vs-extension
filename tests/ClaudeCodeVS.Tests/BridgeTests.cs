using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ClaudeCodeVS.Tests
{
    public class BridgeTests
    {
        private sealed class EchoTool : ITool
        {
            public string Name => "echo";
            public string Description => "echo";
            public JObject InputSchema => new JObject { ["type"] = "object" };
            public Task<string> InvokeAsync(JObject a) => Task.FromResult("hi " + (string)a["who"]);
        }

        private static (WebSocketServer server, string token) StartServer()
        {
            var mcp = new McpServer(new ITool[] { new EchoTool() });
            var token = LockFile.NewToken();
            var server = new WebSocketServer(token, mcp.HandleAsync);
            server.Error += ex => System.Diagnostics.Trace.WriteLine("SERVER ERROR: " + ex);
            server.Error += ex => System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccvs-test.log"), ex + "\n");
            server.Start();
            return (server, token);
        }

        private static async Task<string> RoundTripAsync(ClientWebSocket ws, string json)
        {
            var data = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
            var buf = new byte[8192];
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), new CancellationTokenSource(5000).Token);
            return Encoding.UTF8.GetString(buf, 0, r.Count);
        }

        [Theory]
        [InlineData(200)]      // 16-bit extended length
        [InlineData(70000)]    // 64-bit extended length, multi-read
        public async Task Large_messages_round_trip(int size)
        {
            var (server, token) = StartServer();
            using (server)
            using (var ws = new ClientWebSocket())
            {
                ws.Options.SetRequestHeader("x-claude-code-ide-authorization", token);
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + server.Port), CancellationToken.None);
                var who = new string('x', size);
                var req = new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JObject { ["name"] = "echo", ["arguments"] = new JObject { ["who"] = who } },
                }.ToString(Newtonsoft.Json.Formatting.None);
                var data = Encoding.UTF8.GetBytes(req);
                await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);

                var buf = new byte[size + 1024]; var total = 0; WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, total, buf.Length - total), CancellationToken.None); total += r.Count; }
                while (!r.EndOfMessage);
                Assert.Equal("hi " + who, (string)JObject.Parse(Encoding.UTF8.GetString(buf, 0, total))["result"]["content"][0]["text"]);
            }
        }

        [Fact]
        public void Token_is_32_lowercase_hex()
        {
            Assert.Matches("^[0-9a-f]{32}$", LockFile.NewToken());
        }

        [Fact]
        public async Task Authorized_client_can_list_and_call_tools()
        {
            var (server, token) = StartServer();
            using (server)
            using (var ws = new ClientWebSocket())
            {
                ws.Options.SetRequestHeader("x-claude-code-ide-authorization", token);
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + server.Port), CancellationToken.None);

                var init = JObject.Parse(await RoundTripAsync(ws,
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}"));
                Assert.Equal("2025-03-26", (string)init["result"]["protocolVersion"]);

                var list = JObject.Parse(await RoundTripAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}"));
                Assert.Equal("echo", (string)list["result"]["tools"][0]["name"]);

                var call = JObject.Parse(await RoundTripAsync(ws,
                    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"who\":\"vs\"}}}"));
                Assert.Equal("hi vs", (string)call["result"]["content"][0]["text"]);
            }
        }

        [Fact]
        public async Task Missing_or_wrong_token_is_rejected()
        {
            var (server, _) = StartServer();
            using (server)
            {
                foreach (var bad in new string[] { null, "0123456789abcdef0123456789abcdef" })
                {
                    using (var ws = new ClientWebSocket())
                    {
                        if (bad != null) ws.Options.SetRequestHeader("x-claude-code-ide-authorization", bad);
                        await Assert.ThrowsAnyAsync<Exception>(() =>
                            ws.ConnectAsync(new Uri("ws://127.0.0.1:" + server.Port), CancellationToken.None));
                    }
                }
            }
        }

        [Fact]
        public async Task Browser_origin_is_rejected_even_with_valid_token()
        {
            var (server, token) = StartServer();
            using (server)
            using (var ws = new ClientWebSocket())
            {
                ws.Options.SetRequestHeader("x-claude-code-ide-authorization", token);
                ws.Options.SetRequestHeader("Origin", "http://evil.example");
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    ws.ConnectAsync(new Uri("ws://127.0.0.1:" + server.Port), CancellationToken.None));
            }
        }

        [Fact]
        public async Task Server_listens_on_loopback_only()
        {
            var (server, _) = StartServer();
            using (server)
            {
                var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, server.Port);
                using (var c = new TcpClient()) { await c.ConnectAsync(ep.Address, ep.Port); Assert.True(c.Connected); }
                Assert.InRange(server.Port, 1024, 65535);
            }
        }

        [Fact]
        public async Task Unknown_tool_and_unknown_method_return_errors_and_notifications_get_no_reply()
        {
            var mcp = new McpServer(new ITool[] { new EchoTool() });
            var bad = JObject.Parse(await mcp.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"nope\"}"));
            Assert.Equal(-32601, (int)bad["error"]["code"]);
            var unk = JObject.Parse(await mcp.HandleAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"zzz\"}}"));
            Assert.Equal(-32602, (int)unk["error"]["code"]);
            Assert.Null(await mcp.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        }
    }
}
