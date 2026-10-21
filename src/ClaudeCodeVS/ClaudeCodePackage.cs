using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(Terminal.ClaudeToolWindow), Style = VsDockStyle.Tabbed, DockedWidth = 480,
        Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057" /* Solution Explorer */)]
    [ProvideOptionPage(typeof(Options.GeneralOptions), "Claude Code", "General", 0, 0, true)]
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class ClaudeCodePackage : AsyncPackage
    {
        public const string PackageGuidString = "5b1f6a7e-8c2d-4e39-9a41-0d7c3f2b6e10";

        /// <summary>Tools > Options > Claude Code settings; null until the package has initialised.</summary>
        public static Options.GeneralOptions Options { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Options = (Options.GeneralOptions)GetDialogPage(typeof(Options.GeneralOptions));
            await Commands.LaunchExternalCommand.InitializeAsync(this);
            await Commands.NewSessionCommand.InitializeAsync(this);
            await Commands.OpenWindowCommand.InitializeAsync(this);
            await Commands.SendSelectionCommand.InitializeAsync(this);
            await Commands.AddFileCommand.InitializeAsync(this);
            try { await Bridge.IdeBridgeHost.StartAsync(); }
            catch (Exception ex)
            {
                // Keep the menu commands working even if the IDE bridge fails; the terminal still runs without it.
                ActivityLog.LogError(nameof(ClaudeCodePackage), "IDE bridge failed to start: " + ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Bridge.IdeBridgeHost.Current?.Dispose();
            base.Dispose(disposing);
        }
    }
}
