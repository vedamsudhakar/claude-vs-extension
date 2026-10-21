using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Bridge
{
    internal sealed class DelegateTool : ITool
    {
        private readonly Func<JObject, Task<string>> _invoke;
        public string Name { get; }
        public string Description { get; }
        public JObject InputSchema { get; }

        public DelegateTool(string name, string description, JObject schema, Func<JObject, Task<string>> invoke)
        {
            Name = name; Description = description; InputSchema = schema; _invoke = invoke;
        }

        public Task<string> InvokeAsync(JObject arguments) => _invoke(arguments);
    }

    /// <summary>Editor-facing MCP tools. All DTE access is marshalled to the UI thread.</summary>
    internal static class EditorTools
    {
        public static IEnumerable<ITool> Create()
        {
            yield return new DelegateTool("openFile", "Open a file in the editor and optionally select a range of text.",
                Schema(("filePath", "string", true), ("preview", "boolean", false), ("startText", "string", false),
                       ("endText", "string", false), ("selectToEndOfLine", "boolean", false), ("makeFrontmost", "boolean", false)),
                OpenFileAsync);
            yield return new DelegateTool("getOpenEditors", "Get information about currently open editors.", Schema(), GetOpenEditorsAsync);
            yield return new DelegateTool("getWorkspaceFolders", "Get all workspace folders currently open.", Schema(), GetWorkspaceFoldersAsync);
            yield return new DelegateTool("getCurrentSelection", "Get the current text selection in the active editor.", Schema(), GetSelectionAsync);
            yield return new DelegateTool("getLatestSelection", "Get the most recent text selection.", Schema(), GetSelectionAsync);
            yield return new DelegateTool("checkDocumentDirty", "Check whether a document has unsaved changes.",
                Schema(("filePath", "string", true)), CheckDirtyAsync);
            yield return new DelegateTool("saveDocument", "Save a document with unsaved changes.",
                Schema(("filePath", "string", true)), SaveDocumentAsync);
        }

        private static JObject Schema(params (string name, string type, bool required)[] props)
        {
            var p = new JObject(); var req = new JArray();
            foreach (var (name, type, required) in props)
            {
                p[name] = new JObject { ["type"] = type };
                if (required) req.Add(name);
            }
            return new JObject { ["type"] = "object", ["properties"] = p, ["required"] = req, ["additionalProperties"] = false };
        }

        private static async Task<DTE> DteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return (DTE)Package.GetGlobalService(typeof(DTE));
        }

        private static string Json(JObject o) => o.ToString(Newtonsoft.Json.Formatting.None);

        private static string FullPath(string p) => Path.GetFullPath(p);

        private static Document FindOpen(DTE dte, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var full = FullPath(path);
            foreach (Document d in dte.Documents)
                if (string.Equals(d.FullName, full, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        private static async Task<string> OpenFileAsync(JObject a)
        {
            var dte = await DteAsync();
            var path = FullPath((string)a["filePath"] ?? throw new ArgumentException("filePath is required"));
            if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);

            var window = dte.ItemOperations.OpenFile(path);
            var doc = window.Document;
            var td = doc?.Object("TextDocument") as TextDocument;
            if (td != null && a["startText"] != null)
                SelectRange(td, (string)a["startText"], (string)a["endText"], (bool?)a["selectToEndOfLine"] ?? false);
            if ((bool?)a["makeFrontmost"] ?? true) { window.Activate(); return "Opened file: " + path; }

            return Json(new JObject
            {
                ["success"] = true, ["filePath"] = path, ["languageId"] = doc?.Language,
                ["lineCount"] = td?.EndPoint.Line,
            });
        }

        // Selects the span found by startText / endText (see TextLocator).
        private static void SelectRange(TextDocument td, string startText, string endText, bool toEol)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var all = td.StartPoint.CreateEditPoint().GetText(td.EndPoint);
            if (!TextLocator.TryFindRange(all, startText, endText, toEol, out var s, out var e)) return;
            TextLocator.LineCol(all, s, out var sl, out var sc);
            TextLocator.LineCol(all, e, out var el, out var ec);
            var sel = td.Selection;
            sel.MoveToLineAndOffset(sl, sc);
            sel.MoveToLineAndOffset(el, ec, true);
        }

        private static async Task<string> GetOpenEditorsAsync(JObject _)
        {
            var dte = await DteAsync();
            var tabs = new JArray();
            var active = dte.ActiveDocument?.FullName;
            foreach (Document d in dte.Documents)
            {
                tabs.Add(new JObject
                {
                    ["uri"] = new Uri(d.FullName).AbsoluteUri,
                    ["isActive"] = string.Equals(d.FullName, active, StringComparison.OrdinalIgnoreCase),
                    ["label"] = d.Name,
                    ["languageId"] = d.Language,
                    ["isDirty"] = !d.Saved,
                });
            }
            return Json(new JObject { ["tabs"] = tabs });
        }

        public static async Task<string[]> WorkspaceFoldersAsync()
        {
            var root = await WorkspaceLocator.GetRootAsync();
            return root != null ? new[] { root } : new string[0];
        }

        private static async Task<string> GetWorkspaceFoldersAsync(JObject _)
        {
            var folders = await WorkspaceFoldersAsync();
            var arr = new JArray();
            foreach (var f in folders)
                arr.Add(new JObject { ["name"] = Path.GetFileName(f), ["uri"] = new Uri(f).AbsoluteUri, ["path"] = f });
            return Json(new JObject
            {
                ["success"] = true, ["folders"] = arr, ["rootPath"] = folders.Length > 0 ? folders[0] : null,
            });
        }

        private static async Task<string> GetSelectionAsync(JObject _)
        {
            var dte = await DteAsync();
            var doc = dte.ActiveDocument;
            var sel = doc?.Selection as TextSelection;
            if (sel == null) return Json(new JObject { ["success"] = false, ["message"] = "No active editor found" });
            return Json(new JObject
            {
                ["success"] = true,
                ["text"] = sel.Text,
                ["filePath"] = doc.FullName,
                ["fileUrl"] = new Uri(doc.FullName).AbsoluteUri,
                ["selection"] = SelectionRange(sel),
            });
        }

        public static JObject SelectionRange(TextSelection sel)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // LSP-style: 0-based line / character.
            return new JObject
            {
                ["start"] = new JObject { ["line"] = sel.TopPoint.Line - 1, ["character"] = sel.TopPoint.LineCharOffset - 1 },
                ["end"] = new JObject { ["line"] = sel.BottomPoint.Line - 1, ["character"] = sel.BottomPoint.LineCharOffset - 1 },
                ["isEmpty"] = sel.IsEmpty,
            };
        }

        private static async Task<string> CheckDirtyAsync(JObject a)
        {
            var dte = await DteAsync();
            var path = FullPath((string)a["filePath"]);
            var d = FindOpen(dte, path);
            if (d == null) return Json(new JObject { ["success"] = false, ["message"] = "Document not open: " + path });
            return Json(new JObject { ["success"] = true, ["filePath"] = path, ["isDirty"] = !d.Saved, ["isUntitled"] = !File.Exists(path) });
        }

        private static async Task<string> SaveDocumentAsync(JObject a)
        {
            var dte = await DteAsync();
            var path = FullPath((string)a["filePath"]);
            var d = FindOpen(dte, path);
            if (d == null) return Json(new JObject { ["success"] = false, ["message"] = "Document not open: " + path });
            var wasDirty = !d.Saved;
            if (wasDirty) d.Save();
            return Json(new JObject
            {
                ["success"] = true, ["filePath"] = path, ["saved"] = wasDirty,
                ["message"] = wasDirty ? "Document saved" : "Document had no unsaved changes",
            });
        }
    }
}
