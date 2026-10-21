using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Bridge
{
    /// <summary>
    /// Shows Claude's proposed edit in VS's diff viewer with an Accept / Reject info bar. openDiff blocks
    /// until the user decides; closing the diff tab counts as a rejection.
    /// </summary>
    internal static class DiffService
    {
        private static readonly ConcurrentDictionary<string, DiffSession> Sessions = new ConcurrentDictionary<string, DiffSession>();

        public static IEnumerable<ITool> CreateTools()
        {
            yield return new DelegateTool("openDiff", "Open a diff view of the proposed change and wait for the user to accept or reject it.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["old_file_path"] = new JObject { ["type"] = "string" },
                        ["new_file_path"] = new JObject { ["type"] = "string" },
                        ["new_file_contents"] = new JObject { ["type"] = "string" },
                        ["tab_name"] = new JObject { ["type"] = "string" },
                    },
                    ["required"] = new JArray("old_file_path", "new_file_path", "new_file_contents", "tab_name"),
                },
                OpenDiffAsync);
            yield return new DelegateTool("close_tab", "Close a tab by name.",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject { ["tab_name"] = new JObject { ["type"] = "string" } },
                    ["required"] = new JArray("tab_name"),
                },
                async a =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (Sessions.TryGetValue((string)a["tab_name"], out var s)) s.Finish(accepted: false, closeFrame: true);
                    return "TAB_CLOSED";
                });
            yield return new DelegateTool("closeAllDiffTabs", "Close all diff tabs.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() },
                async _ =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var all = Sessions.Values.ToList();
                    foreach (var s in all) s.Finish(accepted: false, closeFrame: true);
                    return "CLOSED_" + all.Count + "_DIFF_TABS";
                });
        }

        private static async Task<string> OpenDiffAsync(JObject a)
        {
            var oldPath = (string)a["old_file_path"];
            var newPath = (string)a["new_file_path"];
            var contents = (string)a["new_file_contents"] ?? "";
            var tabName = (string)a["tab_name"] ?? Path.GetFileName(newPath);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Sessions.TryRemove(tabName, out var previous)) previous.Finish(accepted: false, closeFrame: true);

            var session = new DiffSession(tabName, oldPath, newPath, contents, () => Sessions.TryRemove(tabName, out _));
            Sessions[tabName] = session;
            try { session.Show(); }
            catch (Exception) { Sessions.TryRemove(tabName, out _); session.Cleanup(); throw; }
            var accepted = await session.Result.Task.ConfigureAwait(false);
            return accepted ? "FILE_SAVED" : "DIFF_REJECTED";
        }

        private sealed class DiffSession : IVsInfoBarUIEvents, IVsWindowFrameNotify
        {
            private readonly string _tabName, _oldPath, _newPath, _contents;
            private readonly Action _onDone;
            private readonly string _tempDir;
            private readonly string _tempOld, _tempNew;
            private IVsWindowFrame _frame;
            private IVsInfoBarUIElement _infoBar;
            private uint _infoBarCookie, _frameCookie;
            private bool _done;

            public TaskCompletionSource<bool> Result { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public DiffSession(string tabName, string oldPath, string newPath, string contents, Action onDone)
            {
                _tabName = tabName; _oldPath = oldPath; _newPath = newPath; _contents = contents; _onDone = onDone;
                _tempDir = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDir);
                var name = Path.GetFileName(newPath);
                var leftDir = Directory.CreateDirectory(Path.Combine(_tempDir, "current")).FullName;
                var rightDir = Directory.CreateDirectory(Path.Combine(_tempDir, "proposed")).FullName;
                _tempOld = Path.Combine(leftDir, name);
                _tempNew = Path.Combine(rightDir, name);
            }

            public void Show()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(_tempOld, File.Exists(_oldPath) ? File.ReadAllText(_oldPath) : "", utf8);
                File.WriteAllText(_tempNew, _contents, utf8);

                var svc = (IVsDifferenceService)Package.GetGlobalService(typeof(SVsDifferenceService));
                var name = Path.GetFileName(_newPath);
                _frame = svc.OpenComparisonWindow2(_tempOld, _tempNew, "Claude: " + name, _newPath,
                    "Current", "Proposed by Claude", "Claude: " + name, null,
                    (uint)(__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary | __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary));
                _frame.Show();
                AddInfoBar();
                (_frame as IVsWindowFrame2)?.Advise(this, out _frameCookie);
            }

            private void AddInfoBar()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!ErrorHandler.Succeeded(_frame.GetProperty((int)__VSFPROPID7.VSFPROPID_InfoBarHost, out var hostObj))
                    || !(hostObj is IVsInfoBarHost host)) return;
                var factory = (IVsInfoBarUIFactory)Package.GetGlobalService(typeof(SVsInfoBarUIFactory));
                var model = new InfoBarModel(
                    new[] { new InfoBarTextSpan("Claude wants to change " + Path.GetFileName(_newPath) + ". You can edit the right side before accepting. ") },
                    new IVsInfoBarActionItem[] { new InfoBarButton("Accept"), new InfoBarButton("Reject") },
                    KnownMonikers.StatusInformation, isCloseButtonVisible: false);
                _infoBar = factory.CreateInfoBar(model);
                _infoBar.Advise(this, out _infoBarCookie);
                host.AddInfoBar(_infoBar);
            }

            public void OnActionItemClicked(IVsInfoBarUIElement element, IVsInfoBarActionItem actionItem)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Finish(accepted: ((InfoBarButton)actionItem).Text == "Accept", closeFrame: true);
            }

            public void OnClosed(IVsInfoBarUIElement infoBarUIElement) { }

            // Closing the diff tab by hand rejects the change.
            public int OnShow(int fShow)
            {
                if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinClosed) Finish(accepted: false, closeFrame: false);
                return VSConstants.S_OK;
            }
            public int OnMove() => VSConstants.S_OK;
            public int OnSize() => VSConstants.S_OK;
            public int OnDockableChange(int fDockable) => VSConstants.S_OK;

            public void Finish(bool accepted, bool closeFrame)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_done) return;
                _done = true;
                _onDone();
                try
                {
                    if (accepted) WriteAccepted();
                    if (_infoBar != null) { _infoBar.Unadvise(_infoBarCookie); _infoBar.Close(); }
                    if (_frameCookie != 0) (_frame as IVsWindowFrame2)?.Unadvise(_frameCookie);
                    if (closeFrame) _frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                    Result.TrySetResult(accepted);
                }
                catch (Exception ex) { Result.TrySetException(ex); }
                finally { Cleanup(); }
            }

            // Writes the right-hand text, including any edits the user made, to the real file.
            private void WriteAccepted()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = (DTE)Package.GetGlobalService(typeof(DTE));
                foreach (Document d in dte.Documents)
                    if (string.Equals(d.FullName, _tempNew, StringComparison.OrdinalIgnoreCase) && !d.Saved) { d.Save(); break; }

                var text = File.ReadAllText(_tempNew);
                var dir = Path.GetDirectoryName(_newPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var keepBom = File.Exists(_newPath) && HasUtf8Bom(_newPath);
                File.WriteAllText(_newPath, text, new UTF8Encoding(keepBom));
            }

            private static bool HasUtf8Bom(string path)
            {
                using (var s = File.OpenRead(path))
                {
                    var b = new byte[3];
                    return s.Read(b, 0, 3) == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
                }
            }

            public void Cleanup()
            {
                try { Directory.Delete(_tempDir, true); } catch (Exception) { /* still open: leave in %TEMP% */ }
            }
        }
    }
}
