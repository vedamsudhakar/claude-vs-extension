using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.Options
{
    public enum ShellKind { CommandPrompt, PowerShell }

    public enum ExternalTerminalKind { Auto, WindowsTerminal, ConsoleWindow }

    /// <summary>Tools > Options > Claude Code > General.</summary>
    [ComVisible(true)]
    [Guid("2f6b9c14-7a30-4e8d-b1c5-93d0a8e4f627")]
    public class GeneralOptions : DialogPage
    {
        [Category("Claude Code")]
        [DisplayName("Claude command")]
        [Description("Command used to start Claude Code. Leave as 'claude' to use the one on PATH, or give a full path to claude.exe / claude.cmd.")]
        public string ClaudeCommand { get; set; } = "claude";

        [Category("Claude Code")]
        [DisplayName("Extra arguments")]
        [Description("Extra command-line arguments appended when starting Claude Code, for example: --model opus")]
        public string ExtraArguments { get; set; } = "";

        [Category("Terminal")]
        [DisplayName("Shell")]
        [Description("Shell that hosts Claude Code, in both the docked and the external terminal. Applies to new sessions.")]
        public ShellKind Shell { get; set; } = ShellKind.CommandPrompt;

        [Category("Terminal")]
        [DisplayName("External terminal")]
        [Description("Window used by 'Claude Code (External Terminal)'. Auto uses Windows Terminal when it is installed, otherwise a console window.")]
        public ExternalTerminalKind ExternalTerminal { get; set; } = ExternalTerminalKind.Auto;

        [Category("Terminal")]
        [DisplayName("Font size")]
        [Description("Font size of the docked terminal (applies to new and existing terminals).")]
        public int FontSize { get; set; } = 13;

        /// <summary>Raised after the user clicks OK/Apply, so open terminals can pick up changes.</summary>
        public static event Action Applied;

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior == ApplyKind.Apply) Applied?.Invoke();
        }

        /// <summary>The command line fragment that starts Claude: quoted command plus arguments.</summary>
        public string BuildClaudeCommandLine()
        {
            var cmd = string.IsNullOrWhiteSpace(ClaudeCommand) ? "claude" : ClaudeCommand.Trim();
            if (cmd.IndexOf(' ') >= 0 && !cmd.StartsWith("\"")) cmd = "\"" + cmd + "\"";
            return string.IsNullOrWhiteSpace(ExtraArguments) ? cmd : cmd + " " + ExtraArguments.Trim();
        }
    }
}
