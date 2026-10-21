using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using ClaudeCodeVS.Terminal;
using Xunit;

namespace ClaudeCodeVS.Tests
{
    public class ConPtyTests
    {
        // ConPTY renders asynchronously and can drop the last frame of a process that exits instantly, so these
        // tests keep the child alive (like a real claude session) and poll the output for the expected text.
        private static bool WaitFor(ConPtySession s, StringBuilder sb, string text, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (sb) if (sb.ToString().Contains(text)) return true;
                Thread.Sleep(50);
            }
            return false;
        }

        private static ConPtySession Start(string cmd, IDictionary<string, string> env, StringBuilder sb)
        {
            var s = ConPtySession.Start(cmd, Environment.CurrentDirectory, env, 100, 30);
            s.Output += (b, n) => { lock (sb) sb.Append(Encoding.UTF8.GetString(b, 0, n)); };
            return s;
        }

        [Fact]
        public void Streams_output_from_a_running_command()
        {
            var sb = new StringBuilder();
            using (var s = Start("cmd.exe /c echo hello-from-pty & ping -n 4 127.0.0.1 >nul", null, sb))
                Assert.True(WaitFor(s, sb, "hello-from-pty"), sb.ToString());
        }

        [Fact]
        public void Passes_extra_environment_to_the_child()
        {
            var sb = new StringBuilder();
            using (var s = Start("cmd.exe /c echo PORT=%CLAUDE_CODE_SSE_PORT%& ping -n 4 127.0.0.1 >nul",
                new Dictionary<string, string> { ["CLAUDE_CODE_SSE_PORT"] = "12345" }, sb))
                Assert.True(WaitFor(s, sb, "PORT=12345"), sb.ToString());
        }

        [Fact]
        public void Input_reaches_the_process()
        {
            var sb = new StringBuilder();
            using (var s = Start("cmd.exe /k", null, sb))
            {
                Assert.True(WaitFor(s, sb, "Microsoft Windows") || true); // banner is optional on some builds
                s.Write(Encoding.UTF8.GetBytes("echo typed-input\r"));
                Assert.True(WaitFor(s, sb, "typed-input"), sb.ToString());
            }
        }

        [Fact]
        public void Resize_does_not_throw_and_session_stays_usable()
        {
            var sb = new StringBuilder();
            using (var s = Start("cmd.exe /k", null, sb))
            {
                s.Resize(80, 24);
                s.Write(Encoding.UTF8.GetBytes("echo after-resize\r"));
                Assert.True(WaitFor(s, sb, "after-resize"), sb.ToString());
            }
        }

        [Fact]
        public void Exited_fires_when_the_child_ends()
        {
            var done = new ManualResetEventSlim();
            using (var s = ConPtySession.Start("cmd.exe /c exit 0", Environment.CurrentDirectory, null, 100, 30))
            {
                s.Exited += () => done.Set();
                Assert.True(done.Wait(10000));
            }
        }

        [Fact]
        public void Dispose_terminates_the_child_process_tree()
        {
            var before = Process.GetProcessesByName("ping").Length;
            var sb = new StringBuilder();
            var s = Start("cmd.exe /c ping -n 60 127.0.0.1 >nul", null, sb);
            Thread.Sleep(1500);
            Assert.True(Process.GetProcessesByName("ping").Length > before, "child ping should be running");
            s.Dispose(); // kill-on-close job object
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000 && Process.GetProcessesByName("ping").Length > before) Thread.Sleep(100);
            Assert.Equal(before, Process.GetProcessesByName("ping").Length);
        }
    }
}
