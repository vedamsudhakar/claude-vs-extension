using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Bridge
{
    /// <summary>Reads the Error List so Claude sees compiler and analyzer diagnostics.</summary>
    internal static class DiagnosticsTool
    {
        public static ITool Create() => new DelegateTool("getDiagnostics",
            "Get language diagnostics from VS (Error List). Optionally pass a file uri.",
            new JObject { ["type"] = "object", ["properties"] = new JObject { ["uri"] = new JObject { ["type"] = "string" } } },
            GetDiagnosticsAsync);

        private static async Task<string> GetDiagnosticsAsync(JObject a)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var filter = (string)a["uri"];
            string filterPath = null;
            if (!string.IsNullOrEmpty(filter))
                filterPath = filter.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(filter).LocalPath : filter;

            var list = Package.GetGlobalService(typeof(SVsErrorList)) as IErrorList;
            var byFile = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            if (list?.TableControl != null)
            {
                foreach (var entry in list.TableControl.Entries)
                {
                    if (!entry.TryGetValue(StandardTableKeyNames.DocumentName, out string path) || string.IsNullOrEmpty(path)) continue;
                    if (filterPath != null && !string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(filterPath), StringComparison.OrdinalIgnoreCase)) continue;

                    entry.TryGetValue(StandardTableKeyNames.Text, out string text);
                    entry.TryGetValue(StandardTableKeyNames.Line, out int line);      // 0-based
                    entry.TryGetValue(StandardTableKeyNames.Column, out int column);  // 0-based
                    entry.TryGetValue(StandardTableKeyNames.ErrorSeverity, out __VSERRORCATEGORY sev);
                    entry.TryGetValue(StandardTableKeyNames.BuildTool, out string tool);
                    entry.TryGetValue(StandardTableKeyNames.ErrorCode, out string code);

                    if (!byFile.TryGetValue(path, out var arr)) byFile[path] = arr = new JArray();
                    arr.Add(new JObject
                    {
                        ["message"] = string.IsNullOrEmpty(code) ? text : code + ": " + text,
                        ["severity"] = sev == __VSERRORCATEGORY.EC_ERROR ? "Error" : sev == __VSERRORCATEGORY.EC_WARNING ? "Warning" : "Information",
                        ["range"] = new JObject
                        {
                            ["start"] = new JObject { ["line"] = line, ["character"] = column },
                            ["end"] = new JObject { ["line"] = line, ["character"] = column },
                        },
                        ["source"] = tool,
                    });
                }
            }
            var result = new JArray(byFile.Select(kv => new JObject
            {
                ["uri"] = SafeUri(kv.Key),
                ["diagnostics"] = kv.Value,
            }));
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string SafeUri(string path)
        {
            try { return new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri; }
            catch (Exception) { return path; }
        }
    }
}
