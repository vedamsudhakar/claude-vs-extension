using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.Terminal
{
    [Guid(WindowGuidString)]
    public sealed class ClaudeToolWindow : ToolWindowPane
    {
        public const string WindowGuidString = "8e4f2b6a-1d3c-4a57-9b08-6c2e7f1a5d90";

        private readonly ClaudeSessionsControl _sessions;

        public ClaudeToolWindow() : base(null)
        {
            Caption = "Claude Code";
            _sessions = new ClaudeSessionsControl();
            Content = _sessions;
        }

        public void FocusTerminal() => _sessions.FocusTerminal();

        /// <summary>Opens another session tab; false when the session limit is reached.</summary>
        public bool NewSession() => _sessions.AddSession(focus: true);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _sessions.Dispose();
            base.Dispose(disposing);
        }
    }
}
