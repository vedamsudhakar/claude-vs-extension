using System.ComponentModel.Design;
using ClaudeCodeVS.Terminal;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS.Commands
{
    /// <summary>Tools > Claude Code: New Session. Opens another session tab next to the running ones (up to 5).</summary>
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
                    // A window created just now already starts with its first session.
                    var existed = await package.FindToolWindowAsync(typeof(ClaudeToolWindow), 0, false, package.DisposalToken) != null;
                    await OpenWindowCommand.ShowAsync(focus: false);
                    var window = (ClaudeToolWindow)await package.FindToolWindowAsync(typeof(ClaudeToolWindow), 0, false, package.DisposalToken);
                    if (existed && window != null && !window.NewSession())
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        VsShellUtilities.ShowMessageBox(package, "Claude Code supports at most " + ClaudeSessionsControl.MaxSessions +
                            " sessions. Close a session tab to start a new one.", "Claude Code",
                            OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    }
                }).FileAndForget("claudecode/newsession"),
                new CommandID(LaunchExternalCommand.CommandSet, CommandId)));
        }
    }
}
