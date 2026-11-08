using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace WindowsTerminalLTSC
{
    public static class SystemIntegration
    {
        public const string OfficialAuthorCredit = "พัฒนาโดย Rachata | แจกจ่ายฟรี (Free Non-Commercial — ห้ามนำไปจำหน่าย)";
        // SHA-256 of "RACHATA_WT_LTSC_2019:" + OfficialAuthorCredit
        private const string ExpectedCreditHash = "6B6E03A0420E1E086475890A4D0946E0F58A90717B6073F424756178436F0A9C";

        public static bool VerifyAuthorCreditIntegrity(out string displayCredit)
        {
            displayCredit = OfficialAuthorCredit;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var companyAttr = Attribute.GetCustomAttribute(asm, typeof(AssemblyCompanyAttribute)) as AssemblyCompanyAttribute;
                var copyrightAttr = Attribute.GetCustomAttribute(asm, typeof(AssemblyCopyrightAttribute)) as AssemblyCopyrightAttribute;
                if (companyAttr == null || companyAttr.Company.IndexOf("Rachata", StringComparison.Ordinal) < 0)
                    return false;
                if (copyrightAttr == null || copyrightAttr.Copyright.IndexOf("Rachata", StringComparison.Ordinal) < 0)
                    return false;
                if (OfficialAuthorCredit.IndexOf("Rachata", StringComparison.Ordinal) < 0 ||
                    OfficialAuthorCredit.IndexOf("ห้ามนำไปจำหน่าย", StringComparison.Ordinal) < 0)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }
        private const string ContextMenuKeyName = "WindowsTerminalLTSC";
        private const string CmdProcessorKeyPath = @"Software\Microsoft\Command Processor";
        private const string AppPathsBaseKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
        private const string UninstallRegKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsTerminalLTSC";

        public static string GetInstallDirectory()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallRegKeyPath))
                {
                    if (k != null)
                    {
                        string loc = k.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(loc)) return loc;
                    }
                }
            }
            catch { }
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Programs", "WindowsTerminalLTSC");
        }

        public static string GetActiveInstallDir()
        {
            string permDir = GetInstallDirectory();
            string permExe = Path.Combine(permDir, "WindowsTerminalLTSC.exe");
            if (File.Exists(permExe))
                return permDir;
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        }

        public static string GetExecutablePath()
        {
            return Path.Combine(GetActiveInstallDir(), "WindowsTerminalLTSC.exe");
        }

        private static string _cachedPrivateTempDir;
        private static readonly object _privateTempLock = new object();

        public static string GenerateSecureHexToken(int byteCount = 16)
        {
            byte[] buf = new byte[Math.Max(4, byteCount)];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(buf);
            }
            var sb = new StringBuilder(buf.Length * 2);
            for (int i = 0; i < buf.Length; i++)
            {
                sb.Append(buf[i].ToString("x2"));
            }
            return sb.ToString();
        }

        public static string GetPrivateTempDirectory()
        {
            lock (_privateTempLock)
            {
                if (!string.IsNullOrEmpty(_cachedPrivateTempDir) && Directory.Exists(_cachedPrivateTempDir))
                    return _cachedPrivateTempDir;

                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "WindowsTerminalLTSC", ".tmp");

                try
                {
                    if (!Directory.Exists(baseDir))
                    {
                        var dirSec = new DirectorySecurity();
                        dirSec.SetAccessRuleProtection(true, false);
                        SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User;
                        if (userSid != null)
                        {
                            dirSec.AddAccessRule(new FileSystemAccessRule(
                                userSid,
                                FileSystemRights.FullControl,
                                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                PropagationFlags.None,
                                AccessControlType.Allow));
                        }
                        dirSec.AddAccessRule(new FileSystemAccessRule(
                            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                            FileSystemRights.FullControl,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));
                        dirSec.AddAccessRule(new FileSystemAccessRule(
                            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                            FileSystemRights.FullControl,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));

                        Directory.CreateDirectory(baseDir, dirSec);
                    }

                    // Best-effort cleanup of stale temporary scripts older than 12 hours
                    DateTime cutoff = DateTime.UtcNow.AddHours(-12);
                    foreach (string f in Directory.GetFiles(baseDir, "wt_*.cmd"))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(f) < cutoff)
                                File.Delete(f);
                        }
                        catch { }
                    }

                    _cachedPrivateTempDir = baseDir;
                    return baseDir;
                }
                catch
                {
                    return Path.GetTempPath();
                }
            }
        }

        public static string GetIconPath()
        {
            string dir = GetActiveInstallDir();
            string ico = Path.Combine(dir, "app.ico");
            if (File.Exists(ico)) return ico;
            string localIco = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            return localIco;
        }

        public static bool IsRunningAsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void LaunchAsAdministrator(string workingDirectory, string autoRunCommand = null)
        {
            try
            {
                string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WindowsTerminalLTSC.exe");
                if (!File.Exists(exePath))
                    exePath = GetExecutablePath();

                string targetDir = workingDirectory ?? Environment.CurrentDirectory;
                string dirB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(targetDir));
                string args = "--elevated-b64 " + dirB64;
                if (!string.IsNullOrWhiteSpace(autoRunCommand))
                {
                    string cmdB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(autoRunCommand));
                    args += " " + cmdB64;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    Verb = "runas",
                    UseShellExecute = true,
                    WorkingDirectory = targetDir
                };
                Process.Start(psi);
            }
            catch
            {
                // User clicked No / cancelled UAC prompt
            }
        }

        public static bool IsRegisteredAsDefault()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\Background\shell\" + ContextMenuKeyName))
                {
                    if (key == null) return false;
                }
                using (var appKey = Registry.CurrentUser.OpenSubKey(AppPathsBaseKey + @"\wt.exe"))
                {
                    if (appKey == null) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void InstallPermanentlyAndRegister(bool interceptPlainCmd = true, bool createDesktopShortcut = true)
        {
            string sourceDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            string targetDir = GetInstallDirectory();

            try
            {
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                if (!string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
                {
                    string[] filesToCopy = new[] { "WindowsTerminalLTSC.exe", "app.ico", "app.png", "settings.json", "history.txt" };
                    foreach (string file in filesToCopy)
                    {
                        string srcFile = Path.Combine(sourceDir, file);
                        string dstFile = Path.Combine(targetDir, file);
                        if (File.Exists(srcFile))
                        {
                            try { File.Copy(srcFile, dstFile, true); } catch { }
                        }
                    }
                }
            }
            catch
            {
            }

            RegisterDefaultTerminal(interceptPlainCmd);

            if (createDesktopShortcut)
            {
                CreateShortcuts();
            }
        }

        public static void RegisterDefaultTerminal(bool interceptPlainCmd = true)
        {
            string installDir = GetActiveInstallDir();
            string exePath = Path.Combine(installDir, "WindowsTerminalLTSC.exe");
            string icoPath = Path.Combine(installDir, "app.ico");
            string iconSpec = File.Exists(icoPath) ? ("\"" + icoPath + "\"") : ("\"" + exePath + "\",0");

            // Ensure clean system state: remove any legacy Command Processor AutoRun or cmd.exe hijack
            // to guarantee 100% clean security reputation with Malwarebytes and Windows Defender.
            PurgeLegacyDirectories();
            try
            {
                using (var cpKey = Registry.CurrentUser.OpenSubKey(CmdProcessorKeyPath, true))
                {
                    if (cpKey != null)
                    {
                        string autoRun = cpKey.GetValue("AutoRun") as string;
                        if (!string.IsNullOrEmpty(autoRun) && autoRun.IndexOf("wt_cmd_hook.cmd", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            cpKey.DeleteValue("AutoRun", false);
                        }
                    }
                }
                Registry.CurrentUser.DeleteSubKeyTree(AppPathsBaseKey + @"\cmd.exe", false);
            }
            catch { }

            // Write wt.cmd launcher helper script
            string[] dirs = new[] { installDir, AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/') };
            foreach (string d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    string wtCmd = Path.Combine(d, "wt.cmd");
                    File.WriteAllText(wtCmd, "@echo off\r\nstart \"\" \"" + exePath + "\" \"%CD%\"\r\n", Encoding.ASCII);

                    string oldHook = Path.Combine(d, "wt_cmd_hook.cmd");
                    if (File.Exists(oldHook)) { try { File.Delete(oldHook); } catch { } }
                }
                catch { }
            }

            // Register "wt.exe" in HKCU App Paths
            using (var wtKey = Registry.CurrentUser.CreateSubKey(AppPathsBaseKey + @"\wt.exe"))
            {
                if (wtKey != null)
                {
                    wtKey.SetValue("", exePath, RegistryValueKind.String);
                    wtKey.SetValue("Path", installDir, RegistryValueKind.String);
                }
            }

            // Ensure installDir is in User PATH so typing "wt" anywhere works
            try
            {
                using (var envKey = Registry.CurrentUser.OpenSubKey("Environment", true))
                {
                    if (envKey != null)
                    {
                        string userPath = (envKey.GetValue("Path") as string) ?? "";
                        if (userPath.IndexOf(installDir, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            string newPath = string.IsNullOrEmpty(userPath)
                                ? installDir
                                : (userPath.TrimEnd(';') + ";" + installDir);
                            envKey.SetValue("Path", newPath, RegistryValueKind.ExpandString);
                        }
                    }
                }
            }
            catch { }

            // Register File Explorer Right-Click Context Menus with custom icon
            RegisterExplorerMenu(@"Software\Classes\Directory\Background\shell\" + ContextMenuKeyName, exePath, iconSpec, "\"%V\"");
            RegisterExplorerMenu(@"Software\Classes\Directory\shell\" + ContextMenuKeyName, exePath, iconSpec, "\"%1\"");
            RegisterExplorerMenu(@"Software\Classes\Drive\shell\" + ContextMenuKeyName, exePath, iconSpec, "\"%1\"");
        }

        public static void UnregisterDefaultTerminal()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(AppPathsBaseKey + @"\cmd.exe", false);
                Registry.CurrentUser.DeleteSubKeyTree(AppPathsBaseKey + @"\wt.exe", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\" + ContextMenuKeyName, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\" + ContextMenuKeyName, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Drive\shell\" + ContextMenuKeyName, false);

                using (var cpKey = Registry.CurrentUser.OpenSubKey(CmdProcessorKeyPath, true))
                {
                    if (cpKey != null)
                    {
                        string autoRun = cpKey.GetValue("AutoRun") as string;
                        if (!string.IsNullOrEmpty(autoRun) && autoRun.IndexOf("wt_cmd_hook.cmd", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            cpKey.DeleteValue("AutoRun", false);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        public static void PurgeLegacyDirectories()
        {
            try
            {
                string oldLegacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsTerminalLTSC");
                if (Directory.Exists(oldLegacyDir))
                {
                    try { Directory.Delete(oldLegacyDir, true); } catch { }
                }
                string uninstExe = Path.Combine(GetInstallDirectory(), "Uninstall-WindowsTerminalLTSC.exe");
                if (File.Exists(uninstExe))
                {
                    try { File.Delete(uninstExe); } catch { }
                }
            }
            catch { }
        }

        public static int PerformFullUninstall(bool silent)
        {
            if (!silent)
            {
                var res = MessageBox.Show(
                    "คุณต้องการถอนการติดตั้ง Windows Terminal LTSC 2019 ออกจากระบบหรือไม่?",
                    "ถอนการติดตั้ง Windows Terminal LTSC",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes)
                    return 0;
            }

            try
            {
                foreach (var proc in Process.GetProcessesByName("WindowsTerminalLTSC"))
                {
                    if (proc.Id != Process.GetCurrentProcess().Id)
                    {
                        try { proc.Kill(); proc.WaitForExit(1500); } catch { }
                    }
                }
            }
            catch { }

            UnregisterDefaultTerminal();

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKeyPath, false);
            }
            catch { }

            try
            {
                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Windows Terminal LTSC 2019.lnk");
                string startLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Windows Terminal LTSC 2019.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
                if (File.Exists(startLnk)) File.Delete(startLnk);
            }
            catch { }

            PurgeLegacyDirectories();

            string installDir = GetInstallDirectory();
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c choice /C Y /N /D Y /T 2 > nul & rmdir /s /q \"" + installDir + "\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }

            if (!silent)
            {
                MessageBox.Show(
                    "ถอนการติดตั้ง Windows Terminal LTSC เรียบร้อยแล้ว",
                    "ถอนการติดตั้งสำเร็จ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return 0;
        }

        private static void RegisterExplorerMenu(string subKeyPath, string exePath, string iconSpec, string argToken)
        {
            using (var shellKey = Registry.CurrentUser.CreateSubKey(subKeyPath))
            {
                if (shellKey != null)
                {
                    shellKey.SetValue("", "เปิดใน Windows Terminal (ภาษาไทย)", RegistryValueKind.String);
                    shellKey.SetValue("Icon", iconSpec, RegistryValueKind.String);

                    using (var cmdKey = shellKey.CreateSubKey("command"))
                    {
                        if (cmdKey != null)
                        {
                            cmdKey.SetValue("", "\"" + exePath + "\" " + argToken, RegistryValueKind.String);
                        }
                    }
                }
            }
        }

        public static void CreateShortcuts()
        {
            try
            {
                string installDir = GetActiveInstallDir();
                string exePath = Path.Combine(installDir, "WindowsTerminalLTSC.exe");
                string icoPath = Path.Combine(installDir, "app.ico");
                if (!File.Exists(icoPath))
                    icoPath = exePath;

                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);

                CreateSingleShortcut(Path.Combine(desktopDir, "Windows Terminal LTSC 2019.lnk"), exePath, userHome, icoPath);
                CreateSingleShortcut(Path.Combine(startMenuDir, "Windows Terminal LTSC 2019.lnk"), exePath, userHome, icoPath);
            }
            catch
            {
            }
        }

        private static void CreateSingleShortcut(string lnkPath, string targetExe, string workingDir, string iconPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (shortcut == null) return;

                Type scType = shortcut.GetType();
                scType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetExe });
                scType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                scType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { iconPath + ",0" });
                scType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Windows Terminal สำหรับ Windows 10 LTSC 2019 (รองรับภาษาไทย)" });
                scType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch
            {
            }
        }

        // Converts the user's PNG image into a clean, transparent, multi-resolution Windows .ico file
        public static void ConvertPngToMultiSizeIco(string pngPath, string icoOutputPath, string croppedPngOutputPath)
        {
            using (var rawBmp = new Bitmap(pngPath))
            using (var bmp = new Bitmap(rawBmp.Width, rawBmp.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.DrawImage(rawBmp, 0, 0, rawBmp.Width, rawBmp.Height);
                }

                // Flood-fill outer white/near-white background from the 4 corners to transparent
                int w = bmp.Width;
                int h = bmp.Height;
                bool[,] visited = new bool[w, h];
                var q = new Queue<System.Drawing.Point>();

                int[,] corners = new[,] { { 0, 0 }, { w - 1, 0 }, { 0, h - 1 }, { w - 1, h - 1 } };
                for (int c = 0; c < 4; c++)
                {
                    int cx = corners[c, 0];
                    int cy = corners[c, 1];
                    visited[cx, cy] = true;
                    q.Enqueue(new System.Drawing.Point(cx, cy));
                }

                int[] dx = new[] { -1, 1, 0, 0 };
                int[] dy = new[] { 0, 0, -1, 1 };

                while (q.Count > 0)
                {
                    var pt = q.Dequeue();
                    Color col = bmp.GetPixel(pt.X, pt.Y);
                    if (col.A > 0 && col.R >= 235 && col.G >= 235 && col.B >= 235)
                    {
                        bmp.SetPixel(pt.X, pt.Y, Color.FromArgb(0, 0, 0, 0));
                        for (int dir = 0; dir < 4; dir++)
                        {
                            int nx = pt.X + dx[dir];
                            int ny = pt.Y + dy[dir];
                            if (nx >= 0 && nx < w && ny >= 0 && ny < h && !visited[nx, ny])
                            {
                                visited[nx, ny] = true;
                                Color ncol = bmp.GetPixel(nx, ny);
                                if (ncol.A == 0 || (ncol.R >= 225 && ncol.G >= 225 && ncol.B >= 225))
                                {
                                    q.Enqueue(new System.Drawing.Point(nx, ny));
                                }
                            }
                        }
                    }
                }

                // Find tight bounding box of non-transparent icon badge
                int minX = w, minY = h, maxX = 0, maxY = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (bmp.GetPixel(x, y).A > 20)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }

                Rectangle cropRect = (maxX > minX && maxY > minY)
                    ? new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1)
                    : new Rectangle(0, 0, w, h);

                int side = Math.Max(cropRect.Width, cropRect.Height);
                using (var squareBmp = new Bitmap(side, side, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(squareBmp))
                    {
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        int offsetX = (side - cropRect.Width) / 2;
                        int offsetY = (side - cropRect.Height) / 2;
                        g.DrawImage(bmp, new Rectangle(offsetX, offsetY, cropRect.Width, cropRect.Height), cropRect, GraphicsUnit.Pixel);
                    }

                    if (!string.IsNullOrEmpty(croppedPngOutputPath))
                    {
                        squareBmp.Save(croppedPngOutputPath, ImageFormat.Png);
                    }

                    int[] sizes = new[] { 256, 64, 48, 32, 16 };
                    var pngPayloads = new List<byte[]>();

                    foreach (int sz in sizes)
                    {
                        using (var resized = new Bitmap(sz, sz, PixelFormat.Format32bppArgb))
                        {
                            using (var g = Graphics.FromImage(resized))
                            {
                                g.SmoothingMode = SmoothingMode.HighQuality;
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.DrawImage(squareBmp, new Rectangle(0, 0, sz, sz));
                            }
                            using (var ms = new MemoryStream())
                            {
                                resized.Save(ms, ImageFormat.Png);
                                pngPayloads.Add(ms.ToArray());
                            }
                        }
                    }

                    using (var fs = new FileStream(icoOutputPath, FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        // ICONDIR header (6 bytes)
                        bw.Write((ushort)0); // Reserved
                        bw.Write((ushort)1); // Type: 1 = ICO
                        bw.Write((ushort)sizes.Length); // Count

                        int dataOffset = 6 + (16 * sizes.Length);
                        for (int i = 0; i < sizes.Length; i++)
                        {
                            int sz = sizes[i];
                            byte[] payload = pngPayloads[i];
                            bw.Write((byte)(sz >= 256 ? 0 : sz)); // Width
                            bw.Write((byte)(sz >= 256 ? 0 : sz)); // Height
                            bw.Write((byte)0); // ColorCount
                            bw.Write((byte)0); // Reserved
                            bw.Write((ushort)1); // Planes
                            bw.Write((ushort)32); // BitCount
                            bw.Write((uint)payload.Length); // BytesInRes
                            bw.Write((uint)dataOffset); // ImageOffset
                            dataOffset += payload.Length;
                        }

                        for (int i = 0; i < pngPayloads.Count; i++)
                        {
                            bw.Write(pngPayloads[i]);
                        }
                    }
                }
            }
        }
    }
}
