using System;
using System.IO;
using System.Text.RegularExpressions;

namespace WindowsTerminalLTSC
{
    public enum InteractivePromptKind
    {
        None,
        YesNo,
        FullWordYesNo,
        YesNoAll,
        PressKeyToContinue,
        PythonRepl,
        ElevationRequired,
        GeneralInput
    }

    public static class CommandDescriber
    {
        private static readonly Regex YesNoAllRegex = new Regex(
            @"(\(Y/N/A\)|\[Y/N/A\]|\(Yes/No/All\)|\[Yes/No/All\]|\[Y\]\s*Yes\s+\[A\]\s*Yes to All)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FullWordYesNoRegex = new Regex(
            @"(\(yes/no(?:/\[fingerprint\])?\)|Please type 'yes' or 'no'|Type 'yes' to continue)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex YesNoRegex = new Regex(
            @"(\(Y/N\)\s*\??|\[Y/N\]\s*\??|\(y/n\)\s*\??|\[y/n\]\s*\??|\[Y/n\]|\[y/N\]|\(Yes/No\)|\[Yes/No\]|\[Y\]\s*Yes\s+.*\[N\]\s*No|Proceed\s*\([yn/]+\)|Continue\s*\([yn/]+\)|Overwrite\s*.*\([yn/]+|Are you sure|Do you agree to all|ต้องการดำเนินการต่อหรือไม่|ยืนยันหรือไม่)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PressAnyKeyRegex = new Regex(
            @"(Press any key to continue|Press ENTER to continue|กดปุ่มใดก็ได้เพื่อทำต่อ)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ElevationErrorRegex = new Regex(
            @"(requires elevation|Elevated permissions are required|running in elevated mode|sufficient privilege|You must be an administrator|Access is denied|Access Denied|System error 5 has occurred|Error:\s*740\b|การเข้าถึงถูกปฏิเสธ|administrator privileges required)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool DetectElevationError(string outputText)
        {
            if (string.IsNullOrEmpty(outputText))
                return false;
            string cleaned = outputText.IndexOf('\0') >= 0 ? outputText.Replace("\0", "") : outputText;
            return ElevationErrorRegex.IsMatch(cleaned);
        }

        public static InteractivePromptKind DetectInteractivePrompt(string recentOutput, out string promptQuestion)
        {
            promptQuestion = "";
            if (string.IsNullOrEmpty(recentOutput))
                return InteractivePromptKind.None;

            string trimmedEnd = recentOutput.TrimEnd(' ', '\t');
            string[] lines = trimmedEnd.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            string lastLine = lines.Length > 0 ? lines[lines.Length - 1].Trim() : "";

            if (string.IsNullOrEmpty(lastLine))
                return InteractivePromptKind.None;

            if (YesNoAllRegex.IsMatch(lastLine))
            {
                promptQuestion = lastLine;
                return InteractivePromptKind.YesNoAll;
            }

            if (FullWordYesNoRegex.IsMatch(lastLine))
            {
                promptQuestion = lastLine;
                return InteractivePromptKind.FullWordYesNo;
            }

            if (YesNoRegex.IsMatch(lastLine))
            {
                promptQuestion = lastLine;
                return InteractivePromptKind.YesNo;
            }

            if (PressAnyKeyRegex.IsMatch(lastLine))
            {
                promptQuestion = lastLine;
                return InteractivePromptKind.PressKeyToContinue;
            }

            if (lastLine.StartsWith(">>>") ||
                recentOutput.IndexOf("Type \"help\", \"copyright\", \"credits\" or \"license\"", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                promptQuestion = "Python Interactive Shell (>>>)";
                return InteractivePromptKind.PythonRepl;
            }

            return InteractivePromptKind.None;
        }

        public static string DescribeCommand(string rawCommandLine, bool isRunning)
        {
            if (string.IsNullOrWhiteSpace(rawCommandLine))
                return "";

            string trimmed = rawCommandLine.Trim();
            string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "";

            string cmd = parts[0].Trim('"', '\'').ToLowerInvariant();
            if (cmd.EndsWith(".exe") || cmd.EndsWith(".cmd") || cmd.EndsWith(".bat") || cmd.EndsWith(".com"))
            {
                int dot = cmd.LastIndexOf('.');
                if (dot > 0) cmd = cmd.Substring(0, dot);
            }

            string args = parts.Length > 1 ? trimmed.Substring(parts[0].Length).Trim() : "";
            string prefix = isRunning ? "กำลัง" : "คำสั่งสำหรับ";

            switch (cmd)
            {
                case "dir":
                case "ls":
                case "gci":
                case "get-childitem":
                    return prefix + "แสดงรายชื่อไฟล์และโฟลเดอร์ในตำแหน่งปัจจุบัน" + (string.IsNullOrEmpty(args) ? "" : " (" + args + ")");

                case "cd":
                case "chdir":
                case "sl":
                case "set-location":
                    if (args == "..") return prefix + "ย้อนกลับไปยังโฟลเดอร์ก่อนหน้า 1 ระดับ";
                    return prefix + "เปลี่ยนโฟลเดอร์ทำงานไปยัง: " + (string.IsNullOrEmpty(args) ? "โฟลเดอร์ปัจจุบัน" : args);

                case "md":
                case "mkdir":
                case "new-item":
                    return prefix + "สร้างโฟลเดอร์ใหม่ชื่อ: " + args;

                case "rd":
                case "rmdir":
                    if (args.IndexOf("/s", StringComparison.OrdinalIgnoreCase) >= 0)
                        return prefix + "ลบโฟลเดอร์และไฟล์ข้างในทั้งหมด (จะถามยืนยัน Y/N หากไม่ได้ใส่ /q): " + args;
                    return prefix + "ลบโฟลเดอร์: " + args;

                case "del":
                case "erase":
                case "rm":
                case "remove-item":
                    if (args.IndexOf("/p", StringComparison.OrdinalIgnoreCase) >= 0 || args.IndexOf("-confirm", StringComparison.OrdinalIgnoreCase) >= 0)
                        return prefix + "ลบไฟล์โดยถามยืนยัน (Y/N) ก่อนลบทุกไฟล์: " + args;
                    return prefix + "ลบไฟล์: " + args;

                case "copy":
                case "cp":
                case "copy-item":
                    return prefix + "คัดลอกไฟล์: " + args;

                case "xcopy":
                case "robocopy":
                    return prefix + "คัดลอกไฟล์และโครงสร้างโฟลเดอร์ขั้นสูง: " + args;

                case "move":
                case "mv":
                case "move-item":
                    return prefix + "ย้ายไฟล์หรือเปลี่ยนตำแหน่งโฟลเดอร์: " + args;

                case "ren":
                case "rename":
                case "rename-item":
                    return prefix + "เปลี่ยนชื่อไฟล์หรือโฟลเดอร์: " + args;

                case "type":
                case "cat":
                case "gc":
                case "get-content":
                    return prefix + "อ่านและแสดงเนื้อหาข้างในไฟล์ข้อความ: " + args;

                case "echo":
                case "write-host":
                case "write-output":
                    return prefix + "แสดงข้อความออกทางหน้าจอเทอร์มินัล";

                case "cls":
                case "clear":
                case "clear-host":
                    return prefix + "ล้างข้อความทั้งหมดบนหน้าจอเทอร์มินัล";

                case "ping":
                case "test-connection":
                    if (args.IndexOf("-t", StringComparison.OrdinalIgnoreCase) >= 0)
                        return prefix + "ทดสอบสัญญาณเครือข่าย (Ping) แบบต่อเนื่อง — กด Ctrl+C เพื่อหยุด (" + args + ")";
                    return prefix + "ทดสอบความเร็วและการเชื่อมต่อเครือข่าย (Ping) ไปยัง " + (string.IsNullOrEmpty(args) ? "ปลายทาง" : args);

                case "ipconfig":
                    if (args.IndexOf("/flushdns", StringComparison.OrdinalIgnoreCase) >= 0)
                        return prefix + "ล้างแคช DNS ของระบบเครือข่าย (Flush DNS)";
                    if (args.IndexOf("/all", StringComparison.OrdinalIgnoreCase) >= 0)
                        return prefix + "แสดงรายละเอียดเครือข่าย, IP Address, MAC Address และ DNS ทั้งหมด";
                    return prefix + "ตรวจสอบหมายเลข IP Address และการเชื่อมต่อเครือข่ายของเครื่อง";

                case "tracert":
                case "pathping":
                    return prefix + "ตรวจสอบเส้นทางวิ่งของข้อมูลเครือข่าย (Trace Route) ไปยัง " + args;

                case "netstat":
                    return prefix + "ตรวจสอบพอร์ตเครือข่ายและการเชื่อมต่อที่กำลังเปิดใช้งานในเครื่อง";

                case "nslookup":
                    return prefix + "ค้นหาหมายเลข IP จากชื่อโดเมนผ่านระบบ DNS (" + args + ")";

                case "sfc":
                    return prefix + "สแกนและซ่อมแซมไฟล์ระบบของ Windows ที่เสียหาย (ต้องใช้สิทธิ์ Administrator)";

                case "dism":
                    return prefix + "ตรวจสอบและซ่อมแซมอิมเมจของระบบ Windows (ต้องใช้สิทธิ์ Administrator)";

                case "chkdsk":
                    return prefix + "ตรวจสอบและซ่อมแซมข้อผิดพลาดของฮาร์ดดิสก์/พาร์ทิชัน (ต้องใช้สิทธิ์ Administrator)";

                case "diskpart":
                    return prefix + "จัดการดิสก์และพาร์ทิชันขั้นสูง (DiskPart — ต้องใช้สิทธิ์ Administrator)";

                case "tasklist":
                case "gps":
                case "get-process":
                    return prefix + "แสดงรายชื่อโปรแกรมและโปรเซสทั้งหมดที่กำลังทำงานอยู่ในเครื่อง";

                case "taskkill":
                case "spps":
                case "stop-process":
                    return prefix + "สั่งปิดการทำงานของโปรแกรมหรือโปรเซส (" + args + ")";

                case "systeminfo":
                    return prefix + "ดึงข้อมูลสเปกเครื่อง รุ่น Windows และการอัปเดตทั้งหมดของระบบ";

                case "whoami":
                    return prefix + "แสดงชื่อบัญชีผู้ใช้ปัจจุบันและสิทธิ์การใช้งาน";

                case "hostname":
                    return prefix + "แสดงชื่อเครื่องคอมพิวเตอร์ (Computer Name)";

                case "ver":
                    return prefix + "แสดงเลขเวอร์ชันและ Build ของระบบปฏิบัติการ Windows";

                case "set":
                    if (string.IsNullOrEmpty(args))
                        return prefix + "แสดงตัวแปรสภาพแวดล้อม (Environment Variables) ทั้งหมด";
                    return prefix + "ตั้งค่าหรือแสดงค่าตัวแปรสภาพแวดล้อม: " + args;

                case "chcp":
                    return prefix + "ตรวจสอบหรือเปลี่ยนรหัสภาษาของคอนโซล (Code Page)";

                case "pause":
                    return prefix + "หยุดรอให้ผู้ใช้กดปุ่มเพื่อดำเนินการต่อ";

                case "choice":
                    return prefix + "รอให้ผู้ใช้เลือกตอบตัวเลือก (เช่น Y/N)";

                case "winget":
                case "choco":
                    return prefix + "ติดตั้งหรืออัปเดตโปรแกรมผ่านระบบจัดการแพ็กเกจ (" + args + ")";

                case "agy":
                    return prefix + "ใช้งานเครื่องมือ Antigravity CLI" + (string.IsNullOrEmpty(args) ? "" : " (" + args + ")");

                case "git":
                    return prefix + "ทำงานกับระบบจัดการซอร์สโค้ด Git (" + args + ")";

                case "python":
                case "py":
                    if (args == "-v")
                        return prefix + "เข้าโหมดโต้ตอบ Python แบบแสดงการโหลดโมดูล (หากต้องการเช็กเลขเวอร์ชัน ให้ใช้ python -V ตัวใหญ่ หรือ python --version)";
                    if (args == "-V" || string.Equals(args, "--version", StringComparison.OrdinalIgnoreCase))
                        return prefix + "ตรวจสอบเลขเวอร์ชันของภาษา Python ที่ติดตั้งในเครื่อง";
                    if (string.IsNullOrEmpty(args))
                        return prefix + "เปิดโหมดโต้ตอบภาษา Python (พิมพ์ exit() เพื่อออกจากโหมด Python)";
                    return prefix + "รันโปรแกรมหรือสคริปต์ภาษา Python (" + args + ")";

                case "pip":
                    return prefix + "ติดตั้งหรือจัดการไลบรารี Python (" + args + ")";

                case "node":
                    return prefix + "รันโปรแกรม JavaScript ด้วย Node.js (" + args + ")";

                case "npm":
                case "npx":
                    return prefix + "จัดการแพ็กเกจหรือรันสคริปต์ Node.js (" + args + ")";

                case "curl":
                case "invoke-webrequest":
                case "iwr":
                    if (trimmed.IndexOf("antigravity.google/cli/install", StringComparison.OrdinalIgnoreCase) >= 0)
                        return prefix + "ดาวน์โหลดและติดตั้ง Antigravity CLI (~212 MB แบบซ่อนแถบโหลด -s กรุณารอจนเสร็จ)";
                    if (args.IndexOf("-s", StringComparison.Ordinal) >= 0 || args.IndexOf("-fsSL", StringComparison.Ordinal) >= 0)
                        return prefix + "ดาวน์โหลดไฟล์แบบซ่อนข้อความสถานะ (-s Silent Mode): " + args;
                    return prefix + "ดึงข้อมูลหรือดาวน์โหลดไฟล์จาก URL (" + args + ")";

                case "ssh":
                    return prefix + "เชื่อมต่อเข้าสู่เครื่องเซิร์ฟเวอร์ระยะไกลผ่านโปรโตคอล SSH (" + args + ")";

                case "explorer":
                    return prefix + "เปิดหน้าต่าง File Explorer ไปยังโฟลเดอร์ (" + args + ")";

                case "notepad":
                    return prefix + "เปิดโปรแกรม Notepad เพื่อแก้ไขไฟล์ข้อความ (" + args + ")";

                case "shutdown":
                    return prefix + "สั่งปิดเครื่องหรือรีสตาร์ทคอมพิวเตอร์ (" + args + ")";

                default:
                    return (isRunning ? "กำลังรันคำสั่ง: " : "เตรียมรันคำสั่ง: ") + trimmed;
            }
        }

        private static readonly Regex OutputFileRegex = new Regex(
            @"(?:^|\s)(?:-o|--output|-OutFile)\s+(""[^""]+""|'[^']+'|[^\s&|]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string TryGetLiveDownloadProgress(string rawCommandLine, string currentDirectory, DateTime startTime)
        {
            if (string.IsNullOrWhiteSpace(rawCommandLine))
                return null;

            try
            {
                if (rawCommandLine.IndexOf("antigravity.google/cli", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rawCommandLine.IndexOf("install.cmd", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string stagingPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "antigravity", "staging", "agy.exe");
                    if (File.Exists(stagingPath))
                    {
                        var fi = new FileInfo(stagingPath);
                        if (fi.LastWriteTime >= startTime.AddSeconds(-3) && fi.Length > 0)
                        {
                            return string.Format("📥 โหลดแล้ว {0:0.1} / ~212 MB", fi.Length / 1048576.0);
                        }
                    }
                }

                MatchCollection matches = OutputFileRegex.Matches(rawCommandLine);
                for (int i = matches.Count - 1; i >= 0; i--)
                {
                    string target = matches[i].Groups[1].Value.Trim('"', '\'');
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    string fullPath = Path.IsPathRooted(target)
                        ? target
                        : Path.Combine(currentDirectory ?? Environment.CurrentDirectory, target);
                    if (File.Exists(fullPath))
                    {
                        var fi = new FileInfo(fullPath);
                        if (fi.LastWriteTime >= startTime.AddSeconds(-3) && fi.Length > 10240)
                        {
                            if (fi.Length >= 1048576)
                                return string.Format("📥 โหลดแล้ว {0:0.1} MB", fi.Length / 1048576.0);
                            return string.Format("📥 โหลดแล้ว {0:0} KB", fi.Length / 1024.0);
                        }
                    }
                }
            }
            catch
            {
            }
            return null;
        }
    }
}
