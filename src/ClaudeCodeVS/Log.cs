using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    /// <summary>Writes to the "Claude Code" pane of the Output window. Never logs auth tokens or file contents.</summary>
    internal static class Log
    {
        private static readonly Guid PaneGuid = new Guid("b7a0c3d2-4e91-4f6a-8c15-2d9e0f3a6b48");
        private static IVsOutputWindowPane _pane;

        public static void Info(string message)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                GetPane()?.OutputStringThreadSafe(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            }).FileAndForget("claudecode/log");
        }

        private static IVsOutputWindowPane GetPane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_pane != null) return _pane;
            if (!(Package.GetGlobalService(typeof(SVsOutputWindow)) is IVsOutputWindow output)) return null;
            var g = PaneGuid;
            output.CreatePane(ref g, "Claude Code", 1, 0);
            output.GetPane(ref g, out _pane);
            return _pane;
        }
    }
}
