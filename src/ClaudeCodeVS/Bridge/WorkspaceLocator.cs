using System;
using System.IO;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;

namespace ClaudeCodeVS.Bridge
{
    /// <summary>
    /// Finds the folder VS currently has open, whether it is a solution, an "Open Folder" workspace,
    /// a loose project, or just a document.
    /// </summary>
    internal static class WorkspaceLocator
    {
        public static async Task<string> GetRootAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return GetRoot();
        }

        public static string GetRoot()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // 1. Open Folder workspace (also the case for "File > Open > Folder" and CMake/Node folders).
            var dir = Existing(FolderWorkspaceLocation());
            if (dir != null) return dir;

            // 2. Solution directory as reported by the shell (works for .sln and .slnx).
            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution sol
                && ErrorHandler.Succeeded(sol.GetProperty((int)__VSPROPID.VSPROPID_SolutionDirectory, out var o)))
            {
                dir = Existing(o as string);
                if (dir != null) return dir;
            }

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;

            // 3. Solution file, 4. first project, 5. active document.
            dir = Existing(DirOf(dte?.Solution?.FullName));
            if (dir != null) return dir;
            try
            {
                foreach (Project p in dte.Solution.Projects)
                {
                    dir = Existing(DirOf(p.FullName));
                    if (dir != null) return dir;
                }
            }
            catch (Exception) { /* solution still loading */ }
            return Existing(DirOf(dte?.ActiveDocument?.FullName));
        }

        // Path.GetDirectoryName throws on "" in .NET Framework, so guard it.
        private static string DirOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return Path.GetDirectoryName(path); }
            catch (ArgumentException) { return null; }
        }

        private static string FolderWorkspaceLocation()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Exported through MEF rather than as a global service.
                var cm = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                return cm?.GetService<IVsFolderWorkspaceService>()?.CurrentWorkspace?.Location;
            }
            catch (Exception) { return null; }
        }

        private static string Existing(string path) => !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : null;
    }
}
