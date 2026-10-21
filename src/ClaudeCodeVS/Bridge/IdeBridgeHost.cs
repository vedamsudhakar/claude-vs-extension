using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Events;

namespace ClaudeCodeVS.Bridge
{
    /// <summary>Owns the server, lock file and tools for this VS instance. One port + lock file per instance.</summary>
    internal sealed class IdeBridgeHost : IDisposable
    {
        private readonly WebSocketServer _server;
        private readonly LockFile _lock;
        private readonly McpServer _mcp;
        private SelectionTracker _selection;

        public static IdeBridgeHost Current { get; private set; }

        public int Port => _server.Port;

        private IdeBridgeHost()
        {
            LockFile.CleanStale();
            _mcp = new McpServer(EditorTools.Create().Concat(DiffService.CreateTools()).Concat(new[] { DiagnosticsTool.Create() }));
            // The token must exist before the server so the server can check it; lock file needs the port after bind.
            var token = LockFile.NewToken();
            _mcp.Trace = Log.Info;
            _server = new WebSocketServer(token, _mcp.HandleAsync);
            _server.Error += ex => Log.Info("Connection error: " + ex.GetType().Name + ": " + ex.Message);
            Log.Info("IDE bridge listening on 127.0.0.1:" + _server.Port);
            _lock = new LockFile(_server.Port, token);
        }

        public static async Task StartAsync()
        {
            var host = new IdeBridgeHost();
            Current = host;
            host._server.Start();
            await host.RefreshLockAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            host._selection = new SelectionTracker(host._server.BroadcastAsync);
            SolutionEvents.OnAfterOpenSolution += host.OnSolutionChanged;
            SolutionEvents.OnAfterCloseSolution += host.OnSolutionChanged;
        }

        private void OnSolutionChanged(object sender, EventArgs e) => _ = ThreadHelper.JoinableTaskFactory.RunAsync(RefreshLockAsync);

        private async Task RefreshLockAsync()
        {
            var folders = await EditorTools.WorkspaceFoldersAsync();
            _lock.Write(folders);
        }

        /// <summary>Tells connected Claude sessions to @-mention these files (no line range).</summary>
        public async Task SendFileMentionsAsync(System.Collections.Generic.IEnumerable<string> paths)
        {
            foreach (var p in paths)
                await _server.BroadcastAsync(McpServer.Notification("at_mentioned", new Newtonsoft.Json.Linq.JObject { ["filePath"] = p }));
        }

        /// <summary>Tells connected Claude sessions to @-mention the active file and selected lines.</summary>
        public async Task<bool> SendAtMentionAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var p = SelectionTracker.AtMention();
            if (p == null) return false;
            await _server.BroadcastAsync(McpServer.Notification("at_mentioned", p));
            return true;
        }

        public void Dispose()
        {
            _selection?.Dispose();
            SolutionEvents.OnAfterOpenSolution -= OnSolutionChanged;
            SolutionEvents.OnAfterCloseSolution -= OnSolutionChanged;
            _server.Dispose();
            _lock.Delete();
            if (Current == this) Current = null;
        }
    }
}
