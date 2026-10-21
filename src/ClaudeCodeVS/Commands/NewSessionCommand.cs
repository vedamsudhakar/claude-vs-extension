using System.ComponentModel.Design;
using ClaudeCodeVS.Terminal;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS.Commands
{
    /// <summary>Tools > Claude Code: New Session. Restarts the docked terminal's process.</summary>
    internal static class NewSessionCommand
    {
        public const int CommandId = 0x0103;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var mcs = (IMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            mcs.AddCommand(new MenuCommand(
                (s, e) => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await OpenWindowCommand.ShowAsync(focus: false);
                    var window = (ClaudeToolWindow)await package.FindToolWindowAsync(typeof(ClaudeToolWindow), 0, false, package.DisposalToken);
                    window?.NewSession();
                }).FileAndForget("claudecode/newsession"),
                new CommandID(LaunchExternalCommand.CommandSet, CommandId)));
        }
    }
}
