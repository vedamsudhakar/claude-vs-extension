using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClaudeCodeVS.Options;

namespace ClaudeCodeVS.Terminal
{
    /// <summary>Builds the command lines used to start Claude in the docked terminal and in an external window.</summary>
    internal static class ShellLauncher
    {
        /// <summary>Command line for the docked ConPTY session (environment is passed separately).</summary>
        public static string DockedCommandLine(GeneralOptions o)
        {
            var claude = o?.BuildClaudeCommandLine() ?? "claude";
            return o?.Shell == ShellKind.PowerShell
                ? "powershell.exe -NoLogo -NoExit -Command " + claude
                : "cmd.exe /k " + claude;
        }

        /// <summary>Starts Claude in an external terminal window with the given environment.</summary>
        public static void LaunchExternal(GeneralOptions o, string dir, IDictionary<string, string> env)
        {
            var claude = o?.BuildClaudeCommandLine() ?? "claude";
            var ps = o?.Shell == ShellKind.PowerShell;

            // Env vars are set inside the new shell: wt.exe may hand the command to an existing Terminal process,
            // which would not inherit this process's environment.
            string inner, shellExe, shellArgs;
            if (ps)
            {
                var sets = string.Concat(env.Select(kv => "$env:" + kv.Key + "='" + kv.Value.Replace("'", "''") + "'; "));
                inner = sets + claude;
                shellExe = "powershell.exe";
                shellArgs = "-NoLogo -NoExit -Command \"" + inner.Replace("\"", "\\\"") + "\"";
            }
            else
            {
                var sets = string.Concat(env.Select(kv => "set " + kv.Key + "=" + kv.Value + "&& "));
                inner = sets + claude;
                shellExe = "cmd.exe";
                shellArgs = "/k \"" + inner + "\"";
            }

            var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            var useWt = (o?.ExternalTerminal ?? ExternalTerminalKind.Auto) != ExternalTerminalKind.ConsoleWindow && File.Exists(wt);
            var psi = useWt
                ? new ProcessStartInfo(wt, "-d \"" + dir + "\" " + shellExe + " " + shellArgs)
                : new ProcessStartInfo(shellExe, shellArgs) { WorkingDirectory = dir };
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
    }
}
