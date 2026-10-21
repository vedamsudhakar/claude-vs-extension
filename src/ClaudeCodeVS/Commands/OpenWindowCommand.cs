using System;
using System.ComponentModel.Design;
using ClaudeCodeVS.Terminal;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS.Commands
{
    /// <summary>Tools > Claude Code (Ctrl+Alt+C): show the docked Claude Code terminal.</summary>
    internal static class OpenWindowCommand
    {
        public const int CommandId = 0x0102;
        private static AsyncPackage _package;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            _package = package;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var mcs = (IMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            mcs.AddCommand(new MenuCommand(
                (s, e) => ThreadHelper.JoinableTaskFactory.RunAsync(() => ShowAsync(focus: true)).FileAndForget("claudecode/openwindow"),
                new CommandID(LaunchExternalCommand.CommandSet, CommandId)));
        }

        public static async Task ShowAsync(bool focus)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var window = (ClaudeToolWindow)await _package.ShowToolWindowAsync(typeof(ClaudeToolWindow), 0, true, _package.DisposalToken);
            if (focus) window.FocusTerminal();
        }
    }
}
