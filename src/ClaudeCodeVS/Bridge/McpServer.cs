using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Bridge
{
    internal interface ITool
    {
        string Name { get; }
        string Description { get; }
        JObject InputSchema { get; }
        /// <summary>Returns the text placed in the MCP result's content[0].text.</summary>
        Task<string> InvokeAsync(JObject arguments);
    }

    /// <summary>JSON-RPC 2.0 dispatcher for the subset of MCP the Claude CLI uses against an IDE.</summary>
    internal sealed class McpServer
    {
        private readonly Dictionary<string, ITool> _tools;

        public McpServer(IEnumerable<ITool> tools) => _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        /// <summary>Optional sink for one-line events (tool names and outcomes only, never arguments or contents).</summary>
        public Action<string> Trace { get; set; }

        /// <summary>Returns the JSON reply, or null for notifications.</summary>
        public async Task<string> HandleAsync(string message)
        {
            JObject req;
            try { req = JObject.Parse(message); }
            catch (Exception) { return Error(null, -32700, "Parse error"); }

            var id = req["id"];
            var method = (string)req["method"];
            if (id == null) return null; // notification (initialized, ide_connected, ...)

            try
            {
                switch (method)
                {
                    case "initialize":
                        return Result(id, new JObject
                        {
                            ["protocolVersion"] = (string)req["params"]?["protocolVersion"] ?? "2025-03-26",
                            ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                            ["serverInfo"] = new JObject { ["name"] = "visual-studio", ["version"] = "0.1.0" },
                        });
                    case "ping":
                        return Result(id, new JObject());
                    case "tools/list":
                        return Result(id, new JObject
                        {
                            ["tools"] = new JArray(_tools.Values.Select(t => new JObject
                            {
                                ["name"] = t.Name,
                                ["description"] = t.Description,
                                ["inputSchema"] = t.InputSchema,
                            })),
                        });
                    case "tools/call":
                        return await CallToolAsync(id, (JObject)req["params"]).ConfigureAwait(false);
                    default:
                        return Error(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                return Error(id, -32603, ex.Message);
            }
        }

        private async Task<string> CallToolAsync(JToken id, JObject p)
        {
            var name = (string)p?["name"];
            if (name == null || !_tools.TryGetValue(name, out var tool))
                return Error(id, -32602, "Unknown tool: " + name);
            try
            {
                var text = await tool.InvokeAsync(p["arguments"] as JObject ?? new JObject()).ConfigureAwait(false);
                Trace?.Invoke("tool " + name + " ok");
                return Result(id, new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
                });
            }
            catch (Exception ex)
            {
                Trace?.Invoke("tool " + name + " failed: " + ex.Message);
                return Result(id, new JObject
                {
                    ["isError"] = true,
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = ex.Message }),
                });
            }
        }

        public static string Notification(string method, JObject @params) =>
            new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params }.ToString(Newtonsoft.Json.Formatting.None);

        private static string Result(JToken id, JObject result) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToString(Newtonsoft.Json.Formatting.None);

        private static string Error(JToken id, int code, string message) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = code, ["message"] = message } }
                .ToString(Newtonsoft.Json.Formatting.None);
    }
}
