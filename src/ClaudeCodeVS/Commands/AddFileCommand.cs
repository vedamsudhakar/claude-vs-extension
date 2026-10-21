using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS.Commands
{
    /// <summary>Solution Explorer context menu: @-mention the selected file(s) in Claude.</summary>
    internal static class AddFileCommand
    {
        public const int CommandId = 0x0104;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var mcs = (IMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            mcs.AddCommand(new MenuCommand(
                (s, e) => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var files = SelectedFiles();
                    if (files.Count == 0 || Bridge.IdeBridgeHost.Current == null) return;
                    await Bridge.IdeBridgeHost.Current.SendFileMentionsAsync(files);
                    await OpenWindowCommand.ShowAsync(focus: true);
                }).FileAndForget("claudecode/addfile"),
                new CommandID(LaunchExternalCommand.CommandSet, CommandId)));
        }

        private static List<string> SelectedFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<string>();
            var dte = (DTE)Package.GetGlobalService(typeof(DTE));
            if (dte?.SelectedItems == null) return result;
            foreach (SelectedItem item in dte.SelectedItems)
            {
                string path = null;
                try { path = item.ProjectItem?.FileCount > 0 ? item.ProjectItem.FileNames[1] : null; }
                catch (System.Exception) { /* not a file node */ }
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) result.Add(path);
            }
            return result;
        }
    }
}
