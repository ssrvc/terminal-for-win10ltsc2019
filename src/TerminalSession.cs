using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

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
        public List<string> CommandHistory { get; private set; }
        public int HistoryIndex { get; set; }

        private Process _process;
        private Stream _stdinStream;
        private bool _disposed;
        private int _cmdSeq;
        private readonly List<string> _tempFiles = new List<string>();
        private string _tabMatchPrefix = null;
        private List<string> _tabMatches = null;
        private int _tabMatchIndex = 0;

        public event Action<string, bool> OutputReceived; // (text, isError)
        public event Action<string, string> PromptReady;  // (promptText, currentDir)
        public event Action ClearRequested;
        public event Action SessionExited;

        private const string PwdMarkerStart = "__WT_PWD__";
        private const string PwdMarkerEnd = "__WT_END__";

        public TerminalSession(ShellType shell, string initialDirectory, int codePage = 65001)
        {
            SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
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

            if (Shell == ShellType.CommandPrompt)
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/D /Q /K chcp " + CodePage + " >nul";
            }
            else
            {
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoLogo -NoExit -Command -";
            }

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += (s, e) =>
            {
                if (!_disposed && SessionExited != null)
                    SessionExited();
            };

            _process.Start();
            _stdinStream = _process.StandardInput.BaseStream;

            StartStreamReader(_process.StandardOutput.BaseStream, false);
            StartStreamReader(_process.StandardError.BaseStream, true);

            if (Shell == ShellType.PowerShell)
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

        private void StartStreamReader(Stream stream, bool isError)
        {
            var thread = new Thread(() =>
            {
                byte[] buffer = new byte[4096];
                char[] charBuf = new char[8192];
                int lastDecoderCp = CodePage;
                Decoder decoder = GetActiveEncoding().GetDecoder();
                var lineAccumulator = new StringBuilder();

                try
                {
                    while (!_disposed)
                    {
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead <= 0)
                            break;

                        if (lastDecoderCp != CodePage)
                        {
                            lastDecoderCp = CodePage;
                            decoder = GetActiveEncoding().GetDecoder();
                        }

                        int charsDecoded = decoder.GetChars(buffer, 0, bytesRead, charBuf, 0, false);
                        if (charsDecoded <= 0)
                            continue;

                        string chunk = new string(charBuf, 0, charsDecoded);

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

                                    CleanupTempFiles();
                                    IsBusy = false;

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
                            if (OutputReceived != null)
                                OutputReceived(chunk, true);
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

            try
            {
                if (Shell == ShellType.CommandPrompt)
                {
                    int seq = Interlocked.Increment(ref _cmdSeq);
                    string tempCmdPath = Path.Combine(Path.GetTempPath(), string.Format("wt_ltsc_{0}_{1}.cmd", SessionId, seq));
                    lock (_tempFiles)
                    {
                        _tempFiles.Add(tempCmdPath);
                    }

                    var batchContent = new StringBuilder();
                    batchContent.AppendLine("@chcp " + CodePage + " >nul");
                    batchContent.AppendLine("@" + commandLine);
                    batchContent.AppendLine("@echo " + PwdMarkerStart + "%CD%" + PwdMarkerEnd);

                    File.WriteAllText(tempCmdPath, batchContent.ToString(), GetActiveEncoding());

                    string callLine = string.Format("@call \"{0}\"\r\n", tempCmdPath);
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
                if (_process != null && !_process.HasExited)
                {
                    // Restart shell cleanly in the same working directory to immediately stop hung commands like ping -t
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

        private void CleanupTempFiles()
        {
            lock (_tempFiles)
            {
                for (int i = _tempFiles.Count - 1; i >= 0; i--)
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
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    int pid = _process.Id;
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
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
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
