using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.Bridge
{
    /// <summary>
    /// One MCP server + port + lock file. Each docked Claude session gets its own endpoint so IDE notifications
    /// (@-mentions) and diff tabs stay with the session they belong to; external terminals share the default one.
    /// </summary>
    internal sealed class BridgeEndpoint : IDisposable
    {
        private readonly WebSocketServer _server;
        private readonly LockFile _lock;
        private readonly DiffService _diffs;

        public int Port => _server.Port;

        /// <param name="ideName">Lock-file name shown by /ide, e.g. "Visual Studio" or "Visual Studio (Session 2)".</param>
        /// <param name="diffCaption">Diff window title prefix, e.g. "Claude" or "Claude 2".</param>
        public BridgeEndpoint(string ideName, string diffCaption)
        {
            _diffs = new DiffService(diffCaption);
            var mcp = new McpServer(EditorTools.Create().Concat(_diffs.CreateTools()).Concat(new[] { DiagnosticsTool.Create() }));
            // The token must exist before the server so the server can check it; lock file needs the port after bind.
            var token = LockFile.NewToken();
            mcp.Trace = Log.Info;
            _server = new WebSocketServer(token, mcp.HandleAsync);
            _server.Error += ex => Log.Info("Connection error: " + ex.GetType().Name + ": " + ex.Message);
            _server.Start();
            Log.Info(ideName + " bridge listening on 127.0.0.1:" + _server.Port);
            _lock = new LockFile(_server.Port, token, ideName);
        }

        public void WriteLock(string[] workspaceFolders) => _lock.Write(workspaceFolders);

        public Task BroadcastAsync(string message) => _server.BroadcastAsync(message);

        public void Dispose()
        {
            // Leftover diffs would keep live Accept buttons with no Claude behind them.
            if (ThreadHelper.CheckAccess()) { try { _diffs.CloseAll(); } catch (Exception) { /* shutting down */ } }
            _server.Dispose();
            _lock.Delete();
        }
    }
}
