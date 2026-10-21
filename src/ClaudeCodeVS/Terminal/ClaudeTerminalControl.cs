using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Terminal
{
    /// <summary>xterm.js (in WebView2) bound to a ConPTY session running Claude Code.</summary>
    internal sealed class ClaudeTerminalControl : UserControl, IDisposable
    {
        private const string VirtualHost = "claude.local";

        private readonly WebView2 _web = new WebView2();
        private readonly object _outLock = new object();
        private readonly StringBuilder _pending = new StringBuilder();
        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
        private ConPtySession _session;
        private bool _flushQueued, _exited, _initStarted;
        private short _cols = 120, _rows = 30;
        private string _cwd;

        public ClaudeTerminalControl()
        {
            Content = _web;
            _web.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCodeVS", "WebView2"),
            };
            Loaded += async (s, e) => await InitAsync();
            VSColorTheme.ThemeChanged += OnThemeChanged;
            Options.GeneralOptions.Applied += OnOptionsApplied;
        }

        private async System.Threading.Tasks.Task InitAsync()
        {
            if (_initStarted) return;
            _initStarted = true;
            try
            {
                await _web.EnsureCoreWebView2Async();
                var core = _web.CoreWebView2;
                var webDir = Path.Combine(Path.GetDirectoryName(typeof(ClaudeTerminalControl).Assembly.Location), "web");
                core.SetVirtualHostNameToFolderMapping(VirtualHost, webDir, CoreWebView2HostResourceAccessKind.DenyCors);
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("https://" + VirtualHost + "/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
                core.NewWindowRequested += (s, e) => { e.Handled = true; OpenLink(e.Uri); };
                core.WebMessageReceived += OnWebMessage;
                core.Navigate("https://" + VirtualHost + "/index.html");
            }
            catch (Exception ex)
            {
                _initStarted = false;
                Content = new TextBlock
                {
                    Text = "Claude Code terminal could not start (WebView2 unavailable?):\n" + ex.Message,
                    Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap,
                };
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var m = JObject.Parse(e.WebMessageAsJson);
            switch ((string)m["t"])
            {
                case "ready":
                    _cols = (short)Math.Max(20, (int)m["cols"]); _rows = (short)Math.Max(5, (int)m["rows"]);
                    PostTheme();
                    if (_session == null) StartSession();
                    break;
                case "input":
                    var d = (string)m["d"];
                    if (_exited) { if (d.IndexOf('\r') >= 0) StartSession(); break; }
                    _session?.Write(Encoding.UTF8.GetBytes(d));
                    break;
                case "resize":
                    _cols = (short)(int)m["cols"]; _rows = (short)(int)m["rows"];
                    _session?.Resize(_cols, _rows);
                    break;
                case "openLink":
                    OpenLink((string)m["url"]);
                    break;
                case "openFile":
                    OpenFileReference((string)m["ref"]);
                    break;
            }
        }

        private void StartSession()
        {
            _session?.Dispose();
            _exited = false;
            var cwd = Bridge.WorkspaceLocator.GetRoot() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _cwd = cwd;
            var env = new Dictionary<string, string> { ["ENABLE_IDE_INTEGRATION"] = "true", ["COLORTERM"] = "truecolor", ["TERM_PROGRAM"] = "VisualStudio" };
            var host = Bridge.IdeBridgeHost.Current;
            if (host != null) env["CLAUDE_CODE_SSE_PORT"] = host.Port.ToString();
            try
            {
                var session = ConPtySession.Start(ShellLauncher.DockedCommandLine(ClaudeCodePackage.Options), cwd, env, _cols, _rows);
                session.Output += OnOutput;
                session.Exited += () => Dispatcher.BeginInvoke(new Action(OnExited));
                _session = session;
            }
            catch (Exception ex)
            {
                _exited = true;
                Post(new JObject { ["t"] = "output", ["d"] = "\r\nCould not start Claude Code: " + ex.Message + "\r\nPress Enter to retry.\r\n" });
            }
        }

        private void OnExited()
        {
            _exited = true;
            Post(new JObject { ["t"] = "output", ["d"] = "\r\n\x1b[2m[Session ended. Press Enter to start a new one.]\x1b[0m\r\n" });
        }

        // Called on the reader thread; coalesce chunks so a burst of output is one message.
        private void OnOutput(byte[] buf, int n)
        {
            lock (_outLock)
            {
                var chars = new char[_decoder.GetCharCount(buf, 0, n)];
                var count = _decoder.GetChars(buf, 0, n, chars, 0);
                _pending.Append(chars, 0, count);
                if (_flushQueued) return;
                _flushQueued = true;
            }
            Dispatcher.BeginInvoke(new Action(Flush), DispatcherPriority.Background);
        }

        private void Flush()
        {
            string text;
            lock (_outLock) { text = _pending.ToString(); _pending.Clear(); _flushQueued = false; }
            if (text.Length > 0) Post(new JObject { ["t"] = "output", ["d"] = text });
        }

        private void Post(JObject m)
        {
            try { _web.CoreWebView2?.PostWebMessageAsJson(m.ToString(Newtonsoft.Json.Formatting.None)); }
            catch (Exception) { /* control closing */ }
        }

        private void OnThemeChanged(ThemeChangedEventArgs e) => Dispatcher.BeginInvoke(new Action(PostTheme));

        private void OnOptionsApplied() => Dispatcher.BeginInvoke(new Action(PostTheme));

        private void PostTheme()
        {
            var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var fg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            var sel = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            Post(new JObject
            {
                ["t"] = "theme", ["bg"] = Hex(bg.R, bg.G, bg.B), ["fg"] = Hex(fg.R, fg.G, fg.B),
                ["fontSize"] = Math.Max(8, Math.Min(32, ClaudeCodePackage.Options?.FontSize ?? 13)),
                ["sel"] = "rgba(" + sel.R + "," + sel.G + "," + sel.B + ",0.3)",
            });
        }

        private static string Hex(byte r, byte g, byte b) => "#" + r.ToString("x2") + g.ToString("x2") + b.ToString("x2");

        private static readonly System.Text.RegularExpressions.Regex FileRef = new System.Text.RegularExpressions.Regex(
            @"^(?<p>.+?)(?::(?<l>\d+)(?::(?<c>\d+))?|\((?<l>\d+)(?:,(?<c>\d+))?\))?$");

        // A file path clicked in the terminal: resolve it against the session folder / workspace and open it in the
        // VS editor (text view, so project and solution files open as text rather than being loaded).
        private void OpenFileReference(string reference)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var m = FileRef.Match(reference ?? "");
                if (!m.Success) return;
                var path = ResolveFile(m.Groups["p"].Value);
                if (path == null) { Log.Info("File not found: " + reference); return; }

                var dte = (EnvDTE.DTE)Package.GetGlobalService(typeof(EnvDTE.DTE));
                dte.ItemOperations.OpenFile(path, "{7651A703-06E5-11D1-8EBD-00A0C90F26EA}"); // vsViewKindTextView
                if (m.Groups["l"].Success && dte.ActiveDocument?.Selection is EnvDTE.TextSelection sel)
                {
                    var col = m.Groups["c"].Success ? int.Parse(m.Groups["c"].Value) : 1;
                    sel.MoveToLineAndOffset(int.Parse(m.Groups["l"].Value), Math.Max(1, col));
                    sel.SelectLine();
                }
            }
            catch (Exception ex) { Log.Info("Could not open " + reference + ": " + ex.Message); }
        }

        private string ResolveFile(string p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            p = p.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(p)) return File.Exists(p) ? Path.GetFullPath(p) : null;
            foreach (var root in new[] { _cwd, Bridge.WorkspaceLocator.GetRoot() })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var candidate = Path.GetFullPath(Path.Combine(root, p));
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static void OpenLink(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }

        /// <summary>Ends the current process (if any), clears the screen and starts a fresh Claude session.</summary>
        public void RestartSession()
        {
            _session?.Dispose();
            _session = null;
            Post(new JObject { ["t"] = "output", ["d"] = "\x1b[2J\x1b[3J\x1b[H" });
            StartSession();
        }

        public void FocusTerminal()
        {
            _web.Focus();
            Post(new JObject { ["t"] = "focus" });
        }

        public void Dispose()
        {
            VSColorTheme.ThemeChanged -= OnThemeChanged;
            Options.GeneralOptions.Applied -= OnOptionsApplied;
            _session?.Dispose();
            _session = null;
            _web.Dispose();
        }
    }
}
