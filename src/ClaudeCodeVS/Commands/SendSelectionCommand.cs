using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS.Commands
{
    /// <summary>Ctrl+Alt+K / editor context menu: @-mention the current file and selected lines in Claude.</summary>
    internal static class SendSelectionCommand
    {
        public const int CommandId = 0x0101;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var mcs = (IMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            mcs.AddCommand(new MenuCommand(
                (s, e) => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    var sent = await (Bridge.IdeBridgeHost.Current?.SendAtMentionAsync() ?? Task.FromResult(false));
                    if (sent) await OpenWindowCommand.ShowAsync(focus: true); // bring Claude forward so the user can keep typing
                }).FileAndForget("claudecode/sendselection"),
                new CommandID(LaunchExternalCommand.CommandSet, CommandId)));
        }
    }
}
