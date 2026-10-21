using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Bridge
{
    /// <summary>Writes ~/.claude/ide/&lt;port&gt;.lock so the Claude CLI can discover this IDE instance.</summary>
    internal sealed class LockFile
    {
        private readonly string _path;
        private readonly int _port;
        private readonly string _authToken;
        private readonly string _ideName;

        /// <param name="ideName">Shown by /ide; must start with "Visual Studio" so <see cref="CleanStale"/> recognises it.</param>
        public LockFile(int port, string authToken, string ideName = "Visual Studio")
        {
            _port = port;
            _authToken = authToken;
            _ideName = ideName;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ide");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, port + ".lock");
        }

        public string AuthToken => _authToken;

        public void Write(string[] workspaceFolders)
        {
            var json = new JObject
            {
                ["pid"] = Process.GetCurrentProcess().Id,
                ["workspaceFolders"] = new JArray(workspaceFolders),
                ["ideName"] = _ideName,
                ["transport"] = "ws",
                ["runningInWindows"] = true,
                ["authToken"] = _authToken,
            };
            File.WriteAllText(_path, json.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
        }

        public void Delete()
        {
            try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>Removes lock files left behind by VS processes that no longer exist.</summary>
        public static void CleanStale()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ide");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.lock"))
            {
                try
                {
                    var obj = JObject.Parse(File.ReadAllText(file));
                    if (((string)obj["ideName"])?.StartsWith("Visual Studio", StringComparison.Ordinal) != true) continue;
                    var pid = (int?)obj["pid"];
                    if (pid == null || IsAlive(pid.Value)) continue;
                    File.Delete(file);
                }
                catch (Exception) { /* unreadable or in use: leave it */ }
            }
        }

        private static bool IsAlive(int pid)
        {
            try { Process.GetProcessById(pid); return true; }
            catch (ArgumentException) { return false; }
        }

        // 128 bits from the OS CSPRNG, as 32 lowercase hex chars.
        public static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(32);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
