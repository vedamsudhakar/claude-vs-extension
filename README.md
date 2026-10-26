# Claude Code for Visual Studio

A Visual Studio 2026 extension that runs the Claude Code CLI inside the IDE and connects it to the editor, built
as a self-contained extension, so no marketplace extension is needed. It has no telemetry and no auto-update, and makes no outbound network
calls of its own.

## Features
| Feature | How to use |
|---|---|
| Docked Claude Code terminal | **Tools > Claude Code**, or `Ctrl+Alt+Shift+C` |
| External terminal (Windows Terminal) | **Tools > Claude Code (External Terminal)**, or `Ctrl+Alt+Shift+E` |
| Multiple sessions (up to 5), shown as tabs in the Claude Code window; each runs independently | **+** in the tab strip, or **Tools > Claude Code: New Session**; **✕** on a tab ends that session |
| Settings (claude path, extra arguments, shell, external terminal, font size) | **Tools > Options > Claude Code** |
| Click a file path in the terminal (`src/Foo.cs:42`, `Foo.cs(12,5)`, `C:\x\Foo.cs`) | opens it in the VS editor at that line |
| Add a file to Claude | Solution Explorer context menu **Add File to Claude** |
| Activity log | Output window > **Claude Code** pane |
| Send file + selected lines to Claude as an @-mention (goes to the selected session tab) | `Ctrl+Alt+K`, or editor context menu **Send Selection to Claude** |
| Claude opens files / ranges in the editor | automatic (`openFile`) |
| Claude's edits appear in VS's diff viewer with **Accept / Reject** (you can edit the right side first) | automatic (`openDiff`) |
| Claude sees your current selection and open files | automatic (`selection_changed`, `getOpenEditors`, ...) |
| Claude reads the Error List | automatic (`getDiagnostics`) |

Claude starts in the open solution folder, or the Open Folder workspace, falling back to the open project and then the
active document's folder.

## Build
Requires VS 2026 (or 2022 17.x) and the .NET SDK. The *Visual Studio extension development* workload is **not** required;
the VSIX is packaged with the `Microsoft.VSSDK.BuildTools` NuGet package.

```
MSBuild ClaudeCodeVS.sln -restore -p:Configuration=Release
dotnet test tests\ClaudeCodeVS.Tests
```
Output: `src\ClaudeCodeVS\bin\Release\net48\ClaudeCodeVS.vsix`. Install with VS closed:
`.\install.ps1` (newest built VSIX), `.\install.ps1 -Build` (rebuild Release first) or `.\install.ps1 -Uninstall`.
It installs into every compatible VS instance via `VSIXInstaller.exe /quiet`.

`nuget.config` restricts restore to nuget.org because other configured package feeds can be unreachable. Delete it to use
your own feeds.

## How it connects to Claude Code
The extension starts a WebSocket MCP server and writes `~/.claude/ide/<port>.lock`, the same mechanism the official
IDE integrations use. Processes it launches get `CLAUDE_CODE_SSE_PORT` and `ENABLE_IDE_INTEGRATION=true`, so
`claude` connects to this IDE directly; `/ide` shows it. Each docked session tab has its own server and lock file
(listed by `/ide` as "Visual Studio (Session N)"), so @-mentions and diff tabs stay with the session they belong to;
external terminals use the default "Visual Studio" entry.

**This protocol is not officially documented by Anthropic.** It was reconstructed from public implementations
(for example `coder/claudecode.nvim` `PROTOCOL.md`). A Claude Code update could change it; all of it lives in
`src/ClaudeCodeVS/Bridge`.

## Security notes for reviewers
* Server binds to **127.0.0.1 only**, random port; every connection must present the 128-bit lock-file token
  (`x-claude-code-ide-authorization`, constant-time compare) and any request carrying an `Origin` header (browser) is
  rejected. Covered by tests.
* The lock file contains the token and lives under the user's profile (`~/.claude/ide`); it is removed on shutdown and
  stale files from dead VS processes are cleaned up.
* The extension executes only `claude` (via `cmd.exe`) and opens `http(s)` links clicked in the terminal in the default
  browser. It writes files only when the user clicks **Accept** in the diff viewer (target = the path Claude supplied).
* Third-party code: **xterm.js 5.5.0**, `addon-fit` 0.10.0, `addon-web-links` 0.11.0, `addon-unicode11` 0.8.0 (MIT), bundled in
  `src/ClaudeCodeVS/web`, loaded from the extension folder via a WebView2 virtual host with a strict CSP; no CDN.
  Microsoft packages: VS SDK, WebView2 (runtime supplied by VS/Windows).
* The terminal child (and everything it spawns) is placed in a kill-on-close job object, so nothing outlives VS.

## Layout
```
src/ClaudeCodeVS/Bridge     WebSocket server, MCP dispatcher, lock file, editor/diff/diagnostics tools
src/ClaudeCodeVS/Terminal   ConPTY host, WebView2 + xterm.js control, tool window
src/ClaudeCodeVS/Commands   menu commands
src/ClaudeCodeVS/web        bundled xterm.js front-end
tests/ClaudeCodeVS.Tests    bridge protocol + ConPTY tests (no VS needed)
```
