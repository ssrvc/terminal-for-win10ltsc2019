using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace WindowsTerminalLTSC
{
    public enum ShellType
    {
        CommandPrompt,
        PowerShell
    }

    public class TerminalSession : IDisposable
    {
        public string SessionId { get; private set; }
        public ShellType Shell { get; private set; }
        public string CurrentDirectory { get; private set; }
        public int CodePage { get; private set; }
        public bool IsBusy { get; private set; }
        public string CurrentCommand { get; private set; }
        public DateTime CommandStartTime { get; private set; }
        public List<string> CommandHistory { get; private set; }
        public int HistoryIndex { get; set; }

        private Process _process;
        private Stream _stdinStream;
        private bool _disposed;
        private int _cmdSeq;
        private readonly List<string> _tempFiles = new List<string>();
        private readonly HashSet<string> _knownPathEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _tabMatchPrefix = null;
        private List<string> _tabMatches = null;
        private int _tabMatchIndex = 0;

        public event Action<string, bool> OutputReceived; // (text, isError)
        public event Action<string, string> PromptReady;  // (promptText, currentDir)
        public event Action<string> CommandStarted;       // (commandLine)
        public event Action ClearRequested;
        public event Action SessionExited;

        private const string PwdMarkerStart = "__WT_PWD__";
        private const string PwdMarkerEnd = "__WT_END__";

        public TerminalSession(ShellType shell, string initialDirectory, int codePage = 65001)
        {
            SessionId = SystemIntegration.GenerateSecureHexToken(8);
            Shell = shell;
            CurrentDirectory = string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : initialDirectory;
            CodePage = codePage;
            CommandHistory = LoadPersistentHistory();
            HistoryIndex = CommandHistory.Count;
        }

        private static string GetHistoryFilePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history.txt");
        }

        private static List<string> LoadPersistentHistory()
        {
            var list = new List<string>();
            try
            {
                string path = GetHistoryFilePath();
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    int start = Math.Max(0, lines.Length - 300);
                    for (int i = start; i < lines.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(lines[i]))
                            list.Add(lines[i]);
                    }
                }
            }
            catch { }
            return list;
        }

        private void SavePersistentHistory()
        {
            try
            {
                int count = CommandHistory.Count;
                int start = Math.Max(0, count - 300);
                var lines = CommandHistory.GetRange(start, count - start);
                File.WriteAllLines(GetHistoryFilePath(), lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

        public Encoding GetActiveEncoding()
        {
            try
            {
                if (CodePage == 65001)
                    return new UTF8Encoding(false);
                return Encoding.GetEncoding(CodePage);
            }
            catch
            {
                return new UTF8Encoding(false);
            }
        }

        public string GetPromptString()
        {
            if (Shell == ShellType.PowerShell)
                return "PS " + CurrentDirectory + "> ";
            return CurrentDirectory + ">";
        }

        public void Start()
        {
            StopProcessOnly();

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = CurrentDirectory
            };

            psi.EnvironmentVariables["WT_LTSC_SESSION"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";

            string freshPath = BuildFreshEnvironmentPath();
            if (!string.IsNullOrEmpty(freshPath))
            {
                psi.EnvironmentVariables["PATH"] = freshPath;
            }

            if (Shell == ShellType.CommandPrompt)
            {
                psi.FileName = "cmd.exe";
                // Keep %CMDCMDLINE% clean (no >nul or & characters) so scripts like install.cmd that inspect %CMDCMDLINE% never fail
                psi.Arguments = "/D /Q";
            }
            else
            {
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoLogo -NoExit -Command -";
            }

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process = proc;
            proc.Exited += (s, e) =>
            {
                if (!_disposed && object.ReferenceEquals(s, _process))
                {
                    IsBusy = false;
                    CurrentCommand = "";
                    try
                    {
                        Start();
                        if (PromptReady != null)
                            PromptReady(GetPromptString(), CurrentDirectory);
                    }
                    catch { }
                }
            };

            proc.Start();
            _stdinStream = proc.StandardInput.BaseStream;

            StartStreamReader(proc.StandardOutput.BaseStream, false);
            StartStreamReader(proc.StandardError.BaseStream, true);

            if (Shell == ShellType.CommandPrompt)
            {
                byte[] initCmd = Encoding.ASCII.GetBytes("@chcp " + CodePage + " >nul\r\n");
                _stdinStream.Write(initCmd, 0, initCmd.Length);
                _stdinStream.Flush();
            }
            else if (Shell == ShellType.PowerShell)
            {
                string initPs = "[Console]::InputEncoding = [Console]::OutputEncoding = $OutputEncoding = New-Object System.Text.UTF8Encoding($false)\r\n";
                byte[] initBytes = new UTF8Encoding(false).GetBytes(initPs);
                _stdinStream.Write(initBytes, 0, initBytes.Length);
                _stdinStream.Flush();
            }

            IsBusy = false;
        }

        public void SetCodePage(int newCodePage)
        {
            CodePage = newCodePage;
            if (_process != null && !_process.HasExited && _stdinStream != null)
            {
                try
                {
                    if (Shell == ShellType.CommandPrompt)
                    {
                        byte[] bytes = Encoding.ASCII.GetBytes("@chcp " + CodePage + " >nul\r\n");
                        _stdinStream.Write(bytes, 0, bytes.Length);
                        _stdinStream.Flush();
                    }
                }
                catch
                {
                }
            }
        }

        private string BuildFreshEnvironmentPath()
        {
            try
            {
                var ordered = new List<string>();
                lock (_knownPathEntries)
                {
                    _knownPathEntries.Clear();
                }

                Action<string> addRawPathList = rawList =>
                {
                    if (string.IsNullOrWhiteSpace(rawList)) return;
                    string expanded = Environment.ExpandEnvironmentVariables(rawList);
                    string[] parts = expanded.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string p in parts)
                    {
                        string clean = p.Trim().Trim('"');
                        if (string.IsNullOrEmpty(clean)) continue;
                        lock (_knownPathEntries)
                        {
                            if (_knownPathEntries.Add(clean))
                            {
                                ordered.Add(clean);
                            }
                        }
                    }
                };

                try
                {
                    using (var sysKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
                    {
                        if (sysKey != null) addRawPathList(sysKey.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                    }
                }
                catch { }

                try
                {
                    using (var usrKey = Registry.CurrentUser.OpenSubKey("Environment"))
                    {
                        if (usrKey != null) addRawPathList(usrKey.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                    }
                }
                catch { }

                addRawPathList(Environment.GetEnvironmentVariable("PATH"));

                string merged = string.Join(";", ordered.ToArray());
                Environment.SetEnvironmentVariable("PATH", merged);
                return merged;
            }
            catch
            {
                return Environment.GetEnvironmentVariable("PATH");
            }
        }

        private void SyncNewlyInstalledPathsIntoShell()
        {
            try
            {
                var newlyAdded = new List<string>();
                Action<string> checkRawList = rawList =>
                {
                    if (string.IsNullOrWhiteSpace(rawList)) return;
                    string expanded = Environment.ExpandEnvironmentVariables(rawList);
                    string[] parts = expanded.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string p in parts)
                    {
                        string clean = p.Trim().Trim('"');
                        if (string.IsNullOrEmpty(clean)) continue;
                        lock (_knownPathEntries)
                        {
                            if (_knownPathEntries.Add(clean))
                            {
                                newlyAdded.Add(clean);
                            }
                        }
                    }
                };

                using (var sysKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
                {
                    if (sysKey != null) checkRawList(sysKey.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                }
                using (var usrKey = Registry.CurrentUser.OpenSubKey("Environment"))
                {
                    if (usrKey != null) checkRawList(usrKey.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                }

                if (newlyAdded.Count > 0 && _process != null && !_process.HasExited && _stdinStream != null)
                {
                    string extra = string.Join(";", newlyAdded.ToArray());
                    string curProcPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    Environment.SetEnvironmentVariable("PATH", curProcPath.TrimEnd(';') + ";" + extra);

                    if (Shell == ShellType.CommandPrompt)
                    {
                        byte[] updateBytes = new UTF8Encoding(false).GetBytes("@set \"PATH=%PATH%;" + extra + "\"\r\n");
                        _stdinStream.Write(updateBytes, 0, updateBytes.Length);
                        _stdinStream.Flush();
                    }
                    else if (Shell == ShellType.PowerShell)
                    {
                        string escaped = extra.Replace("'", "''");
                        byte[] updateBytes = new UTF8Encoding(false).GetBytes("$env:Path = $env:Path.TrimEnd(';') + ';' + '" + escaped + "'\r\n");
                        _stdinStream.Write(updateBytes, 0, updateBytes.Length);
                        _stdinStream.Flush();
                    }
                }
            }
            catch
            {
            }
        }

        private static string DecodeSmartUtf8OrCp874(byte[] buffer, int bytesRead, ref byte[] carry, ref int carryLen)
        {
            int total = carryLen + bytesRead;
            byte[] combined = new byte[total];
            if (carryLen > 0)
            {
                Buffer.BlockCopy(carry, 0, combined, 0, carryLen);
                carryLen = 0;
            }
            Buffer.BlockCopy(buffer, 0, combined, total - bytesRead, bytesRead);

            bool isValidUtf8 = true;
            int incompleteStart = total;
            int i = 0;

            while (i < total)
            {
                byte b = combined[i];
                if (b < 0x80)
                {
                    i++;
                }
                else if (b >= 0xC2 && b <= 0xDF)
                {
                    if (i + 1 >= total) { incompleteStart = i; break; }
                    if ((combined[i + 1] & 0xC0) != 0x80) { isValidUtf8 = false; break; }
                    i += 2;
                }
                else if (b >= 0xE0 && b <= 0xEF)
                {
                    if (i + 1 >= total) { incompleteStart = i; break; }
                    byte b1 = combined[i + 1];
                    if ((b1 & 0xC0) != 0x80 || (b == 0xE0 && b1 < 0xA0) || (b == 0xED && b1 >= 0xA0))
                    {
                        isValidUtf8 = false;
                        break;
                    }
                    if (i + 2 >= total) { incompleteStart = i; break; }
                    if ((combined[i + 2] & 0xC0) != 0x80) { isValidUtf8 = false; break; }
                    i += 3;
                }
                else if (b >= 0xF0 && b <= 0xF4)
                {
                    if (i + 1 >= total) { incompleteStart = i; break; }
                    byte b1 = combined[i + 1];
                    if ((b1 & 0xC0) != 0x80 || (b == 0xF0 && b1 < 0x90) || (b == 0xF4 && b1 > 0x8F))
                    {
                        isValidUtf8 = false;
                        break;
                    }
                    if (i + 2 >= total) { incompleteStart = i; break; }
                    if ((combined[i + 2] & 0xC0) != 0x80) { isValidUtf8 = false; break; }
                    if (i + 3 >= total) { incompleteStart = i; break; }
                    if ((combined[i + 3] & 0xC0) != 0x80) { isValidUtf8 = false; break; }
                    i += 4;
                }
                else
                {
                    isValidUtf8 = false;
                    break;
                }
            }

            if (isValidUtf8)
            {
                int rem = total - incompleteStart;
                if (rem > 0)
                {
                    Buffer.BlockCopy(combined, incompleteStart, carry, 0, rem);
                    carryLen = rem;
                }
                if (incompleteStart <= 0)
                    return string.Empty;
                return Encoding.UTF8.GetString(combined, 0, incompleteStart);
            }

            try
            {
                return Encoding.GetEncoding(874).GetString(combined, 0, total);
            }
            catch
            {
                return Encoding.UTF8.GetString(combined, 0, total);
            }
        }

        private void StartStreamReader(Stream stream, bool isError)
        {
            var thread = new Thread(() =>
            {
                byte[] buffer = new byte[4096];
                byte[] utf8Carry = new byte[8];
                int utf8CarryLen = 0;
                var lineAccumulator = new StringBuilder();

                try
                {
                    while (!_disposed)
                    {
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead <= 0)
                            break;

                        string chunk = DecodeSmartUtf8OrCp874(buffer, bytesRead, ref utf8Carry, ref utf8CarryLen);
                        if (string.IsNullOrEmpty(chunk))
                            continue;

                        if (chunk.IndexOf('\0') >= 0 || chunk.IndexOf('\u200B') >= 0)
                        {
                            chunk = chunk.Replace("\0", "").Replace("\uFFFD", "").Replace("\u200B", "");
                        }

                        if (!isError)
                        {
                            lineAccumulator.Append(chunk);
                            string accumulated = lineAccumulator.ToString();

                            int markerIdx = accumulated.IndexOf(PwdMarkerStart, StringComparison.Ordinal);
                            if (markerIdx >= 0)
                            {
                                int endIdx = accumulated.IndexOf(PwdMarkerEnd, markerIdx + PwdMarkerStart.Length, StringComparison.Ordinal);
                                if (endIdx >= 0)
                                {
                                    string beforeMarker = accumulated.Substring(0, markerIdx);
                                    if (!string.IsNullOrEmpty(beforeMarker) && OutputReceived != null)
                                    {
                                        OutputReceived(beforeMarker, false);
                                    }

                                    string newPwd = accumulated.Substring(
                                        markerIdx + PwdMarkerStart.Length,
                                        endIdx - (markerIdx + PwdMarkerStart.Length)).Trim();

                                    if (!string.IsNullOrEmpty(newPwd) && Directory.Exists(newPwd))
                                    {
                                        CurrentDirectory = newPwd;
                                    }

                                    int afterEnd = endIdx + PwdMarkerEnd.Length;
                                    while (afterEnd < accumulated.Length && (accumulated[afterEnd] == '\r' || accumulated[afterEnd] == '\n'))
                                    {
                                        afterEnd++;
                                    }

                                    string remaining = accumulated.Substring(afterEnd);
                                    lineAccumulator.Clear();
                                    lineAccumulator.Append(remaining);

                                    // Keep the latest pair of temp .cmd files until the next command so cmd.exe finishes reading EOF cleanly
                                    CleanupTempFiles(true);
                                    SyncNewlyInstalledPathsIntoShell();
                                    IsBusy = false;
                                    CurrentCommand = "";

                                    if (PromptReady != null)
                                    {
                                        PromptReady(GetPromptString(), CurrentDirectory);
                                    }

                                    if (!string.IsNullOrEmpty(remaining))
                                    {
                                        OutputReceived(remaining, false);
                                        lineAccumulator.Clear();
                                    }
                                    continue;
                                }
                                else
                                {
                                    // Marker started but not finished yet; flush text before marker
                                    if (markerIdx > 0)
                                    {
                                        string safeText = accumulated.Substring(0, markerIdx);
                                        if (OutputReceived != null)
                                            OutputReceived(safeText, false);
                                        lineAccumulator.Remove(0, markerIdx);
                                    }
                                    continue;
                                }
                            }
                            else
                            {
                                // Hold back last few chars if they could be start of "__WT_PWD__"
                                int holdBack = 0;
                                for (int k = Math.Min(PwdMarkerStart.Length - 1, accumulated.Length); k >= 1; k--)
                                {
                                    if (accumulated.EndsWith(PwdMarkerStart.Substring(0, k), StringComparison.Ordinal))
                                    {
                                        holdBack = k;
                                        break;
                                    }
                                }

                                int emitLen = accumulated.Length - holdBack;
                                if (emitLen > 0)
                                {
                                    string emitText = accumulated.Substring(0, emitLen);
                                    if (OutputReceived != null)
                                        OutputReceived(emitText, false);
                                    lineAccumulator.Remove(0, emitLen);
                                }
                            }
                        }
                        else
                        {
                            // Suppress spurious cmd.exe message when a user script deletes itself (e.g., install.cmd && del install.cmd)
                            string filteredErr = chunk.Replace("The batch file cannot be found.\r\n", "")
                                                      .Replace("The batch file cannot be found.\n", "")
                                                      .Replace("The batch file cannot be found.", "");
                            if (!string.IsNullOrEmpty(filteredErr) && OutputReceived != null)
                                OutputReceived(filteredErr, true);
                        }
                    }
                }
                catch
                {
                }
            })
            {
                IsBackground = true,
                Name = "WT_Reader_" + SessionId + "_" + (isError ? "ERR" : "OUT")
            };
            thread.Start();
        }

        public void ExecuteCommand(string commandLine)
        {
            _tabMatchPrefix = null;
            _tabMatches = null;

            if (commandLine == null)
                commandLine = string.Empty;

            string trimmed = commandLine.Trim();

            if (!string.IsNullOrEmpty(trimmed))
            {
                if (CommandHistory.Count == 0 || !string.Equals(CommandHistory[CommandHistory.Count - 1], commandLine, StringComparison.Ordinal))
                {
                    CommandHistory.Add(commandLine);
                    SavePersistentHistory();
                }
                HistoryIndex = CommandHistory.Count;
            }

            if (_process == null || _process.HasExited)
            {
                Start();
            }

            // If a child command is currently running and waiting for interactive stdin, send directly
            if (IsBusy)
            {
                SendRawInput(commandLine + "\r\n");
                return;
            }

            if (string.Equals(trimmed, "cls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase))
            {
                if (ClearRequested != null)
                    ClearRequested();
                if (PromptReady != null)
                    PromptReady(GetPromptString(), CurrentDirectory);
                return;
            }

            if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase))
            {
                if (SessionExited != null)
                    SessionExited();
                return;
            }

            if (string.IsNullOrEmpty(trimmed))
            {
                if (PromptReady != null)
                    PromptReady(GetPromptString(), CurrentDirectory);
                return;
            }

            IsBusy = true;
            CurrentCommand = trimmed;
            CommandStartTime = DateTime.Now;
            if (CommandStarted != null)
            {
                CommandStarted(trimmed);
            }

            try
            {
                if (Shell == ShellType.CommandPrompt)
                {
                    int seq = Interlocked.Increment(ref _cmdSeq);
                    string tempDir = SystemIntegration.GetPrivateTempDirectory();
                    string outerCmdPath = Path.Combine(tempDir, string.Format("wt_ltsc_{0}_{1}_out.cmd", SessionId, seq));
                    string innerCmdPath = Path.Combine(tempDir, string.Format("wt_ltsc_{0}_{1}_in.cmd", SessionId, seq));
                    lock (_tempFiles)
                    {
                        _tempFiles.Add(outerCmdPath);
                        _tempFiles.Add(innerCmdPath);
                    }

                    string normalizedCmd = NormalizeBatchChaining(commandLine);
                    File.WriteAllText(innerCmdPath, "@" + normalizedCmd + "\r\n", GetActiveEncoding());

                    var outerContent = new StringBuilder();
                    outerContent.AppendLine("@chcp " + CodePage + " >nul");
                    outerContent.AppendLine(string.Format("@call \"{0}\"", innerCmdPath));
                    outerContent.AppendLine("@echo " + PwdMarkerStart + "%CD%" + PwdMarkerEnd);

                    File.WriteAllText(outerCmdPath, outerContent.ToString(), GetActiveEncoding());

                    string callLine = string.Format("@call \"{0}\"\r\n", outerCmdPath);
                    byte[] callBytes = Encoding.ASCII.GetBytes(callLine);
                    _stdinStream.Write(callBytes, 0, callBytes.Length);
                    _stdinStream.Flush();
                }
                else
                {
                    // PowerShell UTF-8 Base64 execution bridge (100% immune to stdin encoding issues)
                    string psScript = commandLine + "\r\n" +
                        "Write-Output ('" + PwdMarkerStart + "' + (Get-Location).Path + '" + PwdMarkerEnd + "')";
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(psScript));
                    string invokeLine = string.Format(
                        "Invoke-Expression ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{0}')))\r\n",
                        b64);
                    byte[] bytes = Encoding.ASCII.GetBytes(invokeLine);
                    _stdinStream.Write(bytes, 0, bytes.Length);
                    _stdinStream.Flush();
                }
            }
            catch (Exception ex)
            {
                IsBusy = false;
                if (OutputReceived != null)
                    OutputReceived("Error executing command: " + ex.Message + "\r\n", true);
                if (PromptReady != null)
                    PromptReady(GetPromptString(), CurrentDirectory);
            }
        }

        private static string NormalizeBatchChaining(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine))
                return commandLine;

            string trimmed = commandLine.Trim();
            if (string.Equals(trimmed, "python", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "py", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed + " -i";
            }
            if (string.Equals(trimmed, "python -v", StringComparison.Ordinal))
            {
                return "python -i -v";
            }

            // In Windows cmd.exe batch files, invoking another .cmd or .bat file inside a "&&" chain without "call"
            // transfers control and never returns to subsequent "&&" commands. Prefix "call " automatically.
            string chained = System.Text.RegularExpressions.Regex.Replace(
                commandLine,
                @"(\&\&|\&)\s*(?!call\s+)([""']?[^\s\&\|><""']+\.(?:cmd|bat)[""']?)",
                "$1 call $2",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return EscapeBatchPercentPreservingEnvVars(chained);
        }

        private static readonly HashSet<string> CmdDynamicVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CD", "DATE", "TIME", "RANDOM", "ERRORLEVEL", "CMDEXTVERSION", "CMDCMDLINE", "HIGHESTNUMANODENUMBER"
        };

        // Preserves %ENV_VAR% and %VAR:~0,5% while escaping literal % (e.g. URL %20, 100%, interactive "for %i") as %% for .cmd execution
        public static string EscapeBatchPercentPreservingEnvVars(string line)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf('%') < 0)
                return line;

            var sb = new StringBuilder(line.Length + 8);
            int i = 0;
            int len = line.Length;

            while (i < len)
            {
                char c = line[i];
                if (c != '%')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                // Already escaped %%
                if (i + 1 < len && line[i + 1] == '%')
                {
                    sb.Append("%%");
                    i += 2;
                    continue;
                }

                int closeIdx = line.IndexOf('%', i + 1);
                if (closeIdx > i + 1 && closeIdx - i - 1 <= 64)
                {
                    string expr = line.Substring(i + 1, closeIdx - i - 1);
                    int colonIdx = expr.IndexOf(':');
                    string baseName = colonIdx > 0 ? expr.Substring(0, colonIdx) : expr;

                    if (IsValidEnvVarIdentifier(baseName) &&
                        (CmdDynamicVars.Contains(baseName) || Environment.GetEnvironmentVariable(baseName) != null))
                    {
                        sb.Append(line, i, closeIdx - i + 1);
                        i = closeIdx + 1;
                        continue;
                    }
                }

                sb.Append("%%");
                i++;
            }

            return sb.ToString();
        }

        private static bool IsValidEnvVarIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            char first = name[0];
            if (!((first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z') || first == '_'))
                return false;
            for (int i = 1; i < name.Length; i++)
            {
                char ch = name[i];
                bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
                          (ch >= '0' && ch <= '9') || ch == '_' || ch == '(' || ch == ')';
                if (!ok) return false;
            }
            return true;
        }

        public void SendRawInput(string text)
        {
            if (_process == null || _process.HasExited || _stdinStream == null)
                return;
            try
            {
                byte[] bytes = GetActiveEncoding().GetBytes(text);
                _stdinStream.Write(bytes, 0, bytes.Length);
                _stdinStream.Flush();
            }
            catch
            {
            }
        }

        public void Interrupt()
        {
            try
            {
                bool wasBusy = IsBusy;
                IsBusy = false;
                CurrentCommand = "";

                if (wasBusy && _process != null && !_process.HasExited)
                {
                    // Kill running command tree and restart shell cleanly in the same working directory
                    // StopProcessOnly detaches _process first so SessionExited is never triggered on Ctrl+C
                    Start();
                }

                if (OutputReceived != null)
                    OutputReceived("^C\r\n", true);
                if (PromptReady != null)
                    PromptReady(GetPromptString(), CurrentDirectory);
            }
            catch
            {
            }
        }

        public string CompletePath(string inputLine)
        {
            try
            {
                if (inputLine == null) inputLine = "";

                if (_tabMatches != null && _tabMatches.Count > 0 && _tabMatchPrefix != null &&
                    inputLine.StartsWith(_tabMatchPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    _tabMatchIndex = (_tabMatchIndex + 1) % _tabMatches.Count;
                    return _tabMatchPrefix + _tabMatches[_tabMatchIndex];
                }

                // Extract last token
                int lastSpace = inputLine.LastIndexOf(' ');
                string cmdPart = lastSpace >= 0 ? inputLine.Substring(0, lastSpace + 1) : "";
                string token = lastSpace >= 0 ? inputLine.Substring(lastSpace + 1).Trim('"') : inputLine.Trim('"');

                string searchDir = CurrentDirectory;
                string filePrefix = token;

                int lastSlash = token.LastIndexOfAny(new[] { '\\', '/' });
                string dirPrefixInToken = "";
                if (lastSlash >= 0)
                {
                    dirPrefixInToken = token.Substring(0, lastSlash + 1);
                    filePrefix = token.Substring(lastSlash + 1);
                    string candidateDir = Path.IsPathRooted(dirPrefixInToken)
                        ? dirPrefixInToken
                        : Path.Combine(CurrentDirectory, dirPrefixInToken);
                    if (Directory.Exists(candidateDir))
                        searchDir = candidateDir;
                }

                if (!Directory.Exists(searchDir))
                    return inputLine;

                var matches = new List<string>();
                foreach (string d in Directory.GetDirectories(searchDir))
                {
                    string name = Path.GetFileName(d);
                    if (name.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string fullToken = dirPrefixInToken + name + "\\";
                        matches.Add(fullToken.Contains(" ") ? "\"" + fullToken + "\"" : fullToken);
                    }
                }
                foreach (string f in Directory.GetFiles(searchDir))
                {
                    string name = Path.GetFileName(f);
                    if (name.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string fullToken = dirPrefixInToken + name;
                        matches.Add(fullToken.Contains(" ") ? "\"" + fullToken + "\"" : fullToken);
                    }
                }

                if (matches.Count == 0)
                    return inputLine;

                _tabMatchPrefix = cmdPart;
                _tabMatches = matches;
                _tabMatchIndex = 0;
                return _tabMatchPrefix + _tabMatches[0];
            }
            catch
            {
                return inputLine;
            }
        }

        private void CleanupTempFiles(bool keepLatestPair = false)
        {
            lock (_tempFiles)
            {
                int stopExclusive = keepLatestPair ? Math.Max(0, _tempFiles.Count - 2) : _tempFiles.Count;
                for (int i = stopExclusive - 1; i >= 0; i--)
                {
                    try
                    {
                        if (File.Exists(_tempFiles[i]))
                            File.Delete(_tempFiles[i]);
                        _tempFiles.RemoveAt(i);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void StopProcessOnly()
        {
            var oldProc = _process;
            _process = null;
            _stdinStream = null;

            try
            {
                if (oldProc != null)
                {
                    try { oldProc.EnableRaisingEvents = false; } catch { }
                    if (!oldProc.HasExited)
                    {
                        int pid = oldProc.Id;
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
                        catch
                        {
                        }
                        if (!oldProc.HasExited)
                        {
                            try { oldProc.Kill(); } catch { }
                        }
                    }
                    try { oldProc.Dispose(); } catch { }
                }
            }
            catch
            {
            }
            CleanupTempFiles();
        }

        public void Dispose()
        {
            _disposed = true;
            StopProcessOnly();
        }
    }
}
