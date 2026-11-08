using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace WindowsTerminalLTSC
{
    public class TerminalTheme
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Background { get; set; }
        public string TabBarBackground { get; set; }
        public string ActiveTabBackground { get; set; }
        public string InactiveTabBackground { get; set; }
        public string Foreground { get; set; }
        public string SelectionBackground { get; set; }
        public string CursorColor { get; set; }
        public string AccentColor { get; set; }
        public string PromptColor { get; set; }
        public string CommandColor { get; set; }
        public string ErrorColor { get; set; }
        public string[] AnsiColors { get; set; } // 16 standard + bright colors

        private static readonly Dictionary<string, Color> _hexColorCache = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<uint, SolidColorBrush> _argbBrushCache = new Dictionary<uint, SolidColorBrush>();
        private static readonly object _cacheLock = new object();

        public static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex))
                return Colors.White;

            lock (_cacheLock)
            {
                Color cached;
                if (_hexColorCache.TryGetValue(hex, out cached))
                    return cached;
            }

            Color parsed;
            try
            {
                parsed = (Color)ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                parsed = Colors.White;
            }

            lock (_cacheLock)
            {
                if (_hexColorCache.Count > 512)
                    _hexColorCache.Clear();
                _hexColorCache[hex] = parsed;
            }
            return parsed;
        }

        public static SolidColorBrush GetCachedBrush(Color color)
        {
            uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            lock (_cacheLock)
            {
                SolidColorBrush existing;
                if (_argbBrushCache.TryGetValue(key, out existing))
                    return existing;

                if (_argbBrushCache.Count > 2048)
                    _argbBrushCache.Clear();

                var created = new SolidColorBrush(color);
                created.Freeze();
                _argbBrushCache[key] = created;
                return created;
            }
        }

        public static SolidColorBrush BrushFromHex(string hex)
        {
            return GetCachedBrush(ParseColor(hex));
        }

        public static List<TerminalTheme> GetBuiltInThemes()
        {
            return new List<TerminalTheme>
            {
                new TerminalTheme
                {
                    Name = "Campbell",
                    DisplayName = "Campbell (ค่าเริ่มต้น Windows Terminal)",
                    Background = "#0C0C0C",
                    TabBarBackground = "#1F1F1F",
                    ActiveTabBackground = "#0C0C0C",
                    InactiveTabBackground = "#2D2D2D",
                    Foreground = "#CCCCCC",
                    SelectionBackground = "#264F78",
                    CursorColor = "#FFFFFF",
                    AccentColor = "#0078D4",
                    PromptColor = "#16C60C",
                    CommandColor = "#F9F1A5",
                    ErrorColor = "#E74856",
                    AnsiColors = new[]
                    {
                        "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00",
                        "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
                        "#767676", "#E74856", "#16C60C", "#F9F1A5",
                        "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2"
                    }
                },
                new TerminalTheme
                {
                    Name = "OneHalfDark",
                    DisplayName = "One Half Dark (โทนมืดสบายตา)",
                    Background = "#282C34",
                    TabBarBackground = "#1E222A",
                    ActiveTabBackground = "#282C34",
                    InactiveTabBackground = "#21252B",
                    Foreground = "#DCDFE4",
                    SelectionBackground = "#3E4452",
                    CursorColor = "#528BFF",
                    AccentColor = "#61AFEF",
                    PromptColor = "#98C379",
                    CommandColor = "#E5C07B",
                    ErrorColor = "#E06C75",
                    AnsiColors = new[]
                    {
                        "#282C34", "#E06C75", "#98C379", "#E5C07B",
                        "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
                        "#5A6374", "#E06C75", "#98C379", "#E5C07B",
                        "#61AFEF", "#C678DD", "#56B6C2", "#FFFFFF"
                    }
                },
                new TerminalTheme
                {
                    Name = "Dracula",
                    DisplayName = "Dracula (โทนม่วงนีออน)",
                    Background = "#1E1F29",
                    TabBarBackground = "#15161E",
                    ActiveTabBackground = "#1E1F29",
                    InactiveTabBackground = "#282A36",
                    Foreground = "#F8F8F2",
                    SelectionBackground = "#44475A",
                    CursorColor = "#FF79C6",
                    AccentColor = "#BD93F9",
                    PromptColor = "#50FA7B",
                    CommandColor = "#F1FA8C",
                    ErrorColor = "#FF5555",
                    AnsiColors = new[]
                    {
                        "#21222C", "#FF5555", "#50FA7B", "#F1FA8C",
                        "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
                        "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5",
                        "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"
                    }
                },
                new TerminalTheme
                {
                    Name = "SolarizedDark",
                    DisplayName = "Solarized Dark (โทนน้ำเงินเข้ม)",
                    Background = "#002B36",
                    TabBarBackground = "#001E26",
                    ActiveTabBackground = "#002B36",
                    InactiveTabBackground = "#073642",
                    Foreground = "#93A1A1",
                    SelectionBackground = "#073642",
                    CursorColor = "#839496",
                    AccentColor = "#268BD2",
                    PromptColor = "#859900",
                    CommandColor = "#B58900",
                    ErrorColor = "#DC322F",
                    AnsiColors = new[]
                    {
                        "#073642", "#DC322F", "#859900", "#B58900",
                        "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                        "#586E75", "#CB4B16", "#586E75", "#657B83",
                        "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"
                    }
                },
                new TerminalTheme
                {
                    Name = "Matrix",
                    DisplayName = "Vintage Green (โทนเขียวเรโทร)",
                    Background = "#050A05",
                    TabBarBackground = "#0B160B",
                    ActiveTabBackground = "#050A05",
                    InactiveTabBackground = "#102010",
                    Foreground = "#00FF66",
                    SelectionBackground = "#114422",
                    CursorColor = "#00FF66",
                    AccentColor = "#00CC55",
                    PromptColor = "#33FF88",
                    CommandColor = "#CCFF66",
                    ErrorColor = "#FF5555",
                    AnsiColors = new[]
                    {
                        "#050A05", "#FF3333", "#00DD55", "#CCFF33",
                        "#00AAFF", "#CC66FF", "#00FFCC", "#00FF66",
                        "#226633", "#FF6666", "#33FF88", "#EEFF77",
                        "#55CCFF", "#EE99FF", "#66FFEE", "#CCFFDD"
                    }
                },
                new TerminalTheme
                {
                    Name = "OneHalfLight",
                    DisplayName = "One Half Light (โทนสว่าง)",
                    Background = "#FAFAFA",
                    TabBarBackground = "#E5E5E6",
                    ActiveTabBackground = "#FAFAFA",
                    InactiveTabBackground = "#DBDBDC",
                    Foreground = "#383A42",
                    SelectionBackground = "#BFCEFF",
                    CursorColor = "#526FFF",
                    AccentColor = "#0078D4",
                    PromptColor = "#50A14F",
                    CommandColor = "#986801",
                    ErrorColor = "#E45649",
                    AnsiColors = new[]
                    {
                        "#383A42", "#E45649", "#50A14F", "#C18401",
                        "#0184BC", "#A626A4", "#0997B3", "#FAFAFA",
                        "#4F525D", "#DF6C75", "#98C379", "#E4C07A",
                        "#61AFEF", "#C577DD", "#56B5C1", "#202227"
                    }
                }
            };
        }
    }

    public class TerminalSettings
    {
        public string ThemeName { get; set; }
        public string FontFamily { get; set; }
        public double FontSize { get; set; }
        public double WindowOpacity { get; set; }
        public string DefaultShell { get; set; } // "cmd" or "powershell"
        public int DefaultCodePage { get; set; } // 65001 (UTF-8) or 874 (Thai TIS-620)
        public bool ShowCommandInputBar { get; set; }

        public TerminalSettings()
        {
            ThemeName = "Campbell";
            FontFamily = "Consolas, Leelawadee UI, Tahoma, Segoe UI, Segoe UI Emoji, Segoe UI Symbol";
            FontSize = 15.0;
            WindowOpacity = 0.98;
            DefaultShell = "cmd";
            DefaultCodePage = 65001;
            ShowCommandInputBar = true;
        }

        private static string GetSettingsPath()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(dir, "settings.json");
        }

        public static TerminalSettings Load()
        {
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    var loaded = serializer.Deserialize<TerminalSettings>(json);
                    if (loaded != null)
                    {
                        if (string.IsNullOrEmpty(loaded.ThemeName)) loaded.ThemeName = "Campbell";
                        if (string.IsNullOrEmpty(loaded.FontFamily))
                            loaded.FontFamily = "Consolas, Leelawadee UI, Tahoma, Segoe UI, Segoe UI Emoji, Segoe UI Symbol";
                        else if (loaded.FontFamily.IndexOf("Segoe UI Emoji", StringComparison.OrdinalIgnoreCase) < 0)
                            loaded.FontFamily = loaded.FontFamily + ", Segoe UI Emoji, Segoe UI Symbol";
                        if (loaded.FontSize < 9 || loaded.FontSize > 42) loaded.FontSize = 15.0;
                        if (loaded.WindowOpacity < 0.75 || loaded.WindowOpacity > 1.0) loaded.WindowOpacity = 0.98;
                        if (loaded.DefaultCodePage != 65001 && loaded.DefaultCodePage != 874) loaded.DefaultCodePage = 65001;
                        return loaded;
                    }
                }
            }
            catch
            {
            }
            var def = new TerminalSettings();
            def.Save();
            return def;
        }

        public void Save()
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(this);
                File.WriteAllText(GetSettingsPath(), FormatJson(json), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static string FormatJson(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inQuotes = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                {
                    inQuotes = !inQuotes;
                    sb.Append(c);
                }
                else if (!inQuotes)
                {
                    if (c == '{' || c == '[')
                    {
                        sb.Append(c);
                        sb.AppendLine();
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                    }
                    else if (c == '}' || c == ']')
                    {
                        sb.AppendLine();
                        indent = Math.Max(0, indent - 1);
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(c);
                    }
                    else if (c == ',')
                    {
                        sb.Append(c);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                    }
                    else if (c == ':')
                    {
                        sb.Append(": ");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
