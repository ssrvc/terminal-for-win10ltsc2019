using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

namespace WindowsTerminalLTSCSetup
{
    public static class SetupProgram
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                SetProcessDPIAware();
            }
            catch { }

            bool isUninstallMode = false;
            bool isSilentInstall = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        isUninstallMode = true;
                    }
                    else if (string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase))
                    {
                        isSilentInstall = true;
                    }
                }
            }

            if (isSilentInstall)
            {
                string defaultDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "WindowsTerminalLTSC");
                InstallerEngine.PerformInstall(defaultDir, true, true, true, true, true, null);
                return 0;
            }

            var app = new Application();
            var win = new SetupWindow(isUninstallMode);
            app.Run(win);
            return 0;
        }
    }

    public static class InstallerEngine
    {
        private const string ContextMenuKeyName = "WindowsTerminalLTSC";
        private const string CmdProcessorKeyPath = @"Software\Microsoft\Command Processor";
        private const string AppPathsBaseKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
        private const string UninstallRegKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsTerminalLTSC";

        public static string GetExistingInstallDir()
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
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "WindowsTerminalLTSC");
        }

        public static bool IsAlreadyInstalled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallRegKeyPath))
                {
                    return k != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void ExtractResourceToFile(string resourceName, string targetPath)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null)
                    throw new InvalidOperationException("ไม่พบไฟล์ในแพ็กเกจติดตั้ง: " + resourceName);
                using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                {
                    s.CopyTo(fs);
                }
            }
        }

        public static void PerformInstall(
            string installDir,
            bool replaceFolderCmd,
            bool registerWtCommand,
            bool addExplorerRightClick,
            bool createDesktopShortcut,
            bool createStartMenuShortcut,
            Action<int, string> progressCallback)
        {
            if (progressCallback != null) progressCallback(10, "กำลังเตรียมโฟลเดอร์ปลายทาง...");

            // Stop running instances so files can be overwritten cleanly
            try
            {
                foreach (var proc in Process.GetProcessesByName("WindowsTerminalLTSC"))
                {
                    try { proc.Kill(); proc.WaitForExit(1500); } catch { }
                }
            }
            catch { }

            if (!Directory.Exists(installDir))
            {
                Directory.CreateDirectory(installDir);
            }

            string exePath = Path.Combine(installDir, "WindowsTerminalLTSC.exe");
            string icoPath = Path.Combine(installDir, "app.ico");
            string pngPath = Path.Combine(installDir, "app.png");
            string uninstallerPath = Path.Combine(installDir, "Uninstall-WindowsTerminalLTSC.exe");

            // Purge obsolete legacy folder from previous versions if present
            try
            {
                string oldLegacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsTerminalLTSC");
                if (Directory.Exists(oldLegacyDir))
                {
                    Directory.Delete(oldLegacyDir, true);
                }
            }
            catch { }

            // Purge legacy standalone uninstaller if present (uninstallation is now handled directly by WindowsTerminalLTSC.exe --uninstall)
            if (File.Exists(uninstallerPath))
            {
                try { File.Delete(uninstallerPath); } catch { }
            }

            if (progressCallback != null) progressCallback(30, "กำลังแตกไฟล์โปรแกรม WindowsTerminalLTSC.exe และไอคอน...");

            ExtractResourceToFile("WindowsTerminalLTSC.exe", exePath);
            ExtractResourceToFile("app.ico", icoPath);
            ExtractResourceToFile("app.png", pngPath);

            if (progressCallback != null) progressCallback(55, "กำลังตั้งค่าการเชื่อมต่อกับ Windows Explorer และคำสั่งลัด...");

            // Clean old integrations and ensure any legacy AutoRun / cmd.exe hijacks are removed
            UnregisterAllIntegrationsOnly();

            // Write helper scripts
            string wtCmd = Path.Combine(installDir, "wt.cmd");
            File.WriteAllText(wtCmd, "@echo off\r\nstart \"\" \"" + exePath + "\" \"%CD%\"\r\n", Encoding.ASCII);

            string oldHookCmd = Path.Combine(installDir, "wt_cmd_hook.cmd");
            if (File.Exists(oldHookCmd)) { try { File.Delete(oldHookCmd); } catch { } }

            if (registerWtCommand)
            {
                using (var wtKey = Registry.CurrentUser.CreateSubKey(AppPathsBaseKey + @"\wt.exe"))
                {
                    if (wtKey != null)
                    {
                        wtKey.SetValue("", exePath, RegistryValueKind.String);
                        wtKey.SetValue("Path", installDir, RegistryValueKind.String);
                    }
                }

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
            }

            if (addExplorerRightClick)
            {
                string iconSpec = "\"" + icoPath + "\"";
                RegisterExplorerMenu(@"Software\Classes\Directory\Background\shell\" + ContextMenuKeyName, exePath, iconSpec, "\"%V\"");
                RegisterExplorerMenu(@"Software\Classes\Directory\shell\" + ContextMenuKeyName, exePath, iconSpec, "\"%1\"");
                RegisterExplorerMenu(@"Software\Classes\Drive\shell\" + ContextMenuKeyName, exePath, iconSpec, "\"%1\"");
            }

            if (progressCallback != null) progressCallback(80, "กำลังสร้างไอคอนทางลัด (Shortcuts) และลงทะเบียนระบบ...");

            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (createDesktopShortcut)
            {
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktopDir, "Windows Terminal LTSC 2019.lnk"), exePath, userHome, icoPath);
            }

            if (createStartMenuShortcut)
            {
                string startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                CreateShortcut(Path.Combine(startMenuDir, "Windows Terminal LTSC 2019.lnk"), exePath, userHome, icoPath);
            }

            // Register in Windows Apps & Features (Add/Remove Programs)
            using (var uninstKey = Registry.CurrentUser.CreateSubKey(UninstallRegKeyPath))
            {
                if (uninstKey != null)
                {
                    uninstKey.SetValue("DisplayName", "Windows Terminal LTSC 2019 (รองรับภาษาไทย)", RegistryValueKind.String);
                    uninstKey.SetValue("DisplayIcon", icoPath, RegistryValueKind.String);
                    uninstKey.SetValue("DisplayVersion", "2.0.17763", RegistryValueKind.String);
                    uninstKey.SetValue("Publisher", "ssrvc (Free Non-Commercial Open Source)", RegistryValueKind.String);
                    uninstKey.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                    uninstKey.SetValue("UninstallString", "\"" + exePath + "\" --uninstall", RegistryValueKind.String);
                    uninstKey.SetValue("QuietUninstallString", "\"" + exePath + "\" --uninstall --silent", RegistryValueKind.String);
                    uninstKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    uninstKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }

            if (progressCallback != null) progressCallback(100, "ติดตั้งเสร็จสมบูรณ์ พร้อมใช้งานทันที!");
        }

        public static void PerformUninstall(Action<int, string> progressCallback)
        {
            if (progressCallback != null) progressCallback(20, "กำลังปิดโปรเซสที่กำลังทำงานอยู่...");
            try
            {
                foreach (var proc in Process.GetProcessesByName("WindowsTerminalLTSC"))
                {
                    try { proc.Kill(); proc.WaitForExit(1500); } catch { }
                }
            }
            catch { }

            string installDir = GetExistingInstallDir();

            if (progressCallback != null) progressCallback(50, "กำลังถอนการตั้งค่าออกจาก Registry และเมนูคลิกขวา...");
            UnregisterAllIntegrationsOnly();

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKeyPath, false);
            }
            catch { }

            if (progressCallback != null) progressCallback(80, "กำลังลบไอคอนลัดและไฟล์โปรแกรม...");
            try
            {
                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Windows Terminal LTSC 2019.lnk");
                string startLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Windows Terminal LTSC 2019.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
                if (File.Exists(startLnk)) File.Delete(startLnk);
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                {
                    foreach (string f in Directory.GetFiles(installDir))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }

            if (progressCallback != null) progressCallback(100, "ถอนการติดตั้งเรียบร้อยแล้ว");
        }

        private static void UnregisterAllIntegrationsOnly()
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
            catch { }
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

        private static void CreateShortcut(string lnkPath, string targetExe, string workingDir, string iconPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (shortcut == null) return;

                Type scType = shortcut.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { iconPath + ",0" });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Windows Terminal สำหรับ Windows 10 LTSC 2019 (รองรับภาษาไทย)" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch { }
        }
    }

    public class SetupWindow : Window
    {
        private TextBox _installPathBox;
        private CheckBox _chkReplaceCmd;
        private CheckBox _chkRegisterWt;
        private CheckBox _chkRightClickMenu;
        private CheckBox _chkDesktopShortcut;
        private CheckBox _chkStartMenuShortcut;
        private CheckBox _chkLaunchAfterInstall;

        private ProgressBar _progressBar;
        private TextBlock _statusText;
        private Button _installBtn;
        private Button _uninstallBtn;

        public SetupWindow(bool startInUninstallMode)
        {
            Title = "ติดตั้ง Windows Terminal LTSC 2019 (Setup Wizard)";
            Width = 680;
            Height = 650;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Background = new SolidColorBrush(Color.FromRgb(13, 17, 23));

            var chrome = new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0)
            };
            WindowChrome.SetWindowChrome(this, chrome);

            BitmapImage logoImg = LoadEmbeddedLogo();
            if (logoImg != null)
            {
                Icon = logoImg;
            }

            BuildModernSetupUI(logoImg, startInUninstallMode);
        }

        private BitmapImage LoadEmbeddedLogo()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("app.png"))
                {
                    if (s != null)
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = s;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                }
            }
            catch { }
            return null;
        }

        private void BuildModernSetupUI(BitmapImage logoImg, bool startInUninstallMode)
        {
            var outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(48, 58, 78)),
                Background = new SolidColorBrush(Color.FromRgb(13, 17, 23))
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });  // Custom Title Bar
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // Hero Banner
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Body Content
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // Footer Bar

            // 1. Custom Top Title Bar
            var titleBar = new Grid
            {
                Background = new SolidColorBrush(Color.FromRgb(10, 13, 18))
            };
            titleBar.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

            var titleLeft = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0)
            };
            titleLeft.Children.Add(new TextBlock
            {
                Text = "📦 ตัวติดตั้งโปรแกรม Windows Terminal สำหรับ Windows 10 LTSC 2019",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 175, 200)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            });
            titleBar.Children.Add(titleLeft);

            var closeWinBtn = new Button
            {
                Content = "✕",
                Width = 42,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 13
            };
            closeWinBtn.MouseEnter += (s, e) => closeWinBtn.Background = new SolidColorBrush(Color.FromRgb(232, 17, 35));
            closeWinBtn.MouseLeave += (s, e) => closeWinBtn.Background = Brushes.Transparent;
            closeWinBtn.Click += (s, e) => Close();
            titleBar.Children.Add(closeWinBtn);

            Grid.SetRow(titleBar, 0);
            rootGrid.Children.Add(titleBar);

            // 2. Gradient Hero Header Card
            var heroGradient = new LinearGradientBrush(
                Color.FromRgb(18, 34, 62),
                Color.FromRgb(13, 19, 31),
                new Point(0, 0),
                new Point(1, 1));

            var heroBorder = new Border
            {
                Background = heroGradient,
                BorderBrush = new SolidColorBrush(Color.FromRgb(38, 64, 110)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(24, 18, 24, 18)
            };
            heroBorder.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

            var heroGrid = new Grid();
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (logoImg != null)
            {
                var img = new Image
                {
                    Source = logoImg,
                    Width = 72,
                    Height = 72,
                    Margin = new Thickness(0, 0, 20, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Grid.SetColumn(img, 0);
                heroGrid.Children.Add(img);
            }

            var heroTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            titleRow.Children.Add(new TextBlock
            {
                Text = "Windows Terminal LTSC 2019",
                Foreground = Brushes.White,
                FontSize = 22,
                FontWeight = FontWeights.Bold
            });

            var badgeBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeBorder.Child = new TextBlock
            {
                Text = "THAI UTF-8 EDITION",
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.Bold
            };
            titleRow.Children.Add(badgeBorder);
            heroTextStack.Children.Add(titleRow);

            heroTextStack.Children.Add(new TextBlock
            {
                Text = "เทอร์มินัลยุคใหม่ที่ออกแบบมาเพื่อ Windows 10 Enterprise LTSC 2019 (Build 17763) โดยเฉพาะ\nพัฒนาโดย ssrvc — แจกจ่ายฟรี (Free Non-Commercial) ห้ามนำไปจำหน่ายหรือดัดแปลงเครดิตผู้พัฒนา",
                Foreground = new SolidColorBrush(Color.FromRgb(185, 200, 225)),
                FontSize = 12.5,
                LineHeight = 18
            });

            Grid.SetColumn(heroTextStack, 1);
            heroGrid.Children.Add(heroTextStack);
            heroBorder.Child = heroGrid;

            Grid.SetRow(heroBorder, 1);
            rootGrid.Children.Add(heroBorder);

            // 3. Main Content Area (Install Path Card + Checkboxes Card + Progress Card)
            var contentStack = new StackPanel
            {
                Margin = new Thickness(24, 16, 24, 12)
            };

            // Card A: Installation Directory
            var pathCard = CreateSectionCard("📁 โฟลเดอร์สำหรับติดตั้งโปรแกรม (Installation Folder)");
            var pathStack = new StackPanel();

            var pathGrid = new Grid();
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _installPathBox = new TextBox
            {
                Text = InstallerEngine.GetExistingInstallDir(),
                Background = new SolidColorBrush(Color.FromRgb(11, 15, 22)),
                Foreground = new SolidColorBrush(Color.FromRgb(235, 240, 250)),
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(50, 65, 90)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7),
                FontSize = 13,
                FontFamily = new FontFamily("Consolas, Segoe UI")
            };
            Grid.SetColumn(_installPathBox, 0);
            pathGrid.Children.Add(_installPathBox);

            var browseBtn = CreateSecondaryButton("เลือกโฟลเดอร์...", () =>
            {
                using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
                {
                    fbd.Description = "เลือกโฟลเดอร์ที่ต้องการติดตั้ง Windows Terminal LTSC 2019";
                    fbd.SelectedPath = _installPathBox.Text;
                    if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(fbd.SelectedPath))
                    {
                        string selected = fbd.SelectedPath;
                        if (!selected.EndsWith("WindowsTerminalLTSC", StringComparison.OrdinalIgnoreCase))
                        {
                            selected = Path.Combine(selected, "WindowsTerminalLTSC");
                        }
                        _installPathBox.Text = selected;
                    }
                }
            });
            browseBtn.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(browseBtn, 1);
            pathGrid.Children.Add(browseBtn);
            pathStack.Children.Add(pathGrid);

            pathCard.Child = pathStack;
            contentStack.Children.Add(pathCard);

            // Card B: Options Checkboxes
            var optionsCard = CreateSectionCard("⚙ ตัวเลือกการติดตั้งและการทำงานร่วมกับ Windows");
            var optionsStack = new StackPanel();

            _chkReplaceCmd = CreateModernOptionCheckBox(
                "ใช้แทน cmd เมื่อพิมพ์ในช่องที่อยู่ (Address Bar) ของโฟลเดอร์",
                "เมื่อเปิดโฟลเดอร์แล้วพิมพ์ cmd ในช่องค้นหาด้านบน จะเปิดเทอร์มินัลภาษาไทยตัวนี้ในโฟลเดอร์นั้นทันที",
                true);

            _chkRegisterWt = CreateModernOptionCheckBox(
                "เปิดใช้งานคำสั่งลัด 'wt' (เหมือน Windows Terminal ของแท้)",
                "สามารถพิมพ์ wt ในช่องที่อยู่โฟลเดอร์ หรือในหน้าต่าง Run (Win + R) เพื่อเรียกใช้งานได้ทันที",
                true);

            _chkRightClickMenu = CreateModernOptionCheckBox(
                "เพิ่มเมนูคลิกขวา \"เปิดใน Windows Terminal (ภาษาไทย)\" ในทุกโฟลเดอร์",
                "คลิกขวาในพื้นที่ว่างของโฟลเดอร์ หรือคลิกขวาที่ไอคอนโฟลเดอร์เพื่อเปิดเทอร์มินัลพร้อมไอคอน",
                true);

            _chkDesktopShortcut = CreateModernOptionCheckBox(
                "สร้างไอคอนทางลัด (Shortcut) บนหน้าจอ Desktop",
                "สร้างไอคอน Windows Terminal LTSC 2019 บนหน้าจอหลักเพื่อความสะดวกในการเปิดใช้งาน",
                true);

            _chkStartMenuShortcut = CreateModernOptionCheckBox(
                "เพิ่มโปรแกรมลงใน Start Menu และระบบถอนการติดตั้งของ Windows",
                "แสดงในเมนู Start และในหน้าต่าง Apps & Features ของ Windows",
                true);

            _chkLaunchAfterInstall = CreateModernOptionCheckBox(
                "เปิดโปรแกรม Windows Terminal LTSC 2019 ทันทีหลังติดตั้งเสร็จ",
                "เริ่มใช้งานเทอร์มินัลภาษาไทยทันทีเมื่อการติดตั้งเสร็จสมบูรณ์",
                true);

            optionsStack.Children.Add(_chkRegisterWt);
            optionsStack.Children.Add(_chkRightClickMenu);
            optionsStack.Children.Add(_chkDesktopShortcut);
            optionsStack.Children.Add(_chkStartMenuShortcut);
            optionsStack.Children.Add(_chkLaunchAfterInstall);

            optionsCard.Child = optionsStack;
            contentStack.Children.Add(optionsCard);

            // Progress Area
            var progressBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 26, 36)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(42, 54, 74)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 4, 0, 0)
            };
            var progStack = new StackPanel();
            _statusText = new TextBlock
            {
                Text = "พร้อมสำหรับการติดตั้ง กรุณาตรวจสอบตัวเลือกด้านบนแล้วกดปุ่ม \"ติดตั้งลงเครื่องทันที\"",
                Foreground = new SolidColorBrush(Color.FromRgb(180, 195, 220)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6)
            };
            _progressBar = new ProgressBar
            {
                Height = 8,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Background = new SolidColorBrush(Color.FromRgb(11, 15, 22)),
                Foreground = new SolidColorBrush(Color.FromRgb(0, 180, 216)),
                BorderThickness = new Thickness(0)
            };
            progStack.Children.Add(_statusText);
            progStack.Children.Add(_progressBar);
            progressBorder.Child = progStack;
            contentStack.Children.Add(progressBorder);

            Grid.SetRow(contentStack, 2);
            rootGrid.Children.Add(contentStack);

            // 4. Footer Bar with Uninstall + Install CTA Buttons
            var footerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(17, 22, 31)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(38, 48, 66)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(24, 14, 24, 14)
            };

            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _uninstallBtn = CreateSecondaryButton("🗑 ถอนการติดตั้ง (Uninstall)", RunUninstallFlow);
            _uninstallBtn.Visibility = (InstallerEngine.IsAlreadyInstalled() || startInUninstallMode)
                ? Visibility.Visible
                : Visibility.Collapsed;
            Grid.SetColumn(_uninstallBtn, 0);
            footerGrid.Children.Add(_uninstallBtn);

            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var cancelBtn = CreateSecondaryButton("ปิดหน้าต่าง", Close);
            cancelBtn.Margin = new Thickness(0, 0, 10, 0);

            _installBtn = new Button
            {
                Content = "🚀 ติดตั้งลงเครื่องทันที (Install Now)",
                Padding = new Thickness(22, 9, 22, 9),
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            _installBtn.MouseEnter += (s, e) => _installBtn.Background = new SolidColorBrush(Color.FromRgb(16, 142, 238));
            _installBtn.MouseLeave += (s, e) => _installBtn.Background = new SolidColorBrush(Color.FromRgb(0, 120, 212));
            _installBtn.Click += (s, e) => RunInstallFlow();

            rightButtons.Children.Add(cancelBtn);
            rightButtons.Children.Add(_installBtn);
            Grid.SetColumn(rightButtons, 2);
            footerGrid.Children.Add(rightButtons);

            footerBorder.Child = footerGrid;
            Grid.SetRow(footerBorder, 3);
            rootGrid.Children.Add(footerBorder);

            outerBorder.Child = rootGrid;
            Content = outerBorder;

            if (startInUninstallMode)
            {
                Loaded += (s, e) =>
                {
                    if (MessageBox.Show(
                        this,
                        "คุณต้องการถอนการติดตั้ง Windows Terminal LTSC 2019 และคืนค่า cmd เดิมใช่หรือไม่?",
                        "ยืนยันการถอนการติดตั้ง",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        RunUninstallFlow();
                    }
                };
            }
        }

        private Border CreateSectionCard(string headerTitle)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(21, 27, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(42, 54, 74)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var wrapper = new StackPanel();
            wrapper.Children.Add(new TextBlock
            {
                Text = headerTitle,
                Foreground = new SolidColorBrush(Color.FromRgb(95, 190, 255)),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var contentHolder = new ContentControl();
            wrapper.Children.Add(contentHolder);

            return new SectionCardBorder(card, wrapper, contentHolder);
        }

        private class SectionCardBorder : Border
        {
            private readonly ContentControl _holder;
            public SectionCardBorder(Border template, StackPanel wrapper, ContentControl holder)
            {
                Background = template.Background;
                BorderBrush = template.BorderBrush;
                BorderThickness = template.BorderThickness;
                CornerRadius = template.CornerRadius;
                Padding = template.Padding;
                Margin = template.Margin;
                _holder = holder;
                base.Child = wrapper;
            }

            public new UIElement Child
            {
                get { return _holder.Content as UIElement; }
                set { _holder.Content = value; }
            }
        }

        private CheckBox CreateModernOptionCheckBox(string title, string subtitle, bool defaultChecked)
        {
            var stack = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold
            });
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = new SolidColorBrush(Color.FromRgb(145, 160, 185)),
                FontSize = 11
            });

            return new CheckBox
            {
                Content = stack,
                IsChecked = defaultChecked,
                Margin = new Thickness(0, 3, 0, 5),
                VerticalContentAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand
            };
        }

        private Button CreateSecondaryButton(string text, Action onClick)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(14, 7, 14, 7),
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 230, 245)),
                Background = new SolidColorBrush(Color.FromRgb(32, 42, 58)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(64, 82, 110)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            btn.MouseEnter += (s, e) => btn.Background = new SolidColorBrush(Color.FromRgb(48, 62, 86));
            btn.MouseLeave += (s, e) => btn.Background = new SolidColorBrush(Color.FromRgb(32, 42, 58));
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private async void RunInstallFlow()
        {
            string targetDir = _installPathBox.Text.Trim();
            if (string.IsNullOrEmpty(targetDir))
            {
                MessageBox.Show(this, "กรุณาระบุโฟลเดอร์สำหรับติดตั้ง", "แจ้งเตือน", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool replaceCmd = _chkReplaceCmd.IsChecked == true;
            bool registerWt = _chkRegisterWt.IsChecked == true;
            bool rightClick = _chkRightClickMenu.IsChecked == true;
            bool desktopSc = _chkDesktopShortcut.IsChecked == true;
            bool startMenuSc = _chkStartMenuShortcut.IsChecked == true;
            bool launchNow = _chkLaunchAfterInstall.IsChecked == true;

            _installBtn.IsEnabled = false;
            _uninstallBtn.IsEnabled = false;

            try
            {
                await Task.Run(() =>
                {
                    InstallerEngine.PerformInstall(
                        targetDir,
                        replaceCmd,
                        registerWt,
                        rightClick,
                        desktopSc,
                        startMenuSc,
                        (pct, msg) =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _progressBar.Value = pct;
                                _statusText.Text = msg;
                                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123));
                            }));
                        });
                });

                _uninstallBtn.Visibility = Visibility.Visible;
                _installBtn.Content = "✓ ติดตั้งเรียบร้อยแล้ว (ติดตั้งซ้ำ/อัปเดต)";

                if (launchNow)
                {
                    string installedExe = Path.Combine(targetDir, "WindowsTerminalLTSC.exe");
                    if (File.Exists(installedExe))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = installedExe,
                            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                        });
                        Close();
                        return;
                    }
                }

                MessageBox.Show(
                    this,
                    "ติดตั้ง Windows Terminal LTSC 2019 เรียบร้อยแล้ว!\n\n" +
                    "• ตำแหน่งไฟล์: " + targetDir + "\n" +
                    (replaceCmd ? "• พิมพ์ cmd ในช่องที่อยู่โฟลเดอร์ได้ทันที\n" : "") +
                    (registerWt ? "• พิมพ์ wt ในช่องที่อยู่โฟลเดอร์ หรือ Win+R ได้ทันที\n" : "") +
                    (rightClick ? "• คลิกขวาในโฟลเดอร์เพื่อเลือก 'เปิดใน Windows Terminal (ภาษาไทย)' ได้ทันที" : ""),
                    "ติดตั้งสำเร็จ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "เกิดข้อผิดพลาดระหว่างติดตั้ง:\n" + ex.Message, "ข้อผิดพลาด", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _installBtn.IsEnabled = true;
                _uninstallBtn.IsEnabled = true;
            }
        }

        private async void RunUninstallFlow()
        {
            _installBtn.IsEnabled = false;
            _uninstallBtn.IsEnabled = false;

            try
            {
                await Task.Run(() =>
                {
                    InstallerEngine.PerformUninstall((pct, msg) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _progressBar.Value = pct;
                            _statusText.Text = msg;
                            _statusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 184, 108));
                        }));
                    });
                });

                _uninstallBtn.Visibility = Visibility.Collapsed;
                MessageBox.Show(
                    this,
                    "ถอนการติดตั้งและคืนค่า cmd เดิมเรียบร้อยแล้วครับ",
                    "ถอนการติดตั้งสำเร็จ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "เกิดข้อผิดพลาดระหว่างถอนการติดตั้ง:\n" + ex.Message, "ข้อผิดพลาด", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _installBtn.IsEnabled = true;
                _uninstallBtn.IsEnabled = true;
            }
        }
    }
}
