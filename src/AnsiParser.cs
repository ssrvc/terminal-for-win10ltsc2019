using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Media;

namespace WindowsTerminalLTSC
{
    public class AnsiSegment
    {
        public string Text { get; set; }
        public Color? Foreground { get; set; }
        public Color? Background { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public bool ClearScreen { get; set; }
        public string WindowTitle { get; set; }
    }

    public class AnsiParser
    {
        private Color? _currentFg = null;
        private Color? _currentBg = null;
        private bool _bold = false;
        private bool _italic = false;
        private bool _underline = false;
        private bool _inverse = false;
        private TerminalTheme _theme;

        public AnsiParser(TerminalTheme theme)
        {
            _theme = theme;
        }

        public void SetTheme(TerminalTheme theme)
        {
            _theme = theme;
        }

        public void ResetStyle()
        {
            _currentFg = null;
            _currentBg = null;
            _bold = false;
            _italic = false;
            _underline = false;
            _inverse = false;
        }

        public List<AnsiSegment> Parse(string input)
        {
            var segments = new List<AnsiSegment>();
            if (string.IsNullOrEmpty(input))
                return segments;

            var sb = new StringBuilder();
            int i = 0;
            int len = input.Length;

            while (i < len)
            {
                char c = input[i];

                // Form feed (clear screen)
                if (c == '\x0C')
                {
                    FlushCurrentText(segments, sb);
                    segments.Add(new AnsiSegment { ClearScreen = true });
                    i++;
                    continue;
                }

                // ESC sequence
                if (c == '\x1B' && i + 1 < len)
                {
                    char next = input[i + 1];

                    // CSI sequence: ESC [
                    if (next == '[')
                    {
                        FlushCurrentText(segments, sb);
                        i += 2;
                        int startParams = i;
                        while (i < len && !((input[i] >= '@' && input[i] <= '~')))
                        {
                            i++;
                        }
                        if (i < len)
                        {
                            char cmd = input[i];
                            string paramStr = input.Substring(startParams, i - startParams);
                            i++;
                            HandleCsi(cmd, paramStr, segments);
                        }
                        continue;
                    }
                    // OSC sequence: ESC ] (e.g. window title ESC ] 0 ; Title BEL)
                    else if (next == ']')
                    {
                        FlushCurrentText(segments, sb);
                        i += 2;
                        int startOsc = i;
                        while (i < len && input[i] != '\x07' && !(input[i] == '\x1B' && i + 1 < len && input[i + 1] == '\\'))
                        {
                            i++;
                        }
                        string oscContent = input.Substring(startOsc, i - startOsc);
                        if (i < len)
                        {
                            if (input[i] == '\x07') i++;
                            else if (input[i] == '\x1B') i += 2;
                        }
                        if (oscContent.StartsWith("0;") || oscContent.StartsWith("2;"))
                        {
                            segments.Add(new AnsiSegment { WindowTitle = oscContent.Substring(2) });
                        }
                        continue;
                    }
                }

                sb.Append(c);
                i++;
            }

            FlushCurrentText(segments, sb);
            return segments;
        }

        private void FlushCurrentText(List<AnsiSegment> segments, StringBuilder sb)
        {
            if (sb.Length == 0) return;

            Color? fg = _currentFg;
            Color? bg = _currentBg;
            if (_inverse)
            {
                Color defaultFg = TerminalTheme.ParseColor(_theme.Foreground);
                Color defaultBg = TerminalTheme.ParseColor(_theme.Background);
                Color actualFg = fg ?? defaultFg;
                Color actualBg = bg ?? defaultBg;
                fg = actualBg;
                bg = actualFg;
            }

            segments.Add(new AnsiSegment
            {
                Text = sb.ToString(),
                Foreground = fg,
                Background = bg,
                Bold = _bold,
                Italic = _italic,
                Underline = _underline
            });
            sb.Clear();
        }

        private void HandleCsi(char cmd, string paramStr, List<AnsiSegment> segments)
        {
            if (cmd == 'J')
            {
                if (paramStr == "2" || paramStr == "3")
                {
                    segments.Add(new AnsiSegment { ClearScreen = true });
                }
                return;
            }

            if (cmd != 'm')
                return;

            if (string.IsNullOrEmpty(paramStr))
            {
                ResetStyle();
                return;
            }

            string[] parts = paramStr.Split(';');
            int idx = 0;
            while (idx < parts.Length)
            {
                int code;
                if (!int.TryParse(parts[idx], out code))
                {
                    code = 0;
                }

                if (code == 0)
                {
                    ResetStyle();
                }
                else if (code == 1) _bold = true;
                else if (code == 3) _italic = true;
                else if (code == 4) _underline = true;
                else if (code == 7) _inverse = true;
                else if (code == 22) _bold = false;
                else if (code == 23) _italic = false;
                else if (code == 24) _underline = false;
                else if (code == 27) _inverse = false;
                else if (code >= 30 && code <= 37)
                {
                    int colorIdx = code - 30 + (_bold ? 8 : 0);
                    _currentFg = GetAnsiColor(colorIdx);
                }
                else if (code == 39)
                {
                    _currentFg = null;
                }
                else if (code >= 40 && code <= 47)
                {
                    _currentBg = GetAnsiColor(code - 40);
                }
                else if (code == 49)
                {
                    _currentBg = null;
                }
                else if (code >= 90 && code <= 97)
                {
                    _currentFg = GetAnsiColor(code - 90 + 8);
                }
                else if (code >= 100 && code <= 107)
                {
                    _currentBg = GetAnsiColor(code - 100 + 8);
                }
                else if ((code == 38 || code == 48) && idx + 1 < parts.Length)
                {
                    bool isFg = (code == 38);
                    int mode;
                    int.TryParse(parts[idx + 1], out mode);
                    if (mode == 5 && idx + 2 < parts.Length)
                    {
                        int n;
                        if (int.TryParse(parts[idx + 2], out n))
                        {
                            Color c256 = Get256Color(n);
                            if (isFg) _currentFg = c256;
                            else _currentBg = c256;
                        }
                        idx += 2;
                    }
                    else if (mode == 2 && idx + 4 < parts.Length)
                    {
                        int r, g, b;
                        int.TryParse(parts[idx + 2], out r);
                        int.TryParse(parts[idx + 3], out g);
                        int.TryParse(parts[idx + 4], out b);
                        Color rgb = Color.FromRgb(
                            (byte)Math.Max(0, Math.Min(255, r)),
                            (byte)Math.Max(0, Math.Min(255, g)),
                            (byte)Math.Max(0, Math.Min(255, b)));
                        if (isFg) _currentFg = rgb;
                        else _currentBg = rgb;
                        idx += 4;
                    }
                }

                idx++;
            }
        }

        private Color GetAnsiColor(int index)
        {
            if (_theme != null && _theme.AnsiColors != null && index >= 0 && index < _theme.AnsiColors.Length)
            {
                return TerminalTheme.ParseColor(_theme.AnsiColors[index]);
            }
            return Colors.LightGray;
        }

        private Color Get256Color(int n)
        {
            if (n < 0) n = 0;
            if (n > 255) n = 255;
            if (n < 16) return GetAnsiColor(n);
            if (n < 232)
            {
                int idx = n - 16;
                int r = (idx / 36) % 6;
                int g = (idx / 6) % 6;
                int b = idx % 6;
                byte rb = (byte)(r == 0 ? 0 : 55 + r * 40);
                byte gb = (byte)(g == 0 ? 0 : 55 + g * 40);
                byte bb = (byte)(b == 0 ? 0 : 55 + b * 40);
                return Color.FromRgb(rb, gb, bb);
            }
            byte gray = (byte)(8 + (n - 232) * 10);
            return Color.FromRgb(gray, gray, gray);
        }
    }
}
