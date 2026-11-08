using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace WindowsTerminalLTSC
{
    public static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                Console.InputEncoding = new UTF8Encoding(false);
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch
            {
            }

            SystemIntegration.PurgeLegacyDirectories();

            string initialDir = Environment.CurrentDirectory;
            string startupCommand = null;

            if (args == null || args.Length == 0)
            {
                string appBase = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                string installBase = SystemIntegration.GetActiveInstallDir().TrimEnd('\\', '/');
                string curTrim = (initialDir ?? "").TrimEnd('\\', '/');
                if (string.Equals(curTrim, appBase, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(curTrim, installBase, StringComparison.OrdinalIgnoreCase))
                {
                    initialDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
            }

            if (args != null && args.Length > 0)
            {
                if (string.Equals(args[0], "--tui-wait", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length >= 2 && !string.IsNullOrEmpty(args[1]))
                    {
                        try
                        {
                            EventWaitHandle ev;
                            if (EventWaitHandle.TryOpenExisting(args[1], out ev))
                            {
                                using (ev)
                                {
                                    ev.WaitOne(2500);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                    return 0;
                }
                if (string.Equals(args[0], "--tui-host", StringComparison.OrdinalIgnoreCase))
                {
                    return TuiHostWorker.Run(args);
                }
                if (string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    return RunSelfTest();
                }
                if (string.Equals(args[0], "--make-icon", StringComparison.OrdinalIgnoreCase) && args.Length >= 4)
                {
                    SystemIntegration.ConvertPngToMultiSizeIco(args[1], args[2], args[3]);
                    return 0;
                }
                if (string.Equals(args[0], "--install-default", StringComparison.OrdinalIgnoreCase))
                {
                    SystemIntegration.InstallPermanentlyAndRegister(true, true);
                    return 0;
                }
                if (string.Equals(args[0], "--register-default", StringComparison.OrdinalIgnoreCase))
                {
                    SystemIntegration.RegisterDefaultTerminal(true);
                    return 0;
                }
                if (string.Equals(args[0], "--unregister-default", StringComparison.OrdinalIgnoreCase))
                {
                    SystemIntegration.UnregisterDefaultTerminal();
                    return 0;
                }
                if (string.Equals(args[0], "--uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    bool isSilent = args.Length > 1 && string.Equals(args[1], "--silent", StringComparison.OrdinalIgnoreCase);
                    return SystemIntegration.PerformFullUninstall(isSilent);
                }
                if (string.Equals(args[0], "--elevated-b64", StringComparison.OrdinalIgnoreCase) && args.Length >= 2)
                {
                    try
                    {
                        string decodedDir = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
                        if (!string.IsNullOrWhiteSpace(decodedDir) && Directory.Exists(decodedDir))
                            initialDir = decodedDir;
                        if (args.Length >= 3 && !string.IsNullOrWhiteSpace(args[2]))
                            startupCommand = Encoding.UTF8.GetString(Convert.FromBase64String(args[2]));
                    }
                    catch { }
                }
                else
                {
                    string candidate = args[0];
                    if ((string.Equals(candidate, "-d", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(candidate, "--startingDirectory", StringComparison.OrdinalIgnoreCase)) &&
                        args.Length > 1)
                    {
                        candidate = args[1];
                    }

                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        candidate = candidate.Trim().Trim('"');
                        if (candidate.EndsWith(":\"", StringComparison.Ordinal))
                            candidate = candidate.Substring(0, candidate.Length - 1) + "\\";
                        else if (candidate.EndsWith(":", StringComparison.Ordinal))
                            candidate = candidate + "\\";

                        if (Directory.Exists(candidate))
                        {
                            initialDir = Path.GetFullPath(candidate);
                        }
                        else if (File.Exists(candidate))
                        {
                            initialDir = Path.GetDirectoryName(Path.GetFullPath(candidate));
                        }
                    }
                }
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }

            var app = new Application();
            var win = new MainWindow(initialDir, startupCommand);
            app.Run(win);
            return 0;
        }

        private static int RunSelfTest()
        {
            var sb = new StringBuilder();
            using (var doneEvent = new ManualResetEvent(false))
            using (var session = new TerminalSession(ShellType.CommandPrompt, Environment.CurrentDirectory, 65001))
            {
                session.OutputReceived += (text, isErr) =>
                {
                    lock (sb) sb.Append(text);
                };
                session.PromptReady += (prompt, dir) =>
                {
                    doneEvent.Set();
                };
                session.Start();
                session.ExecuteCommand("echo สวัสดีครับ ทดสอบภาษาไทย น้ำใสใจจริง 1234");
                doneEvent.WaitOne(4000);
            }

            string output = sb.ToString();
            bool ok = output.Contains("สวัสดีครับ ทดสอบภาษาไทย น้ำใสใจจริง 1234");
            return ok ? 0 : 1;
        }
    }
}
