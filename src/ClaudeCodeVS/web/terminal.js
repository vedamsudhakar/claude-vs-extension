// Glue between xterm.js and the VS host. Messages to host: ready, input, resize, openLink, openFile, title, clipboard.
// Messages from host: output, theme, focus.
(function () {
  const host = window.chrome.webview;
  const post = (m) => host.postMessage(m);

  const term = new Terminal({
    cursorBlink: true,
    fontFamily: '"Cascadia Mono", Consolas, monospace',
    fontSize: 13,
    scrollback: 10000,
    allowProposedApi: false,
    macOptionIsMeta: false,
    theme: { background: '#1e1e1e', foreground: '#d4d4d4' },
    // Hyperlinks the CLI emits itself (OSC 8), e.g. file paths that wrap over several rows. xterm's default handler
    // only opens http(s) after a confirm(); route file:// to the VS editor and http(s) to the browser.
    linkHandler: {
      allowNonHttpProtocols: true,
      activate: (event, uri) => {
        try {
          const u = new URL(uri);
          if (u.protocol === 'http:' || u.protocol === 'https:') { post({ t: 'openLink', url: uri }); return; }
          if (u.protocol !== 'file:') return;
          let p = decodeURIComponent(u.pathname);
          if (/^\/[A-Za-z]:/.test(p)) p = p.slice(1);                       // /D:/x -> D:/x
          if (u.host) p = '//' + u.host + p;                                // file://server/share/x -> UNC
          const line = /^#L?(\d+)/i.exec(u.hash || '');
          post({ t: 'openFile', ref: p.replace(/\//g, '\\') + (line ? ':' + line[1] : '') });
        } catch (_) { /* malformed link */ }
      },
    },
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  // Only http(s) links are forwarded to the host, which opens them in the default browser.
  term.loadAddon(new WebLinksAddon.WebLinksAddon((e, uri) => post({ t: 'openLink', url: uri })));
  term.open(document.getElementById('term'));

  // File links: "src/Foo.cs", "src\Foo.cs:42", "C:\x\Foo.cs:42:7", "Foo.cs(42,7)". Clicking asks the host to open the
  // file in the VS editor (at the line). The host checks the file exists, so a false positive is simply ignored.
  const EXT = 'cs|csx|vb|fs|cpp|cc|c|h|hpp|cshtml|razor|xaml|csproj|vbproj|fsproj|vcxproj|props|targets|sln|slnx|json|xml|config|resx|' +
              'ts|tsx|js|jsx|mjs|css|scss|html|htm|py|java|go|rs|sql|ps1|psm1|bat|cmd|sh|yml|yaml|toml|ini|md|txt|csv|log|editorconfig';
  const FILE_RE = new RegExp(
    '(?<![\\w/\\\\:.@~-])' +                                   // must start a token (not mid-URL or mid-word)
    '(?:[A-Za-z]:[\\\\/]|\\.{1,2}[\\\\/])?' +                   // optional drive or ./ ../
    '[\\w.@~-]+(?:[\\\\/][\\w.@~-]+)*\\.(?:' + EXT + ')' +      // path ending in a known extension
    '(?::\\d+(?::\\d+)?|\\(\\d+(?:,\\d+)?\\))?(?![\\w])', 'gi'); // optional :line[:col] or (line[,col])
  term.registerLinkProvider({
    provideLinks(y, callback) {
      const line = term.buffer.active.getLine(y - 1);
      if (!line) { callback(undefined); return; }
      const text = line.translateToString(true);
      const links = [];
      FILE_RE.lastIndex = 0;
      let m;
      while ((m = FILE_RE.exec(text)) !== null) {
        const ref = m[0];
        links.push({
          range: { start: { x: m.index + 1, y }, end: { x: m.index + ref.length, y } },
          text: ref,
          activate: () => post({ t: 'openFile', ref }),
        });
      }
      callback(links.length ? links : undefined);
    },
  });

  // Ctrl+C copies when text is selected; otherwise it is sent to the process as an interrupt.
  // Ctrl+V pastes: xterm would otherwise send ^V (0x16) and cancel the keydown, so the browser never fires 'paste'.
  // Returning false lets the default action run and xterm's paste handler delivers the text (bracketed if enabled).
  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown' || !e.ctrlKey || e.shiftKey || e.altKey || e.metaKey) return true;
    const k = e.key.toLowerCase();
    if (k === 'c' && term.hasSelection()) {
      navigator.clipboard.writeText(term.getSelection());
      term.clearSelection();
      return false;
    }
    return k !== 'v';
  });

  // OSC 52 ("ESC ]52;c;<base64> BEL"): Claude copies its own mouse selection this way. xterm ignores it unless handled;
  // the host sets the Windows clipboard (navigator.clipboard needs page focus and a user gesture). "?" is a read query.
  // Only honoured shortly after a mouse release or keypress in the terminal, so text that merely scrolls past
  // (a cat'ed file, a log, fetched content) cannot silently replace the clipboard.
  const OSC52_WINDOW_MS = 2000;
  let lastGesture = 0;
  const termEl = document.getElementById('term');
  const markGesture = () => { lastGesture = Date.now(); };
  termEl.addEventListener('mouseup', markGesture, true);
  termEl.addEventListener('keydown', markGesture, true);
  term.parser.registerOscHandler(52, (data) => {
    const b64 = data.slice(data.indexOf(';') + 1);
    if (b64 === '?' || Date.now() - lastGesture > OSC52_WINDOW_MS) return true;
    try {
      const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
      post({ t: 'clipboard', text: new TextDecoder().decode(bytes) });
    } catch (_) { /* malformed base64 */ }
    return true;
  });

  term.onData((d) => post({ t: 'input', d }));
  term.onResize((s) => post({ t: 'resize', cols: s.cols, rows: s.rows }));
  // Terminal title set by the CLI (OSC 0/2); the host shows it as the session tab name.
  term.onTitleChange((title) => post({ t: 'title', title }));

  const refit = () => { try { fit.fit(); } catch (_) { /* hidden */ } };
  new ResizeObserver(refit).observe(document.getElementById('term'));

  host.addEventListener('message', (e) => {
    const m = e.data;
    if (m.t === 'output') term.write(m.d);
    else if (m.t === 'focus') term.focus();
    else if (m.t === 'theme') {
      term.options.theme = { background: m.bg, foreground: m.fg, cursor: m.fg, selectionBackground: m.sel };
      document.body.style.background = m.bg;
      if (m.fontSize && term.options.fontSize !== m.fontSize) { term.options.fontSize = m.fontSize; refit(); }
    }
  });

  refit();
  post({ t: 'ready', cols: term.cols, rows: term.rows });
})();
