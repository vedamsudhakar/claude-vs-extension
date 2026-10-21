using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS.Terminal
{
    /// <summary>
    /// Session tab strip above up to <see cref="MaxSessions"/> independent Claude terminals. All terminals stay in one
    /// Grid and are switched by Visibility: WebView2 is an HwndHost, so it is never re-parented the way a TabControl would.
    /// </summary>
    internal sealed class ClaudeSessionsControl : UserControl, IDisposable
    {
        public const int MaxSessions = 5;

        private readonly List<ClaudeTerminalControl> _terminals = new List<ClaudeTerminalControl>();
        private readonly Dictionary<ClaudeTerminalControl, Border> _tabs = new Dictionary<ClaudeTerminalControl, Border>();
        private readonly WrapPanel _tabStrip = new WrapPanel(); // wraps to a second row rather than hiding tabs
        private readonly Grid _body = new Grid();
        private readonly Border _addButton;
        private ClaudeTerminalControl _active;

        public ClaudeSessionsControl()
        {
            _addButton = MakeGlyphButton("+", "New Claude session", (s, e) => AddSession(focus: true));
            _addButton.Margin = new Thickness(2, 0, 4, 0);

            var strip = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_addButton, Dock.Right);
            strip.Children.Add(_addButton);
            strip.Children.Add(_tabStrip);
            var stripBorder = new Border { Child = strip, BorderThickness = new Thickness(0, 0, 0, 1) };
            stripBorder.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            stripBorder.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);

            var root = new DockPanel();
            DockPanel.SetDock(stripBorder, Dock.Top);
            root.Children.Add(stripBorder);
            root.Children.Add(_body);
            root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            Content = root;

            AddSession(focus: false); // VS may restore this window at startup; don't pull focus from the editor
        }

        public bool CanAddSession => _terminals.Count < MaxSessions;

        /// <summary>Opens a new session tab and selects it. Returns false when <see cref="MaxSessions"/> are already open.</summary>
        public bool AddSession(bool focus)
        {
            if (!CanAddSession) return false;
            var number = Enumerable.Range(1, MaxSessions).First(n => _terminals.All(t => t.Number != n));
            var term = new ClaudeTerminalControl(number) { Visibility = Visibility.Hidden };
            var index = _terminals.FindIndex(t => t.Number > number);
            if (index < 0) index = _terminals.Count;
            _terminals.Insert(index, term);
            _body.Children.Add(term);
            var tab = MakeTab(term);
            _tabs[term] = tab;
            _tabStrip.Children.Insert(index, tab);
            Select(term, focus);
            return true;
        }

        public void FocusTerminal() => _active?.FocusTerminal();

        // Inactive terminals are Hidden, not Collapsed, so they keep their size and Claude doesn't reflow on every switch.
        private void Select(ClaudeTerminalControl term, bool focus)
        {
            _active = term;
            foreach (var t in _terminals)
            {
                t.Visibility = t == term ? Visibility.Visible : Visibility.Hidden;
                StyleTab(_tabs[t], t == term);
            }
            if (Bridge.IdeBridgeHost.Current != null) Bridge.IdeBridgeHost.Current.ActiveSession = term.Endpoint;
            _addButton.IsEnabled = CanAddSession;
            _addButton.Opacity = CanAddSession ? 1 : 0.35;
            _addButton.ToolTip = CanAddSession ? "New Claude session" : "At most " + MaxSessions + " sessions";
            if (focus) Dispatcher.BeginInvoke(new Action(term.FocusTerminal), DispatcherPriority.Input);
        }

        private void Close(ClaudeTerminalControl term)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (term.IsRunning && VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider,
                    "End Claude session " + term.Number + "? Its process will be stopped.", "Claude Code",
                    OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST) != 1 /* IDOK */)
                return;

            var index = _terminals.IndexOf(term);
            _terminals.Remove(term);
            _tabStrip.Children.Remove(_tabs[term]);
            _tabs.Remove(term);
            _body.Children.Remove(term);
            term.Dispose();

            if (_terminals.Count == 0) { AddSession(focus: true); return; } // the window always has a session
            if (_active == term) Select(_terminals[Math.Min(index, _terminals.Count - 1)], focus: true);
            else Select(_active, focus: true);
        }

        private Border MakeTab(ClaudeTerminalControl term)
        {
            var label = new TextBlock { Text = "Session " + term.Number, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var close = MakeGlyphButton("✕", "End this session", (s, e) => { e.Handled = true; Close(term); });
            TextElement.SetFontSize(close, 10);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(label);
            panel.Children.Add(close);
            var tab = new Border { Child = panel, Padding = new Thickness(8, 3, 4, 3), Cursor = Cursors.Hand, ToolTip = "Claude session " + term.Number };
            term.TitleChanged += () =>
            {
                label.Text = TabText(term.SessionTitle) ?? "Session " + term.Number;
                tab.ToolTip = "Claude session " + term.Number + (term.SessionTitle != null ? ": " + term.SessionTitle : "");
            };
            tab.MouseLeftButtonUp += (s, e) => { if (_active != term) Select(term, focus: true); else term.FocusTerminal(); };
            tab.MouseEnter += (s, e) => { if (_active != term) tab.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowTabMouseOverBackgroundBeginBrushKey); };
            tab.MouseLeave += (s, e) => StyleTab(tab, _active == term);
            return tab;
        }

        private const int MaxTabText = 24;

        private static string TabText(string title) =>
            string.IsNullOrEmpty(title) ? null : title.Length <= MaxTabText ? title : title.Substring(0, MaxTabText - 1).TrimEnd() + "…";

        private static void StyleTab(Border tab, bool selected)
        {
            tab.SetResourceReference(Border.BackgroundProperty,
                selected ? EnvironmentColors.ToolWindowTabSelectedTabBrushKey : EnvironmentColors.ToolWindowBackgroundBrushKey);
            tab.SetResourceReference(TextElement.ForegroundProperty,
                selected ? EnvironmentColors.ToolWindowTabSelectedTextBrushKey : EnvironmentColors.ToolWindowTabTextBrushKey);
        }

        private static Border MakeGlyphButton(string glyph, string tip, MouseButtonEventHandler click)
        {
            var b = new Border
            {
                Child = new TextBlock { Text = glyph, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                Padding = new Thickness(5, 1, 5, 1), Cursor = Cursors.Hand, ToolTip = tip,
                Background = System.Windows.Media.Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
            };
            b.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            b.MouseLeftButtonUp += (s, e) => { if (b.IsEnabled) click(s, e); };
            b.MouseEnter += (s, e) => b.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowButtonHoverActiveBrushKey);
            b.MouseLeave += (s, e) => b.Background = System.Windows.Media.Brushes.Transparent;
            return b;
        }

        public void Dispose()
        {
            foreach (var t in _terminals) t.Dispose();
            _terminals.Clear();
            _tabs.Clear();
        }
    }
}
