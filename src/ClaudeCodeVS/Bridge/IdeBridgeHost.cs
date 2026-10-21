using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Events;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Bridge
{
    /// <summary>
    /// Owns the IDE bridge for this VS instance: a default endpoint (external terminals) plus one endpoint per docked
    /// Claude session. Selection changes go to every endpoint; @-mentions go to the active session and external terminals.
    /// </summary>
    internal sealed class IdeBridgeHost : IDisposable
    {
        private readonly BridgeEndpoint _default;
        private readonly List<BridgeEndpoint> _sessions = new List<BridgeEndpoint>();
        private readonly object _gate = new object();
        private string[] _folders = new string[0];
        private SelectionTracker _selection;

        public static IdeBridgeHost Current { get; private set; }

        /// <summary>Port of the default endpoint, handed to external terminals.</summary>
        public int Port => _default.Port;

        /// <summary>Endpoint of the selected docked session tab, or null when none is open.</summary>
        public BridgeEndpoint ActiveSession { get; set; }

        private IdeBridgeHost()
        {
            LockFile.CleanStale();
            _default = new BridgeEndpoint("Visual Studio", "Claude");
        }

        public static async Task StartAsync()
        {
            var host = new IdeBridgeHost();
            Current = host;
            await host.RefreshLocksAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            host._selection = new SelectionTracker(host.BroadcastAllAsync);
            SolutionEvents.OnAfterOpenSolution += host.OnSolutionChanged;
            SolutionEvents.OnAfterCloseSolution += host.OnSolutionChanged;
        }

        /// <summary>Starts a bridge endpoint for docked session <paramref name="number"/>; dispose it via <see cref="ReleaseSession"/>.</summary>
        public BridgeEndpoint CreateSession(int number)
        {
            var endpoint = new BridgeEndpoint("Visual Studio (Session " + number + ")", "Claude " + number);
            lock (_gate)
            {
                _sessions.Add(endpoint);
                endpoint.WriteLock(_folders);
            }
            return endpoint;
        }

        public void ReleaseSession(BridgeEndpoint endpoint)
        {
            if (endpoint == null) return;
            lock (_gate) _sessions.Remove(endpoint);
            if (ActiveSession == endpoint) ActiveSession = null;
            endpoint.Dispose();
        }

        private void OnSolutionChanged(object sender, EventArgs e) => _ = ThreadHelper.JoinableTaskFactory.RunAsync(RefreshLocksAsync);

        private async Task RefreshLocksAsync()
        {
            var folders = await EditorTools.WorkspaceFoldersAsync();
            lock (_gate)
            {
                _folders = folders;
                _default.WriteLock(folders);
                foreach (var s in _sessions) s.WriteLock(folders);
            }
        }

        private BridgeEndpoint[] AllEndpoints()
        {
            lock (_gate) return new[] { _default }.Concat(_sessions).ToArray();
        }

        private async Task BroadcastAllAsync(string message)
        {
            foreach (var e in AllEndpoints()) await e.BroadcastAsync(message);
        }

        // @-mentions are typed into Claude's prompt, so only the session the user is looking at should get them.
        private async Task MentionAsync(string message)
        {
            var active = ActiveSession;
            if (active != null) await active.BroadcastAsync(message);
            await _default.BroadcastAsync(message);
        }

        /// <summary>Tells the active Claude session (and external terminals) to @-mention these files (no line range).</summary>
        public async Task SendFileMentionsAsync(IEnumerable<string> paths)
        {
            foreach (var p in paths)
                await MentionAsync(McpServer.Notification("at_mentioned", new JObject { ["filePath"] = p }));
        }

        /// <summary>Tells the active Claude session (and external terminals) to @-mention the active file and selected lines.</summary>
        public async Task<bool> SendAtMentionAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var p = SelectionTracker.AtMention();
            if (p == null) return false;
            await MentionAsync(McpServer.Notification("at_mentioned", p));
            return true;
        }

        public void Dispose()
        {
            _selection?.Dispose();
            SolutionEvents.OnAfterOpenSolution -= OnSolutionChanged;
            SolutionEvents.OnAfterCloseSolution -= OnSolutionChanged;
            foreach (var e in AllEndpoints()) e.Dispose();
            lock (_gate) _sessions.Clear();
            if (Current == this) Current = null;
        }
    }
}
