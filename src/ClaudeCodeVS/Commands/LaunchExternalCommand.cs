using System;
using System.ComponentModel.Design;
using System.Collections.Generic;
using ClaudeCodeVS.Terminal;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS.Commands
{
    internal sealed class LaunchExternalCommand
    {
        public static readonly Guid CommandSet = new Guid("c3a9d1f4-6b72-4d85-8e0a-1f5b7a9c2d34");
        public const int CommandId = 0x0100;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var mcs = (IMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            mcs.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, CommandId)));
        }

        private static void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dir = Bridge.WorkspaceLocator.GetRoot() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var env = new Dictionary<string, string>();
            var host = Bridge.IdeBridgeHost.Current;
            if (host != null) { env["CLAUDE_CODE_SSE_PORT"] = host.Port.ToString(); env["ENABLE_IDE_INTEGRATION"] = "true"; }
            try { ShellLauncher.LaunchExternal(ClaudeCodePackage.Options, dir, env); }
            catch (Exception ex) { Log.Info("Could not start external terminal: " + ex.Message); throw; }
        }
    }
}
