using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.Terminal
{
    [Guid(WindowGuidString)]
    public sealed class ClaudeToolWindow : ToolWindowPane
    {
        public const string WindowGuidString = "8e4f2b6a-1d3c-4a57-9b08-6c2e7f1a5d90";

        private readonly ClaudeTerminalControl _control;

        public ClaudeToolWindow() : base(null)
        {
            Caption = "Claude Code";
            _control = new ClaudeTerminalControl();
            Content = _control;
        }

        public void FocusTerminal() => _control.FocusTerminal();

        public void NewSession() => _control.RestartSession();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _control.Dispose();
            base.Dispose(disposing);
        }
    }
}
