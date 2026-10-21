using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Bridge
{
    /// <summary>
    /// Pushes selection_changed to Claude when the active editor's selection moves. Polls on the UI thread:
    /// DTE exposes no selection-changed event, and a poll avoids the MEF text-view listener machinery.
    /// </summary>
    internal sealed class SelectionTracker : IDisposable
    {
        private readonly Func<string, Task> _broadcast;
        private readonly DispatcherTimer _timer;
        private string _lastKey;

        public SelectionTracker(Func<string, Task> broadcast)
        {
            _broadcast = broadcast;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += (s, e) => Poll();
            _timer.Start();
        }

        private void Poll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var p = Current();
                if (p == null) return;
                var key = p["filePath"] + "|" + p["selection"].ToString(Newtonsoft.Json.Formatting.None) + "|" + ((string)p["text"]).Length;
                if (key == _lastKey) return;
                _lastKey = key;
                _ = _broadcast(McpServer.Notification("selection_changed", p));
            }
            catch (Exception) { /* editor mid-transition; try again next tick */ }
        }

        /// <summary>Selection payload for the active text editor, or null.</summary>
        public static JObject Current()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (DTE)Package.GetGlobalService(typeof(DTE));
            var doc = dte?.ActiveDocument;
            if (doc == null || !(doc.Selection is TextSelection sel) || string.IsNullOrEmpty(doc.FullName)) return null;
            return new JObject
            {
                ["text"] = sel.Text ?? "",
                ["filePath"] = doc.FullName,
                ["fileUrl"] = new Uri(doc.FullName).AbsoluteUri,
                ["selection"] = EditorTools.SelectionRange(sel),
            };
        }

        public static JObject AtMention()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = (DTE)Package.GetGlobalService(typeof(DTE));
            var doc = dte?.ActiveDocument;
            if (doc == null || string.IsNullOrEmpty(doc.FullName)) return null;
            var sel = doc.Selection as TextSelection;
            return new JObject
            {
                ["filePath"] = doc.FullName,
                ["lineStart"] = sel != null ? sel.TopPoint.Line - 1 : 0,
                ["lineEnd"] = sel != null ? sel.BottomPoint.Line - 1 : 0,
            };
        }

        public void Dispose() => _timer.Stop();
    }
}
