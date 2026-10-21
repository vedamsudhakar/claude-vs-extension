using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCodeVS.Terminal
{
    /// <summary>
    /// Runs a command attached to a Windows pseudo console (ConPTY). The child and everything it spawns is placed in a
    /// kill-on-close job object so nothing outlives Visual Studio.
    /// </summary>
    internal sealed class ConPtySession : IDisposable
    {
        public event Action<byte[], int> Output;
        public event Action Exited;

        /// <summary>Child exit code once <see cref="Exited"/> has fired.</summary>
        public int? ExitCode { get; private set; }

        private IntPtr _hpc;
        private SafeFileHandle _inWrite, _outRead, _inRead, _outWrite;
        private FileStream _input;
        private IntPtr _process, _job;
        private Thread _reader;
        private int _disposed;

        public static ConPtySession Start(string commandLine, string workingDirectory, IDictionary<string, string> extraEnv, short cols, short rows)
        {
            var s = new ConPtySession();
            try { s.StartCore(commandLine, workingDirectory, extraEnv, cols, rows); }
            catch { s.Dispose(); throw; }
            return s;
        }

        private void StartCore(string commandLine, string cwd, IDictionary<string, string> extraEnv, short cols, short rows)
        {
            SafeFileHandle inRead, outWrite;
            if (!CreatePipe(out inRead, out _inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
            if (!CreatePipe(out _outRead, out outWrite, IntPtr.Zero, 0)) throw new Win32Exception();

            var hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, 0, out _hpc);
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed (needs Windows 10 1809+)");
            // Keep our copies of the pseudo console's ends open for the session lifetime (disposed in Dispose).
            _inRead = inRead;
            _outWrite = outWrite;

            var attrSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            var attrList = Marshal.AllocHGlobal(attrSize);
            var envBlock = IntPtr.Zero;
            try
            {
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize)) throw new Win32Exception();
                if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();

                var si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                si.lpAttributeList = attrList;
                // Without this, a parent whose own stdio is redirected (CI, test hosts, some shells) leaks those
                // handles into the child instead of the pseudo console. Null handles = "use the console".
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;

                envBlock = BuildEnvironmentBlock(extraEnv);
                PROCESS_INFORMATION pi;
                var cmd = new StringBuilder(commandLine);
                if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, envBlock, cwd, ref si, out pi))
                    throw new Win32Exception();

                CloseHandle(pi.hThread);
                _process = pi.hProcess;
                AssignToKillOnCloseJob(_process);
            }
            finally
            {
                if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
                if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            }

            _input = new FileStream(_inWrite, FileAccess.Write, 4096, false);
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ClaudeCode ConPTY reader" };
            _reader.Start();
            new Thread(WaitLoop) { IsBackground = true, Name = "ClaudeCode ConPTY wait" }.Start();
        }

        public void Write(byte[] data)
        {
            if (_disposed != 0 || _input == null) return;
            try { _input.Write(data, 0, data.Length); _input.Flush(); }
            catch (IOException) { /* process gone */ }
        }

        public void Resize(short cols, short rows)
        {
            if (_disposed == 0 && _hpc != IntPtr.Zero && cols > 0 && rows > 0)
                ResizePseudoConsole(_hpc, new COORD { X = cols, Y = rows });
        }

        private void ReadLoop()
        {
            var buf = new byte[8192];
            try
            {
                using (var fs = new FileStream(_outRead, FileAccess.Read, 8192, false))
                {
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0) Output?.Invoke(buf, n);
                }
            }
            catch (IOException) { /* pty closed */ }
            catch (ObjectDisposedException) { }
        }

        private void WaitLoop()
        {
            var p = _process;
            if (p == IntPtr.Zero) return;
            WaitForSingleObject(p, 0xFFFFFFFF);
            if (GetExitCodeProcess(p, out var code)) ExitCode = unchecked((int)code);
            if (_disposed != 0) return;
            // ConPTY only flushes its last rendered output when the pseudo console is closed. The reader keeps
            // draining the pipe, so this cannot deadlock; wait for it to hit EOF before announcing the exit.
            CloseConsole();
            _reader?.Join(2000);
            if (_disposed == 0) Exited?.Invoke();
        }

        private void CloseConsole()
        {
            var hpc = Interlocked.Exchange(ref _hpc, IntPtr.Zero);
            if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _input?.Dispose(); } catch (IOException) { }
            _inWrite?.Dispose();
            if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; } // kills the process tree
            CloseConsole();
            _inRead?.Dispose();
            _outWrite?.Dispose();
            if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
        }

        private void AssignToKillOnCloseJob(IntPtr process)
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) return;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ptr, (uint)size);
                AssignProcessToJobObject(_job, process);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        private static IntPtr BuildEnvironmentBlock(IDictionary<string, string> extra)
        {
            var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string)e.Value;
            if (extra != null) foreach (var kv in extra) env[kv.Key] = kv.Value;
            var sb = new StringBuilder();
            foreach (var kv in env) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            sb.Append('\0');
            return Marshal.StringToHGlobalUni(sb.ToString());
        }

        // ---- Win32 -------------------------------------------------------------------------------------------

        private const int STARTF_USESTDHANDLES = 0x00000100;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

        // Blittable on purpose: the strings are always NULL here, and a non-blittable layout gets copied by the marshaler.
        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);
        [DllImport("kernel32.dll")]
        private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
        [DllImport("kernel32.dll")]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr hPC);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
        [DllImport("kernel32.dll")]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);
        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpInfo, uint cbInfoLength);
        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    }
}
