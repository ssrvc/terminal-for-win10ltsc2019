using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WindowsTerminalLTSC
{
    public static class TuiDetector
    {
        private static readonly HashSet<string> AlwaysTuiCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vim", "nvim", "vi", "nano", "micro", "edit",
            "htop", "btop", "top", "less", "more",
            "diskpart", "ftp", "telnet", "wsl", "bash"
        };

        private static readonly HashSet<string> AiCliTuiCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "agy", "claude", "gemini", "codex"
        };

        private static readonly HashSet<string> NonInteractiveAgySubcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "help", "install", "update", "agent", "agents", "models",
            "changelog", "plugin", "plugins", "mcp", "acp", "mic-serve", "remote-control"
        };

        public static string ExtractCommandExeName(string rawCommandLine)
        {
            if (string.IsNullOrWhiteSpace(rawCommandLine))
                return string.Empty;

            string trimmed = rawCommandLine.Trim();
            string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return string.Empty;

            string exe = Path.GetFileName(parts[0].Trim('"', '\''));
            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                exe = Path.GetFileNameWithoutExtension(exe);
            }
            return exe ?? string.Empty;
        }

        public static bool IsAgyInteractiveCommand(string rawCommandLine)
        {
            if (!ShouldRunInTuiMode(rawCommandLine))
                return false;
            string exe = ExtractCommandExeName(rawCommandLine);
            return string.Equals(exe, "agy", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldRunInTuiMode(string rawCommandLine)
        {
            if (string.IsNullOrWhiteSpace(rawCommandLine))
                return false;

            string trimmed = rawCommandLine.Trim();
            // Do not hijack pipelines or redirections
            if (trimmed.IndexOf('|') >= 0 || trimmed.IndexOf('>') >= 0 || trimmed.IndexOf('<') >= 0 || trimmed.IndexOf('&') >= 0)
                return false;

            string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            string exe = ExtractCommandExeName(trimmed);
            if (string.IsNullOrEmpty(exe))
                return false;

            if (AlwaysTuiCommands.Contains(exe))
                return true;

            if (string.Equals(exe, "ssh", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i] == "-V" || parts[i] == "--version" || parts[i] == "-h" || parts[i] == "--help")
                        return false;
                }
                return parts.Length >= 2;
            }

            if (AiCliTuiCommands.Contains(exe))
            {
                for (int i = 1; i < parts.Length; i++)
                {
                    string arg = parts[i];
                    if (string.Equals(arg, "-v", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "-p", StringComparison.Ordinal) ||
                        string.Equals(arg, "--print", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "--prompt", StringComparison.OrdinalIgnoreCase) ||
                        arg.StartsWith("--print=", StringComparison.OrdinalIgnoreCase) ||
                        arg.StartsWith("--prompt=", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    if (i == 1 && !arg.StartsWith("-") && NonInteractiveAgySubcommands.Contains(arg))
                    {
                        return false;
                    }
                }
                return true;
            }

            return false;
        }
    }

    public class AgyResumeInfo
    {
        public string ConversationId { get; set; }
        public string Title { get; set; }
        public string Preview { get; set; }
        public bool ModifiedDuringSession { get; set; }
    }

    public static class AgySessionHelper
    {
        private static readonly Regex ExplicitConvRegex = new Regex(
            @"(?:--conversation|--resume|-r)[=\s]+[""']?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})[""']?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UuidOnlyRegex = new Regex(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static AgyResumeInfo TryGetLatestCliResumeInfo(string rawCommandLine, DateTime sessionStartUtc)
        {
            string explicitId = null;
            if (!string.IsNullOrEmpty(rawCommandLine))
            {
                Match m = ExplicitConvRegex.Match(rawCommandLine);
                if (m.Success)
                    explicitId = m.Groups[1].Value;
            }

            string bestId = null;
            string bestTitle = null;
            string bestPreview = null;
            DateTime bestTimeUtc = DateTime.MinValue;

            var metaTitleById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var metaPreviewById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string cliRoot = Path.Combine(userProfile, ".gemini", "antigravity-cli");

                // 1. Read conversation_metadata.json for titles/previews and cached timestamps
                string metaPath = Path.Combine(cliRoot, "cache", "conversation_metadata.json");
                if (File.Exists(metaPath))
                {
                    string json = File.ReadAllText(metaPath, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                    var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                    if (root != null && root.ContainsKey("conversations"))
                    {
                        var convs = root["conversations"] as Dictionary<string, object>;
                        if (convs != null)
                        {
                            foreach (var kv in convs)
                            {
                                var entry = kv.Value as Dictionary<string, object>;
                                if (entry == null) continue;

                                if (entry.ContainsKey("is_internal") && entry["is_internal"] is bool && (bool)entry["is_internal"])
                                    continue;

                                var summary = entry.ContainsKey("summary") ? entry["summary"] as Dictionary<string, object> : null;
                                if (summary == null) continue;

                                string appDataDir = summary.ContainsKey("AppDataDir") ? (summary["AppDataDir"] as string) : null;
                                if (!string.Equals(appDataDir, "antigravity-cli", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                string id = summary.ContainsKey("ID") ? (summary["ID"] as string) : kv.Key;
                                if (string.IsNullOrWhiteSpace(id)) continue;

                                string title = summary.ContainsKey("Title") ? (summary["Title"] as string) : null;
                                string preview = summary.ContainsKey("Preview") ? (summary["Preview"] as string) : null;
                                if (!string.IsNullOrWhiteSpace(title)) metaTitleById[id] = title;
                                if (!string.IsNullOrWhiteSpace(preview)) metaPreviewById[id] = preview;

                                DateTime modUtc = DateTime.MinValue;
                                string lastModStr = entry.ContainsKey("last_modified_time") ? (entry["last_modified_time"] as string) : null;
                                DateTimeOffset dto;
                                if (!string.IsNullOrEmpty(lastModStr) && DateTimeOffset.TryParse(lastModStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dto))
                                {
                                    modUtc = dto.UtcDateTime;
                                }
                                else
                                {
                                    string updStr = summary.ContainsKey("UpdatedAt") ? (summary["UpdatedAt"] as string) : null;
                                    if (!string.IsNullOrEmpty(updStr) && DateTimeOffset.TryParse(updStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dto))
                                    {
                                        modUtc = dto.UtcDateTime;
                                    }
                                }

                                if (bestId == null || modUtc > bestTimeUtc)
                                {
                                    bestId = id;
                                    bestTitle = title;
                                    bestPreview = preview;
                                    bestTimeUtc = modUtc;
                                }
                            }
                        }
                    }
                }

                // 2. Scan live .gemini\antigravity-cli\conversations\*.db files (updated immediately on every chat turn!)
                string convDbDir = Path.Combine(cliRoot, "conversations");
                if (Directory.Exists(convDbDir))
                {
                    foreach (string file in Directory.GetFiles(convDbDir))
                    {
                        string ext = Path.GetExtension(file);
                        if (!string.Equals(ext, ".db", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(ext, ".pb", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string candidateId = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrEmpty(candidateId) || !UuidOnlyRegex.IsMatch(candidateId))
                            continue;

                        DateTime writeUtc = File.GetLastWriteTimeUtc(file);
                        if (bestId == null || writeUtc > bestTimeUtc)
                        {
                            bestId = candidateId;
                            bestTimeUtc = writeUtc;
                            metaTitleById.TryGetValue(candidateId, out bestTitle);
                            metaPreviewById.TryGetValue(candidateId, out bestPreview);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(explicitId))
                {
                    string expTitle = null;
                    string expPreview = null;
                    metaTitleById.TryGetValue(explicitId, out expTitle);
                    metaPreviewById.TryGetValue(explicitId, out expPreview);
                    return new AgyResumeInfo
                    {
                        ConversationId = explicitId,
                        Title = expTitle,
                        Preview = expPreview,
                        ModifiedDuringSession = true
                    };
                }

                if (!string.IsNullOrEmpty(bestId))
                {
                    return new AgyResumeInfo
                    {
                        ConversationId = bestId,
                        Title = bestTitle,
                        Preview = bestPreview,
                        ModifiedDuringSession = (bestTimeUtc >= sessionStartUtc.AddSeconds(-5))
                    };
                }
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(explicitId))
            {
                return new AgyResumeInfo
                {
                    ConversationId = explicitId,
                    ModifiedDuringSession = true
                };
            }

            return null;
        }
    }

    public class TuiExitResult
    {
        public int ExitCode { get; set; }
        public string FinalConsoleText { get; set; }
        public DateTime SessionStartUtc { get; set; }
    }

    public static class TuiHostWorker
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SMALL_RECT
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD dwSize;
            public COORD dwCursorPosition;
            public ushort wAttributes;
            public SMALL_RECT srWindow;
            public COORD dwMaximumWindowSize;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct CHAR_INFO
        {
            [FieldOffset(0)] public char UnicodeChar;
            [FieldOffset(0)] public byte AsciiChar;
            [FieldOffset(2)] public ushort Attributes;
        }

        [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)]
        private struct INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public int bKeyDown;
            [FieldOffset(8)] public ushort wRepeatCount;
            [FieldOffset(10)] public ushort wVirtualKeyCode;
            [FieldOffset(12)] public ushort wVirtualScanCode;
            [FieldOffset(14)] public char UnicodeChar;
            [FieldOffset(16)] public uint dwControlKeyState;
        }

        [StructLayout(LayoutKind.Explicit, Size = 20)]
        private struct MOUSE_INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public short MouseX;
            [FieldOffset(6)] public short MouseY;
            [FieldOffset(8)] public uint dwButtonState;
            [FieldOffset(12)] public uint dwControlKeyState;
            [FieldOffset(16)] public uint dwEventFlags;
        }

        [StructLayout(LayoutKind.Explicit, Size = 20)]
        private struct SIZE_INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public short X;
            [FieldOffset(6)] public short Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOW
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        private const ushort KEY_EVENT = 0x0001;
        private const ushort MOUSE_EVENT = 0x0002;
        private const ushort WINDOW_BUFFER_SIZE_EVENT = 0x0004;
        private const ushort COMMON_LVB_LEADING_BYTE = 0x0100;
        private const ushort COMMON_LVB_TRAILING_BYTE = 0x0200;
        private const uint ENABLE_MOUSE_INPUT = 0x0010;
        private const uint MOUSE_WHEELED = 0x0004;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
        private const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
        private const short DefaultScrollbackRows = 2000;
        public const int MaxStreamedScrollbackRows = 600;
        private const int MaxConsoleReadChunkRows = 50;
        private const uint STARTF_USESHOWWINDOW = 0x00000001;
        private const uint STARTF_USECOUNTCHARS = 0x00000008;
        private const short SW_HIDE = 0;
        private const uint CREATE_NEW_CONSOLE = 0x00000010;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        private static readonly Regex ValidPipeNameRegex = new Regex(
            @"^wt_ltsc_tui_[0-9a-f]{32}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleCP(uint wCodePageID);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleOutputCP(uint wCodePageID);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, COORD dwSize);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleWindowInfo(IntPtr hConsoleOutput, bool bAbsolute, [In] ref SMALL_RECT lpConsoleWindow);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ReadConsoleOutputW(
            IntPtr hConsoleOutput,
            [Out] CHAR_INFO[] lpBuffer,
            COORD dwBufferSize,
            COORD dwBufferCoord,
            ref SMALL_RECT lpReadRegion);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool WriteConsoleInputW(
            IntPtr hConsoleInput,
            [In] INPUT_RECORD[] lpBuffer,
            uint nLength,
            out uint lpNumberOfEventsWritten);

        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", CharSet = CharSet.Unicode)]
        private static extern bool WriteConsoleMouseInputW(
            IntPtr hConsoleInput,
            [In] MOUSE_INPUT_RECORD[] lpBuffer,
            uint nLength,
            out uint lpNumberOfEventsWritten);

        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", CharSet = CharSet.Unicode)]
        private static extern bool WriteConsoleSizeInputW(
            IntPtr hConsoleInput,
            [In] SIZE_INPUT_RECORD[] lpBuffer,
            uint nLength,
            out uint lpNumberOfEventsWritten);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScanW(char ch);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOW lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static IntPtr OpenActiveConsoleOutput()
        {
            return CreateFileW(
                "CONOUT$",
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);
        }

        private static IntPtr OpenConsoleInput()
        {
            return CreateFileW(
                "CONIN$",
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);
        }

        private static bool IsValidHandle(IntPtr h)
        {
            return h != IntPtr.Zero && h != INVALID_HANDLE_VALUE;
        }

        // WindowsTerminalLTSC.exe --tui-host <pipeName> <cols> <rows> <dirB64> <cmdB64>
        public static int Run(string[] args)
        {
            if (args == null || args.Length < 6)
                return 1;

            string pipeName = args[1];
            if (string.IsNullOrEmpty(pipeName) || !ValidPipeNameRegex.IsMatch(pipeName))
                return 1;

            short cols = 120;
            short rows = 32;
            short.TryParse(args[2], out cols);
            short.TryParse(args[3], out rows);
            cols = (short)Math.Max(40, Math.Min(240, (int)cols));
            rows = (short)Math.Max(10, Math.Min(100, (int)rows));

            string workDir;
            string commandLine;
            try
            {
                workDir = Encoding.UTF8.GetString(Convert.FromBase64String(args[4]));
                commandLine = Encoding.UTF8.GetString(Convert.FromBase64String(args[5]));
            }
            catch
            {
                return 1;
            }

            try
            {
                using (var conKey = Registry.CurrentUser.CreateSubKey("Console"))
                {
                    if (conKey != null)
                    {
                        conKey.SetValue("VirtualTerminalLevel", 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }

            string privateTempDir = SystemIntegration.GetPrivateTempDirectory();
            string wrapperPath = Path.Combine(privateTempDir, "wt_tui_" + SystemIntegration.GenerateSecureHexToken(12) + ".cmd");
            string readyEventName = @"Local\wt_rdy_" + pipeName;
            EventWaitHandle readyEvent = null;
            PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
            bool procStarted = false;
            IntPtr hConIn = IntPtr.Zero;

            try
            {
                try
                {
                    readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
                }
                catch
                {
                    readyEvent = null;
                }

                string ownExePath = typeof(TuiHostWorker).Assembly.Location;
                if (string.IsNullOrEmpty(ownExePath) || !File.Exists(ownExePath))
                    ownExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WindowsTerminalLTSC.exe");
                if (!File.Exists(ownExePath))
                    ownExePath = SystemIntegration.GetExecutablePath();

                // Hold cmd.exe on a zero-CPU kernel EventWaitHandle until AttachConsole + ResizeConsoleViewport finishes,
                // guaranteeing agy.exe reads the 100% exact window cols x rows on instruction #0 with zero file locks or window flash!
                var wrapperSb = new StringBuilder();
                wrapperSb.AppendLine("@echo off");
                if (readyEvent != null && !string.IsNullOrEmpty(ownExePath) && File.Exists(ownExePath))
                {
                    wrapperSb.AppendLine("\"" + ownExePath + "\" --tui-wait " + readyEventName);
                }
                wrapperSb.AppendLine("@chcp 65001 >nul");
                wrapperSb.AppendLine(TerminalSession.EscapeBatchPercentPreservingEnvVars(commandLine));
                File.WriteAllText(wrapperPath, wrapperSb.ToString(), new UTF8Encoding(false));

                Environment.SetEnvironmentVariable("WT_LTSC_SESSION", "1");
                Environment.SetEnvironmentVariable("TERM", "xterm-256color");
                Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
                Environment.SetEnvironmentVariable("PYTHONIOENCODING", "utf-8");
                Environment.SetEnvironmentVariable("PYTHONUTF8", "1");

                FreeConsole();

                var si = new STARTUPINFOW();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFOW));
                si.dwFlags = STARTF_USESHOWWINDOW | STARTF_USECOUNTCHARS;
                si.wShowWindow = SW_HIDE;
                si.dwXCountChars = (short)Math.Min(260, cols + 2);
                si.dwYCountChars = DefaultScrollbackRows;

                string startDir = (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
                    ? workDir
                    : Environment.CurrentDirectory;
                var cmdLineBuf = new StringBuilder("cmd.exe /D /Q /C \"" + wrapperPath + "\"");

                procStarted = CreateProcessW(
                    null,
                    cmdLineBuf,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CREATE_NEW_CONSOLE,
                    IntPtr.Zero,
                    startDir,
                    ref si,
                    out pi);

                if (!procStarted)
                    return 2;

                bool attached = false;
                for (int attempt = 0; attempt < 80; attempt++)
                {
                    if (WaitForSingleObject(pi.hProcess, 0) == 0)
                        break;
                    if (AttachConsole(pi.dwProcessId))
                    {
                        attached = true;
                        break;
                    }
                    Thread.Sleep(15);
                }

                if (attached)
                {
                    IntPtr hConWnd = GetConsoleWindow();
                    if (hConWnd != IntPtr.Zero)
                    {
                        ShowWindow(hConWnd, SW_HIDE);
                    }
                }

                using (var wakeEvent = new AutoResetEvent(false))
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(5000);

                    if (!attached)
                    {
                        if (readyEvent != null) { try { readyEvent.Set(); } catch { } }
                        SendExitPacket(pipe, SafeGetWin32ExitCode(pi.hProcess), string.Empty);
                        return 0;
                    }

                    SetConsoleCP(65001);
                    SetConsoleOutputCP(65001);

                    hConIn = OpenConsoleInput();
                    IntPtr hInitialOut = OpenActiveConsoleOutput();
                    if (IsValidHandle(hInitialOut))
                    {
                        uint outMode;
                        if (GetConsoleMode(hInitialOut, out outMode))
                        {
                            SetConsoleMode(hInitialOut, outMode | ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
                        }
                        ResizeConsoleViewport(hInitialOut, hConIn, cols, rows, true);
                        CloseHandle(hInitialOut);
                    }

                    // Release zero-CPU kernel startup event NOW that exact console dimensions & UTF-8 modes are finalized!
                    if (readyEvent != null) { try { readyEvent.Set(); } catch { } }

                    bool stopRequested = false;
                    bool wasForceKilled = false;
                    int lastActivityTick = Environment.TickCount;
                    int lastInputTick = Environment.TickCount;
                    var writeLock = new object();
                    int childPid = pi.dwProcessId;
                    IntPtr childProcHandle = pi.hProcess;

                    // Reader thread receiving keystrokes / resize / mouse-wheel / graceful-exit / stop commands from UI
                    var inputThread = new Thread(() =>
                    {
                        try
                        {
                            int lastConInWriteTick = unchecked(Environment.TickCount - 100);
                            var br = new BinaryReader(pipe, Encoding.UTF8);
                            while (!stopRequested && pipe.IsConnected)
                            {
                                int cmd = pipe.ReadByte();
                                if (cmd < 0) break;

                                int nowTick = Environment.TickCount;
                                Interlocked.Exchange(ref lastActivityTick, nowTick);
                                Interlocked.Exchange(ref lastInputTick, nowTick);

                                if (cmd == 1) // Virtual key + char
                                {
                                    ushort vk = br.ReadUInt16();
                                    char ch = (char)br.ReadUInt16();
                                    uint ctrlState = br.ReadUInt32();
                                    if (IsValidHandle(hConIn))
                                    {
                                        WriteKeyStroke(hConIn, vk, ch, ctrlState);
                                        lastConInWriteTick = Environment.TickCount;
                                    }
                                    wakeEvent.Set();
                                }
                                else if (cmd == 2) // Batched Unicode text string (Thai / Paste)
                                {
                                    string text = br.ReadString();
                                    if (!string.IsNullOrEmpty(text) && IsValidHandle(hConIn))
                                    {
                                        WriteBatchedUnicodeString(hConIn, text);
                                        lastConInWriteTick = Environment.TickCount;
                                    }
                                    wakeEvent.Set();
                                }
                                else if (cmd == 3) // Dynamic Resize
                                {
                                    short newCols = br.ReadInt16();
                                    short newRows = br.ReadInt16();
                                    newCols = (short)Math.Max(40, Math.Min(240, (int)newCols));
                                    newRows = (short)Math.Max(10, Math.Min(100, (int)newRows));
                                    IntPtr hResizeOut = OpenActiveConsoleOutput();
                                    if (IsValidHandle(hResizeOut))
                                    {
                                        ResizeConsoleViewport(hResizeOut, hConIn, newCols, newRows, false);
                                        CloseHandle(hResizeOut);
                                    }
                                    wakeEvent.Set();
                                }
                                else if (cmd == 4) // Native Win32 Console Mouse Wheel (only for ENABLE_MOUSE_INPUT TUIs)
                                {
                                    short wheelDelta = br.ReadInt16();
                                    short mouseX = br.ReadInt16();
                                    short mouseY = br.ReadInt16();
                                    if (IsValidHandle(hConIn))
                                    {
                                        WriteMouseWheelEvent(hConIn, mouseX, mouseY, wheelDelta);
                                    }
                                    wakeEvent.Set();
                                }
                                else if (cmd == 8) // Graceful exit request (Ctrl+D twice so agy prints resume block cleanly)
                                {
                                    if (IsValidHandle(hConIn))
                                    {
                                        WriteKeyStroke(hConIn, 0x44, '\x04', 0x0008);
                                        Thread.Sleep(55);
                                        WriteKeyStroke(hConIn, 0x44, '\x04', 0x0008);
                                    }
                                    wakeEvent.Set();

                                    bool cleanExit = (WaitForSingleObject(childProcHandle, 650) == 0);
                                    if (!cleanExit)
                                    {
                                        wasForceKilled = true;
                                        KillProcessTree(childPid);
                                    }
                                    wakeEvent.Set();
                                }
                                else if (cmd == 9) // Hard terminate
                                {
                                    stopRequested = true;
                                    wasForceKilled = true;
                                    KillProcessTree(childPid);
                                    wakeEvent.Set();
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            stopRequested = true;
                            wakeEvent.Set();
                        }
                    })
                    { IsBackground = true };
                    inputThread.Start();

                    CHAR_INFO[] chunkBuf = new CHAR_INFO[cols * MaxConsoleReadChunkRows];
                    CHAR_INFO[] cellBuf = new CHAR_INFO[cols * rows];
                    ushort[] packedChars = new ushort[cols * rows];
                    byte[] packedAttrs = new byte[cols * rows];
                    byte[] curFrameBuf = null;
                    byte[] prevFrameBuf = null;
                    int prevFrameLen = 0;

                    while (!stopRequested && pipe.IsConnected)
                    {
                        bool exited = (WaitForSingleObject(pi.hProcess, 0) == 0);

                        // Open CONOUT$ each frame so Alternate Screen <-> Main Screen switches are always tracked!
                        IntPtr hActiveOut = OpenActiveConsoleOutput();
                        if (IsValidHandle(hActiveOut))
                        {
                            try
                            {
                                CONSOLE_SCREEN_BUFFER_INFO csbi;
                                if (GetConsoleScreenBufferInfo(hActiveOut, out csbi))
                                {
                                    short winW = (short)Math.Max(1, Math.Min(260, csbi.srWindow.Right - csbi.srWindow.Left + 1));
                                    int bottomRow = Math.Min((int)csbi.dwSize.Y - 1, Math.Max((int)csbi.srWindow.Bottom, (int)csbi.dwCursorPosition.Y));
                                    if (bottomRow < 0) bottomRow = 0;
                                    int topRow = Math.Max(0, bottomRow - MaxStreamedScrollbackRows + 1);
                                    short totalH = (short)Math.Max(1, bottomRow - topRow + 1);
                                    int cellCount = winW * totalH;

                                    int chunkCells = winW * MaxConsoleReadChunkRows;
                                    if (chunkBuf.Length < chunkCells)
                                    {
                                        chunkBuf = new CHAR_INFO[chunkCells];
                                    }
                                    if (cellBuf.Length < cellCount)
                                    {
                                        cellBuf = new CHAR_INFO[cellCount];
                                        packedChars = new ushort[cellCount];
                                        packedAttrs = new byte[cellCount];
                                    }

                                    if (ReadConsoleRowsChunked(hActiveOut, csbi.srWindow.Left, (short)topRow, winW, totalH, chunkBuf, cellBuf))
                                    {
                                        short curX = (short)(csbi.dwCursorPosition.X - csbi.srWindow.Left);
                                        short curY = (short)(csbi.dwCursorPosition.Y - topRow);

                                        uint inMode = 0;
                                        if (IsValidHandle(hConIn))
                                        {
                                            GetConsoleMode(hConIn, out inMode);
                                        }
                                        byte consoleFlags = (byte)(((inMode & ENABLE_MOUSE_INPUT) != 0) ? 1 : 0);

                                        for (int i = 0; i < cellCount; i++)
                                        {
                                            int col = i % winW;
                                            ushort chVal = IsTrueTrailingWideCell(cellBuf, i - col, col)
                                                ? (ushort)0
                                                : (ushort)cellBuf[i].UnicodeChar;
                                            if (chVal < 32 && chVal != 0) chVal = 32;
                                            packedChars[i] = chVal;
                                            packedAttrs[i] = (byte)(cellBuf[i].Attributes & 0xFF);
                                        }

                                        // Zero-allocation double-buffered frame layout: [1B pkt=1][9B w,h,curX,curY,flags][cellCount*2B chars][cellCount*1B attrs]
                                        int totalLen = 1 + 9 + (cellCount * 2) + cellCount;
                                        if (curFrameBuf == null || curFrameBuf.Length < totalLen)
                                            curFrameBuf = new byte[totalLen];

                                        curFrameBuf[0] = 1;
                                        WriteInt16LE(curFrameBuf, 1, winW);
                                        WriteInt16LE(curFrameBuf, 3, totalH);
                                        WriteInt16LE(curFrameBuf, 5, curX);
                                        WriteInt16LE(curFrameBuf, 7, curY);
                                        curFrameBuf[9] = consoleFlags;
                                        Buffer.BlockCopy(packedChars, 0, curFrameBuf, 10, cellCount * 2);
                                        Buffer.BlockCopy(packedAttrs, 0, curFrameBuf, 10 + (cellCount * 2), cellCount);

                                        if (!ByteSlicesEqual(prevFrameBuf, prevFrameLen, curFrameBuf, totalLen))
                                        {
                                            Interlocked.Exchange(ref lastActivityTick, Environment.TickCount);
                                            lock (writeLock)
                                            {
                                                // Do NOT call pipe.Flush() (FlushFileBuffers stalls kernel IPC)
                                                pipe.Write(curFrameBuf, 0, totalLen);
                                            }
                                            byte[] tmp = prevFrameBuf;
                                            prevFrameBuf = curFrameBuf;
                                            curFrameBuf = tmp;
                                            prevFrameLen = totalLen;
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                CloseHandle(hActiveOut);
                            }
                        }

                        if (exited)
                        {
                            // Child TUI exited — capture restored Main Screen Buffer output unless process was force-killed mid-TUI
                            string finalMainScreenText = wasForceKilled ? string.Empty : CaptureMainScreenExitText();
                            lock (writeLock)
                            {
                                SendExitPacket(pipe, SafeGetWin32ExitCode(pi.hProcess), finalMainScreenText);
                            }
                            // Give UI reader 40ms to read exit packet cleanly before pipe closes
                            Thread.Sleep(40);
                            break;
                        }

                        int now = Environment.TickCount;
                        int sinceInput = unchecked(now - lastInputTick);
                        int sinceActivity = unchecked(now - lastActivityTick);
                        int waitMs;
                        if (sinceInput >= 0 && sinceInput < 300)
                            waitMs = 4;
                        else if (sinceActivity >= 0 && sinceActivity < 2000)
                            waitMs = 12;
                        else
                            waitMs = 35;

                        wakeEvent.WaitOne(waitMs);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (IsValidHandle(hConIn))
                {
                    try { CloseHandle(hConIn); } catch { }
                }
                try { FreeConsole(); } catch { }
                if (procStarted)
                {
                    if (WaitForSingleObject(pi.hProcess, 0) != 0)
                    {
                        KillProcessTree(pi.dwProcessId);
                    }
                    if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                    if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                }
                if (readyEvent != null)
                {
                    try { readyEvent.Set(); } catch { }
                    try { readyEvent.Dispose(); } catch { }
                }
                try { if (File.Exists(wrapperPath)) File.Delete(wrapperPath); } catch { }
            }

            return 0;
        }

        private static void WriteInt16LE(byte[] buf, int offset, short value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static string CaptureMainScreenExitText()
        {
            IntPtr hOut = OpenActiveConsoleOutput();
            if (!IsValidHandle(hOut))
                return string.Empty;

            try
            {
                CONSOLE_SCREEN_BUFFER_INFO csbi;
                if (!GetConsoleScreenBufferInfo(hOut, out csbi))
                    return string.Empty;

                short w = (short)Math.Max(1, Math.Min(260, (int)csbi.dwSize.X));
                short maxRow = (short)Math.Min((int)csbi.dwSize.Y - 1, Math.Max((int)csbi.dwCursorPosition.Y + 2, (int)csbi.srWindow.Bottom));
                if (maxRow < 0) maxRow = 0;
                short startRow = (short)Math.Max(0, (int)maxRow - 109);
                short h = (short)Math.Max(1, (int)maxRow - (int)startRow + 1);

                var chunkBuf = new CHAR_INFO[w * MaxConsoleReadChunkRows];
                var buf = new CHAR_INFO[w * h];
                if (!ReadConsoleRowsChunked(hOut, 0, startRow, w, h, chunkBuf, buf))
                    return string.Empty;

                var lines = new List<string>();
                var rowSb = new StringBuilder(w);
                for (int r = 0; r < h; r++)
                {
                    rowSb.Length = 0;
                    int offset = r * w;
                    for (int c = 0; c < w; c++)
                    {
                        if (IsTrueTrailingWideCell(buf, offset, c))
                            continue;
                        char ch = buf[offset + c].UnicodeChar;
                        if (ch == '\u200B' || ch == '\u200C' || ch == '\u200D' || ch == '\uFEFF')
                            continue;
                        if (ch < ' ') ch = ' ';
                        rowSb.Append(ch);
                    }
                    lines.Add(rowSb.ToString().TrimEnd());
                }

                while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                {
                    lines.RemoveAt(lines.Count - 1);
                }
                while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
                {
                    lines.RemoveAt(0);
                }

                if (lines.Count == 0)
                    return string.Empty;

                // Filter out unrestored full-screen TUI box-drawing frames (e.g. vim '~' screen or BubbleTea borders)
                int boxBorderLines = 0;
                for (int i = 0; i < lines.Count; i++)
                {
                    string ln = lines[i];
                    for (int k = 0; k < ln.Length; k++)
                    {
                        char c = ln[k];
                        if (c >= '\u2500' && c <= '\u257F')
                        {
                            boxBorderLines++;
                            break;
                        }
                    }
                }
                if (boxBorderLines >= 4 && lines.Count >= 8)
                {
                    // Keep only plain post-TUI lines below the last box-drawing border
                    int lastBoxIdx = -1;
                    for (int i = lines.Count - 1; i >= 0; i--)
                    {
                        string ln = lines[i];
                        bool hasBox = false;
                        for (int k = 0; k < ln.Length; k++)
                        {
                            if (ln[k] >= '\u2500' && ln[k] <= '\u257F') { hasBox = true; break; }
                        }
                        if (hasBox) { lastBoxIdx = i; break; }
                    }
                    if (lastBoxIdx >= 0 && lastBoxIdx + 1 < lines.Count)
                        lines = lines.GetRange(lastBoxIdx + 1, lines.Count - (lastBoxIdx + 1));
                    else
                        return string.Empty;
                }

                return string.Join("\r\n", lines.ToArray());
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                CloseHandle(hOut);
            }
        }

        private static int SafeGetWin32ExitCode(IntPtr hProcess)
        {
            try
            {
                uint code;
                if (hProcess != IntPtr.Zero && GetExitCodeProcess(hProcess, out code))
                    return (int)code;
            }
            catch { }
            return 0;
        }

        private static void SendExitPacket(Stream pipe, int exitCode, string exitText)
        {
            try
            {
                using (var ms = new MemoryStream(256))
                using (var bw = new BinaryWriter(ms, Encoding.UTF8))
                {
                    bw.Write((byte)2);
                    bw.Write(exitCode);
                    bw.Write(exitText ?? string.Empty);
                    bw.Flush();
                    byte[] data = ms.ToArray();
                    pipe.Write(data, 0, data.Length);
                }
            }
            catch { }
        }

        private static bool IsTrueTrailingWideCell(CHAR_INFO[] buf, int offset, int col)
        {
            if (col <= 0) return false;
            if ((buf[offset + col].Attributes & COMMON_LVB_TRAILING_BYTE) == 0) return false;

            ushort curCh = (ushort)buf[offset + col].UnicodeChar;
            ushort prevCh = (ushort)buf[offset + col - 1].UnicodeChar;
            ushort prevAttr = buf[offset + col - 1].Attributes;

            return (prevAttr & COMMON_LVB_LEADING_BYTE) != 0 &&
                   (curCh == prevCh || curCh == 0 || curCh == 32) &&
                   IsEastAsianWideChar(prevCh);
        }

        private static bool IsEastAsianWideChar(ushort ch)
        {
            return (ch >= 0x1100 && ch <= 0x115F) ||
                   (ch >= 0x2E80 && ch <= 0xA4CF && ch != 0x303F) ||
                   (ch >= 0xAC00 && ch <= 0xD7A3) ||
                   (ch >= 0xF900 && ch <= 0xFAFF) ||
                   (ch >= 0xFE30 && ch <= 0xFE6F) ||
                   (ch >= 0xFF00 && ch <= 0xFF60) ||
                   (ch >= 0xFFE0 && ch <= 0xFFE6);
        }

        // Reads console screen buffer rows in chunks of <= 50 rows so Win10 1809's 64 KB LPC ReadConsoleOutputW limit is never hit.
        private static bool ReadConsoleRowsChunked(
            IntPtr hOut,
            short leftCol,
            short startRow,
            short width,
            short totalRows,
            CHAR_INFO[] chunkBuf,
            CHAR_INFO[] destBuf)
        {
            var bufCoord = new COORD { X = 0, Y = 0 };
            int rowOffset = 0;
            while (rowOffset < totalRows)
            {
                short chunkH = (short)Math.Min(MaxConsoleReadChunkRows, totalRows - rowOffset);
                var chunkSize = new COORD { X = width, Y = chunkH };
                var readRect = new SMALL_RECT
                {
                    Left = leftCol,
                    Top = (short)(startRow + rowOffset),
                    Right = (short)(leftCol + width - 1),
                    Bottom = (short)(startRow + rowOffset + chunkH - 1)
                };
                if (!ReadConsoleOutputW(hOut, chunkBuf, chunkSize, bufCoord, ref readRect))
                {
                    return false;
                }
                Array.Copy(chunkBuf, 0, destBuf, rowOffset * width, width * chunkH);
                rowOffset += chunkH;
            }
            return true;
        }

        private static void ResizeConsoleViewport(IntPtr hOut, IntPtr hIn, short cols, short rows, bool initialSetup)
        {
            try
            {
                CONSOLE_SCREEN_BUFFER_INFO csbi;
                if (!GetConsoleScreenBufferInfo(hOut, out csbi))
                    return;

                short effectiveRows = (short)Math.Max(75, (int)rows);

                short bufCols = (short)Math.Min(260, cols + 2);
                if (initialSetup)
                {
                    short initBufH = DefaultScrollbackRows;
                    // Clamp existing srWindow ONLY to target cols/rows (NEVER to 10x5!) so child TUI never observes a tiny 11x6 window
                    if (csbi.srWindow.Right >= cols || csbi.srWindow.Bottom >= initBufH || csbi.srWindow.Left != 0 || csbi.srWindow.Top != 0)
                    {
                        var clampWin = new SMALL_RECT
                        {
                            Left = 0,
                            Top = 0,
                            Right = (short)Math.Max(0, Math.Min((int)csbi.dwSize.X - 1, (int)cols - 1)),
                            Bottom = (short)Math.Max(0, Math.Min((int)csbi.dwSize.Y - 1, (int)effectiveRows - 1))
                        };
                        SetConsoleWindowInfo(hOut, true, ref clampWin);
                    }
                    if (!SetConsoleScreenBufferSize(hOut, new COORD { X = bufCols, Y = initBufH }))
                    {
                        initBufH = (short)Math.Max((int)effectiveRows, 600);
                        SetConsoleScreenBufferSize(hOut, new COORD { X = bufCols, Y = initBufH });
                    }
                    var targetWin = new SMALL_RECT { Left = 0, Top = 0, Right = (short)(cols - 1), Bottom = (short)(effectiveRows - 1) };
                    SetConsoleWindowInfo(hOut, true, ref targetWin);
                    return;
                }

                // Main Screen Buffer with scrollback (e.g. agy inline mode where dwSize.Y >= 400):
                // Set target srWindow rows BEFORE SetConsoleScreenBufferSize so conhost's native WINDOW_BUFFER_SIZE_EVENT
                // fires ONCE with BOTH dwSize.X == cols AND srWindow height == effectiveRows already finalized!
                if (csbi.dwSize.Y >= 400)
                {
                    short prevBufW = csbi.dwSize.X;
                    short prevBufH = csbi.dwSize.Y;
                    bool wasAtTop = (csbi.srWindow.Top == 0 && csbi.dwCursorPosition.Y < effectiveRows);
                    int prevBottom = Math.Max((int)csbi.srWindow.Bottom, (int)csbi.dwCursorPosition.Y);
                    short targetBufH = (short)Math.Min(4000, Math.Max((int)DefaultScrollbackRows, Math.Max((int)csbi.dwSize.Y, prevBottom + effectiveRows + 100)));

                    short preWinBottom;
                    short preWinTop;
                    short maxExistingY = (short)(Math.Min((int)prevBufH, (int)targetBufH) - 1);
                    if (wasAtTop)
                    {
                        preWinTop = 0;
                        preWinBottom = (short)Math.Min((int)maxExistingY, (int)effectiveRows - 1);
                    }
                    else
                    {
                        preWinBottom = (short)Math.Min((int)maxExistingY, Math.Max((int)effectiveRows - 1, prevBottom));
                        preWinTop = (short)Math.Max(0, (int)preWinBottom - (int)effectiveRows + 1);
                    }

                    var preWin = new SMALL_RECT
                    {
                        Left = 0,
                        Top = preWinTop,
                        Right = (short)Math.Max(0, Math.Min((int)prevBufW - 1, (int)cols - 1)),
                        Bottom = preWinBottom
                    };
                    SetConsoleWindowInfo(hOut, true, ref preWin);

                    bool bufSizeChanged = (bufCols != prevBufW || targetBufH != prevBufH);
                    if (bufSizeChanged)
                    {
                        SetConsoleScreenBufferSize(hOut, new COORD { X = bufCols, Y = targetBufH });
                    }

                    CONSOLE_SCREEN_BUFFER_INFO csbiAfter;
                    if (GetConsoleScreenBufferInfo(hOut, out csbiAfter))
                    {
                        prevBottom = Math.Max(prevBottom, (int)csbiAfter.dwCursorPosition.Y);
                        if (csbiAfter.dwCursorPosition.Y >= effectiveRows)
                            wasAtTop = false;
                        targetBufH = csbiAfter.dwSize.Y;
                    }

                    short winTop;
                    short winBottom;
                    if (wasAtTop)
                    {
                        winTop = 0;
                        winBottom = (short)Math.Min((int)targetBufH - 1, (int)effectiveRows - 1);
                    }
                    else
                    {
                        winBottom = (short)Math.Min((int)targetBufH - 1, Math.Max((int)effectiveRows - 1, prevBottom));
                        winTop = (short)Math.Max(0, (int)winBottom - (int)effectiveRows + 1);
                    }

                    var targetWin = new SMALL_RECT { Left = 0, Top = winTop, Right = (short)(cols - 1), Bottom = winBottom };
                    SetConsoleWindowInfo(hOut, true, ref targetWin);

                    // Only synthesize WINDOW_BUFFER_SIZE_EVENT on height-only resizes where SetConsoleScreenBufferSize did not run
                    if (!bufSizeChanged && IsValidHandle(hIn))
                    {
                        WriteWindowBufferSizeEvent(hIn, cols, targetBufH);
                    }
                }
                else
                {
                    // Alternate Screen Buffer (e.g. vim / htop / btop with exact viewport buffer)
                    if (csbi.srWindow.Right >= cols || csbi.srWindow.Bottom >= rows)
                    {
                        var clampWin = new SMALL_RECT
                        {
                            Left = 0,
                            Top = 0,
                            Right = (short)Math.Max(0, Math.Min((int)csbi.dwSize.X - 1, (int)cols - 1)),
                            Bottom = (short)Math.Max(0, Math.Min((int)csbi.dwSize.Y - 1, (int)rows - 1))
                        };
                        SetConsoleWindowInfo(hOut, true, ref clampWin);
                    }
                    SetConsoleScreenBufferSize(hOut, new COORD { X = cols, Y = rows });
                    var targetWin = new SMALL_RECT { Left = 0, Top = 0, Right = (short)(cols - 1), Bottom = (short)(rows - 1) };
                    SetConsoleWindowInfo(hOut, true, ref targetWin);
                    SetConsoleScreenBufferSize(hOut, new COORD { X = cols, Y = rows });

                    if (IsValidHandle(hIn))
                    {
                        WriteWindowBufferSizeEvent(hIn, cols, rows);
                    }
                }
            }
            catch { }
        }

        private static void WriteWindowBufferSizeEvent(IntPtr hIn, short bufCols, short bufRows)
        {
            var records = new SIZE_INPUT_RECORD[1];
            records[0] = new SIZE_INPUT_RECORD
            {
                EventType = WINDOW_BUFFER_SIZE_EVENT,
                X = bufCols,
                Y = bufRows
            };
            uint written;
            WriteConsoleSizeInputW(hIn, records, 1, out written);
        }

        private static void WriteMouseWheelEvent(IntPtr hIn, short mouseX, short mouseY, short wheelDelta)
        {
            if (wheelDelta == 0) return;
            var records = new MOUSE_INPUT_RECORD[1];
            records[0] = new MOUSE_INPUT_RECORD
            {
                EventType = MOUSE_EVENT,
                MouseX = (short)Math.Max((short)0, mouseX),
                MouseY = (short)Math.Max((short)0, mouseY),
                dwButtonState = ((uint)(ushort)wheelDelta) << 16,
                dwControlKeyState = 0,
                dwEventFlags = MOUSE_WHEELED
            };
            uint written;
            WriteConsoleMouseInputW(hIn, records, 1, out written);
        }

        private static void WriteKeyStroke(IntPtr hIn, ushort vk, char ch, uint ctrlState)
        {
            ushort scan = (ushort)MapVirtualKey(vk, 0);
            var records = new INPUT_RECORD[2];
            records[0] = new INPUT_RECORD
            {
                EventType = KEY_EVENT,
                bKeyDown = 1,
                wRepeatCount = 1,
                wVirtualKeyCode = vk,
                wVirtualScanCode = scan,
                UnicodeChar = ch,
                dwControlKeyState = ctrlState
            };
            records[1] = records[0];
            records[1].bKeyDown = 0;
            records[1].UnicodeChar = '\0';
            uint written;
            WriteConsoleInputW(hIn, records, 2, out written);
        }

        private static void WriteBatchedUnicodeString(IntPtr hIn, string text)
        {
            // Use VK_PACKET (0xE7) on key-down and UnicodeChar='\0' on key-up (NEVER vk=0 on key-up, which triggers
            // Win10 1809 conhost's Alt+Numpad duplicate-char race!). Deliver multi-char pastes in 6-char chunks at 35ms.
            var chunkBuf = new List<INPUT_RECORD>(12);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\n')
                    continue;

                if (ch == '\r' || ch == '\t')
                {
                    if (chunkBuf.Count > 0)
                    {
                        uint wFlush;
                        INPUT_RECORD[] arrFlush = chunkBuf.ToArray();
                        WriteConsoleInputW(hIn, arrFlush, (uint)arrFlush.Length, out wFlush);
                        chunkBuf.Clear();
                        Thread.Sleep(12);
                    }
                    if (ch == '\r')
                        WriteKeyStroke(hIn, 0x0D, '\r', 0);
                    else
                        WriteKeyStroke(hIn, 0x09, '\t', 0);
                    Thread.Sleep(10);
                    continue;
                }

                chunkBuf.Add(new INPUT_RECORD
                {
                    EventType = KEY_EVENT,
                    bKeyDown = 1,
                    wRepeatCount = 1,
                    wVirtualKeyCode = 0xE7, // VK_PACKET
                    wVirtualScanCode = 0,
                    UnicodeChar = ch,
                    dwControlKeyState = 0
                });
                chunkBuf.Add(new INPUT_RECORD
                {
                    EventType = KEY_EVENT,
                    bKeyDown = 0,
                    wRepeatCount = 1,
                    wVirtualKeyCode = 0xE7, // VK_PACKET
                    wVirtualScanCode = 0,
                    UnicodeChar = '\0',
                    dwControlKeyState = 0
                });

                if (chunkBuf.Count >= 256) // 128 Unicode chars per batch
                {
                    uint written;
                    INPUT_RECORD[] arr = chunkBuf.ToArray();
                    WriteConsoleInputW(hIn, arr, (uint)arr.Length, out written);
                    chunkBuf.Clear();
                    if (i + 1 < text.Length)
                    {
                        Thread.Sleep(10);
                    }
                }
            }

            if (chunkBuf.Count > 0)
            {
                uint writtenRem;
                INPUT_RECORD[] arrRem = chunkBuf.ToArray();
                WriteConsoleInputW(hIn, arrRem, (uint)arrRem.Length, out writtenRem);
            }
        }

        private static bool ByteSlicesEqual(byte[] a, int aLen, byte[] b, int bLen)
        {
            if (a == null || b == null || aLen != bLen)
                return false;
            for (int i = 0; i < aLen; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        public static void KillProcessTree(int pid)
        {
            try
            {
                var tk = new ProcessStartInfo("taskkill.exe", "/F /T /PID " + pid)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(tk))
                {
                    if (p != null) p.WaitForExit(1500);
                }
            }
            catch { }
        }
    }

    internal sealed class TuiFrameData
    {
        public short Width;
        public short Height;
        public short CursorX;
        public short CursorY;
        public byte Flags;
        public ushort[] Chars;
        public byte[] Attrs;
    }

    // Hardware-accelerated retained-mode per-row DrawingVisual surface for 60+ FPS zero-lag TUI rendering
    internal sealed class TuiCanvasElement : FrameworkElement
    {
        private readonly VisualCollection _visuals;
        private readonly List<DrawingVisual> _rowVisuals = new List<DrawingVisual>();
        private readonly DrawingVisual _overlayVisual = new DrawingVisual();

        private TerminalTheme _theme;
        private FontFamily _fontFamily;
        private Typeface _normalTypeface;
        private Typeface _boldTypeface;
        private double _fontSize = 15.0;
        private double _pixelsPerDip = 1.0;
        private double _cellWidth = 8.5;
        private double _cellHeight = 20.0;

        private Brush _bgBrush = Brushes.Black;
        private Brush _fgBrush = Brushes.LightGray;
        private Brush _cursorBrush = Brushes.White;
        private Brush _cursorTranslucentBrush;
        private Brush _selectionBrush;
        private readonly Brush[] _ansiBrushes = new Brush[16];

        private short _w = 120;
        private short _h = 30;
        private short _curX = -1;
        private short _curY = -1;
        private ushort[] _chars;
        private byte[] _attrs;
        private float[][] _rowColVisualX;
        private bool _hasRealFrame;
        private string _loadingCommand = "";

        private bool _isSelecting;
        private bool _hasSelection;
        private int _selAnchorRow;
        private int _selAnchorCol;
        private int _selEndRow;
        private int _selEndCol;

        public const double PadLeft = 12.0;
        public const double PadTop = 8.0;

        // Maps Win32 Console BGR color index (0..15) to ANSI RGB index (0..15)
        private static readonly int[] Win32ToAnsiColorIndex = new[]
        {
            0, 4, 2, 6, 1, 5, 3, 7,
            8, 12, 10, 14, 9, 13, 11, 15
        };

        public double CellWidth { get { return _cellWidth; } }
        public double CellHeight { get { return _cellHeight; } }
        public bool HasSelection { get { return _hasSelection; } }

        public TuiCanvasElement(TerminalTheme theme, TerminalSettings settings)
        {
            Focusable = true;
            Cursor = Cursors.IBeam;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

            _visuals = new VisualCollection(this);
            _visuals.Add(_overlayVisual);

            ApplyThemeAndMetrics(theme, settings);
        }

        protected override int VisualChildrenCount
        {
            get { return _visuals.Count; }
        }

        protected override Visual GetVisualChild(int index)
        {
            return _visuals[index];
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double reqW = (_w * _cellWidth) + (PadLeft * 2);
            double reqH = (_h * _cellHeight) + (PadTop * 2);
            if (!double.IsInfinity(availableSize.Width))
                reqW = Math.Max(reqW, availableSize.Width);
            if (!double.IsInfinity(availableSize.Height))
                reqH = Math.Max(reqH, availableSize.Height);
            return new Size(reqW, reqH);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            // Fill hit-testable surface background
            drawingContext.DrawRectangle(_bgBrush, null, new Rect(0, 0, Math.Max(1, RenderSize.Width), Math.Max(1, RenderSize.Height)));
        }

        public void ApplyThemeAndMetrics(TerminalTheme theme, TerminalSettings settings)
        {
            _theme = theme;
            _fontSize = settings.FontSize;
            _fontFamily = new FontFamily(settings.FontFamily);
            _normalTypeface = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _boldTypeface = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

            _bgBrush = TerminalTheme.BrushFromHex(theme.Background);
            _fgBrush = TerminalTheme.BrushFromHex(theme.Foreground);
            _cursorBrush = TerminalTheme.BrushFromHex(theme.CursorColor);

            Color curColor = TerminalTheme.ParseColor(theme.CursorColor);
            _cursorTranslucentBrush = TerminalTheme.GetCachedBrush(Color.FromArgb(96, curColor.R, curColor.G, curColor.B));

            Color selColor = TerminalTheme.ParseColor(theme.SelectionBackground);
            _selectionBrush = TerminalTheme.GetCachedBrush(Color.FromArgb(128, selColor.R, selColor.G, selColor.B));

            for (int i = 0; i < 16; i++)
            {
                string hex = (theme.AnsiColors != null && i < theme.AnsiColors.Length) ? theme.AnsiColors[i] : theme.Foreground;
                _ansiBrushes[i] = TerminalTheme.BrushFromHex(hex);
            }

            try
            {
                _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                if (_pixelsPerDip <= 0) _pixelsPerDip = 1.0;
            }
            catch
            {
                _pixelsPerDip = 1.0;
            }

            var probe = new FormattedText(
                "0",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _normalTypeface,
                _fontSize,
                _fgBrush,
                _pixelsPerDip);
            _cellWidth = Math.Max(6.0, probe.WidthIncludingTrailingWhitespace);
            _cellHeight = Math.Max(14.0, Math.Round(_fontSize * 1.35));

            InvalidateVisual();
            InvalidateMeasure();

            if (!_hasRealFrame)
            {
                RenderLoadingOverlay();
            }
            else if (_chars != null && _attrs != null)
            {
                RedrawAllRows();
                RenderSelectionOverlay();
            }
        }

        public void ShowLoadingState(string commandLine)
        {
            _hasRealFrame = false;
            _loadingCommand = commandLine ?? "";
            ClearRowVisuals();
            RenderLoadingOverlay();
        }

        private void RenderLoadingOverlay()
        {
            using (DrawingContext dc = _overlayVisual.RenderOpen())
            {
                if (_hasRealFrame) return;

                var titleFt = new FormattedText(
                    "⏳ กำลังเปิดหน้าจอโต้ตอบ (Interactive TUI): " + _loadingCommand,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    _boldTypeface,
                    _fontSize,
                    TerminalTheme.BrushFromHex(_theme.AccentColor),
                    _pixelsPerDip);
                dc.DrawText(titleFt, new Point(PadLeft, PadTop));

                var subFt = new FormattedText(
                    "   กรุณารอสักครู่... (การเปิดเซิร์ฟเวอร์ครั้งแรกใช้เวลาประมาณ 3–8 วินาที)",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    _normalTypeface,
                    _fontSize,
                    TerminalTheme.BrushFromHex(_theme.PromptColor),
                    _pixelsPerDip);
                dc.DrawText(subFt, new Point(PadLeft, PadTop + _cellHeight * 1.25));

                var tipFt = new FormattedText(
                    "   • พิมพ์ภาษาไทย/อังกฤษ เลื่อนทัชแพด/ลูกกลิ้งเมาส์ดูย้อนหลัง และลากเมาส์คลุมดำคัดลอกได้ทันที\r\n" +
                    "   • เมื่อต้องการออก ให้พิมพ์ /exit หรือกด Ctrl+D สองครั้ง (ระบบจะแสดงรหัสแชท agy -c ให้อัตโนมัติ)",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    _normalTypeface,
                    _fontSize * 0.94,
                    _fgBrush,
                    _pixelsPerDip);
                dc.DrawText(tipFt, new Point(PadLeft, PadTop + _cellHeight * 2.6));
            }
        }

        private void ClearRowVisuals()
        {
            for (int i = 0; i < _rowVisuals.Count; i++)
            {
                _visuals.Remove(_rowVisuals[i]);
            }
            _rowVisuals.Clear();
        }

        private void EnsureRowVisualCount(int targetRows)
        {
            if (_rowVisuals.Count == targetRows)
                return;

            // Keep _overlayVisual on top (last child in _visuals)
            _visuals.Remove(_overlayVisual);

            while (_rowVisuals.Count < targetRows)
            {
                var dv = new DrawingVisual();
                _rowVisuals.Add(dv);
                _visuals.Add(dv);
            }
            while (_rowVisuals.Count > targetRows)
            {
                int last = _rowVisuals.Count - 1;
                _visuals.Remove(_rowVisuals[last]);
                _rowVisuals.RemoveAt(last);
            }

            _visuals.Add(_overlayVisual);
        }

        public void RenderFrame(TuiFrameData frame)
        {
            if (frame == null || frame.Chars == null || frame.Attrs == null)
                return;

            if (!_hasRealFrame)
            {
                bool anyVisible = false;
                for (int i = 0; i < frame.Chars.Length; i++)
                {
                    if (frame.Chars[i] > 32) { anyVisible = true; break; }
                }
                if (!anyVisible)
                    return;

                _hasRealFrame = true;
                using (DrawingContext dc = _overlayVisual.RenderOpen()) { } // Clear loading overlay
            }

            bool widthChanged = (frame.Width != _w || _chars == null || _attrs == null);
            bool heightChanged = (frame.Height != _h);
            short prevCurY = _curY;

            EnsureRowVisualCount(frame.Height);
            EnsureRowColVisualX(frame.Width, frame.Height);

            for (int r = 0; r < frame.Height; r++)
            {
                bool rowDirty = widthChanged || (r >= _h) || (r == prevCurY) || (r == frame.CursorY);
                if (!rowDirty)
                {
                    int rowStart = r * frame.Width;
                    for (int c = 0; c < frame.Width; c++)
                    {
                        int idx = rowStart + c;
                        if (frame.Chars[idx] != _chars[idx] || frame.Attrs[idx] != _attrs[idx])
                        {
                            rowDirty = true;
                            break;
                        }
                    }
                }

                if (rowDirty)
                {
                    DrawSingleRow(_rowVisuals[r], r, frame.Width, frame.CursorX, frame.CursorY, frame.Chars, frame.Attrs);
                }
            }

            _w = frame.Width;
            _h = frame.Height;
            _curX = frame.CursorX;
            _curY = frame.CursorY;
            _chars = frame.Chars;
            _attrs = frame.Attrs;

            if (widthChanged || heightChanged)
            {
                InvalidateMeasure();
            }
        }

        private void RedrawAllRows()
        {
            if (_chars == null || _attrs == null) return;
            EnsureRowVisualCount(_h);
            EnsureRowColVisualX(_w, _h);
            for (int r = 0; r < _h; r++)
            {
                DrawSingleRow(_rowVisuals[r], r, _w, _curX, _curY, _chars, _attrs);
            }
        }

        private void EnsureRowColVisualX(short w, short h)
        {
            if (_rowColVisualX == null || _rowColVisualX.Length < h)
            {
                int newCap = Math.Max((int)h, _rowColVisualX != null ? _rowColVisualX.Length * 2 : 64);
                var next = new float[newCap][];
                if (_rowColVisualX != null)
                {
                    Array.Copy(_rowColVisualX, 0, next, 0, _rowColVisualX.Length);
                }
                _rowColVisualX = next;
            }
            int targetLen = w + 1;
            for (int r = 0; r < h; r++)
            {
                if (_rowColVisualX[r] == null || _rowColVisualX[r].Length != targetLen)
                {
                    _rowColVisualX[r] = new float[targetLen];
                }
            }
        }

        private struct RowDrawItem
        {
            public bool IsHorizontalRule;
            public double X;
            public double Width;
            public double LineThickness;
            public Brush FgBrush;
            public FormattedText TextObj;
        }

        private static int ResolveCursorCellIndex(ushort[] chars, int rowOffset, short w, int lastContentEnd, short curX)
        {
            if (curX < 0)
                return -1;
            if (curX >= lastContentEnd)
                return Math.Min((int)w - 1, (int)curX);

            int contentVisCols = 0;
            for (int i = 0; i < lastContentEnd; i++)
            {
                ushort iv = chars[rowOffset + i];
                if (IsZeroWidthFormatChar(iv) || (i > 0 && IsThaiCombiningMark((char)iv)))
                    continue;
                contentVisCols++;
            }

            // If curX exceeds total visual columns of the row content, conhost positioned cursor via raw cell index
            // (e.g. relative \x1b[D move from raw cell end) — snap backwards onto the owning base consonant cluster!
            if (curX > contentVisCols)
            {
                int rawCell = Math.Min((int)w - 1, (int)curX);
                while (rawCell > 0)
                {
                    ushort rv = chars[rowOffset + rawCell];
                    if (IsZeroWidthFormatChar(rv) || IsThaiCombiningMark((char)rv))
                        rawCell--;
                    else
                        break;
                }
                return rawCell;
            }

            // When curX <= contentVisCols, TUI positioned cursor via visual column (wcwidth),
            // where zero-width format chars and Thai combining vowels/tone marks take 0 visual columns!
            int visCols = 0;
            int c = 0;
            while (c < w && visCols < curX)
            {
                ushort cv = chars[rowOffset + c];
                if (IsZeroWidthFormatChar(cv) || (c > 0 && IsThaiCombiningMark((char)cv)))
                {
                    c++;
                    continue;
                }
                visCols++;
                c++;
            }

            // Advance past any trailing combining marks / zero-width chars attached to the preceding base glyph
            while (c < w)
            {
                ushort cv = chars[rowOffset + c];
                if (IsZeroWidthFormatChar(cv) || (c > 0 && IsThaiCombiningMark((char)cv)))
                    c++;
                else
                    break;
            }

            return Math.Min((int)w - 1, c);
        }

        private void DrawSingleRow(DrawingVisual visual, int row, short w, short curX, short curY, ushort[] chars, byte[] attrs)
        {
            double y = PadTop + (row * _cellHeight);
            int rowOffset = row * w;
            float[] colX = _rowColVisualX[row];

            int lastContentEnd = 0;
            for (int k = w - 1; k >= 0; k--)
            {
                ushort kv = chars[rowOffset + k];
                if (kv > 32 && !IsZeroWidthFormatChar(kv))
                {
                    lastContentEnd = k + 1;
                    break;
                }
            }

            int realCurCol = (row == curY && curX >= 0 && curX < w)
                ? ResolveCursorCellIndex(chars, rowOffset, w, lastContentEnd, curX)
                : -1;

            var drawItems = new List<RowDrawItem>(16);
            var runBuilder = new StringBuilder(96);
            double currentX = PadLeft;
            double cursorMinX = -1.0;
            int col = 0;
            int visualCol = 0;

            while (col < w)
            {
                int idx = rowOffset + col;
                ushort chVal = chars[idx];

                // 1. Skip trailing wide-char cell (0 -> +1 visual col) and Win10 1809 conhost zero-width format chars (0 visual col)
                if (chVal == 0 || IsZeroWidthFormatChar(chVal))
                {
                    colX[col] = (float)currentX;
                    if (chVal == 0)
                        visualCol++;
                    col++;
                    continue;
                }

                // 2. Spaces: leading spaces indent on grid; 1..5 inline spaces flow naturally (NEVER giant 250px jumps!);
                // >=6 spaces snap cleanly using visualCol (NOT raw CHAR_INFO index inflated by Thai combining marks!)
                if (chVal == 32)
                {
                    int spEnd = col + 1;
                    int spVisCount = 1;
                    while (spEnd < w)
                    {
                        ushort sv = chars[rowOffset + spEnd];
                        if (sv == 32)
                        {
                            spVisCount++;
                            spEnd++;
                        }
                        else if (sv == 0)
                        {
                            spVisCount++;
                            spEnd++;
                        }
                        else if (IsZeroWidthFormatChar(sv))
                        {
                            spEnd++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    int spCellCount = spEnd - col;

                    if (col == 0)
                    {
                        double step0 = (spVisCount * _cellWidth) / Math.Max(1, spCellCount);
                        for (int k = 0; k < spCellCount; k++)
                        {
                            colX[col + k] = (float)(PadLeft + (k * step0));
                        }
                        visualCol += spVisCount;
                        currentX = PadLeft + (visualCol * _cellWidth);
                        col = spEnd;
                        continue;
                    }

                    double targetX;
                    if (spVisCount <= 5 || spEnd >= lastContentEnd)
                    {
                        // Natural inline word/sentence spacing (fixes mid-sentence giant gaps after proportional Thai runs!)
                        double spaceAdv = (spEnd >= lastContentEnd) ? _cellWidth : (_cellWidth * 0.90);
                        targetX = currentX + (spVisCount * spaceAdv);
                    }
                    else
                    {
                        // Multi-column table or right-aligned UI widget (>= 6 spaces before more visible text):
                        // Snap to visual column grid so right-aligned status labels align cleanly with window width!
                        int targetVisCol = visualCol + spVisCount;
                        targetX = Math.Max(currentX + (_cellWidth * 2.5), PadLeft + (targetVisCol * _cellWidth));
                    }

                    double step = (targetX - currentX) / Math.Max(1, spCellCount);
                    colX[col] = (float)currentX;
                    for (int k = 1; k < spCellCount; k++)
                    {
                        colX[col + k] = (float)(currentX + (k * step));
                    }
                    visualCol += spVisCount;
                    currentX = targetX;
                    col = spEnd;
                    continue;
                }

                // 3. Horizontal rule box-drawing chars (─, ━, ═): render seamless crisp geometric line matching exact visual grid
                if (chVal == 0x2500 || chVal == 0x2501 || chVal == 0x2550)
                {
                    double gridX = PadLeft + (visualCol * _cellWidth);
                    if (col == 0 || gridX > currentX) currentX = gridX;

                    byte hAttr = attrs[idx];
                    double hStartX = currentX;
                    while (col < w && chars[rowOffset + col] == chVal && attrs[rowOffset + col] == hAttr)
                    {
                        colX[col] = (float)currentX;
                        currentX += _cellWidth;
                        visualCol++;
                        col++;
                    }
                    drawItems.Add(new RowDrawItem
                    {
                        IsHorizontalRule = true,
                        X = hStartX,
                        Width = currentX - hStartX,
                        LineThickness = (chVal == 0x2501 || chVal == 0x2550) ? 2.0 : 1.0,
                        FgBrush = GetForegroundBrush(hAttr)
                    });
                    continue;
                }

                // 4. Other Box-Drawing & Block Elements (│, ╭, ╮, ╰, ╯, ▄, ▀, █): snap to exact visual column grid
                if (IsBoxOrBlockChar((char)chVal))
                {
                    double gridX = PadLeft + (visualCol * _cellWidth);
                    if (gridX > currentX) currentX = gridX;

                    colX[col] = (float)currentX;
                    byte bAttr = attrs[idx];
                    Typeface bTf = ((bAttr & 0x0F) >= 8) ? _boldTypeface : _normalTypeface;
                    var bFt = new FormattedText(
                        ((char)chVal).ToString(),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        bTf,
                        _fontSize,
                        GetForegroundBrush(bAttr),
                        _pixelsPerDip);
                    drawItems.Add(new RowDrawItem { TextObj = bFt, X = currentX });

                    currentX += _cellWidth;
                    visualCol++;
                    col++;
                    if (col < w && chars[rowOffset + col] == 0)
                    {
                        colX[col] = (float)currentX;
                        visualCol++;
                        col++;
                    }
                    continue;
                }

                // 5. Unbroken DirectWrite Text Run (NEVER split at cursor so Thai vowels/tone marks shape 100% intact!)
                byte attr = attrs[idx];
                int runStartCol = col;
                double runStartX = currentX;
                runBuilder.Length = 0;
                int baseAdvanceCount = 0;

                while (col < w)
                {
                    int cIdx = rowOffset + col;
                    ushort cv = chars[cIdx];

                    if (cv == 0)
                    {
                        visualCol++;
                        col++;
                        continue;
                    }
                    if (IsZeroWidthFormatChar(cv))
                    {
                        col++;
                        continue;
                    }

                    if (attrs[cIdx] != attr)
                        break;
                    if (IsBoxOrBlockChar((char)cv))
                        break;

                    if (cv == 32)
                    {
                        // Absorb single word space if followed immediately by another non-box printable char of same style
                        int peek = col + 1;
                        while (peek < w && (chars[rowOffset + peek] == 0 || IsZeroWidthFormatChar(chars[rowOffset + peek])))
                            peek++;

                        if (peek < w &&
                            chars[rowOffset + peek] > 32 &&
                            !IsBoxOrBlockChar((char)chars[rowOffset + peek]) &&
                            attrs[rowOffset + peek] == attr)
                        {
                            runBuilder.Append(' ');
                            baseAdvanceCount++;
                            visualCol++;
                            col++;
                            continue;
                        }
                        break;
                    }

                    char ch = (char)cv;
                    runBuilder.Append(ch);
                    if (!IsThaiCombiningMark(ch))
                    {
                        baseAdvanceCount++;
                        visualCol++;
                    }
                    col++;
                }

                if (runBuilder.Length > 0)
                {
                    Typeface tf = ((attr & 0x0F) >= 8) ? _boldTypeface : _normalTypeface;
                    var ft = new FormattedText(
                        runBuilder.ToString(),
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        tf,
                        _fontSize,
                        GetForegroundBrush(attr),
                        _pixelsPerDip);

                    double runWidth = ft.WidthIncludingTrailingWhitespace;
                    drawItems.Add(new RowDrawItem { TextObj = ft, X = runStartX });

                    double perBase = (baseAdvanceCount > 0) ? (runWidth / baseAdvanceCount) : 0.0;
                    double walkX = runStartX;
                    for (int c = runStartCol; c < col; c++)
                    {
                        colX[c] = (float)walkX;
                        ushort cv = chars[rowOffset + c];
                        if (cv != 0 && !IsZeroWidthFormatChar(cv) && !IsThaiCombiningMark((char)cv))
                        {
                            walkX += perBase;
                        }
                    }

                    currentX = runStartX + runWidth;

                    // Thai above/below vowels & tone marks (e.g. ิ ี ่ ้) are zero-advance glyphs whose ink overhangs
                    // PAST the run's advance width. If the block cursor sits right after this run, push it beyond the
                    // real ink bounds so the opaque cursor never paints over (hides) the freshly typed vowel!
                    if (realCurCol >= 0 && col == realCurCol)
                    {
                        double inkRight = currentX;
                        try
                        {
                            Geometry inkGeo = ft.BuildGeometry(new Point(runStartX, y));
                            if (inkGeo != null && !inkGeo.Bounds.IsEmpty)
                                inkRight = Math.Max(inkRight, inkGeo.Bounds.Right);
                        }
                        catch { }
                        cursorMinX = Math.Max(currentX, inkRight) + 1.5;
                    }
                }
                else
                {
                    for (int c = runStartCol; c < col; c++)
                        colX[c] = (float)currentX;
                }
            }

            colX[w] = (float)currentX;

            using (DrawingContext dc = visual.RenderOpen())
            {
                // Pass 1: Non-default background highlight spans using exact visual column coordinates
                int c = 0;
                while (c < w)
                {
                    int bgWin = (attrs[rowOffset + c] >> 4) & 0x0F;
                    if (bgWin == 0)
                    {
                        c++;
                        continue;
                    }

                    Brush bgBrush = _ansiBrushes[Win32ToAnsiColorIndex[bgWin]];
                    int startCol = c;
                    c++;
                    while (c < w && ((attrs[rowOffset + c] >> 4) & 0x0F) == bgWin)
                    {
                        c++;
                    }

                    double x1 = colX[startCol];
                    double x2 = Math.Max(x1 + 2.0, colX[c]);
                    dc.DrawRectangle(bgBrush, null, new Rect(x1, y, x2 - x1, _cellHeight));
                }

                // Pass 2: Foreground text runs & crisp box rules (drawn BEFORE cursor overlay so glyphs are never lost)
                for (int i = 0; i < drawItems.Count; i++)
                {
                    RowDrawItem item = drawItems[i];
                    if (item.IsHorizontalRule)
                    {
                        dc.DrawRectangle(
                            item.FgBrush,
                            null,
                            new Rect(item.X, Math.Round(y + (_cellHeight * 0.5)), item.Width, item.LineThickness));
                    }
                    else if (item.TextObj != null)
                    {
                        dc.DrawText(item.TextObj, new Point(item.X, y));
                    }
                }

                // Pass 3: Non-destructive cursor positioned at exact Thai-aware cell index (realCurCol)
                if (realCurCol >= 0 && realCurCol < w)
                {
                    double cx = colX[realCurCol];
                    ushort curCh = chars[rowOffset + realCurCol];
                    if (realCurCol >= lastContentEnd || curCh <= 32 || IsZeroWidthFormatChar(curCh))
                    {
                        if (cursorMinX > cx)
                            cx = cursorMinX;
                        if (realCurCol > 0 && IsThaiCombiningMark((char)chars[rowOffset + realCurCol - 1]))
                        {
                            cx = Math.Max(cx, colX[realCurCol - 1] + _cellWidth + 1.5);
                        }
                        double cw = Math.Max(6.5, _cellWidth * 0.85);
                        dc.DrawRectangle(_cursorBrush, null, new Rect(cx, y, cw, _cellHeight));
                    }
                    else
                    {
                        // Cursor is inside existing text: span full Thai cluster with translucent highlight + crisp 2px caret bar
                        int clusterEnd = realCurCol + 1;
                        while (clusterEnd < w)
                        {
                            ushort nextCh = chars[rowOffset + clusterEnd];
                            if (nextCh == 0 || IsZeroWidthFormatChar(nextCh) || IsThaiCombiningMark((char)nextCh))
                                clusterEnd++;
                            else
                                break;
                        }
                        double cw = Math.Max(6.0, colX[clusterEnd] - cx);
                        if (_cursorTranslucentBrush != null)
                        {
                            dc.DrawRectangle(_cursorTranslucentBrush, null, new Rect(cx, y, cw, _cellHeight));
                        }
                        dc.DrawRectangle(_cursorBrush, null, new Rect(cx, y, 2.0, _cellHeight));
                    }
                }
            }
        }

        private Brush GetForegroundBrush(byte attr)
        {
            int fgWin = attr & 0x0F;
            return (fgWin == 7) ? _fgBrush : _ansiBrushes[Win32ToAnsiColorIndex[fgWin]];
        }

        private static bool IsZeroWidthFormatChar(ushort ch)
        {
            // U+200B (ZWSP) is injected before U+0E33 ('ำ') by Win10 1809 conhost.exe!
            return ch == 0x200B || ch == 0x200C || ch == 0x200D || ch == 0xFEFF || ch == 0x2060;
        }

        public static bool IsThaiCombiningMark(char ch)
        {
            return ch == '\u0E31' ||
                   (ch >= '\u0E34' && ch <= '\u0E3A') ||
                   (ch >= '\u0E47' && ch <= '\u0E4E');
        }

        private static bool IsThaiCombiningOrAm(char ch)
        {
            return ch == '\u0E33' || IsThaiCombiningMark(ch);
        }

        private static bool IsBoxOrBlockChar(char ch)
        {
            return ch >= '\u2500' && ch <= '\u259F';
        }

        // --- Mouse Text Selection ---

        public void PointToCell(Point pt, out int row, out int col)
        {
            int r = (int)Math.Floor((pt.Y - PadTop) / _cellHeight);
            row = Math.Max(0, Math.Min((int)_h - 1, r));

            if (_rowColVisualX != null && row >= 0 && row < _rowColVisualX.Length && _rowColVisualX[row] != null)
            {
                float[] colX = _rowColVisualX[row];
                double px = pt.X;
                if (px <= colX[0])
                {
                    col = 0;
                    return;
                }
                for (int c = 0; c < _w; c++)
                {
                    if (px < colX[c + 1])
                    {
                        col = c;
                        return;
                    }
                }
                col = Math.Max(0, (int)_w - 1);
                return;
            }

            int fallbackC = (int)Math.Floor((pt.X - PadLeft) / _cellWidth);
            col = Math.Max(0, Math.Min((int)_w - 1, fallbackC));
        }

        public bool IsSelecting { get { return _isSelecting; } }
        public event Action SelectionDragStarted;
        public event Action SelectionDragEnded;

        public void UpdateDragSelection(Point pt)
        {
            if (!_isSelecting || !_hasRealFrame)
                return;

            int r, c;
            PointToCell(pt, out r, out c);
            if (r != _selEndRow || c != _selEndCol)
            {
                _selEndRow = r;
                _selEndCol = c;
                _hasSelection = (_selAnchorRow != _selEndRow || _selAnchorCol != _selEndCol);
                RenderSelectionOverlay();
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();

            if (!_hasRealFrame || _chars == null)
                return;

            int r, c;
            PointToCell(e.GetPosition(this), out r, out c);

            if (e.ClickCount == 2)
            {
                SelectWordAt(r, c);
                e.Handled = true;
                return;
            }

            _isSelecting = true;
            _hasSelection = false;
            _selAnchorRow = r;
            _selAnchorCol = c;
            _selEndRow = r;
            _selEndCol = c;
            CaptureMouse();
            RenderSelectionOverlay();
            if (SelectionDragStarted != null) SelectionDragStarted();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_isSelecting || !_hasRealFrame)
                return;

            UpdateDragSelection(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_isSelecting)
            {
                _isSelecting = false;
                ReleaseMouseCapture();
                if (SelectionDragEnded != null) SelectionDragEnded();
                e.Handled = true;
            }
        }

        private void SelectWordAt(int row, int col)
        {
            if (_chars == null || row < 0 || row >= _h) return;
            int offset = row * _w;
            int left = col;
            int right = col;
            while (left > 0 && _chars[offset + left - 1] > 32) left--;
            while (right + 1 < _w && _chars[offset + right + 1] > 32) right++;

            _selAnchorRow = row;
            _selAnchorCol = left;
            _selEndRow = row;
            _selEndCol = right;
            _hasSelection = (right >= left && _chars[offset + left] > 32);
            RenderSelectionOverlay();
        }

        public void ClearSelection()
        {
            if (!_hasSelection) return;
            _hasSelection = false;
            RenderSelectionOverlay();
        }

        private void GetNormalizedSelection(out int r1, out int c1, out int r2, out int c2)
        {
            if (_selAnchorRow < _selEndRow || (_selAnchorRow == _selEndRow && _selAnchorCol <= _selEndCol))
            {
                r1 = _selAnchorRow; c1 = _selAnchorCol;
                r2 = _selEndRow; c2 = _selEndCol;
            }
            else
            {
                r1 = _selEndRow; c1 = _selEndCol;
                r2 = _selAnchorRow; c2 = _selAnchorCol;
            }
        }

        private void RenderSelectionOverlay()
        {
            if (!_hasRealFrame)
                return;

            using (DrawingContext dc = _overlayVisual.RenderOpen())
            {
                if (!_hasSelection || _selectionBrush == null)
                    return;

                int r1, c1, r2, c2;
                GetNormalizedSelection(out r1, out c1, out r2, out c2);

                for (int r = r1; r <= r2; r++)
                {
                    int startC = Math.Max(0, (r == r1) ? c1 : 0);
                    int endC = Math.Min(_w - 1, (r == r2) ? c2 : (_w - 1));
                    if (endC < startC) continue;

                    double x1;
                    double x2;
                    if (_rowColVisualX != null && r < _rowColVisualX.Length && _rowColVisualX[r] != null)
                    {
                        x1 = _rowColVisualX[r][startC];
                        x2 = Math.Max(x1 + 4.0, _rowColVisualX[r][endC + 1]);
                    }
                    else
                    {
                        x1 = PadLeft + (startC * _cellWidth);
                        x2 = PadLeft + ((endC + 1) * _cellWidth);
                    }

                    dc.DrawRectangle(
                        _selectionBrush,
                        null,
                        new Rect(x1, PadTop + (r * _cellHeight), x2 - x1, _cellHeight));
                }
            }
        }

        public string GetSelectedText()
        {
            if (!_hasSelection || !_hasRealFrame || _chars == null)
                return string.Empty;

            int r1, c1, r2, c2;
            GetNormalizedSelection(out r1, out c1, out r2, out c2);

            var sb = new StringBuilder();
            var lineSb = new StringBuilder(_w);
            for (int r = r1; r <= r2 && r < _h; r++)
            {
                int startC = Math.Max(0, (r == r1) ? c1 : 0);
                int endC = Math.Min(_w - 1, (r == r2) ? c2 : (_w - 1));
                lineSb.Length = 0;
                int offset = r * _w;
                for (int c = startC; c <= endC; c++)
                {
                    ushort ch = _chars[offset + c];
                    if (ch == 0 || IsZeroWidthFormatChar(ch)) continue;
                    lineSb.Append(ch < 32 ? ' ' : (char)ch);
                }
                if (r > r1) sb.AppendLine();
                sb.Append(lineSb.ToString().TrimEnd());
            }
            return sb.ToString();
        }
    }

    public class TuiSurfaceControl : Border
    {
        private readonly ScrollViewer _scrollViewer;
        private readonly TuiCanvasElement _canvas;
        private NamedPipeServerStream _pipeServer;
        private BlockingCollection<byte[]> _outgoingQueue;
        private Process _workerProcess;
        private TerminalTheme _theme;
        private TerminalSettings _settings;
        private volatile bool _closed;
        private DateTime _sessionStartUtc;

        private TuiFrameData _latestPendingFrame;
        private int _renderQueued;
        private readonly DispatcherTimer _resizeDebounceTimer;
        private readonly DispatcherTimer _dragAutoScrollTimer;
        private short _lastSentCols;
        private short _lastSentRows;
        private short _lastRenderedHeight;
        private double _targetVerticalOffset = -1.0;
        private bool _pinnedToBottom = true;
        private bool _suppressScrollChangedPinUpdate;
        private bool _childMouseInputEnabled;

        private const uint LEFT_CTRL_PRESSED = 0x0008;
        private const uint LEFT_ALT_PRESSED = 0x0002;
        private const uint SHIFT_PRESSED = 0x0010;

        public event Action<TuiExitResult> TuiExited;
        public event Action<string> StatusToast;

        public TuiSurfaceControl(TerminalTheme theme, TerminalSettings settings)
        {
            _theme = theme;
            _settings = settings;
            Focusable = true;

            _canvas = new TuiCanvasElement(theme, settings);
            _canvas.PreviewKeyDown += OnSurfacePreviewKeyDown;
            _canvas.PreviewTextInput += OnSurfacePreviewTextInput;
            _canvas.PreviewMouseRightButtonUp += OnSurfaceRightClick;
            _canvas.SelectionDragStarted += () =>
            {
                if (_dragAutoScrollTimer != null)
                    _dragAutoScrollTimer.Start();
            };
            _canvas.SelectionDragEnded += () =>
            {
                if (_dragAutoScrollTimer != null)
                    _dragAutoScrollTimer.Stop();
            };

            _dragAutoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            _dragAutoScrollTimer.Tick += OnDragAutoScrollTick;

            _scrollViewer = new ScrollViewer
            {
                Content = _canvas,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0),
                Focusable = false
            };
            _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;

            // Tunnel mouse-wheel / precision-touchpad scroll at TuiSurfaceControl level (above ScrollViewer)
            PreviewMouseWheel += OnSurfaceMouseWheel;

            Child = _scrollViewer;
            Background = TerminalTheme.BrushFromHex(theme.Background);

            _resizeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            _resizeDebounceTimer.Tick += (s, e) =>
            {
                _resizeDebounceTimer.Stop();
                SendCurrentViewportResizeIfNeeded();
            };

            SizeChanged += (s, e) =>
            {
                if (!_closed)
                {
                    _resizeDebounceTimer.Stop();
                    _resizeDebounceTimer.Start();
                }
            };
        }

        public void ApplyTheme(TerminalTheme theme, TerminalSettings settings)
        {
            _theme = theme;
            _settings = settings;
            Background = TerminalTheme.BrushFromHex(theme.Background);
            _canvas.ApplyThemeAndMetrics(theme, settings);
            SendCurrentViewportResizeIfNeeded();
        }

        public void ComputeViewportGrid(out short cols, out short rows)
        {
            ComputeViewportGridForDimensions(ActualWidth, ActualHeight, out cols, out rows);
        }

        public void ComputeViewportGridForDimensions(double w, double h, out short cols, out short rows)
        {
            if (w <= 100 || h <= 60)
            {
                cols = 100;
                rows = 28;
                return;
            }

            double cw = Math.Max(6.0, _canvas.CellWidth);
            double ch = Math.Max(14.0, _canvas.CellHeight);
            double scrollBarReserve = Math.Max(18.0, SystemParameters.VerticalScrollBarWidth) + 6.0;
            cols = (short)Math.Max(40, Math.Min(240, (int)Math.Floor((w - (TuiCanvasElement.PadLeft * 2) - scrollBarReserve) / cw)));
            rows = (short)Math.Max(10, Math.Min(100, (int)Math.Floor((h - (TuiCanvasElement.PadTop * 2) - 4) / ch)));
        }

        private void SendCurrentViewportResizeIfNeeded()
        {
            if (_closed) return;
            if (ActualWidth <= 100 || ActualHeight <= 60) return;
            short cols, rows;
            ComputeViewportGrid(out cols, out rows);
            if (cols == _lastSentCols && rows == _lastSentRows)
                return;

            _lastSentCols = cols;
            _lastSentRows = rows;

            byte[] pkt = new byte[5];
            pkt[0] = 3;
            pkt[1] = (byte)(cols & 0xFF);
            pkt[2] = (byte)((cols >> 8) & 0xFF);
            pkt[3] = (byte)(rows & 0xFF);
            pkt[4] = (byte)((rows >> 8) & 0xFF);
            EnqueuePacket(pkt);
        }

        private static NamedPipeServerStream CreateUserRestrictedPipeServer(string pipeName)
        {
            try
            {
                var pipeSec = new PipeSecurity();
                SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User;
                if (currentUser != null)
                {
                    pipeSec.AddAccessRule(new PipeAccessRule(
                        currentUser,
                        PipeAccessRights.FullControl,
                        AccessControlType.Allow));
                }
                return new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    65536,
                    65536,
                    pipeSec);
            }
            catch
            {
                return new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
        }

        public void StartTuiCommand(string commandLine, string workingDirectory, short cols = 120, short rows = 30)
        {
            _closed = false;
            _sessionStartUtc = DateTime.UtcNow;
            _lastSentCols = cols;
            _lastSentRows = rows;
            _lastRenderedHeight = 0;
            _targetVerticalOffset = -1.0;
            _pinnedToBottom = true;
            _childMouseInputEnabled = false;

            _canvas.ShowLoadingState(commandLine);

            string pipeName = "wt_ltsc_tui_" + SystemIntegration.GenerateSecureHexToken(16);
            _pipeServer = CreateUserRestrictedPipeServer(pipeName);
            _outgoingQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());

            string exePath = typeof(TuiSurfaceControl).Assembly.Location;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WindowsTerminalLTSC.exe");
            if (!File.Exists(exePath))
                exePath = SystemIntegration.GetExecutablePath();

            string dirB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(workingDirectory ?? Environment.CurrentDirectory));
            string cmdB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(commandLine));

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Format("--tui-host {0} {1} {2} {3} {4}", pipeName, cols, rows, dirB64, cmdB64),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };

            _workerProcess = Process.Start(psi);

            var serverRef = _pipeServer;
            var queueRef = _outgoingQueue;

            // Dedicated non-blocking writer thread with frame-aligned text coalescing (0ms first-key latency, 0 mid-render drops!)
            var writerThread = new Thread(() =>
            {
                try
                {
                    int lastTextSendTick = unchecked(Environment.TickCount - 100);
                    byte[] pendingPkt = null;
                    while (!_closed)
                    {
                        byte[] packet = pendingPkt;
                        pendingPkt = null;
                        if (packet == null)
                        {
                            if (!queueRef.TryTake(out packet, Timeout.Infinite))
                                break;
                        }

                        if (_closed || serverRef == null || !serverRef.IsConnected || packet == null)
                            continue;

                        if (packet.Length > 1 && packet[0] == 2)
                        {
                            byte[] nextPkt;
                            if (queueRef.TryTake(out nextPkt, 0))
                            {
                                var sb = new StringBuilder(DecodeTextPacketPayload(packet));
                                while (nextPkt != null)
                                {
                                    if (nextPkt.Length > 1 && nextPkt[0] == 2)
                                    {
                                        sb.Append(DecodeTextPacketPayload(nextPkt));
                                    }
                                    else
                                    {
                                        pendingPkt = nextPkt;
                                        break;
                                    }
                                    if (!queueRef.TryTake(out nextPkt, 0))
                                        break;
                                }

                                byte[] merged = EncodeTextPacket(sb.ToString());
                                serverRef.Write(merged, 0, merged.Length);
                                lastTextSendTick = Environment.TickCount;
                            }
                            else
                            {
                                // Single interactive keystroke: 0ms delay, instantaneous delivery to TUI
                                serverRef.Write(packet, 0, packet.Length);
                                lastTextSendTick = Environment.TickCount;
                            }
                        }
                        else
                        {
                            serverRef.Write(packet, 0, packet.Length);
                        }
                    }
                }
                catch
                {
                }
            })
            {
                IsBackground = true,
                Name = "WT_TUI_PipeWriter"
            };

            var readerThread = new Thread(() =>
            {
                int exitCode = 0;
                string finalConsoleText = string.Empty;
                try
                {
                    serverRef.WaitForConnection();
                    writerThread.Start();

                    // Immediately sync exact viewport dimensions once layout has settled
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SendCurrentViewportResizeIfNeeded));

                    byte[] hdrBuf = new byte[9];
                    byte[] charByteBuf = null;
                    var br = new BinaryReader(serverRef, Encoding.UTF8);

                    while (!_closed && serverRef.IsConnected)
                    {
                        int pkt = serverRef.ReadByte();
                        if (pkt < 0) break;

                        if (pkt == 1) // Bulk-packed screen + scrollback frame
                        {
                            if (!ReadExact(serverRef, hdrBuf, 9))
                                break;

                            short w = (short)(hdrBuf[0] | (hdrBuf[1] << 8));
                            short h = (short)(hdrBuf[2] | (hdrBuf[3] << 8));
                            short curX = (short)(hdrBuf[4] | (hdrBuf[5] << 8));
                            short curY = (short)(hdrBuf[6] | (hdrBuf[7] << 8));
                            byte flags = hdrBuf[8];

                            if (w < 1 || w > 400 || h < 1 || h > TuiHostWorker.MaxStreamedScrollbackRows + 100)
                                break;

                            int count = w * h;
                            int charBytesLen = count * 2;
                            if (charByteBuf == null || charByteBuf.Length < charBytesLen)
                                charByteBuf = new byte[charBytesLen];

                            if (!ReadExact(serverRef, charByteBuf, charBytesLen))
                                break;

                            byte[] attrs = new byte[count];
                            if (!ReadExact(serverRef, attrs, count))
                                break;

                            ushort[] chars = new ushort[count];
                            Buffer.BlockCopy(charByteBuf, 0, chars, 0, charBytesLen);

                            var frame = new TuiFrameData
                            {
                                Width = w,
                                Height = h,
                                CursorX = curX,
                                CursorY = curY,
                                Flags = flags,
                                Chars = chars,
                                Attrs = attrs
                            };

                            // Coalesce frames & schedule at DispatcherPriority.Render for tear-free 60-144 FPS V-Sync smoothness
                            Interlocked.Exchange(ref _latestPendingFrame, frame);
                            if (Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
                            {
                                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(ProcessCoalescedFrame));
                            }
                        }
                        else if (pkt == 2) // Exited with captured Main Screen Buffer text
                        {
                            exitCode = br.ReadInt32();
                            finalConsoleText = br.ReadString();
                            break;
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    NotifyClosed(exitCode, finalConsoleText);
                }
            })
            {
                IsBackground = true,
                Name = "WT_TUI_PipeReader"
            };
            readerThread.Start();

            FocusSurface();
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                    return false;
                offset += read;
            }
            return true;
        }

        private void ProcessCoalescedFrame()
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            if (_closed) return;

            TuiFrameData frame = Interlocked.Exchange(ref _latestPendingFrame, null);
            if (frame != null)
            {
                _childMouseInputEnabled = (frame.Flags & 1) != 0;
                bool heightChanged = (frame.Height != _lastRenderedHeight);
                _canvas.RenderFrame(frame);
                _lastRenderedHeight = frame.Height;

                if (_pinnedToBottom && (heightChanged || _scrollViewer.VerticalOffset < _scrollViewer.ScrollableHeight - 0.5))
                {
                    ScrollToBottomInternal(heightChanged);
                }
            }
        }

        private void ScrollToBottomInternal(bool forceLayoutPass)
        {
            _targetVerticalOffset = -1.0;
            _suppressScrollChangedPinUpdate = true;
            try
            {
                if (forceLayoutPass)
                {
                    _scrollViewer.UpdateLayout();
                }
                _scrollViewer.ScrollToBottom();
            }
            finally
            {
                _suppressScrollChangedPinUpdate = false;
            }
        }

        private void SnapToBottomForInput()
        {
            _pinnedToBottom = true;
            _targetVerticalOffset = -1.0;
            if (_scrollViewer.ScrollableHeight > 0.5 && _scrollViewer.VerticalOffset < _scrollViewer.ScrollableHeight - 0.5)
            {
                ScrollToBottomInternal(false);
            }
        }

        private void OnScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_suppressScrollChangedPinUpdate)
                return;

            if (e.ExtentHeightChange != 0.0)
            {
                if (_pinnedToBottom)
                {
                    ScrollToBottomInternal(false);
                }
                return;
            }

            if (e.VerticalChange != 0.0)
            {
                if (_targetVerticalOffset >= 0.0 && Math.Abs(_scrollViewer.VerticalOffset - _targetVerticalOffset) <= 1.0)
                {
                    // Layout finished applying our queued touchpad targetOffset
                    return;
                }

                // User dragged the scrollbar thumb directly
                _targetVerticalOffset = -1.0;
                if (_scrollViewer.ScrollableHeight <= 0.5)
                {
                    _pinnedToBottom = true;
                }
                else
                {
                    _pinnedToBottom = (_scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - (_canvas.CellHeight * 0.75));
                }
            }
        }

        private void EnqueuePacket(byte[] packet)
        {
            if (_closed || packet == null) return;
            var q = _outgoingQueue;
            if (q != null && !q.IsAddingCompleted)
            {
                try { q.Add(packet); } catch { }
            }
        }

        private void OnSurfaceRightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_canvas.HasSelection)
            {
                CopySelectedTextFromCanvas();
            }
            else
            {
                PasteClipboardToTui();
            }
        }

        private void OnSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                return; // Let MainWindow handle Ctrl+Wheel zoom

            // Always mark handled so WPF's coarse line-jumping ScrollViewer never fires,
            // and NEVER convert touchpad/mouse-wheel scrolling into Up/Down arrow keys!
            e.Handled = true;

            if (_scrollViewer.ScrollableHeight > 0.5)
            {
                // Accumulate against _targetVerticalOffset so sub-frame 200Hz+ Precision Touchpad bursts lose ZERO ticks!
                double baseOffset = (_targetVerticalOffset >= 0.0)
                    ? Math.Min(_scrollViewer.ScrollableHeight, _targetVerticalOffset)
                    : _scrollViewer.VerticalOffset;
                double stepPixels = (e.Delta / 120.0) * (_canvas.CellHeight * 3.0);
                double targetOffset = baseOffset - stepPixels;
                if (targetOffset < 0.0)
                    targetOffset = 0.0;
                if (targetOffset > _scrollViewer.ScrollableHeight)
                    targetOffset = _scrollViewer.ScrollableHeight;

                _targetVerticalOffset = targetOffset;
                _suppressScrollChangedPinUpdate = true;
                try
                {
                    _scrollViewer.ScrollToVerticalOffset(targetOffset);
                }
                finally
                {
                    _suppressScrollChangedPinUpdate = false;
                }

                if (e.Delta > 0 && targetOffset < _scrollViewer.ScrollableHeight - 0.5)
                {
                    // Immediately unpin as soon as the user gently glides upward on a Precision Touchpad!
                    _pinnedToBottom = false;
                }
                else if (targetOffset >= _scrollViewer.ScrollableHeight - 2.0)
                {
                    _pinnedToBottom = true;
                    _targetVerticalOffset = -1.0;
                }
            }
            else if (_childMouseInputEnabled)
            {
                // True Alternate-Screen mouse-enabled TUI (e.g. vim/htop/btop with ENABLE_MOUSE_INPUT):
                // forward native Win32 MOUSE_WHEELED event instead of arrow keys.
                int row, col;
                _canvas.PointToCell(e.GetPosition(_canvas), out row, out col);
                SendMouseWheelEvent((short)e.Delta, (short)col, (short)row);
            }
        }

        private void CopySelectedTextFromCanvas()
        {
            string selected = _canvas.GetSelectedText();
            _canvas.ClearSelection();
            if (!string.IsNullOrEmpty(selected))
            {
                try
                {
                    Clipboard.SetText(selected);
                    if (StatusToast != null) StatusToast("คัดลอกข้อความจากหน้าจอ TUI แล้ว");
                }
                catch { }
            }
        }

        private void OnSurfacePreviewKeyDown(object sender, KeyEventArgs e)
        {
            ModifierKeys mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;

            uint ctrlState = 0;
            if (ctrl) ctrlState |= LEFT_CTRL_PRESSED;
            if (alt) ctrlState |= LEFT_ALT_PRESSED;
            if (shift) ctrlState |= SHIFT_PRESSED;

            Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;

            // Shift+PageUp / Shift+PageDown scrolls conversation history smoothly
            if (shift && !ctrl && !alt && (key == Key.PageUp || key == Key.PageDown) && _scrollViewer.ScrollableHeight > 0.5)
            {
                e.Handled = true;
                double baseOffset = (_targetVerticalOffset >= 0.0)
                    ? Math.Min(_scrollViewer.ScrollableHeight, _targetVerticalOffset)
                    : _scrollViewer.VerticalOffset;
                double pageStep = Math.Max(_canvas.CellHeight * 3.0, _scrollViewer.ViewportHeight * 0.85);
                double targetOffset = (key == Key.PageUp)
                    ? Math.Max(0.0, baseOffset - pageStep)
                    : Math.Min(_scrollViewer.ScrollableHeight, baseOffset + pageStep);

                _targetVerticalOffset = targetOffset;
                _suppressScrollChangedPinUpdate = true;
                try
                {
                    _scrollViewer.ScrollToVerticalOffset(targetOffset);
                }
                finally
                {
                    _suppressScrollChangedPinUpdate = false;
                }

                if (key == Key.PageUp && targetOffset < _scrollViewer.ScrollableHeight - 0.5)
                {
                    _pinnedToBottom = false;
                }
                else if (targetOffset >= _scrollViewer.ScrollableHeight - 2.0)
                {
                    _pinnedToBottom = true;
                    _targetVerticalOffset = -1.0;
                }
                return;
            }

            // Ctrl+V or Shift+Insert pastes into TUI
            if ((ctrl && !alt && key == Key.V) || (shift && !ctrl && !alt && key == Key.Insert))
            {
                e.Handled = true;
                PasteClipboardToTui();
                return;
            }

            // Ctrl+Shift+C always copies selection
            if (ctrl && shift && !alt && key == Key.C)
            {
                e.Handled = true;
                CopySelectedTextFromCanvas();
                return;
            }

            // Ctrl+C copies selection if text is selected, otherwise sends Ctrl+C (\x03) to TUI
            if (ctrl && !alt && !shift && key == Key.C)
            {
                e.Handled = true;
                if (_canvas.HasSelection)
                {
                    CopySelectedTextFromCanvas();
                }
                else
                {
                    SendKeyEvent(0x43, '\x03', ctrlState);
                }
                return;
            }

            // Ctrl+A..Z control characters (e.g. Ctrl+D = \x04, Ctrl+W = \x17 delete word)
            if (ctrl && !alt && !shift && key >= Key.A && key <= Key.Z)
            {
                e.Handled = true;
                int idx = (int)key - (int)Key.A + 1;
                ushort vk = (ushort)(0x41 + ((int)key - (int)Key.A));
                SendKeyEvent(vk, (char)idx, ctrlState);
                return;
            }

            ushort specialVk = 0;
            char specialChar = '\0';
            switch (key)
            {
                case Key.Return: specialVk = 0x0D; specialChar = '\r'; break;
                case Key.Back: specialVk = 0x08; specialChar = '\b'; break;
                case Key.Tab: specialVk = 0x09; specialChar = '\t'; break;
                case Key.Escape:
                    if (_canvas.HasSelection)
                    {
                        _canvas.ClearSelection();
                        e.Handled = true;
                        return;
                    }
                    specialVk = 0x1B;
                    specialChar = '\x1b';
                    break;
                case Key.Up: specialVk = 0x26; break;
                case Key.Down: specialVk = 0x28; break;
                case Key.Left: specialVk = 0x25; break;
                case Key.Right: specialVk = 0x27; break;
                case Key.Home: specialVk = 0x24; break;
                case Key.End: specialVk = 0x23; break;
                case Key.PageUp: specialVk = 0x21; break;
                case Key.PageDown: specialVk = 0x22; break;
                case Key.Delete: specialVk = 0x2E; break;
                case Key.Insert: specialVk = 0x2D; break;
                case Key.F1: specialVk = 0x70; break;
                case Key.F2: specialVk = 0x71; break;
                case Key.F3: specialVk = 0x72; break;
                case Key.F4: specialVk = 0x73; break;
                case Key.F5: specialVk = 0x74; break;
                case Key.F6: specialVk = 0x75; break;
                case Key.F7: specialVk = 0x76; break;
                case Key.F8: specialVk = 0x77; break;
                case Key.F9: specialVk = 0x78; break;
                case Key.F10: specialVk = 0x79; break;
                case Key.F11: specialVk = 0x7A; break;
                case Key.F12: specialVk = 0x7B; break;
            }

            if (specialVk != 0)
            {
                e.Handled = true;
                if (key == Key.Return || key == Key.Back)
                {
                    SnapToBottomForInput();
                }
                SendKeyEvent(specialVk, specialChar, ctrlState);
            }
        }

        private void OnSurfacePreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                return;

            if (!string.IsNullOrEmpty(e.Text))
            {
                if (e.Text.Length == 1 && e.Text[0] < 32)
                    return;

                e.Handled = true;
                _canvas.ClearSelection();
                SnapToBottomForInput();
                SendText(e.Text);
            }
        }

        public void PasteClipboardToTui()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string clip = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(clip))
                    {
                        SnapToBottomForInput();
                        SendText(clip);
                    }
                }
            }
            catch { }
        }

        public void SendMouseWheelEvent(short wheelDelta, short col, short row)
        {
            if (_closed || wheelDelta == 0) return;
            byte[] pkt = new byte[7];
            pkt[0] = 4;
            pkt[1] = (byte)(wheelDelta & 0xFF);
            pkt[2] = (byte)((wheelDelta >> 8) & 0xFF);
            pkt[3] = (byte)(col & 0xFF);
            pkt[4] = (byte)((col >> 8) & 0xFF);
            pkt[5] = (byte)(row & 0xFF);
            pkt[6] = (byte)((row >> 8) & 0xFF);
            EnqueuePacket(pkt);
        }

        public void SendKeyEvent(ushort vk, char ch, uint ctrlState)
        {
            if (_closed) return;
            byte[] pkt = new byte[9];
            pkt[0] = 1;
            pkt[1] = (byte)(vk & 0xFF);
            pkt[2] = (byte)((vk >> 8) & 0xFF);
            ushort cu = (ushort)ch;
            pkt[3] = (byte)(cu & 0xFF);
            pkt[4] = (byte)((cu >> 8) & 0xFF);
            pkt[5] = (byte)(ctrlState & 0xFF);
            pkt[6] = (byte)((ctrlState >> 8) & 0xFF);
            pkt[7] = (byte)((ctrlState >> 16) & 0xFF);
            pkt[8] = (byte)((ctrlState >> 24) & 0xFF);
            EnqueuePacket(pkt);
        }

        private static byte[] EncodeTextPacket(string text)
        {
            using (var ms = new MemoryStream(text.Length * 3 + 8))
            using (var bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write((byte)2);
                bw.Write(text);
                bw.Flush();
                return ms.ToArray();
            }
        }

        private static string DecodeTextPacketPayload(byte[] packet)
        {
            try
            {
                using (var ms = new MemoryStream(packet, 1, packet.Length - 1, false))
                using (var br = new BinaryReader(ms, Encoding.UTF8))
                {
                    return br.ReadString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        public void SendText(string text)
        {
            if (_closed || string.IsNullOrEmpty(text)) return;
            EnqueuePacket(EncodeTextPacket(text));
        }

        public void FocusSurface()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                _canvas.Focus();
            }));
        }

        // Sends graceful Ctrl+D exit so agy prints its resume block cleanly, falling back to StopTui after 900ms
        public void RequestGracefulExit()
        {
            if (_closed) return;
            EnqueuePacket(new byte[] { 8 });

            var fallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
            fallbackTimer.Tick += (s, e) =>
            {
                fallbackTimer.Stop();
                if (!_closed)
                {
                    StopTui();
                }
            };
            fallbackTimer.Start();
        }

        private void OnDragAutoScrollTick(object sender, EventArgs e)
        {
            if (_closed || _canvas == null || !_canvas.IsSelecting)
            {
                if (_dragAutoScrollTimer != null) _dragAutoScrollTimer.Stop();
                return;
            }

            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                if (_dragAutoScrollTimer != null) _dragAutoScrollTimer.Stop();
                return;
            }

            Point ptViewer = Mouse.GetPosition(_scrollViewer);
            double viewH = _scrollViewer.ViewportHeight;
            if (viewH <= 0.0) viewH = _scrollViewer.ActualHeight;

            double scrollDelta = 0.0;
            const double EdgeMargin = 28.0;

            if (ptViewer.Y > viewH - EdgeMargin)
            {
                // Dragging near or below bottom of viewport -> auto-scroll DOWN
                double dist = ptViewer.Y - (viewH - EdgeMargin);
                scrollDelta = Math.Max(_canvas.CellHeight, dist * 0.95);
            }
            else if (ptViewer.Y < EdgeMargin)
            {
                // Dragging near or above top of viewport -> auto-scroll UP
                double dist = EdgeMargin - ptViewer.Y;
                scrollDelta = -Math.Max(_canvas.CellHeight, dist * 0.95);
            }

            if (Math.Abs(scrollDelta) > 0.1 && _scrollViewer.ScrollableHeight > 0.0)
            {
                double targetOffset = Math.Max(0.0, Math.Min(_scrollViewer.ScrollableHeight, _scrollViewer.VerticalOffset + scrollDelta));
                if (Math.Abs(targetOffset - _scrollViewer.VerticalOffset) > 0.1)
                {
                    _suppressScrollChangedPinUpdate = true;
                    try
                    {
                        _scrollViewer.ScrollToVerticalOffset(targetOffset);
                    }
                    finally
                    {
                        _suppressScrollChangedPinUpdate = false;
                    }

                    if (targetOffset >= _scrollViewer.ScrollableHeight - 2.0)
                    {
                        _pinnedToBottom = true;
                    }
                    else
                    {
                        _pinnedToBottom = false;
                    }

                    Point ptCanvas = Mouse.GetPosition(_canvas);
                    _canvas.UpdateDragSelection(ptCanvas);
                }
            }
        }

        public void StopTui()
        {
            if (_closed) return;
            _resizeDebounceTimer.Stop();
            if (_dragAutoScrollTimer != null) _dragAutoScrollTimer.Stop();

            try
            {
                if (_pipeServer != null && _pipeServer.IsConnected)
                {
                    _pipeServer.WriteByte(9);
                }
            }
            catch { }

            try
            {
                if (_workerProcess != null && !_workerProcess.HasExited)
                {
                    TuiHostWorker.KillProcessTree(_workerProcess.Id);
                }
            }
            catch { }

            NotifyClosed(0, string.Empty);
        }

        private void NotifyClosed(int exitCode, string finalConsoleText)
        {
            if (_closed) return;
            _closed = true;

            _resizeDebounceTimer.Stop();
            if (_dragAutoScrollTimer != null) _dragAutoScrollTimer.Stop();

            try
            {
                if (_outgoingQueue != null)
                    _outgoingQueue.CompleteAdding();
            }
            catch { }

            try { if (_pipeServer != null) _pipeServer.Dispose(); } catch { }
            _pipeServer = null;

            DateTime startUtc = _sessionStartUtc;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (TuiExited != null)
                {
                    TuiExited(new TuiExitResult
                    {
                        ExitCode = exitCode,
                        FinalConsoleText = finalConsoleText ?? string.Empty,
                        SessionStartUtc = startUtc
                    });
                }
            }));
        }
    }
}
