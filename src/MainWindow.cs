using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WindowsTerminalLTSC
{
    public class TerminalTabItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string CustomTitle { get; set; }
        public ShellType Shell { get; set; }
        public TerminalPaneControl Pane { get; set; }
        public Border TabHeaderBorder { get; set; }
        public TextBlock TabTitleBlock { get; set; }
        public Border ActiveIndicator { get; set; }
    }

    public class TerminalPaneControl : Grid
    {
        public TerminalSession Session { get; private set; }
        public RichTextBox TerminalBox { get; private set; }
        public TextBox InputBox { get; private set; }
        public TextBlock PromptLabel { get; private set; }
        public Border InputBarBorder { get; private set; }
        public Border ActivityBannerBorder { get; private set; }

        private Paragraph _currentParagraph;
        private Run _liveInputRun;
        private Run _cursorRun;
        private AnsiParser _ansiParser;
        private TerminalTheme _theme;
        private TerminalSettings _settings;
        private DispatcherTimer _cursorTimer;
        private DispatcherTimer _activityTimer;
        private bool _cursorVisible = true;
        private bool _syncingInput = false;
        private int _lastSearchMatchIndex = -1;
        private string _lastSearchQuery = "";

        private TextBlock _activityIconBlock;
        private TextBlock _activityTitleBlock;
        private TextBlock _activityElapsedBlock;
        private StackPanel _ynButtonsPanel;
        private Button _btnYes;
        private Button _btnNo;
        private Button _btnAll;
        private Button _btnContinue;
        private Button _btnElevate;
        private Button _btnDismissElevate;
        private Button _btnExitTui;
        private TuiSurfaceControl _activeTuiSurface;
        private string _activeTuiCommandLine = "";
        private InteractivePromptKind _currentPromptKind = InteractivePromptKind.None;

        public bool IsInTuiMode
        {
            get { return _activeTuiSurface != null; }
        }
        private string _lastExecutedCommand = "";
        private string _pendingElevationRetryCommand = null;
        private string _cachedLiveDownloadText = null;
        private int _spinnerTickCount = 0;
        private int _spinnerIndex = 0;
        private static readonly string[] SpinnerFrames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

        private static readonly Regex UrlRegex = new Regex(@"(https?://[^\s""'<>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const int MaxScrollbackInlines = 3200;

        public event Action<TerminalPaneControl> PaneFocused;
        public event Action<string> TitleChanged;
        public event Action<string> DirectoryChanged;
        public event Action CloseRequested;
        public event Action<string> StatusMessage;

        public TerminalPaneControl(ShellType shell, string startDir, int codePage, TerminalTheme theme, TerminalSettings settings)
        {
            _theme = theme;
            _settings = settings;
            _ansiParser = new AnsiParser(theme);
            AllowDrop = true;

            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Activity & (Y/N)? Banner
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Command Input Bar

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(14, 10, 14, 10),
                LineHeight = Math.Round(_settings.FontSize * 1.48),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };

            _currentParagraph = CreateNewParagraph();
            doc.Blocks.Add(_currentParagraph);

            TerminalBox = new RichTextBox(doc)
            {
                IsReadOnly = true,
                IsDocumentEnabled = true,
                AllowDrop = true,
                BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                UndoLimit = 0,
                AcceptsReturn = false,
                AcceptsTab = false,
                Cursor = Cursors.IBeam,
                Padding = new Thickness(0)
            };
            TextOptions.SetTextFormattingMode(TerminalBox, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(TerminalBox, TextRenderingMode.ClearType);

            // Drag & Drop support on TerminalBox and InputBox
            TerminalBox.PreviewDragOver += OnPreviewDragOver;
            TerminalBox.PreviewDrop += OnPreviewDrop;

            TerminalBox.PreviewMouseDown += (s, e) =>
            {
                if (PaneFocused != null) PaneFocused(this);
            };

            TerminalBox.PreviewMouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (!TerminalBox.Selection.IsEmpty && !string.IsNullOrEmpty(TerminalBox.Selection.Text))
                {
                    try
                    {
                        Clipboard.SetText(TerminalBox.Selection.Text);
                        TerminalBox.Selection.Select(TerminalBox.Selection.End, TerminalBox.Selection.End);
                        if (StatusMessage != null) StatusMessage("คัดลอกข้อความไปยังคลิปบอร์ดแล้ว (Copied)");
                    }
                    catch { }
                }
                else
                {
                    PasteFromClipboard();
                }
            };

            TerminalBox.PreviewTextInput += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    return;
                if (!string.IsNullOrEmpty(e.Text))
                {
                    e.Handled = true;
                    InputBox.Focus();
                    int caret = InputBox.CaretIndex;
                    InputBox.Text = InputBox.Text.Insert(caret, e.Text);
                    InputBox.CaretIndex = caret + e.Text.Length;
                }
            };

            TerminalBox.PreviewKeyDown += TerminalBox_PreviewKeyDown;

            SetRow(TerminalBox, 0);
            Children.Add(TerminalBox);

            // Row 1: Smart Activity & Interactive (Y/N)? Banner
            ActivityBannerBorder = BuildActivityBanner();
            SetRow(ActivityBannerBorder, 1);
            Children.Add(ActivityBannerBorder);

            // Row 2: Bottom Thai-Friendly Smart Command Input Bar
            InputBarBorder = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(10, 6, 10, 6),
                Visibility = _settings.ShowCommandInputBar ? Visibility.Visible : Visibility.Collapsed
            };

            var inputGrid = new Grid();
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            PromptLabel = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0),
                Text = shell == ShellType.PowerShell ? "PS>" : ">_"
            };
            SetColumn(PromptLabel, 0);
            inputGrid.Children.Add(PromptLabel);

            InputBox = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(2, 2, 2, 2),
                AcceptsReturn = false,
                AcceptsTab = false,
                AllowDrop = true,
                ToolTip = "พิมพ์คำสั่งภาษาไทย/อังกฤษ หรือลากไฟล์จากโฟลเดอร์มาวางที่นี่ได้ทันที"
            };
            TextOptions.SetTextFormattingMode(InputBox, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(InputBox, TextRenderingMode.ClearType);

            InputBox.PreviewDragOver += OnPreviewDragOver;
            InputBox.PreviewDrop += OnPreviewDrop;

            InputBox.GotFocus += (s, e) =>
            {
                if (PaneFocused != null) PaneFocused(this);
            };

            InputBox.TextChanged += (s, e) =>
            {
                if (_syncingInput) return;
                UpdateLiveInlineInput(InputBox.Text);
                UpdateTypingHintBanner(InputBox.Text);
            };

            InputBox.PreviewKeyDown += InputBox_PreviewKeyDown;
            SetColumn(InputBox, 1);
            inputGrid.Children.Add(InputBox);

            var hintPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var sendBtn = CreateMiniButton("รัน (Enter)", "ส่งคำสั่ง (Execute Command)", () => SubmitCurrentCommand());
            var clearBtn = CreateMiniButton("ล้างจอ", "ล้างหน้าจอ (Ctrl+L)", () => ClearScreenAndShowPrompt());
            var ctrlCBtn = CreateMiniButton("หยุด (Ctrl+C)", "หยุดคำสั่งที่กำลังทำงาน (Ctrl+C)", () => PerformInterrupt());
            hintPanel.Children.Add(sendBtn);
            hintPanel.Children.Add(clearBtn);
            hintPanel.Children.Add(ctrlCBtn);
            SetColumn(hintPanel, 2);
            inputGrid.Children.Add(hintPanel);

            InputBarBorder.Child = inputGrid;
            SetRow(InputBarBorder, 2);
            Children.Add(InputBarBorder);

            _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _cursorTimer.Tick += (s, e) =>
            {
                _cursorVisible = !_cursorVisible;
                if (_cursorRun != null)
                {
                    _cursorRun.Foreground = _cursorVisible
                        ? TerminalTheme.BrushFromHex(_theme.CursorColor)
                        : Brushes.Transparent;
                }
            };
            _cursorTimer.Start();

            _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _activityTimer.Tick += (s, e) =>
            {
                if (Session != null && Session.IsBusy)
                {
                    _spinnerTickCount++;
                    _spinnerIndex = (_spinnerIndex + 1) % SpinnerFrames.Length;
                    if (_currentPromptKind == InteractivePromptKind.None)
                    {
                        _activityIconBlock.Text = SpinnerFrames[_spinnerIndex];
                    }
                    if (_spinnerTickCount % 5 == 0)
                    {
                        _cachedLiveDownloadText = CommandDescriber.TryGetLiveDownloadProgress(
                            Session.CurrentCommand,
                            Session.CurrentDirectory,
                            Session.CommandStartTime);
                    }
                    double secs = (DateTime.Now - Session.CommandStartTime).TotalSeconds;
                    if (!string.IsNullOrEmpty(_cachedLiveDownloadText))
                        _activityElapsedBlock.Text = string.Format("{0}  |  ⏱ {1:0.0} วินาที", _cachedLiveDownloadText, secs);
                    else
                        _activityElapsedBlock.Text = string.Format("⏱ {0:0.0} วินาที", secs);
                }
            };

            ApplyThemeAndSettings(_theme, _settings);

            Session = new TerminalSession(shell, startDir, codePage);
            Session.CommandStarted += OnSessionCommandStarted;
            Session.OutputReceived += OnSessionOutput;
            Session.PromptReady += OnSessionPromptReady;
            Session.ClearRequested += () => Dispatcher.BeginInvoke(new Action(ClearDocumentOnly));
            Session.SessionExited += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (CloseRequested != null) CloseRequested();
            }));

            PrintWelcomeBanner();
            Session.Start();
            AppendPrompt(Session.GetPromptString());
        }

        private void OnPreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void OnPreviewDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Handled = true;
                    string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        // If Shift is held and a single directory is dropped, cd into it immediately
                        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && files.Length == 1 && Directory.Exists(files[0]))
                        {
                            string cdCmd = Session.Shell == ShellType.CommandPrompt
                                ? ("cd /d \"" + files[0] + "\"")
                                : ("Set-Location -LiteralPath \"" + files[0] + "\"");
                            RunCommandDirectly(cdCmd);
                            if (StatusMessage != null) StatusMessage("ย้ายไปยังโฟลเดอร์ที่ลากมาวางแล้ว");
                            return;
                        }

                        var sb = new StringBuilder();
                        for (int i = 0; i < files.Length; i++)
                        {
                            if (i > 0) sb.Append(" ");
                            string p = files[i];
                            if (p.Contains(" ") || p.Contains("&") || p.Contains("(") || p.Contains(")"))
                                sb.Append("\"").Append(p).Append("\"");
                            else
                                sb.Append(p);
                        }

                        string insertText = sb.ToString();
                        int caret = InputBox.CaretIndex;
                        if (caret > 0 && caret <= InputBox.Text.Length && InputBox.Text[caret - 1] != ' ')
                            insertText = " " + insertText;

                        InputBox.Text = InputBox.Text.Insert(caret, insertText);
                        InputBox.CaretIndex = caret + insertText.Length;
                        InputBox.Focus();
                        if (StatusMessage != null) StatusMessage("วางพาธไฟล์แล้ว (กด Shift+ลากโฟลเดอร์ เพื่อ cd เข้าโฟลเดอร์ทันที)");
                    }
                }
            }
            catch
            {
            }
        }

        private Button CreateMiniButton(string text, string tooltip, Action onClick)
        {
            var btn = new Button
            {
                Content = text,
                ToolTip = tooltip,
                Margin = new Thickness(5, 0, 0, 0),
                Padding = new Thickness(10, 3, 10, 3),
                FontSize = 11.5,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromArgb(38, 0, 120, 212)),
                Foreground = TerminalTheme.BrushFromHex(_theme.Foreground),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 0, 120, 212))
            };
            btn.MouseEnter += (s, e) => btn.Background = new SolidColorBrush(Color.FromArgb(95, 0, 120, 212));
            btn.MouseLeave += (s, e) => btn.Background = new SolidColorBrush(Color.FromArgb(38, 0, 120, 212));
            btn.Click += (s, e) =>
            {
                onClick();
                FocusInput();
            };
            return btn;
        }

        private Paragraph CreateNewParagraph()
        {
            return new Paragraph
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                LineHeight = Math.Round(_settings.FontSize * 1.48),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
        }

        public void ApplyThemeAndSettings(TerminalTheme theme, TerminalSettings settings)
        {
            _theme = theme;
            _settings = settings;
            _ansiParser.SetTheme(theme);

            var fontFamily = new FontFamily(settings.FontFamily);
            double fontSize = settings.FontSize;
            double lineHeight = Math.Round(fontSize * 1.48);

            TerminalBox.Background = TerminalTheme.BrushFromHex(theme.Background);
            TerminalBox.Foreground = TerminalTheme.BrushFromHex(theme.Foreground);
            TerminalBox.FontFamily = fontFamily;
            TerminalBox.FontSize = fontSize;

            if (TerminalBox.Document != null)
            {
                TerminalBox.Document.FontFamily = fontFamily;
                TerminalBox.Document.FontSize = fontSize;
                TerminalBox.Document.LineHeight = lineHeight;
                foreach (Block b in TerminalBox.Document.Blocks)
                {
                    var p = b as Paragraph;
                    if (p != null) p.LineHeight = lineHeight;
                }
            }

            InputBarBorder.Background = TerminalTheme.BrushFromHex(theme.TabBarBackground);
            InputBarBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128));
            InputBarBorder.Visibility = (_activeTuiSurface == null && settings.ShowCommandInputBar)
                ? Visibility.Visible
                : Visibility.Collapsed;

            PromptLabel.Foreground = TerminalTheme.BrushFromHex(theme.PromptColor);
            PromptLabel.FontFamily = fontFamily;
            PromptLabel.FontSize = fontSize;

            InputBox.Foreground = TerminalTheme.BrushFromHex(theme.CommandColor);
            InputBox.CaretBrush = TerminalTheme.BrushFromHex(theme.CursorColor);
            InputBox.FontFamily = fontFamily;
            InputBox.FontSize = fontSize;

            if (_activeTuiSurface != null)
            {
                _activeTuiSurface.ApplyTheme(theme, settings);
            }
        }

        private Border BuildActivityBanner()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(16, 36, 58)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(12, 6, 12, 6),
                Visibility = Visibility.Collapsed
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _activityIconBlock = new TextBlock
            {
                Text = "💡",
                Foreground = new SolidColorBrush(Color.FromRgb(80, 220, 255)),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            SetColumn(_activityIconBlock, 0);
            grid.Children.Add(_activityIconBlock);

            _activityTitleBlock = new TextBlock
            {
                Text = "",
                Foreground = Brushes.White,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            SetColumn(_activityTitleBlock, 1);
            grid.Children.Add(_activityTitleBlock);

            // Interactive (Y/N)? Quick Answer Buttons
            _ynButtonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(10, 0, 8, 0)
            };

            _btnYes = CreateInteractiveChoiceButton("✓ ใช่ / อนุญาต (Y)", Color.FromRgb(22, 163, 74), () =>
                SendInteractiveAnswer(_currentPromptKind == InteractivePromptKind.FullWordYesNo ? "yes" : "Y"));
            _btnNo = CreateInteractiveChoiceButton("✕ ไม่ / ปฏิเสธ (N)", Color.FromRgb(220, 38, 38), () =>
                SendInteractiveAnswer(_currentPromptKind == InteractivePromptKind.FullWordYesNo ? "no" : "N"));
            _btnAll = CreateInteractiveChoiceButton("∀ ใช่ทั้งหมด (A)", Color.FromRgb(37, 99, 235), () => SendInteractiveAnswer("A"));
            _btnContinue = CreateInteractiveChoiceButton("⏎ กดเพื่อทำต่อ (Enter)", Color.FromRgb(37, 99, 235), () =>
                SendInteractiveAnswer(_currentPromptKind == InteractivePromptKind.PythonRepl ? "exit()" : ""));
            _btnElevate = CreateInteractiveChoiceButton("🛡 ขออนุญาต Windows (Yes/No) รันด้วยสิทธิ์ Admin", Color.FromRgb(217, 119, 6), () =>
            {
                string retryCmd = _pendingElevationRetryCommand;
                _pendingElevationRetryCommand = null;
                _currentPromptKind = InteractivePromptKind.None;
                ActivityBannerBorder.Visibility = Visibility.Collapsed;
                SystemIntegration.LaunchAsAdministrator(Session.CurrentDirectory, retryCmd);
            });
            _btnDismissElevate = CreateInteractiveChoiceButton("✕ ปิด", Color.FromRgb(75, 85, 99), () =>
            {
                _pendingElevationRetryCommand = null;
                _currentPromptKind = InteractivePromptKind.None;
                ActivityBannerBorder.Visibility = Visibility.Collapsed;
            });
            _btnExitTui = CreateInteractiveChoiceButton("🚪 ออกจากโหมด TUI", Color.FromRgb(220, 38, 38), () =>
            {
                RequestGracefulExitActiveTuiSession();
            });

            _ynButtonsPanel.Children.Add(_btnYes);
            _ynButtonsPanel.Children.Add(_btnNo);
            _ynButtonsPanel.Children.Add(_btnAll);
            _ynButtonsPanel.Children.Add(_btnContinue);
            _ynButtonsPanel.Children.Add(_btnElevate);
            _ynButtonsPanel.Children.Add(_btnDismissElevate);
            _ynButtonsPanel.Children.Add(_btnExitTui);
            SetColumn(_ynButtonsPanel, 2);
            grid.Children.Add(_ynButtonsPanel);

            _activityElapsedBlock = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(180, 210, 245)),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            SetColumn(_activityElapsedBlock, 3);
            grid.Children.Add(_activityElapsedBlock);

            border.Child = grid;
            return border;
        }

        private Button CreateInteractiveChoiceButton(string label, Color bgColor, Action onClick)
        {
            var btn = new Button
            {
                Content = label,
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(12, 3, 12, 3),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(bgColor),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) =>
            {
                onClick();
                FocusInput();
            };
            return btn;
        }

        private void UpdateTypingHintBanner(string currentInput)
        {
            if (_activeTuiSurface != null)
                return;
            if (Session != null && Session.IsBusy)
                return;

            if (string.IsNullOrWhiteSpace(currentInput))
            {
                if (!string.IsNullOrEmpty(_pendingElevationRetryCommand))
                {
                    ShowElevationRequiredBanner(_pendingElevationRetryCommand);
                    return;
                }
                ActivityBannerBorder.Visibility = Visibility.Collapsed;
                return;
            }

            _pendingElevationRetryCommand = null;
            _currentPromptKind = InteractivePromptKind.None;

            string desc = CommandDescriber.DescribeCommand(currentInput, false);
            if (string.IsNullOrEmpty(desc))
            {
                ActivityBannerBorder.Visibility = Visibility.Collapsed;
                return;
            }

            ActivityBannerBorder.Background = new SolidColorBrush(Color.FromRgb(18, 30, 48));
            ActivityBannerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(45, 85, 140));
            _activityIconBlock.Text = "💡";
            _activityIconBlock.Foreground = new SolidColorBrush(Color.FromRgb(100, 210, 255));
            _activityTitleBlock.Text = desc;
            _activityTitleBlock.Foreground = new SolidColorBrush(Color.FromRgb(210, 225, 245));
            _ynButtonsPanel.Visibility = Visibility.Collapsed;
            _activityElapsedBlock.Text = "กด Enter เพื่อรันคำสั่ง";
            ActivityBannerBorder.Visibility = Visibility.Visible;
        }

        private void OnSessionCommandStarted(string cmd)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _lastExecutedCommand = cmd ?? "";
                _pendingElevationRetryCommand = null;
                _cachedLiveDownloadText = null;
                _spinnerTickCount = 0;
                _currentPromptKind = InteractivePromptKind.None;
                _spinnerIndex = 0;
                string desc = CommandDescriber.DescribeCommand(cmd, true);

                ActivityBannerBorder.Background = new SolidColorBrush(Color.FromRgb(14, 42, 68));
                ActivityBannerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 140, 235));
                _activityIconBlock.Text = SpinnerFrames[0];
                _activityIconBlock.Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123));
                _activityTitleBlock.Text = desc;
                _activityTitleBlock.Foreground = Brushes.White;
                _ynButtonsPanel.Visibility = Visibility.Collapsed;
                _activityElapsedBlock.Text = "⏱ 0.0 วินาที";
                ActivityBannerBorder.Visibility = Visibility.Visible;

                _activityTimer.Start();

                if (TitleChanged != null)
                {
                    string shortCmd = cmd.Length > 18 ? cmd.Substring(0, 18) + "..." : cmd;
                    TitleChanged("⏳ กำลังรัน: " + shortCmd);
                }
            }));
        }

        private void ShowElevationRequiredBanner(string failedCmd)
        {
            _currentPromptKind = InteractivePromptKind.ElevationRequired;
            ActivityBannerBorder.Background = new SolidColorBrush(Color.FromRgb(64, 28, 12));
            ActivityBannerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            _activityIconBlock.Text = "🛡";
            _activityIconBlock.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));

            string shortCmd = !string.IsNullOrEmpty(failedCmd) && failedCmd.Length > 45
                ? failedCmd.Substring(0, 45) + "..."
                : failedCmd;
            _activityTitleBlock.Text = "คำสั่งนี้ต้องใช้สิทธิ์ผู้ดูแลระบบ (Administrator): กดปุ่มด้านขวาเพื่อเปิดหน้าต่างขออนุญาต Windows (Yes/No) แล้วรันคำสั่ง '" + shortCmd + "' อัตโนมัติ";
            _activityTitleBlock.Foreground = new SolidColorBrush(Color.FromRgb(254, 243, 199));

            _btnYes.Visibility = Visibility.Collapsed;
            _btnNo.Visibility = Visibility.Collapsed;
            _btnAll.Visibility = Visibility.Collapsed;
            _btnContinue.Visibility = Visibility.Collapsed;
            _btnElevate.Visibility = Visibility.Visible;
            _btnDismissElevate.Visibility = Visibility.Visible;
            _btnExitTui.Visibility = Visibility.Collapsed;
            _activityElapsedBlock.Text = "";

            _ynButtonsPanel.Visibility = Visibility.Visible;
            ActivityBannerBorder.Visibility = Visibility.Visible;
        }

        private void ShowInteractivePromptBanner(InteractivePromptKind kind, string promptQuestion)
        {
            _currentPromptKind = kind;
            ActivityBannerBorder.Background = new SolidColorBrush(Color.FromRgb(58, 40, 8));
            ActivityBannerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            _activityIconBlock.Text = "❓";
            _activityIconBlock.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            _btnElevate.Visibility = Visibility.Collapsed;
            _btnDismissElevate.Visibility = Visibility.Collapsed;
            _btnExitTui.Visibility = Visibility.Collapsed;

            if (kind == InteractivePromptKind.PythonRepl)
            {
                _activityIconBlock.Text = "🐍";
                _activityTitleBlock.Text = "โหมดโต้ตอบ Python (>>>): พิมพ์โค้ด Python แล้วกด Enter หรือกดปุ่มด้านขวาเพื่อออกจาก Python";
                _btnYes.Visibility = Visibility.Collapsed;
                _btnNo.Visibility = Visibility.Collapsed;
                _btnAll.Visibility = Visibility.Collapsed;
                _btnContinue.Content = "🚪 ออกจาก Python (exit())";
                _btnContinue.Visibility = Visibility.Visible;
            }
            else if (kind == InteractivePromptKind.PressKeyToContinue)
            {
                _activityTitleBlock.Text = "โปรแกรมหยุดรอ: กดปุ่ม 'กดเพื่อทำต่อ (Enter)' ด้านขวา หรือกดปุ่ม Enter บนคีย์บอร์ด";
                _btnYes.Visibility = Visibility.Collapsed;
                _btnNo.Visibility = Visibility.Collapsed;
                _btnAll.Visibility = Visibility.Collapsed;
                _btnContinue.Content = "⏎ กดเพื่อทำต่อ (Enter)";
                _btnContinue.Visibility = Visibility.Visible;
            }
            else if (kind == InteractivePromptKind.FullWordYesNo)
            {
                _btnYes.Content = "✓ ใช่ / อนุญาต (yes)";
                _btnNo.Content = "✕ ไม่ / ปฏิเสธ (no)";
                _activityTitleBlock.Text = "รอการยืนยัน (yes/no): " + promptQuestion + " — คลิกปุ่มด้านขวาเพื่อตอบได้ทันที";
                _btnYes.Visibility = Visibility.Visible;
                _btnNo.Visibility = Visibility.Visible;
                _btnAll.Visibility = Visibility.Collapsed;
                _btnContinue.Visibility = Visibility.Collapsed;
            }
            else
            {
                _btnYes.Content = "✓ ใช่ / อนุญาต (Y)";
                _btnNo.Content = "✕ ไม่ / ปฏิเสธ (N)";
                _activityTitleBlock.Text = "รอการยืนยัน (Y/N): " + promptQuestion + " — คลิกปุ่มด้านขวา หรือพิมพ์ Y / N แล้วกด Enter";
                _btnYes.Visibility = Visibility.Visible;
                _btnNo.Visibility = Visibility.Visible;
                _btnAll.Visibility = (kind == InteractivePromptKind.YesNoAll) ? Visibility.Visible : Visibility.Collapsed;
                _btnContinue.Visibility = Visibility.Collapsed;
            }

            _activityTitleBlock.Foreground = new SolidColorBrush(Color.FromRgb(254, 243, 199));
            _ynButtonsPanel.Visibility = Visibility.Visible;
            ActivityBannerBorder.Visibility = Visibility.Visible;
        }

        public void SendInteractiveAnswer(string answer)
        {
            if (Session == null) return;
            _currentPromptKind = InteractivePromptKind.None;
            _ynButtonsPanel.Visibility = Visibility.Collapsed;

            _syncingInput = true;
            InputBox.Text = string.Empty;
            _syncingInput = false;

            RemoveLiveInlineRuns();
            var ansRun = new Run((string.IsNullOrEmpty(answer) ? "" : answer) + "\r\n")
            {
                Foreground = (string.Equals(answer, "N", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(answer, "no", StringComparison.OrdinalIgnoreCase))
                    ? TerminalTheme.BrushFromHex(_theme.ErrorColor)
                    : TerminalTheme.BrushFromHex(_theme.PromptColor),
                FontWeight = FontWeights.Bold
            };
            _currentParagraph.Inlines.Add(ansRun);
            AttachLiveInlineRuns();
            TerminalBox.ScrollToEnd();

            // Restore running banner state while command finishes
            if (Session.IsBusy)
            {
                ActivityBannerBorder.Background = new SolidColorBrush(Color.FromRgb(14, 42, 68));
                ActivityBannerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 140, 235));
                _activityTitleBlock.Text = CommandDescriber.DescribeCommand(Session.CurrentCommand, true);
                _activityTitleBlock.Foreground = Brushes.White;
            }

            Session.SendRawInput((answer ?? "") + "\r\n");
        }

        private void PrintWelcomeBanner()
        {
            bool isAdmin = SystemIntegration.IsRunningAsAdministrator();
            string creditText;
            bool creditOk = SystemIntegration.VerifyAuthorCreditIntegrity(out creditText);

            if (Session.Shell == ShellType.CommandPrompt)
            {
                AppendStyledText("Microsoft Windows [Version 10.0.17763 - Windows 10 Enterprise LTSC 2019]\r\n", TerminalTheme.ParseColor(_theme.Foreground), false);
                AppendStyledText("★ Windows Terminal LTSC — รองรับภาษาไทยเต็มรูปแบบ (UTF-8 / DirectWrite Thai Rendering)" +
                    (isAdmin ? " [โหมดผู้ดูแลระบบ Administrator]" : "") + "\r\n",
                    TerminalTheme.ParseColor(_theme.AccentColor), true);
            }
            else
            {
                AppendStyledText("Windows PowerShell (Windows 10 LTSC 2019)\r\n", TerminalTheme.ParseColor(_theme.Foreground), false);
                AppendStyledText("★ Windows Terminal LTSC — รองรับภาษาไทยเต็มรูปแบบ (UTF-8 / DirectWrite Thai Rendering)" +
                    (isAdmin ? " [โหมดผู้ดูแลระบบ Administrator]" : "") + "\r\n",
                    TerminalTheme.ParseColor(_theme.AccentColor), true);
            }

            if (creditOk)
            {
                AppendStyledText("  " + creditText + "\r\n\r\n", TerminalTheme.ParseColor(_theme.PromptColor), false);
            }
            else
            {
                AppendStyledText("⚠ คำเตือน: ตรวจพบการดัดแปลงเครดิตผู้พัฒนาโดยไม่ได้รับอนุญาต! ต้นฉบับพัฒนาโดย Rachata (แจกจ่ายฟรี ห้ามจำหน่าย)\r\n\r\n",
                    TerminalTheme.ParseColor(_theme.ErrorColor), true);
            }
        }

        private void OnSessionOutput(string text, bool isError)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RemoveLiveInlineRuns();

                if (isError)
                {
                    AppendStyledText(text, TerminalTheme.ParseColor(_theme.ErrorColor), false);
                }
                else
                {
                    var segments = _ansiParser.Parse(text);
                    foreach (var seg in segments)
                    {
                        if (seg.ClearScreen)
                        {
                            ClearDocumentOnly();
                            continue;
                        }
                        if (!string.IsNullOrEmpty(seg.WindowTitle))
                        {
                            if (TitleChanged != null) TitleChanged(seg.WindowTitle);
                            continue;
                        }
                        if (!string.IsNullOrEmpty(seg.Text))
                        {
                            AppendSegment(seg);
                        }
                    }
                }

                // Check if the command failed due to missing Administrator privileges (UAC Yes/No needed)
                if (!SystemIntegration.IsRunningAsAdministrator() &&
                    !string.IsNullOrWhiteSpace(_lastExecutedCommand) &&
                    CommandDescriber.DetectElevationError(text))
                {
                    _pendingElevationRetryCommand = _lastExecutedCommand;
                    ShowElevationRequiredBanner(_pendingElevationRetryCommand);
                }
                else if (Session.IsBusy)
                {
                    // Check if the running command is prompting for (Y/N)?, (yes/no), or Press any key to continue
                    string promptQuestion;
                    InteractivePromptKind kind = CommandDescriber.DetectInteractivePrompt(text, out promptQuestion);
                    if (kind != InteractivePromptKind.None)
                    {
                        ShowInteractivePromptBanner(kind, promptQuestion);
                    }
                }

                TrimScrollbackBuffer();
                AttachLiveInlineRuns();
                TerminalBox.ScrollToEnd();
            }));
        }

        private void TrimScrollbackBuffer()
        {
            if (_currentParagraph.Inlines.Count <= MaxScrollbackInlines + 200)
                return;

            while (_currentParagraph.Inlines.Count > MaxScrollbackInlines)
            {
                _currentParagraph.Inlines.Remove(_currentParagraph.Inlines.FirstInline);
            }
        }

        private void OnSessionPromptReady(string prompt, string currentDir)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _activityTimer.Stop();
                if (!string.IsNullOrEmpty(_pendingElevationRetryCommand))
                {
                    ShowElevationRequiredBanner(_pendingElevationRetryCommand);
                }
                else
                {
                    _currentPromptKind = InteractivePromptKind.None;
                    _ynButtonsPanel.Visibility = Visibility.Collapsed;
                    UpdateTypingHintBanner(InputBox.Text);
                }

                RemoveLiveInlineRuns();
                AppendPrompt(prompt);
                if (DirectoryChanged != null) DirectoryChanged(currentDir);

                string folderName = Path.GetFileName(currentDir.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(folderName)) folderName = currentDir;
                string prefix = Session.Shell == ShellType.PowerShell ? "PS: " : "cmd: ";
                if (TitleChanged != null) TitleChanged(prefix + folderName);

                TerminalBox.ScrollToEnd();
            }));
        }

        private void AppendPrompt(string promptText)
        {
            RemoveLiveInlineRuns();

            if (_currentParagraph.Inlines.Count > 0)
            {
                _currentParagraph.Inlines.Add(new LineBreak());
            }

            var promptRun = new Run(promptText)
            {
                Foreground = TerminalTheme.BrushFromHex(_theme.PromptColor),
                FontWeight = FontWeights.SemiBold
            };
            _currentParagraph.Inlines.Add(promptRun);

            PromptLabel.Text = Session.Shell == ShellType.PowerShell ? "PS>" : ">_";
            AttachLiveInlineRuns();
            TerminalBox.ScrollToEnd();
        }

        private void AttachLiveInlineRuns()
        {
            RemoveLiveInlineRuns();
            _liveInputRun = new Run(InputBox != null ? InputBox.Text : "")
            {
                Foreground = TerminalTheme.BrushFromHex(_theme.CommandColor),
                FontWeight = FontWeights.SemiBold
            };
            _cursorRun = new Run("▌")
            {
                Foreground = TerminalTheme.BrushFromHex(_theme.CursorColor)
            };
            _currentParagraph.Inlines.Add(_liveInputRun);
            _currentParagraph.Inlines.Add(_cursorRun);
        }

        private void RemoveLiveInlineRuns()
        {
            if (_liveInputRun != null && _currentParagraph.Inlines.Contains(_liveInputRun))
                _currentParagraph.Inlines.Remove(_liveInputRun);
            if (_cursorRun != null && _currentParagraph.Inlines.Contains(_cursorRun))
                _currentParagraph.Inlines.Remove(_cursorRun);
        }

        private void UpdateLiveInlineInput(string text)
        {
            if (_liveInputRun == null || !_currentParagraph.Inlines.Contains(_liveInputRun))
            {
                AttachLiveInlineRuns();
            }
            _liveInputRun.Text = text ?? "";
            if (_cursorRun != null)
            {
                if (!string.IsNullOrEmpty(text) && IsThaiCombiningMark(text[text.Length - 1]))
                {
                    _cursorRun.Text = " ▌";
                }
                else
                {
                    _cursorRun.Text = "▌";
                }
            }
            TerminalBox.ScrollToEnd();
        }

        private static bool IsThaiCombiningMark(char ch)
        {
            return ch == '\u0E31' ||
                   (ch >= '\u0E34' && ch <= '\u0E3A') ||
                   (ch >= '\u0E47' && ch <= '\u0E4E');
        }

        private void RemoveCurrentLineForCarriageReturn()
        {
            while (_currentParagraph.Inlines.LastInline != null && !(_currentParagraph.Inlines.LastInline is LineBreak))
            {
                _currentParagraph.Inlines.Remove(_currentParagraph.Inlines.LastInline);
            }
        }

        private void AppendSegment(AnsiSegment seg)
        {
            Color fg = seg.Foreground ?? TerminalTheme.ParseColor(_theme.Foreground);

            // Preserve standalone '\r' for carriage-return line overwrites (progress bars), while normalizing "\r\n" -> "\n"
            string normalized = seg.Text.Replace("\r\n", "\n");
            string[] lines = normalized.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Contains("\r"))
                {
                    string[] crParts = line.Split('\r');
                    for (int c = 0; c < crParts.Length; c++)
                    {
                        if (c > 0)
                        {
                            RemoveCurrentLineForCarriageReturn();
                        }
                        if (crParts[c].Length > 0)
                        {
                            AppendTextWithClickableUrls(crParts[c], fg, seg);
                        }
                    }
                }
                else if (line.Length > 0)
                {
                    AppendTextWithClickableUrls(line, fg, seg);
                }

                if (i < lines.Length - 1)
                {
                    _currentParagraph.Inlines.Add(new LineBreak());
                }
            }
        }

        private void AppendTextWithClickableUrls(string text, Color fg, AnsiSegment seg)
        {
            MatchCollection matches = UrlRegex.Matches(text);
            if (matches.Count == 0)
            {
                _currentParagraph.Inlines.Add(CreateStyledRun(text, fg, seg));
                return;
            }

            int lastIdx = 0;
            foreach (Match m in matches)
            {
                if (m.Index > lastIdx)
                {
                    string before = text.Substring(lastIdx, m.Index - lastIdx);
                    _currentParagraph.Inlines.Add(CreateStyledRun(before, fg, seg));
                }

                string rawMatch = m.Value;
                string url = rawMatch.TrimEnd('.', ',', ';', ')', ']', '}', '\'', '"');
                string trailingPunct = rawMatch.Substring(url.Length);

                Uri validatedUri;
                bool validHttpUrl = Uri.TryCreate(url, UriKind.Absolute, out validatedUri) &&
                    (validatedUri.Scheme == Uri.UriSchemeHttp || validatedUri.Scheme == Uri.UriSchemeHttps);

                if (validHttpUrl)
                {
                    var linkRun = new Run(url)
                    {
                        Foreground = TerminalTheme.BrushFromHex(_theme.AccentColor),
                        TextDecorations = TextDecorations.Underline,
                        Cursor = Cursors.Hand,
                        ToolTip = "คลิกเพื่อเปิดลิงก์: " + url
                    };
                    string safeUri = validatedUri.AbsoluteUri;
                    linkRun.MouseLeftButtonDown += (s, e) =>
                    {
                        e.Handled = true;
                        try { Process.Start(new ProcessStartInfo(safeUri) { UseShellExecute = true }); } catch { }
                    };
                    _currentParagraph.Inlines.Add(linkRun);
                }
                else
                {
                    _currentParagraph.Inlines.Add(CreateStyledRun(url, fg, seg));
                }

                if (trailingPunct.Length > 0)
                {
                    _currentParagraph.Inlines.Add(CreateStyledRun(trailingPunct, fg, seg));
                }

                lastIdx = m.Index + m.Length;
            }

            if (lastIdx < text.Length)
            {
                _currentParagraph.Inlines.Add(CreateStyledRun(text.Substring(lastIdx), fg, seg));
            }
        }

        private Run CreateStyledRun(string text, Color fg, AnsiSegment seg)
        {
            var run = new Run(text)
            {
                Foreground = TerminalTheme.GetCachedBrush(fg)
            };
            if (seg.Background.HasValue)
                run.Background = TerminalTheme.GetCachedBrush(seg.Background.Value);
            if (seg.Bold)
                run.FontWeight = FontWeights.Bold;
            if (seg.Italic)
                run.FontStyle = FontStyles.Italic;
            if (seg.Underline)
                run.TextDecorations = TextDecorations.Underline;
            return run;
        }

        public void AppendStyledText(string text, Color color, bool bold)
        {
            AppendSegment(new AnsiSegment
            {
                Text = text,
                Foreground = color,
                Bold = bold
            });
        }

        public void ClearDocumentOnly()
        {
            TerminalBox.Document.Blocks.Clear();
            _currentParagraph = CreateNewParagraph();
            TerminalBox.Document.Blocks.Add(_currentParagraph);
        }

        public void ClearScreenAndShowPrompt()
        {
            ClearDocumentOnly();
            AppendPrompt(Session.GetPromptString());
        }

        public string GetAllTerminalText()
        {
            var range = new TextRange(TerminalBox.Document.ContentStart, TerminalBox.Document.ContentEnd);
            return range.Text;
        }

        // Finds, selects, and scrolls to the next match in the RichTextBox document
        public int FindAndHighlightNext(string query, out int currentMatchNum)
        {
            currentMatchNum = 0;
            if (string.IsNullOrEmpty(query)) return 0;

            if (!string.Equals(_lastSearchQuery, query, StringComparison.OrdinalIgnoreCase))
            {
                _lastSearchQuery = query;
                _lastSearchMatchIndex = -1;
            }

            var matchRanges = new List<TextRange>();
            TextPointer navigator = TerminalBox.Document.ContentStart;
            while (navigator != null && navigator.CompareTo(TerminalBox.Document.ContentEnd) < 0)
            {
                if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    string textRun = navigator.GetTextInRun(LogicalDirection.Forward);
                    int idx = 0;
                    while ((idx = textRun.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        TextPointer start = navigator.GetPositionAtOffset(idx);
                        TextPointer end = navigator.GetPositionAtOffset(idx + query.Length);
                        if (start != null && end != null)
                        {
                            matchRanges.Add(new TextRange(start, end));
                        }
                        idx += query.Length;
                    }
                }
                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
            }

            if (matchRanges.Count == 0)
                return 0;

            _lastSearchMatchIndex = (_lastSearchMatchIndex + 1) % matchRanges.Count;
            currentMatchNum = _lastSearchMatchIndex + 1;

            TextRange target = matchRanges[_lastSearchMatchIndex];
            TerminalBox.Selection.Select(target.Start, target.End);

            Rect rect = target.Start.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty)
            {
                TerminalBox.ScrollToVerticalOffset(TerminalBox.VerticalOffset + rect.Top - (TerminalBox.ActualHeight / 2));
            }
            TerminalBox.Focus();

            return matchRanges.Count;
        }

        private void SubmitCurrentCommand()
        {
            if (_activeTuiSurface != null)
            {
                string tuiInput = InputBox.Text;
                _syncingInput = true;
                InputBox.Text = string.Empty;
                _syncingInput = false;

                if (!string.IsNullOrEmpty(tuiInput))
                    _activeTuiSurface.SendText(tuiInput + "\r");
                else
                    _activeTuiSurface.SendKeyEvent(0x0D, '\r', 0);

                _activeTuiSurface.FocusSurface();
                return;
            }

            if (_currentPromptKind != InteractivePromptKind.None &&
                _currentPromptKind != InteractivePromptKind.ElevationRequired)
            {
                SendInteractiveAnswer(InputBox.Text);
                return;
            }

            _pendingElevationRetryCommand = null;
            _currentPromptKind = InteractivePromptKind.None;

            string cmd = InputBox.Text;
            _syncingInput = true;
            InputBox.Text = string.Empty;
            _syncingInput = false;

            RemoveLiveInlineRuns();
            var executedRun = new Run(cmd + "\r\n")
            {
                Foreground = TerminalTheme.BrushFromHex(_theme.CommandColor),
                FontWeight = FontWeights.SemiBold
            };
            _currentParagraph.Inlines.Add(executedRun);
            AttachLiveInlineRuns();
            TerminalBox.ScrollToEnd();

            if (!Session.IsBusy && TuiDetector.ShouldRunInTuiMode(cmd))
            {
                StartInteractiveTuiSession(cmd);
                return;
            }

            Session.ExecuteCommand(cmd);
        }

        private void StartInteractiveTuiSession(string cmd)
        {
            if (_activeTuiSurface != null)
            {
                StopActiveTuiSession();
            }

            string trimmed = cmd.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                if (Session.CommandHistory.Count == 0 || !string.Equals(Session.CommandHistory[Session.CommandHistory.Count - 1], cmd, StringComparison.Ordinal))
                {
                    Session.CommandHistory.Add(cmd);
                }
                Session.HistoryIndex = Session.CommandHistory.Count;
            }

            var surface = new TuiSurfaceControl(_theme, _settings);
            if (StatusMessage != null)
                surface.StatusToast += StatusMessage;

            surface.TuiExited += exitRes =>
            {
                if (_activeTuiSurface == surface)
                {
                    FinishTuiSessionUi(trimmed, exitRes);
                }
            };

            _activeTuiCommandLine = trimmed;
            _activeTuiSurface = surface;
            _cursorTimer.Stop();
            TerminalBox.Visibility = Visibility.Collapsed;
            InputBarBorder.Visibility = Visibility.Collapsed;
            SetRow(surface, 0);
            Children.Add(surface);

            // Show helpful TUI banner with 1-click Exit button
            ActivityBannerBorder.Background = new SolidColorBrush(Color.FromRgb(28, 32, 68));
            ActivityBannerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(99, 102, 241));
            _activityIconBlock.Text = "🖥";
            _activityIconBlock.Foreground = new SolidColorBrush(Color.FromRgb(165, 180, 252));
            _activityTitleBlock.Text = "โหมดหน้าจอโต้ตอบ (TUI 60FPS): " + trimmed + " — พิมพ์ไทย/อังกฤษได้ทันที (พิมพ์ /exit หรือกด Ctrl+D สองครั้งเพื่อออก)";
            _activityTitleBlock.Foreground = Brushes.White;

            _btnYes.Visibility = Visibility.Collapsed;
            _btnNo.Visibility = Visibility.Collapsed;
            _btnAll.Visibility = Visibility.Collapsed;
            _btnContinue.Visibility = Visibility.Collapsed;
            _btnElevate.Visibility = Visibility.Collapsed;
            _btnDismissElevate.Visibility = Visibility.Collapsed;
            _btnExitTui.Visibility = Visibility.Visible;
            _ynButtonsPanel.Visibility = Visibility.Visible;
            _activityElapsedBlock.Text = "";
            ActivityBannerBorder.Visibility = Visibility.Visible;

            if (TitleChanged != null)
                TitleChanged("🖥 TUI: " + (trimmed.Length > 18 ? trimmed.Substring(0, 18) + "..." : trimmed));

            // Force synchronous WPF layout pass AFTER collapsing InputBarBorder and showing ActivityBannerBorder
            // so initial cols x rows passed to --tui-host match the actual viewport 100% identically!
            UpdateLayout();
            short cols;
            short rows;
            if (surface.ActualWidth > 100 && surface.ActualHeight > 60)
            {
                surface.ComputeViewportGrid(out cols, out rows);
            }
            else
            {
                surface.ComputeViewportGridForDimensions(ActualWidth, Math.Max(140.0, ActualHeight - 36.0), out cols, out rows);
            }

            surface.StartTuiCommand(trimmed, Session.CurrentDirectory, cols, rows);
        }

        public void RequestGracefulExitActiveTuiSession()
        {
            if (_activeTuiSurface == null) return;
            _activeTuiSurface.RequestGracefulExit();
        }

        public void StopActiveTuiSession()
        {
            if (_activeTuiSurface == null) return;
            var s = _activeTuiSurface;
            string label = !string.IsNullOrEmpty(_activeTuiCommandLine) ? _activeTuiCommandLine : "TUI";
            s.StopTui();
            if (_activeTuiSurface == s)
            {
                FinishTuiSessionUi(label, null);
            }
        }

        private void FinishTuiSessionUi(string cmdName, TuiExitResult exitResult)
        {
            if (_activeTuiSurface != null)
            {
                if (Children.Contains(_activeTuiSurface))
                    Children.Remove(_activeTuiSurface);
                _activeTuiSurface = null;
            }
            _activeTuiCommandLine = "";

            TerminalBox.Visibility = Visibility.Visible;
            InputBarBorder.Visibility = _settings.ShowCommandInputBar ? Visibility.Visible : Visibility.Collapsed;
            _btnExitTui.Visibility = Visibility.Collapsed;
            _ynButtonsPanel.Visibility = Visibility.Collapsed;
            ActivityBannerBorder.Visibility = Visibility.Collapsed;
            _cursorTimer.Start();

            RemoveLiveInlineRuns();

            bool isAgy = TuiDetector.IsAgyInteractiveCommand(cmdName);
            string finalConsoleText = (exitResult != null && !string.IsNullOrWhiteSpace(exitResult.FinalConsoleText))
                ? exitResult.FinalConsoleText.Trim()
                : string.Empty;

            string printedUuidFromConsole = null;
            if (isAgy && !string.IsNullOrEmpty(finalConsoleText))
            {
                Match uuidMatch = Regex.Match(
                    finalConsoleText,
                    @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
                if (uuidMatch.Success)
                {
                    printedUuidFromConsole = uuidMatch.Groups[1].Value;
                }

                // Strip plain-text resume lines so we render a single clean clickable resume block below without duplication
                string[] rawLines = finalConsoleText.Replace("\r\n", "\n").Split('\n');
                var nonResumeLines = new List<string>();
                for (int i = 0; i < rawLines.Length; i++)
                {
                    string ln = rawLines[i];
                    if (ln.IndexOf("--conversation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ln.IndexOf("agy -c", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ln.IndexOf("To resume", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                    nonResumeLines.Add(ln);
                }
                finalConsoleText = string.Join("\r\n", nonResumeLines.ToArray()).Trim();
            }

            if (!string.IsNullOrEmpty(finalConsoleText))
            {
                var segments = _ansiParser.Parse(finalConsoleText + "\r\n");
                foreach (var seg in segments)
                {
                    if (!string.IsNullOrEmpty(seg.Text))
                    {
                        AppendSegment(seg);
                    }
                }
            }

            if (isAgy)
            {
                DateTime startUtc = (exitResult != null && exitResult.SessionStartUtc != default(DateTime))
                    ? exitResult.SessionStartUtc
                    : DateTime.UtcNow.AddMinutes(-1);

                AgyResumeInfo resumeInfo = AgySessionHelper.TryGetLatestCliResumeInfo(cmdName, startUtc);
                if (!string.IsNullOrEmpty(printedUuidFromConsole))
                {
                    if (resumeInfo == null)
                    {
                        resumeInfo = new AgyResumeInfo
                        {
                            ConversationId = printedUuidFromConsole,
                            ModifiedDuringSession = true
                        };
                    }
                    else if (!string.Equals(resumeInfo.ConversationId, printedUuidFromConsole, StringComparison.OrdinalIgnoreCase))
                    {
                        resumeInfo.ConversationId = printedUuidFromConsole;
                        resumeInfo.ModifiedDuringSession = true;
                    }
                }

                AppendAgyExitResumeSection(resumeInfo);
            }

            AppendStyledText("[ออกจากโหมดหน้าจอโต้ตอบ " + cmdName + " เรียบร้อยแล้ว]\r\n", TerminalTheme.ParseColor(_theme.PromptColor), false);
            AppendPrompt(Session.GetPromptString());

            string folderName = Path.GetFileName(Session.CurrentDirectory.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(folderName)) folderName = Session.CurrentDirectory;
            string prefix = Session.Shell == ShellType.PowerShell ? "PS: " : "cmd: ";
            if (TitleChanged != null) TitleChanged(prefix + folderName);

            InputBox.Focus();
        }

        private void AppendAgyExitResumeSection(AgyResumeInfo resumeInfo)
        {
            string header = (resumeInfo != null && resumeInfo.ModifiedDuringSession)
                ? "To resume this conversation, run:\r\n"
                : "To resume your latest Antigravity CLI conversation, run:\r\n";

            AppendStyledText(header, TerminalTheme.ParseColor(_theme.Foreground), false);

            if (resumeInfo != null && !string.IsNullOrWhiteSpace(resumeInfo.ConversationId))
            {
                string fullResumeCmd = "agy --conversation " + resumeInfo.ConversationId;
                AppendStyledText("  ", TerminalTheme.ParseColor(_theme.Foreground), false);
                AppendClickableTerminalCommandRun(fullResumeCmd);

                string note = "";
                if (!string.IsNullOrWhiteSpace(resumeInfo.Title))
                    note = "   # " + resumeInfo.Title.Trim();
                else if (!string.IsNullOrWhiteSpace(resumeInfo.Preview))
                {
                    string p = resumeInfo.Preview.Trim().Replace("\r", " ").Replace("\n", " ");
                    if (p.Length > 42) p = p.Substring(0, 42) + "...";
                    note = "   # \"" + p + "\"";
                }
                AppendStyledText(note + "\r\n", Color.FromRgb(140, 155, 175), false);
            }

            AppendStyledText("  ", TerminalTheme.ParseColor(_theme.Foreground), false);
            AppendClickableTerminalCommandRun("agy -c");
            AppendStyledText("   # เปิดแชทล่าสุดต่อทันที (คลิกซ้ายเพื่อรัน หรือคลิกขวาเพื่อคัดลอก)\r\n", Color.FromRgb(140, 155, 175), false);
        }

        private void AppendClickableTerminalCommandRun(string commandToRun)
        {
            var cmdRun = new Run(commandToRun)
            {
                Foreground = TerminalTheme.BrushFromHex(_theme.AccentColor),
                FontWeight = FontWeights.Bold,
                TextDecorations = TextDecorations.Underline,
                Cursor = Cursors.Hand,
                ToolTip = "คลิกซ้ายเพื่อรัน \"" + commandToRun + "\" ทันที | คลิกขวาเพื่อคัดลอกคำสั่ง"
            };
            string captured = commandToRun;
            cmdRun.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                RunCommandDirectly(captured);
            };
            cmdRun.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                try
                {
                    Clipboard.SetText(captured);
                    if (StatusMessage != null)
                        StatusMessage("คัดลอกคำสั่งแล้ว: " + captured);
                }
                catch { }
            };
            _currentParagraph.Inlines.Add(cmdRun);
        }

        public void RunCommandDirectly(string cmd)
        {
            _syncingInput = true;
            InputBox.Text = cmd;
            _syncingInput = false;
            SubmitCurrentCommand();
        }

        public void PasteFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string clip = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(clip))
                    {
                        string singleLine = clip.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                        int caret = InputBox.CaretIndex;
                        InputBox.Text = InputBox.Text.Insert(caret, singleLine);
                        InputBox.CaretIndex = caret + singleLine.Length;
                        InputBox.Focus();
                    }
                }
            }
            catch
            {
            }
        }

        public void PerformInterrupt()
        {
            if (_activeTuiSurface != null)
            {
                _activeTuiSurface.SendKeyEvent(0x43, '\x03', 0x0008);
                _activeTuiSurface.FocusSurface();
                return;
            }

            _pendingElevationRetryCommand = null;
            _currentPromptKind = InteractivePromptKind.None;
            _cachedLiveDownloadText = null;

            string typed = InputBox.Text;
            _syncingInput = true;
            InputBox.Text = string.Empty;
            _syncingInput = false;

            RemoveLiveInlineRuns();
            if (!string.IsNullOrEmpty(typed))
            {
                var typedRun = new Run(typed)
                {
                    Foreground = TerminalTheme.BrushFromHex(_theme.CommandColor),
                    FontWeight = FontWeights.SemiBold
                };
                _currentParagraph.Inlines.Add(typedRun);
            }
            AttachLiveInlineRuns();

            Session.Interrupt();
        }

        private void TerminalBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                if (!TerminalBox.Selection.IsEmpty && !string.IsNullOrEmpty(TerminalBox.Selection.Text))
                {
                    try
                    {
                        Clipboard.SetText(TerminalBox.Selection.Text);
                        TerminalBox.Selection.Select(TerminalBox.Selection.End, TerminalBox.Selection.End);
                        if (StatusMessage != null) StatusMessage("คัดลอกข้อความแล้ว (Copied)");
                    }
                    catch { }
                }
                else
                {
                    PerformInterrupt();
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                PasteFromClipboard();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter || e.Key == Key.Back || e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Tab || e.Key == Key.Escape)
            {
                InputBox.Focus();
                InputBox_PreviewKeyDown(InputBox, e);
            }
        }

        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SubmitCurrentCommand();
            }
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                if (!string.IsNullOrEmpty(InputBox.SelectedText))
                {
                    return; // Let standard TextBox copy handle selected input text
                }
                if (!TerminalBox.Selection.IsEmpty && !string.IsNullOrEmpty(TerminalBox.Selection.Text))
                {
                    e.Handled = true;
                    try
                    {
                        Clipboard.SetText(TerminalBox.Selection.Text);
                        TerminalBox.Selection.Select(TerminalBox.Selection.End, TerminalBox.Selection.End);
                        if (StatusMessage != null) StatusMessage("คัดลอกข้อความแล้ว (Copied)");
                    }
                    catch { }
                    return;
                }

                e.Handled = true;
                PerformInterrupt();
            }
            else if (e.Key == Key.L && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                ClearScreenAndShowPrompt();
            }
            else if (e.Key == Key.Up)
            {
                e.Handled = true;
                if (Session.CommandHistory.Count > 0)
                {
                    Session.HistoryIndex = Math.Max(0, Session.HistoryIndex - 1);
                    InputBox.Text = Session.CommandHistory[Session.HistoryIndex];
                    InputBox.CaretIndex = InputBox.Text.Length;
                }
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (Session.CommandHistory.Count > 0)
                {
                    Session.HistoryIndex = Math.Min(Session.CommandHistory.Count, Session.HistoryIndex + 1);
                    if (Session.HistoryIndex < Session.CommandHistory.Count)
                        InputBox.Text = Session.CommandHistory[Session.HistoryIndex];
                    else
                        InputBox.Text = string.Empty;
                    InputBox.CaretIndex = InputBox.Text.Length;
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                InputBox.Text = string.Empty;
            }
            else if (e.Key == Key.Tab)
            {
                e.Handled = true;
                string completed = Session.CompletePath(InputBox.Text);
                InputBox.Text = completed;
                InputBox.CaretIndex = InputBox.Text.Length;
            }
        }

        public void FocusInput()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_activeTuiSurface != null)
                    _activeTuiSurface.FocusSurface();
                else
                    InputBox.Focus();
            }));
        }

        public void DisposePane()
        {
            _cursorTimer.Stop();
            _activityTimer.Stop();
            if (_activeTuiSurface != null)
            {
                _activeTuiSurface.StopTui();
                _activeTuiSurface = null;
            }
            Session.Dispose();
        }
    }

    public class MainWindow : Window
    {
        private TerminalSettings _settings;
        private List<TerminalTheme> _themes;
        private TerminalTheme _currentTheme;

        private readonly List<TerminalTabItem> _tabs = new List<TerminalTabItem>();
        private TerminalTabItem _activeTab;
        private TerminalPaneControl _splitRightPane;

        private StackPanel _tabStripPanel;
        private Grid _workspaceGrid;
        private Border _mainWindowBorder;
        private Grid _titleBarGrid;
        private Border _statusBarBorder;
        private Border _findBarBorder;
        private TextBox _findTextBox;

        private TextBlock _statusShellText;
        private TextBlock _statusDirText;
        private Button _statusEncodingBtn;
        private TextBlock _statusZoomText;
        private TextBlock _statusToastText;
        private DispatcherTimer _toastTimer;

        public MainWindow(string initialDirectory = null, string startupCommand = null)
        {
            _settings = TerminalSettings.Load();
            _themes = TerminalTheme.GetBuiltInThemes();
            _currentTheme = _themes.Find(t => string.Equals(t.Name, _settings.ThemeName, StringComparison.OrdinalIgnoreCase)) ?? _themes[0];

            bool isAdmin = SystemIntegration.IsRunningAsAdministrator();
            Title = (isAdmin ? "[Administrator] " : "") + "Windows Terminal (LTSC 2019 — รองรับภาษาไทย)";
            Width = 1140;
            Height = 730;
            MinWidth = 640;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            TryLoadWindowIcon();

            var chrome = new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0)
            };
            WindowChrome.SetWindowChrome(this, chrome);

            BuildUserInterface();
            ApplyThemeToChrome();

            ShellType initialShell = string.Equals(_settings.DefaultShell, "powershell", StringComparison.OrdinalIgnoreCase)
                ? ShellType.PowerShell
                : ShellType.CommandPrompt;
            string startDir = !string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory)
                ? initialDirectory
                : Environment.CurrentDirectory;
            CreateNewTab(initialShell, startDir, _settings.DefaultCodePage);

            if (!string.IsNullOrWhiteSpace(startupCommand))
            {
                string pendingCmd = startupCommand;
                Loaded += (s, e) =>
                {
                    if (_activeTab != null && _activeTab.Pane != null && !string.IsNullOrWhiteSpace(pendingCmd))
                    {
                        string toRun = pendingCmd;
                        pendingCmd = null;
                        _activeTab.Pane.RunCommandDirectly(toRun);
                    }
                };
            }

            PreviewKeyDown += MainWindow_PreviewKeyDown;
            PreviewMouseWheel += MainWindow_PreviewMouseWheel;
            Closed += (s, e) =>
            {
                foreach (var tab in _tabs)
                {
                    tab.Pane.DisposePane();
                }
                if (_splitRightPane != null)
                {
                    _splitRightPane.DisposePane();
                }
            };
        }

        private ImageSource TryGetCustomLogoImageSource()
        {
            try
            {
                string[] candidates = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.png"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico"),
                    SystemIntegration.GetIconPath()
                };
                foreach (string path in candidates)
                {
                    if (File.Exists(path))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(path, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private void TryLoadWindowIcon()
        {
            var img = TryGetCustomLogoImageSource();
            if (img != null)
            {
                Icon = img;
            }
        }

        private void BuildUserInterface()
        {
            _mainWindowBorder = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 45))
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

            // 1. Titlebar & Tab Bar
            _titleBarGrid = new Grid();
            _titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _titleBarGrid.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource == _titleBarGrid || e.OriginalSource is ScrollViewer)
                {
                    if (e.ClickCount == 2)
                        ToggleMaximize();
                    else
                        DragMove();
                }
            };

            // App Icon + Admin Badge in Titlebar
            var leftBrandStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 6, 0)
            };
            leftBrandStack.MouseLeftButtonDown += (s, e) => DragMove();

            ImageSource customLogo = TryGetCustomLogoImageSource();
            if (customLogo != null)
            {
                var logoImg = new Image
                {
                    Source = customLogo,
                    Width = 24,
                    Height = 24,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Windows Terminal LTSC 2019 (รองรับภาษาไทย)"
                };
                RenderOptions.SetBitmapScalingMode(logoImg, BitmapScalingMode.HighQuality);
                leftBrandStack.Children.Add(logoImg);
            }
            else
            {
                var logoBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                logoBorder.Child = new TextBlock
                {
                    Text = ">_",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                };
                leftBrandStack.Children.Add(logoBorder);
            }

            if (SystemIntegration.IsRunningAsAdministrator())
            {
                var adminBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(200, 80, 20)),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(6, 0, 2, 0),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "กำลังทำงานด้วยสิทธิ์ผู้ดูแลระบบ (Administrator)"
                };
                adminBadge.Child = new TextBlock
                {
                    Text = "🛡 ADMIN",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 10
                };
                leftBrandStack.Children.Add(adminBadge);
            }

            Grid.SetColumn(leftBrandStack, 0);
            _titleBarGrid.Children.Add(leftBrandStack);

            // Tab Strip + New Tab "+" + Dropdown "▾"
            var tabAreaGrid = new Grid();
            tabAreaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tabAreaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tabAreaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var tabScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _tabStripPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            tabScroll.Content = _tabStripPanel;
            Grid.SetColumn(tabScroll, 0);
            tabAreaGrid.Children.Add(tabScroll);

            var tabActionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 4, 0, 0)
            };

            var addTabBtn = CreateTitleBarActionButton("＋", "เปิดแท็บใหม่ (Ctrl+T)", () =>
            {
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                ShellType shell = _activeTab != null ? _activeTab.Shell : ShellType.CommandPrompt;
                CreateNewTab(shell, dir, _settings.DefaultCodePage);
            });
            addTabBtn.Width = 30;
            addTabBtn.Height = 28;

            var dropdownBtn = CreateTitleBarActionButton("▾", "เลือกโปรไฟล์ / เมนูตั้งค่า", null);
            dropdownBtn.Width = 26;
            dropdownBtn.Height = 28;
            dropdownBtn.Click += (s, e) => ShowProfileDropdownMenu(dropdownBtn);

            tabActionsPanel.Children.Add(addTabBtn);
            tabActionsPanel.Children.Add(dropdownBtn);
            Grid.SetColumn(tabActionsPanel, 1);
            tabAreaGrid.Children.Add(tabActionsPanel);

            var dragFiller = new Border { Background = Brushes.Transparent };
            dragFiller.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) ToggleMaximize();
                else DragMove();
            };
            Grid.SetColumn(dragFiller, 2);
            tabAreaGrid.Children.Add(dragFiller);

            Grid.SetColumn(tabAreaGrid, 1);
            _titleBarGrid.Children.Add(tabAreaGrid);

            var winButtonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var minBtn = CreateWindowCaptionButton("─", false, () => WindowState = WindowState.Minimized);
            var maxBtn = CreateWindowCaptionButton("▢", false, ToggleMaximize);
            var closeBtn = CreateWindowCaptionButton("✕", true, Close);

            winButtonsPanel.Children.Add(minBtn);
            winButtonsPanel.Children.Add(maxBtn);
            winButtonsPanel.Children.Add(closeBtn);
            Grid.SetColumn(winButtonsPanel, 2);
            _titleBarGrid.Children.Add(winButtonsPanel);

            Grid.SetRow(_titleBarGrid, 0);
            rootGrid.Children.Add(_titleBarGrid);

            // 2. Main Terminal Workspace + Floating Find Bar
            var centerContainer = new Grid();
            _workspaceGrid = new Grid();
            centerContainer.Children.Add(_workspaceGrid);

            _findBarBorder = BuildFindBar();
            centerContainer.Children.Add(_findBarBorder);

            Grid.SetRow(centerContainer, 1);
            rootGrid.Children.Add(centerContainer);

            // 3. Bottom Status Bar
            _statusBarBorder = BuildStatusBar();
            Grid.SetRow(_statusBarBorder, 2);
            rootGrid.Children.Add(_statusBarBorder);

            _mainWindowBorder.Child = rootGrid;
            Content = _mainWindowBorder;
        }

        private Border BuildFindBar()
        {
            var border = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 24, 0),
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1),
                Visibility = Visibility.Collapsed
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new TextBlock
            {
                Text = "ค้นหา:",
                Foreground = Brushes.LightGray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            _findTextBox = new TextBox
            {
                Width = 190,
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
                Padding = new Thickness(6, 2, 6, 2)
            };
            _findTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    FindTextInActivePane(_findTextBox.Text);
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    _findBarBorder.Visibility = Visibility.Collapsed;
                    if (_activeTab != null) _activeTab.Pane.FocusInput();
                }
            };

            var findBtn = CreateTitleBarActionButton("ถัดไป ▾", "ค้นหาและเลื่อนไปไฮไลต์คำถัดไป (Enter)", () => FindTextInActivePane(_findTextBox.Text));
            findBtn.Margin = new Thickness(6, 0, 0, 0);
            findBtn.Padding = new Thickness(8, 2, 8, 2);

            var closeFindBtn = CreateTitleBarActionButton("✕", "ปิดแถบค้นหา (Esc)", () =>
            {
                _findBarBorder.Visibility = Visibility.Collapsed;
                if (_activeTab != null) _activeTab.Pane.FocusInput();
            });
            closeFindBtn.Margin = new Thickness(4, 0, 0, 0);
            closeFindBtn.Width = 24;

            panel.Children.Add(label);
            panel.Children.Add(_findTextBox);
            panel.Children.Add(findBtn);
            panel.Children.Add(closeFindBtn);
            border.Child = panel;
            return border;
        }

        private Border BuildStatusBar()
        {
            var border = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                Padding = new Thickness(10, 0, 10, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _statusShellText = new TextBlock
            {
                Text = "Command Prompt",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            Grid.SetColumn(_statusShellText, 0);
            grid.Children.Add(_statusShellText);

            _statusDirText = new TextBlock
            {
                Text = Environment.CurrentDirectory,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "คลิกเพื่อเปิดโฟลเดอร์นี้ใน File Explorer"
            };
            _statusDirText.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    if (_activeTab != null && Directory.Exists(_activeTab.Pane.Session.CurrentDirectory))
                    {
                        Process.Start("explorer.exe", "\"" + _activeTab.Pane.Session.CurrentDirectory + "\"");
                    }
                }
                catch { }
            };
            Grid.SetColumn(_statusDirText, 1);
            grid.Children.Add(_statusDirText);

            _statusToastText = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(80, 250, 123)),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_statusToastText, 2);
            grid.Children.Add(_statusToastText);

            var rightStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var adminBtn = CreateStatusBarButton("🛡 โหมด Admin", "เปิดหน้าต่างใหม่ด้วยสิทธิ์ผู้ดูแลระบบ (Ctrl+Shift+A)", () =>
            {
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                SystemIntegration.LaunchAsAdministrator(dir);
            });
            var thaiDemoBtn = CreateStatusBarButton("🇹🇭 ทดสอบภาษาไทย", "รันคำสั่งทดสอบภาษาไทยอัตโนมัติ", RunThaiTestCommand);
            var ynDemoBtn = CreateStatusBarButton("❓ ทดสอบ (Y/N)?", "ทดสอบระบบถามยืนยัน (Y/N)? พร้อมปุ่มกดเลือกตอบ", RunYesNoTestCommand);
            var splitBtn = CreateStatusBarButton("◫ แบ่งหน้าจอ", "เปิด/ปิดการแบ่งหน้าจอ 2 ฝั่ง (Alt+Shift+D)", ToggleSplitPane);

            _statusEncodingBtn = CreateStatusBarButton("UTF-8 (65001)", "คลิกเพื่อสลับรหัสภาษา UTF-8 (65001) / Thai TIS-620 (874)", ToggleEncoding);

            var zoomOutBtn = CreateStatusBarButton("－", "ลดขนาดฟอนต์ (Ctrl+-)", () => ChangeFontSize(-1));
            _statusZoomText = new TextBlock
            {
                Text = _settings.FontSize + "px",
                Foreground = Brushes.LightGray,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            var zoomInBtn = CreateStatusBarButton("＋", "เพิ่มขนาดฟอนต์ (Ctrl++)", () => ChangeFontSize(1));
            var settingsBtn = CreateStatusBarButton("⚙ ตั้งค่า", "ตั้งค่าธีมสี ฟอนต์ และติดตั้งลงระบบ (Ctrl+,)", ShowSettingsDialog);

            rightStack.Children.Add(adminBtn);
            rightStack.Children.Add(thaiDemoBtn);
            rightStack.Children.Add(ynDemoBtn);
            rightStack.Children.Add(splitBtn);
            rightStack.Children.Add(_statusEncodingBtn);
            rightStack.Children.Add(zoomOutBtn);
            rightStack.Children.Add(_statusZoomText);
            rightStack.Children.Add(zoomInBtn);
            rightStack.Children.Add(settingsBtn);

            Grid.SetColumn(rightStack, 3);
            grid.Children.Add(rightStack);

            border.Child = grid;
            return border;
        }

        private Button CreateStatusBarButton(string text, string tooltip, Action onClick)
        {
            var btn = new Button
            {
                Content = text,
                ToolTip = tooltip,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(2, 0, 2, 0),
                FontSize = 11.5,
                Cursor = Cursors.Hand
            };
            btn.MouseEnter += (s, e) => btn.Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
            btn.MouseLeave += (s, e) => btn.Background = Brushes.Transparent;
            if (onClick != null)
                btn.Click += (s, e) => onClick();
            return btn;
        }

        private Button CreateTitleBarActionButton(string text, string tooltip, Action onClick)
        {
            var btn = new Button
            {
                Content = text,
                ToolTip = tooltip,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.MouseEnter += (s, e) => btn.Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
            btn.MouseLeave += (s, e) => btn.Background = Brushes.Transparent;
            if (onClick != null)
                btn.Click += (s, e) => onClick();
            return btn;
        }

        private Button CreateWindowCaptionButton(string symbol, bool isClose, Action onClick)
        {
            var btn = new Button
            {
                Content = symbol,
                Width = 46,
                Height = 40,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand
            };
            btn.MouseEnter += (s, e) =>
            {
                btn.Background = isClose
                    ? new SolidColorBrush(Color.FromRgb(232, 17, 35))
                    : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            };
            btn.MouseLeave += (s, e) => btn.Background = Brushes.Transparent;
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private void CreateNewTab(ShellType shell, string startDir, int codePage)
        {
            var pane = new TerminalPaneControl(shell, startDir, codePage, _currentTheme, _settings);
            var tabItem = new TerminalTabItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Shell = shell,
                Title = shell == ShellType.PowerShell ? "Windows PowerShell" : "Command Prompt",
                Pane = pane
            };

            var tabBorder = new Border
            {
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Margin = new Thickness(2, 6, 2, 0),
                Padding = new Thickness(10, 4, 6, 4),
                MinWidth = 155,
                MaxWidth = 240,
                Height = 34,
                Cursor = Cursors.Hand,
                ToolTip = "คลิกสองครั้งเพื่อเปลี่ยนชื่อแท็บ | คลิกปุ่มกลางเพื่อปิดแท็บ"
            };

            var tabGrid = new Grid();
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });

            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconBadge = new TextBlock
            {
                Text = shell == ShellType.PowerShell ? "PS>" : ">_",
                Foreground = shell == ShellType.PowerShell
                    ? new SolidColorBrush(Color.FromRgb(59, 120, 255))
                    : new SolidColorBrush(Color.FromRgb(22, 198, 12)),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            Grid.SetColumn(iconBadge, 0);
            headerRow.Children.Add(iconBadge);

            var titleBlock = new TextBlock
            {
                Text = tabItem.Title,
                Foreground = Brushes.White,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(titleBlock, 1);
            headerRow.Children.Add(titleBlock);

            var closeTabBtn = new Button
            {
                Content = "✕",
                Width = 20,
                Height = 20,
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = Brushes.LightGray,
                BorderThickness = new Thickness(0),
                FontSize = 10.5,
                Cursor = Cursors.Hand,
                ToolTip = "ปิดแท็บ (Ctrl+W)"
            };
            closeTabBtn.MouseEnter += (s, e) => closeTabBtn.Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            closeTabBtn.MouseLeave += (s, e) => closeTabBtn.Background = Brushes.Transparent;
            closeTabBtn.Click += (s, e) =>
            {
                e.Handled = true;
                CloseTab(tabItem);
            };
            Grid.SetColumn(closeTabBtn, 2);
            headerRow.Children.Add(closeTabBtn);

            var activeIndicator = new Border
            {
                Height = 2,
                Background = TerminalTheme.BrushFromHex(_currentTheme.AccentColor),
                CornerRadius = new CornerRadius(1)
            };

            Grid.SetRow(headerRow, 0);
            Grid.SetRow(activeIndicator, 1);
            tabGrid.Children.Add(headerRow);
            tabGrid.Children.Add(activeIndicator);
            tabBorder.Child = tabGrid;

            tabBorder.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    PromptRenameTab(tabItem);
                    e.Handled = true;
                }
                else
                {
                    ActivateTab(tabItem);
                }
            };

            tabBorder.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle)
                {
                    CloseTab(tabItem);
                    e.Handled = true;
                }
            };

            tabItem.TabHeaderBorder = tabBorder;
            tabItem.TabTitleBlock = titleBlock;
            tabItem.ActiveIndicator = activeIndicator;

            pane.TitleChanged += newTitle =>
            {
                if (string.IsNullOrEmpty(tabItem.CustomTitle))
                {
                    tabItem.Title = newTitle;
                    tabItem.TabTitleBlock.Text = newTitle;
                }
            };

            pane.DirectoryChanged += newDir =>
            {
                if (_activeTab == tabItem)
                {
                    _statusDirText.Text = newDir;
                }
            };

            pane.StatusMessage += ShowToast;
            pane.CloseRequested += () => CloseTab(tabItem);

            _tabs.Add(tabItem);
            _tabStripPanel.Children.Add(tabBorder);
            ActivateTab(tabItem);
        }

        private void ActivateTab(TerminalTabItem tab)
        {
            if (tab == null) return;
            _activeTab = tab;

            foreach (var item in _tabs)
            {
                bool isActive = (item == tab);
                item.TabHeaderBorder.Background = isActive
                    ? TerminalTheme.BrushFromHex(_currentTheme.ActiveTabBackground)
                    : TerminalTheme.BrushFromHex(_currentTheme.InactiveTabBackground);
                item.ActiveIndicator.Visibility = isActive ? Visibility.Visible : Visibility.Hidden;
                item.ActiveIndicator.Background = TerminalTheme.BrushFromHex(_currentTheme.AccentColor);
            }

            RebuildWorkspaceLayout();
            UpdateStatusBarForActiveTab();
            tab.Pane.FocusInput();
        }

        private void RebuildWorkspaceLayout()
        {
            _workspaceGrid.Children.Clear();
            _workspaceGrid.ColumnDefinitions.Clear();

            if (_activeTab == null) return;

            if (_splitRightPane == null)
            {
                _workspaceGrid.Children.Add(_activeTab.Pane);
            }
            else
            {
                _workspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _workspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                _workspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Grid.SetColumn(_activeTab.Pane, 0);
                _workspaceGrid.Children.Add(_activeTab.Pane);

                var splitter = new GridSplitter
                {
                    Width = 4,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = TerminalTheme.BrushFromHex(_currentTheme.AccentColor)
                };
                Grid.SetColumn(splitter, 1);
                _workspaceGrid.Children.Add(splitter);

                Grid.SetColumn(_splitRightPane, 2);
                _workspaceGrid.Children.Add(_splitRightPane);
            }
        }

        private void CloseTab(TerminalTabItem tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return;

            int idx = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            _tabStripPanel.Children.Remove(tab.TabHeaderBorder);
            tab.Pane.DisposePane();

            if (_tabs.Count == 0)
            {
                Close();
                return;
            }

            int nextIdx = Math.Min(idx, _tabs.Count - 1);
            ActivateTab(_tabs[nextIdx]);
        }

        private void ToggleSplitPane()
        {
            if (_splitRightPane == null)
            {
                string startDir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                ShellType splitShell = (_activeTab != null && _activeTab.Shell == ShellType.CommandPrompt)
                    ? ShellType.PowerShell
                    : ShellType.CommandPrompt;
                _splitRightPane = new TerminalPaneControl(splitShell, startDir, _settings.DefaultCodePage, _currentTheme, _settings);
                _splitRightPane.StatusMessage += ShowToast;
                _splitRightPane.CloseRequested += ToggleSplitPane;
                RebuildWorkspaceLayout();
                _splitRightPane.FocusInput();
                ShowToast("เปิดโหมดแบ่งหน้าจอ (Split Pane) แล้ว");
            }
            else
            {
                _splitRightPane.DisposePane();
                _splitRightPane = null;
                RebuildWorkspaceLayout();
                if (_activeTab != null) _activeTab.Pane.FocusInput();
                ShowToast("ปิดโหมดแบ่งหน้าจอแล้ว");
            }
        }

        private void UpdateStatusBarForActiveTab()
        {
            if (_activeTab == null) return;
            _statusShellText.Text = _activeTab.Shell == ShellType.PowerShell ? "PS Windows PowerShell" : ">_ Command Prompt";
            _statusDirText.Text = _activeTab.Pane.Session.CurrentDirectory;
            int cp = _activeTab.Pane.Session.CodePage;
            _statusEncodingBtn.Content = cp == 65001 ? "UTF-8 (65001)" : "Thai CP874 (TIS-620)";
            _statusZoomText.Text = _settings.FontSize + "px";
        }

        private void ToggleEncoding()
        {
            if (_activeTab == null) return;
            int currentCp = _activeTab.Pane.Session.CodePage;
            int nextCp = (currentCp == 65001) ? 874 : 65001;
            _activeTab.Pane.Session.SetCodePage(nextCp);
            _settings.DefaultCodePage = nextCp;
            _settings.Save();
            UpdateStatusBarForActiveTab();
            ShowToast(nextCp == 65001
                ? "สลับรหัสภาษาเป็น UTF-8 (65001) แล้ว"
                : "สลับรหัสภาษาเป็น Thai TIS-620 / Windows-874 แล้ว");
        }

        private void ChangeFontSize(double delta)
        {
            double newSize = Math.Max(10, Math.Min(36, _settings.FontSize + delta));
            if (Math.Abs(newSize - _settings.FontSize) < 0.01) return;
            _settings.FontSize = newSize;
            _settings.Save();
            ApplyThemeToChrome();
            UpdateStatusBarForActiveTab();
        }

        private void RunThaiTestCommand()
        {
            if (_activeTab == null) return;
            if (_activeTab.Shell == ShellType.CommandPrompt)
            {
                _activeTab.Pane.RunCommandDirectly("echo สวัสดีครับ! ทดสอบภาษาไทยบน Windows 10 LTSC 2019 — ผู้ใหญ่หาผ้าใหม่ ให้สะใภ้ใช้คล้องคอ น้ำใสใจจริง ๑๒๓๔๕๖๗๘๙๐");
            }
            else
            {
                _activeTab.Pane.RunCommandDirectly("Write-Host 'สวัสดีครับ! ทดสอบภาษาไทยบน Windows 10 LTSC 2019 — ผู้ใหญ่หาผ้าใหม่ ให้สะใภ้ใช้คล้องคอ น้ำใสใจจริง ๑๒๓๔๕๖๗๘๙๐' -ForegroundColor Cyan");
            }
        }

        private void RunYesNoTestCommand()
        {
            if (_activeTab == null) return;
            if (_activeTab.Shell == ShellType.CommandPrompt)
            {
                _activeTab.Pane.RunCommandDirectly("mkdir โฟลเดอร์ทดสอบ_YN 2>nul & rmdir /s โฟลเดอร์ทดสอบ_YN");
            }
            else
            {
                _activeTab.Pane.RunCommandDirectly("$ans = Read-Host 'ต้องการดำเนินการต่อหรือไม่ (Y/N)?'; Write-Host ('คุณเลือกตอบ: ' + $ans) -ForegroundColor Green");
            }
        }

        private void ShowToast(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _statusToastText.Text = message;
                if (_toastTimer == null)
                {
                    _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.4) };
                    _toastTimer.Tick += (s, e) =>
                    {
                        _toastTimer.Stop();
                        _statusToastText.Text = "";
                    };
                }
                else
                {
                    _toastTimer.Stop();
                }
                _toastTimer.Start();
            }));
        }

        private void ApplyThemeToChrome()
        {
            Opacity = _settings.WindowOpacity;
            _titleBarGrid.Background = TerminalTheme.BrushFromHex(_currentTheme.TabBarBackground);
            _statusBarBorder.Background = TerminalTheme.BrushFromHex(_currentTheme.TabBarBackground);
            _workspaceGrid.Background = TerminalTheme.BrushFromHex(_currentTheme.Background);

            foreach (var tab in _tabs)
            {
                tab.Pane.ApplyThemeAndSettings(_currentTheme, _settings);
                bool isActive = (tab == _activeTab);
                tab.TabHeaderBorder.Background = isActive
                    ? TerminalTheme.BrushFromHex(_currentTheme.ActiveTabBackground)
                    : TerminalTheme.BrushFromHex(_currentTheme.InactiveTabBackground);
                tab.ActiveIndicator.Background = TerminalTheme.BrushFromHex(_currentTheme.AccentColor);
            }

            if (_splitRightPane != null)
            {
                _splitRightPane.ApplyThemeAndSettings(_currentTheme, _settings);
            }
        }

        private void ShowProfileDropdownMenu(UIElement placementTarget)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = placementTarget,
                Placement = PlacementMode.Bottom,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };

            AddMenuItem(menu, ">_  Command Prompt (UTF-8 ภาษาไทย)", "Ctrl+Shift+1", () =>
            {
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                CreateNewTab(ShellType.CommandPrompt, dir, 65001);
            });

            AddMenuItem(menu, "PS  Windows PowerShell (UTF-8 ภาษาไทย)", "Ctrl+Shift+2", () =>
            {
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                CreateNewTab(ShellType.PowerShell, dir, 65001);
            });

            AddMenuItem(menu, ">_  Command Prompt (Legacy Thai CP874)", "Ctrl+Shift+3", () =>
            {
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                CreateNewTab(ShellType.CommandPrompt, dir, 874);
            });

            AddMenuItem(menu, "🛡  เปิดหน้าต่างใหม่ในโหมดผู้ดูแลระบบ (Run as Administrator)", "Ctrl+Shift+A", () =>
            {
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                SystemIntegration.LaunchAsAdministrator(dir);
            });

            menu.Items.Add(new Separator());

            AddMenuItem(menu, "◫  แบ่งหน้าจอซ้าย-ขวา (Split Pane)", "Alt+Shift+D", ToggleSplitPane);
            AddMenuItem(menu, "🔍  ค้นหาและไฮไลต์ข้อความ (Find...)", "Ctrl+Shift+F", ToggleFindBar);
            AddMenuItem(menu, "🇹🇭  รันคำสั่งทดสอบภาษาไทย", "", RunThaiTestCommand);
            AddMenuItem(menu, "🧹  ล้างหน้าจอ (Clear Terminal)", "Ctrl+L", () =>
            {
                if (_activeTab != null) _activeTab.Pane.ClearScreenAndShowPrompt();
            });
            AddMenuItem(menu, "💾  บันทึกผลลัพธ์เป็นไฟล์ UTF-8...", "Ctrl+Shift+S", ExportTerminalOutput);

            menu.Items.Add(new Separator());

            AddMenuItem(menu, "📌  สร้างไอคอนลัดบน Desktop และติดตั้งลงระบบถาวร", "", () =>
            {
                SystemIntegration.InstallPermanentlyAndRegister(true, true);
                ShowToast("ติดตั้งลงระบบถาวรและสร้าง Shortcut บน Desktop เรียบร้อยแล้ว!");
            });

            AddMenuItem(menu, "⚙  ตั้งค่า (Settings — ธีมสี, ฟอนต์, ความโปร่งใส)", "Ctrl+,", ShowSettingsDialog);
            AddMenuItem(menu, "ℹ  เกี่ยวกับ Windows Terminal LTSC", "", ShowAboutDialog);

            menu.IsOpen = true;
        }

        private void AddMenuItem(ContextMenu menu, string header, string gesture, Action action)
        {
            var item = new MenuItem
            {
                Header = header,
                InputGestureText = gesture,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Padding = new Thickness(8, 5, 8, 5)
            };
            item.Click += (s, e) => action();
            menu.Items.Add(item);
        }

        private void ToggleFindBar()
        {
            if (_findBarBorder.Visibility == Visibility.Visible)
            {
                _findBarBorder.Visibility = Visibility.Collapsed;
                if (_activeTab != null) _activeTab.Pane.FocusInput();
            }
            else
            {
                _findBarBorder.Visibility = Visibility.Visible;
                _findTextBox.Focus();
                _findTextBox.SelectAll();
            }
        }

        private void FindTextInActivePane(string query)
        {
            if (_activeTab == null || string.IsNullOrEmpty(query)) return;
            int currentMatch;
            int total = _activeTab.Pane.FindAndHighlightNext(query, out currentMatch);
            ShowToast(total > 0
                ? string.Format("ไฮไลต์ \"{0}\" จุดที่ {1} จากทั้งหมด {2} จุด", query, currentMatch, total)
                : string.Format("ไม่พบ \"{0}\" ในหน้าจอ", query));
        }

        private void ExportTerminalOutput()
        {
            if (_activeTab == null) return;
            var sfd = new SaveFileDialog
            {
                Title = "บันทึกผลลัพธ์เทอร์มินัลเป็นไฟล์ (UTF-8)",
                Filter = "Text Files (*.txt)|*.txt|Log Files (*.log)|*.log|All Files (*.*)|*.*",
                FileName = "terminal_output_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"
            };
            if (sfd.ShowDialog(this) == true)
            {
                File.WriteAllText(sfd.FileName, _activeTab.Pane.GetAllTerminalText(), Encoding.UTF8);
                ShowToast("บันทึกไฟล์เรียบร้อยแล้ว: " + Path.GetFileName(sfd.FileName));
            }
        }

        private void PromptRenameTab(TerminalTabItem tab)
        {
            var dlg = new Window
            {
                Title = "เปลี่ยนชื่อแท็บ (Rename Tab)",
                Width = 360,
                Height = 155,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                Foreground = Brushes.White
            };

            var stack = new StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(new TextBlock
            {
                Text = "ระบุชื่อแท็บใหม่ (รองรับภาษาไทย):",
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var tb = new TextBox
            {
                Text = tab.CustomTitle ?? tab.Title,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 14
            };
            stack.Children.Add(tb);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var okBtn = new Button { Content = "ตกลง", Width = 75, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelBtn = new Button { Content = "ยกเลิก", Width = 75, IsCancel = true };

            okBtn.Click += (s, e) =>
            {
                tab.CustomTitle = tb.Text.Trim();
                tab.TabTitleBlock.Text = string.IsNullOrEmpty(tab.CustomTitle) ? tab.Title : tab.CustomTitle;
                dlg.DialogResult = true;
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            stack.Children.Add(btnRow);

            dlg.Content = stack;
            dlg.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            dlg.ShowDialog();
        }

        private void ShowSettingsDialog()
        {
            var dlg = new Window
            {
                Title = "ตั้งค่า Windows Terminal LTSC",
                Width = 510,
                Height = 510,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 32)),
                Foreground = Brushes.White
            };

            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = "⚙ ตั้งค่าการใช้งาน (Settings)",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14)
            });

            root.Children.Add(new TextBlock { Text = "ธีมสี (Color Scheme):", Margin = new Thickness(0, 0, 0, 4), Foreground = Brushes.LightGray });
            var themeCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(6, 4, 6, 4) };
            foreach (var t in _themes)
            {
                themeCombo.Items.Add(t.DisplayName);
                if (t.Name == _currentTheme.Name)
                    themeCombo.SelectedItem = t.DisplayName;
            }
            root.Children.Add(themeCombo);

            root.Children.Add(new TextBlock { Text = "โปรไฟล์เริ่มต้นเมื่อเปิดโปรแกรม (Default Profile):", Margin = new Thickness(0, 0, 0, 4), Foreground = Brushes.LightGray });
            var shellCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(6, 4, 6, 4) };
            shellCombo.Items.Add("Command Prompt (cmd.exe)");
            shellCombo.Items.Add("Windows PowerShell (powershell.exe)");
            shellCombo.SelectedIndex = string.Equals(_settings.DefaultShell, "powershell", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            root.Children.Add(shellCombo);

            root.Children.Add(new TextBlock { Text = "ฟอนต์ (ลำดับฟอนต์ภาษาอังกฤษและภาษาไทย):", Margin = new Thickness(0, 0, 0, 4), Foreground = Brushes.LightGray });
            var fontBox = new TextBox
            {
                Text = _settings.FontFamily,
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(6, 4, 6, 4)
            };
            root.Children.Add(fontBox);

            var sliderGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            sliderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sliderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            sliderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var fontStack = new StackPanel();
            var fontLabel = new TextBlock { Text = "ขนาดตัวอักษร: " + _settings.FontSize + " px", Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 4) };
            var fontSlider = new Slider { Minimum = 10, Maximum = 30, Value = _settings.FontSize, TickFrequency = 1, IsSnapToTickEnabled = true };
            fontSlider.ValueChanged += (s, e) => fontLabel.Text = "ขนาดตัวอักษร: " + fontSlider.Value + " px";
            fontStack.Children.Add(fontLabel);
            fontStack.Children.Add(fontSlider);
            Grid.SetColumn(fontStack, 0);
            sliderGrid.Children.Add(fontStack);

            var opacityStack = new StackPanel();
            var opacityLabel = new TextBlock { Text = "ความโปร่งใสหน้าต่าง: " + Math.Round(_settings.WindowOpacity * 100) + "%", Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 4) };
            var opacitySlider = new Slider { Minimum = 0.75, Maximum = 1.0, Value = _settings.WindowOpacity, TickFrequency = 0.01 };
            opacitySlider.ValueChanged += (s, e) => opacityLabel.Text = "ความโปร่งใสหน้าต่าง: " + Math.Round(opacitySlider.Value * 100) + "%";
            opacityStack.Children.Add(opacityLabel);
            opacityStack.Children.Add(opacitySlider);
            Grid.SetColumn(opacityStack, 2);
            sliderGrid.Children.Add(opacityStack);

            root.Children.Add(sliderGrid);

            var showBarCheck = new CheckBox
            {
                Content = "แสดงแถบพิมพ์คำสั่งภาษาไทยด้านล่าง (ช่วยให้แก้คำภาษาไทยและกดปุ่มลัดได้สะดวก)",
                Foreground = Brushes.White,
                IsChecked = _settings.ShowCommandInputBar,
                Margin = new Thickness(0, 4, 0, 8)
            };
            root.Children.Add(showBarCheck);

            var defaultTermCheck = new CheckBox
            {
                Content = "ตั้งเป็นเทอร์มินัลหลัก (เมื่อพิมพ์ cmd หรือ wt ในโฟลเดอร์ และเมนูคลิกขวา)",
                Foreground = Brushes.White,
                IsChecked = SystemIntegration.IsRegisteredAsDefault(),
                Margin = new Thickness(0, 0, 0, 8)
            };
            root.Children.Add(defaultTermCheck);

            var shortcutCheck = new CheckBox
            {
                Content = "ติดตั้งลงระบบถาวร (%LOCALAPPDATA%\\WindowsTerminalLTSC) และสร้างไอคอนบน Desktop",
                Foreground = Brushes.White,
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 16)
            };
            root.Children.Add(shortcutCheck);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var saveBtn = new Button
            {
                Content = "บันทึกการตั้งค่า",
                Width = 120,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                IsDefault = true
            };
            var cancelBtn = new Button
            {
                Content = "ยกเลิก",
                Width = 85,
                Height = 32,
                IsCancel = true
            };

            saveBtn.Click += (s, e) =>
            {
                int selectedThemeIdx = Math.Max(0, themeCombo.SelectedIndex);
                _currentTheme = _themes[selectedThemeIdx];
                _settings.ThemeName = _currentTheme.Name;
                _settings.DefaultShell = shellCombo.SelectedIndex == 1 ? "powershell" : "cmd";
                _settings.FontFamily = string.IsNullOrWhiteSpace(fontBox.Text) ? "Consolas, Leelawadee UI, Tahoma, Segoe UI" : fontBox.Text.Trim();
                _settings.FontSize = fontSlider.Value;
                _settings.WindowOpacity = Math.Round(opacitySlider.Value, 2);
                _settings.ShowCommandInputBar = showBarCheck.IsChecked == true;
                _settings.Save();

                if (shortcutCheck.IsChecked == true)
                {
                    SystemIntegration.InstallPermanentlyAndRegister(defaultTermCheck.IsChecked == true, true);
                }
                else if (defaultTermCheck.IsChecked == true)
                {
                    SystemIntegration.RegisterDefaultTerminal(true);
                }
                else
                {
                    SystemIntegration.UnregisterDefaultTerminal();
                }

                ApplyThemeToChrome();
                UpdateStatusBarForActiveTab();
                ShowToast("บันทึกการตั้งค่าเรียบร้อยแล้ว");
                dlg.DialogResult = true;
            };

            btnPanel.Children.Add(saveBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            dlg.Content = root;
            dlg.ShowDialog();
        }

        private void ShowAboutDialog()
        {
            MessageBox.Show(
                this,
                "Windows Terminal สำหรับ Windows 10 Enterprise LTSC 2019 (Build 17763)\n" +
                "ผู้พัฒนาต้นฉบับ (Author): Rachata\n" +
                "สัญญาอนุญาต (License): เปิดซอร์สโค้ดให้ศึกษาและใช้งานฟรี — ห้ามนำไปจำหน่ายเชิงพาณิชย์ และห้ามลบหรือดัดแปลงเครดิตผู้พัฒนา\n\n" +
                "คุณสมบัติเด่น:\n" +
                "• แก้ปัญหา cmd.exe ปกติพิมพ์หรือแสดงผลภาษาไทยไม่ได้ (สระลอย/วรรณยุกต์ซ้อน/ต่างดาว)\n" +
                "• แสดงผลด้วย DirectWrite พร้อมระบบสลับฟอนต์ภาษาไทยอัตโนมัติ (Consolas + Leelawadee UI / Tahoma)\n" +
                "• รองรับการลากไฟล์หรือโฟลเดอร์มาวาง (Drag & Drop) และกด Shift+ลากโฟลเดอร์เพื่อ cd ทันที\n" +
                "• รองรับโหมดผู้ดูแลระบบ (Run as Administrator: Ctrl+Shift+A)\n" +
                "• รองรับการคลิกลิงก์เว็บ (http/https) และการค้นหาพร้อมไฮไลต์คำ (Ctrl+Shift+F)\n" +
                "• รองรับหลายแท็บ (Multi-Tab), การแบ่งหน้าจอ 2 ฝั่ง (Split Pane: Alt+Shift+D) และจำประวัติคำสั่งข้ามรอบ\n\n" +
                "คีย์ลัดหลัก:\n" +
                "• Ctrl+T : เปิดแท็บใหม่ | Ctrl+W : ปิดแท็บ | Ctrl+Tab : สลับแท็บ\n" +
                "• Ctrl+Shift+A : เปิดหน้าต่างใหม่โหมด Administrator\n" +
                "• Alt+Shift+D : แบ่งหน้าจอซ้าย-ขวา\n" +
                "• Ctrl+Shift+F : ค้นหาและไฮไลต์ข้อความ",
                "เกี่ยวกับ Windows Terminal LTSC — Created by Rachata",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ToggleMaximize()
        {
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        }

        private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                ChangeFontSize(e.Delta > 0 ? 1 : -1);
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

            if (ctrl && e.Key == Key.T)
            {
                e.Handled = true;
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                ShellType shell = _activeTab != null ? _activeTab.Shell : ShellType.CommandPrompt;
                CreateNewTab(shell, dir, _settings.DefaultCodePage);
            }
            else if (ctrl && e.Key == Key.W && (shift || _activeTab == null || !_activeTab.Pane.IsInTuiMode))
            {
                e.Handled = true;
                if (_activeTab != null) CloseTab(_activeTab);
            }
            else if (ctrl && e.Key == Key.Tab)
            {
                e.Handled = true;
                if (_tabs.Count > 1 && _activeTab != null)
                {
                    int idx = _tabs.IndexOf(_activeTab);
                    int next = shift ? (idx - 1 + _tabs.Count) % _tabs.Count : (idx + 1) % _tabs.Count;
                    ActivateTab(_tabs[next]);
                }
            }
            else if (ctrl && shift && e.Key == Key.D1)
            {
                e.Handled = true;
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                CreateNewTab(ShellType.CommandPrompt, dir, 65001);
            }
            else if (ctrl && shift && e.Key == Key.D2)
            {
                e.Handled = true;
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                CreateNewTab(ShellType.PowerShell, dir, 65001);
            }
            else if (ctrl && shift && e.Key == Key.D3)
            {
                e.Handled = true;
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                CreateNewTab(ShellType.CommandPrompt, dir, 874);
            }
            else if (ctrl && shift && e.Key == Key.A)
            {
                e.Handled = true;
                string dir = _activeTab != null ? _activeTab.Pane.Session.CurrentDirectory : Environment.CurrentDirectory;
                SystemIntegration.LaunchAsAdministrator(dir);
            }
            else if (alt && shift && e.Key == Key.D)
            {
                e.Handled = true;
                ToggleSplitPane();
            }
            else if (ctrl && shift && e.Key == Key.F)
            {
                e.Handled = true;
                ToggleFindBar();
            }
            else if (ctrl && shift && e.Key == Key.S)
            {
                e.Handled = true;
                ExportTerminalOutput();
            }
            else if (ctrl && e.Key == Key.OemComma)
            {
                e.Handled = true;
                ShowSettingsDialog();
            }
            else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
            {
                e.Handled = true;
                ChangeFontSize(1);
            }
            else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
            {
                e.Handled = true;
                ChangeFontSize(-1);
            }
            else if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0))
            {
                e.Handled = true;
                _settings.FontSize = 15;
                _settings.Save();
                ApplyThemeToChrome();
                UpdateStatusBarForActiveTab();
            }
        }
    }
}
