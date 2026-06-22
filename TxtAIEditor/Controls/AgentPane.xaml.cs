using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace TxtAIEditor.Controls
{
    public sealed class AgentOutputWrapper
    {
        private readonly AgentPane _pane;
        public AgentOutputWrapper(AgentPane pane)
        {
            _pane = pane;
        }

        public string Text => _pane.GetRawOutputText();
        public string SelectedText => _pane.SelectedOutputText;
    }

    public sealed class AgentAttachmentItem
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string TokenText { get; set; } = string.Empty;
        public string RemoveTooltip { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "\uE8A5";
    }

    public sealed class AgentHistoryItemViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string TimeText { get; set; } = string.Empty;
    }

    public sealed class AgentOpenSessionItemViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsSelected { get; set; }
        public bool IsRunning { get; set; }
        public bool CanSelect { get; set; } = true;
        public bool CanClose { get; set; } = true;
    }

    public sealed class AgentSkillItem
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public sealed partial class AgentPane : UserControl
    {
        private int _outputLength;
        private string _rawOutputText = string.Empty;
        private readonly List<string> _renderedLines = new List<string>();
        private readonly StringBuilder _pendingOutputText = new StringBuilder();
        private bool _outputScrollQueued;
        private bool _userScrolledUp;
        private double _lastVerticalOffset;
        private DispatcherTimer? _outputFlushTimer;
        private AgentDisplayLocalizer _displayText = AgentDisplayLocalizer.CreateWithResourceLoader();
        private string _explicitSelectedOutputText = string.Empty;
        private Windows.Foundation.Point? _outputPointerDownPoint;
        private bool _outputPointerSelectionGesture;
        private bool _hasExplicitOutputSelection;
        private List<string> _agentPresetNames = new List<string>();
        private HashSet<string> _selectedAgentPresetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<AgentSkillItem> _agentSkillItems = new List<AgentSkillItem>();
        private HashSet<string> _selectedAgentSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Button> _agentSkillButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private List<AgentMcpItem> _agentMcpItems = new List<AgentMcpItem>();
        private HashSet<string> _selectedAgentMcpNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Func<string, string, string>? _getString;

        public string RawOutputText => GetRawOutputText();
        public string SelectedOutputText
        {
            get
            {
                if (_hasExplicitOutputSelection)
                {
                    CaptureExplicitOutputSelection();
                }

                return _explicitSelectedOutputText;
            }
        }

        public AgentPane()
        {
            InitializeComponent();
            AgentOutputText.SizeChanged += (_, _) => QueueOutputScrollToEnd();
            AgentOutputText.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(OnOutputPointerPressed),
                true);
            AgentOutputText.AddHandler(
                UIElement.PointerMovedEvent,
                new PointerEventHandler(OnOutputPointerMoved),
                true);
            AgentOutputText.AddHandler(
                UIElement.PointerReleasedEvent,
                new PointerEventHandler(OnOutputPointerReleased),
                true);

            ResetOutput(_displayText.OutputPlaceholder);
            Localize(_displayText.GetString);
        }

        public event RoutedEventHandler? RunRequested;
        public event RoutedEventHandler? StopRequested;
        public event RoutedEventHandler? NewSessionRequested;
        public event RoutedEventHandler? RewindSessionRequested;
        public event RoutedEventHandler? InsertOutputRequested;
        public event RoutedEventHandler? InsertNewTabOutputRequested;
        public event RoutedEventHandler? AddAttachmentRequested;
        public event EventHandler<AgentAttachmentItem>? RemoveAttachmentRequested;
        public event RoutedEventHandler? AgentPresetAddRequested;
        public event EventHandler<string>? AgentPresetToggled;
        public event EventHandler<string>? AgentPresetEdited;
        public event EventHandler<string>? AgentPresetDeleted;
        public event EventHandler<string>? AgentPresetRemoved;
        public event RoutedEventHandler? AgentPresetExportRequested;
        public event RoutedEventHandler? AgentPresetImportRequested;
        public event EventHandler? AgentSkillFlyoutOpened;
        public event EventHandler? AgentSkillRefreshRequested;
        public event EventHandler<string>? AgentSkillToggled;
        public event EventHandler<string>? AgentSkillRemoved;
        public event EventHandler? AgentMcpFlyoutOpened;
        public event RoutedEventHandler? AgentMcpAddRequested;
        public event RoutedEventHandler? AgentMcpExportRequested;
        public event RoutedEventHandler? AgentMcpImportRequested;
        public event EventHandler<string>? AgentMcpToggled;
        public event EventHandler<string>? AgentMcpEdited;
        public event EventHandler<string>? AgentMcpDeleted;
        public event EventHandler<string>? AgentMcpRemoved;
        public event RoutedEventHandler? DiffApproved;
        public event RoutedEventHandler? DiffCancelled;
        public event EventHandler<AgentFileEditPreview>? FileRevertRequested;
        public event EventHandler<AgentFileEditPreview>? FileDiffRequested;

        public event EventHandler<string>? HistorySelected;
        public event EventHandler<string>? HistoryDeleted;
        public event RoutedEventHandler? HistoryToolbarDeleteClicked;
        public event EventHandler? OpenSessionsFlyoutOpened;
        public event EventHandler<string>? OpenSessionSelected;
        public event EventHandler<string>? OpenSessionClosed;
        public event RoutedEventHandler? ModelNameClick;

        private List<AgentHistoryItemViewModel> _historyItems = new List<AgentHistoryItemViewModel>();
        private string? _selectedHistoryId;
        private List<AgentOpenSessionItemViewModel> _openSessionItems = new List<AgentOpenSessionItemViewModel>();
        private string? _selectedOpenSessionId;

        public AgentOutputWrapper Output => new AgentOutputWrapper(this);
        public TextBox Prompt => AgentPromptInput;
        public TextBox Activity => AgentActivityText;
        public TextBlock ContextStats => AgentContextStatsText;
        public TextBlock TokenCount => AgentTokenCountText;
        public CheckBox PlanningModeCheckBox => AgentPlanningModeCheckBox;
        public bool PlanningMode => AgentPlanningModeCheckBox.IsChecked == true;
        public CheckBox StreamToTabCheckBox => AgentStreamToTabCheckBox;
        public bool StreamToTab => AgentStreamToTabCheckBox.IsChecked == true;

        public bool HideHtmlCodeBlocks { get; set; }
        public bool IsThinkingActivityActive => _thinkingLineActive;

        private bool _isBusy;
        private bool _canRewindSession;
        private string _runButtonText = string.Empty;
        private string _stopButtonText = string.Empty;
        private const string OutputLineBreak = "\r\n";
        private DispatcherTimer? _thinkingTimer;
        private DispatcherTimer? _thinkingLabelFlushTimer;
        private bool _thinkingLineActive;
        private int _thinkingLineStart;
        private int _thinkingDotCount;
        private string _thinkingLinePrefix = string.Empty;
        private string _thinkingLineTimestamp = string.Empty;
        private string? _pendingThinkingLabel;
        private DateTimeOffset _lastThinkingLabelRender = DateTimeOffset.MinValue;
        private const int MaxOutputFlushChars = 4_000;
        private const int ThinkingLabelMinIntervalMs = 200;
        private const double SelectedChipScrollStep = 160;

        private void AppendText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            int currentLength = _rawOutputText.Length;
            if (_outputLength < 0 || _outputLength > currentLength)
            {
                _outputLength = currentLength;
            }

            bool appendingAtEnd = _outputLength == currentLength;
            if (appendingAtEnd)
            {
                text = CollapseExcessBlankLinesForAppend(_rawOutputText, text);
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                _rawOutputText += text;
                _outputLength += text.Length;
                if (!HideHtmlCodeBlocks)
                {
                    AppendRenderedText(text);
                }
                else
                {
                    UpdateRichText(_rawOutputText);
                }
            }
            else
            {
                _rawOutputText = _rawOutputText.Insert(_outputLength, text);
                _outputLength += text.Length;
                UpdateRichText(_rawOutputText);
            }
        }

        public void Localize(Func<string, string, string> getString)
        {
            _getString = getString;
            _displayText = new AgentDisplayLocalizer(getString);
            string outputText = _rawOutputText.TrimStart();
            if (_displayText.IsOutputPlaceholder(outputText))
            {
                ResetOutput(_displayText.OutputPlaceholder);
            }

            AgentContextStatsText.Text = getString("AgentContextStatsDefault", "현재 탭과 선택 영역을 맥락으로 사용");
            AgentPlanningModeCheckBox.Content = getString("AgentIncludeActiveFile", "계획 모드 (Planning mode)");
            AgentStreamToTabCheckBox.Content = getString("AgentStreamToTab", "탭에 스트리밍");
            AgentPromptInput.PlaceholderText = getString("AgentPromptPlaceholder", "Agent에게 맡길 작업 입력...");
            ToolTipService.SetToolTip(AgentMcpButton, getString("AgentMcpButtonTooltip", "MCP 서버"));
            AgentAddMcpText.Text = getString("AgentMcpAddText", "MCP 추가");
            AgentExportMcpText.Text = getString("PresetExportText", "내보내기");
            AgentImportMcpText.Text = getString("PresetImportText", "가져오기");
            ToolTipService.SetToolTip(AgentAddAttachmentButton, getString("AgentAddAttachmentTooltip", "이미지 또는 파일 추가"));
            ToolTipService.SetToolTip(AgentSkillButton, getString("AgentSkillButtonTooltip", "스킬"));
            AgentSkillTitleText.Text = getString("AgentSkillTitle", "스킬");
            ToolTipService.SetToolTip(AgentPresetButton, getString("AgentPresetButtonTooltip", "페르소나/지침 프리셋"));
            ToolTipService.SetToolTip(AgentSkillRefreshButton, getString("AgentSkillRefreshTooltip", "스킬 디렉터리를 다시 스캔"));
            AgentAddPresetText.Text = getString("AgentPresetAddText", "프리셋 추가");
            AgentExportPresetText.Text = getString("PresetExportText", "내보내기");
            AgentImportPresetText.Text = getString("PresetImportText", "가져오기");
            _runButtonText = getString("AgentRunButton", "실행");
            _stopButtonText = getString("AgentStopButton", "중단");
            AgentRunButton.Content = _isBusy ? _stopButtonText : _runButtonText;
            AgentNewSessionButton.Content = getString("AgentNewSessionButton", "새 세션");
            ToolTipService.SetToolTip(AgentRewindSessionButton, getString("AgentRewindSessionTooltip", "이전 프롬프트 입력 전으로 되감기"));
            ToolTipService.SetToolTip(AgentOpenSessionsButton, getString("AgentOpenSessionsTooltip", "열린 세션"));
            AgentOpenSessionsTitleText.Text = getString("AgentOpenSessionsTitle", "열린 세션");
            ToolTipService.SetToolTip(AgentHistoryButton, getString("AgentHistoryTooltip", "세션 히스토리"));
            AgentHistoryTitleText.Text = getString("AgentHistoryTitle", "세션 히스토리 (최근 20개)");
            ToolTipService.SetToolTip(AgentDeleteHistoryButton, getString("AgentDeleteHistoryTooltip", "히스토리 삭제"));
            AgentInsertOutputButton.Content = getString("LlmInsertOutputButtonText", "입력");
            AgentActivityHeaderText.Text = getString("AgentActivityHeader", "진행 상황");
            if (_displayText.IsActivityIdle(AgentActivityText.Text))
            {
                AgentActivityText.Text = _displayText.ActivityIdle;
            }
            ToolTipService.SetToolTip(AgentInsertOutputButton, getString("AgentInsertOutputTooltip", "Agent 응답을 현재 커서에 입력"));
            AgentInsertNewTabOutputButton.Content = getString("AgentInsertNewTabOutputButtonText", "새 탭에 입력");
            ToolTipService.SetToolTip(AgentInsertNewTabOutputButton, getString("AgentInsertNewTabOutputTooltip", "Agent 응답을 새 탭에 입력 (선택한 경우 선택부위만)"));
            ToolTipService.SetToolTip(AgentSelectedPresetScrollLeftButton, getString("AgentSelectedChipsScrollLeftTooltip", "선택 칩 왼쪽으로 스크롤"));
            ToolTipService.SetToolTip(AgentSelectedPresetScrollRightButton, getString("AgentSelectedChipsScrollRightTooltip", "선택 칩 오른쪽으로 스크롤"));

            AgentDiffApproveButton.Content = getString("AgentDiffApplyButton", "승인");
            AgentDiffCancelButton.Content = getString("AgentDiffCancelButton", "취소");
            AgentDiffConfirmHeader.Text = getString("AgentDiffConfirmHeaderDefault", "파일 변경 확인");
            AgentDiffConfirmDescription.Text = getString("AgentDiffConfirmDescriptionDefault", "파일을 수정하시겠습니까?");
            AgentModifiedFilesHeader.Text = getString("AgentModifiedFilesHeader", "변경됨 (클릭 시 비교)");
            AgentModifiedFilesDescription.Text = getString("AgentModifiedFilesDescription", "수정된 파일 목록입니다. 되돌리려면 우측 아이콘을 클릭하세요.");
            ToolTipService.SetToolTip(AgentModifiedFilesCloseButton, getString("AgentModifiedFilesCloseTooltip", "목록 닫기"));
            RebuildAgentMcpMenu();
            RebuildAgentSkillMenu();
            RebuildAgentPresetMenu();
            RebuildSelectedAgentPresetChips();
            RebuildOpenSessionMenu();
        }

        public void SetBusy(bool isBusy)
        {
            if (!isBusy)
            {
                FlushAllPendingOutputText();
            }

            _isBusy = isBusy;
            AgentRunButton.IsEnabled = true;
            AgentRunButton.Content = isBusy ? _stopButtonText : _runButtonText;
            AgentNewSessionButton.IsEnabled = true;
            UpdateRewindSessionButtonEnabled();
            AgentOpenSessionsButton.IsEnabled = true;
            AgentHistoryButton.IsEnabled = true;
            AgentDeleteHistoryButton.IsEnabled = !isBusy;
            AgentPromptInput.IsEnabled = !isBusy;
            AgentPlanningModeCheckBox.IsEnabled = !isBusy;
            AgentStreamToTabCheckBox.IsEnabled = !isBusy;
            AgentMcpButton.IsEnabled = !isBusy;
            AgentAddAttachmentButton.IsEnabled = !isBusy;
            AgentSkillButton.IsEnabled = !isBusy;
            AgentAttachmentsList.IsEnabled = !isBusy;
            AgentPresetButton.IsEnabled = !isBusy;
            AgentSelectedPresetScrollViewer.IsHitTestVisible = !isBusy;
            AgentSelectedPresetScrollViewer.Opacity = isBusy ? 0.65 : 1.0;
            RebuildHistoryMenu();

            if (!isBusy)
            {
                ScrollOutputToEnd(true);
            }
        }

        public void SetCanRewindSession(bool canRewind)
        {
            _canRewindSession = canRewind;
            UpdateRewindSessionButtonEnabled();
        }

        private void UpdateRewindSessionButtonEnabled()
        {
            if (AgentRewindSessionButton == null)
            {
                return;
            }

            AgentRewindSessionButton.IsEnabled = !_isBusy && _canRewindSession;
        }

        public void ClearActivity(string idleText)
        {
            AgentActivityText.Text = idleText;
        }

        public void AppendActivity(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            AppendOutputLine($"{timestamp}  {message}");
            if (string.IsNullOrWhiteSpace(AgentActivityText.Text) ||
                _displayText.IsActivityIdle(AgentActivityText.Text))
            {
                AgentActivityText.Text = $"{timestamp}  {message}";
            }
            else
            {
                AgentActivityText.Text += Environment.NewLine + $"{timestamp}  {message}";
            }

            AgentActivityText.SelectionStart = AgentActivityText.Text.Length;
            AgentActivityText.SelectionLength = 0;
        }

        public void AppendOutputText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            CompleteThinkingLine();
            ClearOutputPlaceholder();
            QueueOutputText(text);
        }

        public void AppendOutputLine(string line)
        {
            FlushAllPendingOutputText();
            CompleteThinkingLine();
            ClearOutputPlaceholder();

            string currentText = _rawOutputText;
            if (!string.IsNullOrEmpty(currentText) &&
                !EndsWithLineBreak(currentText))
            {
                AppendText(OutputLineBreak);
            }

            AppendText(line + OutputLineBreak);
            ScrollOutputToEnd();
        }

        public void BeginOutputBlock(string title)
        {
            FlushAllPendingOutputText();
            CompleteThinkingLine();
            ClearOutputPlaceholder();

            string currentText = _rawOutputText;
            if (!string.IsNullOrWhiteSpace(currentText))
            {
                if (!EndsWithBlankLine(currentText))
                {
                    AppendText(EndsWithLineBreak(currentText)
                        ? OutputLineBreak
                        : OutputLineBreak + OutputLineBreak);
                }
            }

            AppendText(title + OutputLineBreak);
            ScrollOutputToEnd(true);
        }

        public void BeginThinkingActivity(string label)
        {
            FlushAllPendingOutputText();
            CompleteThinkingLine();
            ClearOutputPlaceholder();

            string currentText = _rawOutputText;
            int lineBreakLength = 0;
            if (!string.IsNullOrEmpty(currentText) &&
                !EndsWithLineBreak(currentText))
            {
                AppendText(OutputLineBreak);
                lineBreakLength = OutputLineBreak.Length;
            }

            _thinkingLineTimestamp = DateTime.Now.ToString("HH:mm:ss");
            _thinkingLinePrefix = $"{_thinkingLineTimestamp}  {label}";
            _pendingThinkingLabel = null;
            _lastThinkingLabelRender = DateTimeOffset.Now;
            _thinkingLineStart = currentText.Length + lineBreakLength;
            _thinkingDotCount = 0;
            _thinkingLineActive = true;
            AppendText(_thinkingLinePrefix);
            ScrollOutputToEnd(true);

            _thinkingTimer ??= CreateThinkingTimer();
            _thinkingTimer.Start();
        }

        public void UpdateThinkingActivity(string label)
        {
            if (!_thinkingLineActive)
            {
                return;
            }

            _pendingThinkingLabel = label;
            DateTimeOffset now = DateTimeOffset.Now;
            if ((now - _lastThinkingLabelRender).TotalMilliseconds >= ThinkingLabelMinIntervalMs)
            {
                FlushPendingThinkingLabel(now);
                return;
            }

            _thinkingLabelFlushTimer ??= CreateThinkingLabelFlushTimer();
            if (!_thinkingLabelFlushTimer.IsEnabled)
            {
                _thinkingLabelFlushTimer.Start();
            }
        }

        public void StopThinkingActivity()
        {
            CompleteThinkingLine();
        }

        public void ResumeThinkingActivityFromOutput()
        {
            FlushAllPendingOutputText();
            ResetThinkingState();
            if (string.IsNullOrEmpty(_rawOutputText))
            {
                return;
            }

            int lineStart = FindLastLineStart(_rawOutputText);
            string line = _rawOutputText.Substring(lineStart).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            int trailingDotCount = CountTrailingThinkingDots(line);
            _thinkingLineStart = lineStart;
            _thinkingDotCount = trailingDotCount;
            _thinkingLinePrefix = trailingDotCount > 0
                ? line.Substring(0, line.Length - trailingDotCount)
                : line;
            _thinkingLineTimestamp = string.Empty;
            _thinkingLineActive = true;

            _thinkingTimer ??= CreateThinkingTimer();
            _thinkingTimer.Start();
        }

        private void ClearOutputPlaceholder()
        {
            string text = _rawOutputText;
            if (text.Length > 100)
            {
                return;
            }

            string trimmed = text.TrimStart();
            if (_displayText.IsOutputPlaceholder(trimmed))
            {
                _rawOutputText = string.Empty;
                UpdateRichText(_rawOutputText);
                _outputLength = 0;
            }
            else
            {
                _outputLength = text.Length;
            }
        }

        public void ResetOutput(string text)
        {
            ResetThinkingState();
            ClearPendingOutputText();
            ClearExplicitOutputSelection();
            _rawOutputText = text ?? string.Empty;
            UpdateRichText(_rawOutputText);
            _outputLength = _rawOutputText.Length;
        }

        public string GetRawOutputText()
        {
            FlushAllPendingOutputText();
            return _rawOutputText;
        }

        private void QueueOutputText(string text)
        {
            _pendingOutputText.Append(text);
            _outputFlushTimer ??= CreateOutputFlushTimer();

            if (!_outputFlushTimer.IsEnabled)
            {
                _outputFlushTimer.Start();
            }
        }

        private DispatcherTimer CreateOutputFlushTimer()
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            timer.Tick += (_, _) => FlushPendingOutputText();
            return timer;
        }

        private void FlushPendingOutputText()
        {
            if (_pendingOutputText.Length == 0)
            {
                _outputFlushTimer?.Stop();
                return;
            }

            int take = Math.Min(_pendingOutputText.Length, MaxOutputFlushChars);
            string text = _pendingOutputText.ToString(0, take);
            _pendingOutputText.Remove(0, take);

            AppendText(text);
            ScrollOutputToEnd();

            if (_pendingOutputText.Length == 0)
            {
                _outputFlushTimer?.Stop();
            }
        }

        private void FlushAllPendingOutputText()
        {
            while (_pendingOutputText.Length > 0)
            {
                FlushPendingOutputText();
            }
        }

        private void ClearPendingOutputText()
        {
            _pendingOutputText.Clear();
            _outputFlushTimer?.Stop();
        }

        private void ScrollOutputToEnd(bool force = false)
        {
            if (force)
            {
                _userScrolledUp = false;
            }

            int currentLength = _rawOutputText.Length;
            if (_outputLength < 0 || _outputLength > currentLength)
            {
                _outputLength = currentLength;
            }

            if (!_userScrolledUp)
            {
                if (force)
                {
                    ChangeOutputViewToEnd();
                }

                QueueOutputScrollToEnd();
            }
        }

        private void QueueOutputScrollToEnd()
        {
            if (_outputScrollQueued)
            {
                return;
            }

            _outputScrollQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _outputScrollQueued = false;
                ChangeOutputViewToEnd();
                QueueDeferredOutputScrollToEnd();
            });
        }

        private void QueueDeferredOutputScrollToEnd()
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                ChangeOutputViewToEnd);
        }

        private void ChangeOutputViewToEnd()
        {
            AgentOutputScrollViewer.ChangeView(null, double.MaxValue, null, true);
        }

        private void OnAgentOutputScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            double offset = AgentOutputScrollViewer.VerticalOffset;
            double maxOffset = AgentOutputScrollViewer.ScrollableHeight;

            if (offset < _lastVerticalOffset - 1.0)
            {
                _userScrolledUp = true;
            }
            else if (offset >= maxOffset - 5.0)
            {
                _userScrolledUp = false;
            }

            _lastVerticalOffset = offset;
        }

        private DispatcherTimer CreateThinkingTimer()
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(450)
            };
            timer.Tick += (_, _) =>
            {
                if (!_thinkingLineActive)
                {
                    timer.Stop();
                    return;
                }

                _thinkingDotCount = (_thinkingDotCount + 1) % 4;
                ReplaceThinkingLine(_thinkingLinePrefix + new string('.', _thinkingDotCount));
            };
            return timer;
        }

        private DispatcherTimer CreateThinkingLabelFlushTimer()
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ThinkingLabelMinIntervalMs)
            };
            timer.Tick += (_, _) =>
            {
                if (!_thinkingLineActive || string.IsNullOrEmpty(_pendingThinkingLabel))
                {
                    timer.Stop();
                    return;
                }

                FlushPendingThinkingLabel(DateTimeOffset.Now);
                timer.Stop();
            };
            return timer;
        }

        private void FlushPendingThinkingLabel(DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(_pendingThinkingLabel))
            {
                return;
            }

            _thinkingLinePrefix = $"{_thinkingLineTimestamp}  {_pendingThinkingLabel}";
            _pendingThinkingLabel = null;
            _lastThinkingLabelRender = now;
            ReplaceThinkingLine(_thinkingLinePrefix + new string('.', _thinkingDotCount));
        }

        private void CompleteThinkingLine()
        {
            if (!_thinkingLineActive)
            {
                return;
            }

            if (!string.IsNullOrEmpty(_pendingThinkingLabel))
            {
                _thinkingLinePrefix = $"{_thinkingLineTimestamp}  {_pendingThinkingLabel}";
                _pendingThinkingLabel = null;
            }

            _thinkingTimer?.Stop();
            _thinkingLabelFlushTimer?.Stop();
            ReplaceThinkingLine(_thinkingLinePrefix + new string('.', _thinkingDotCount));
            string currentText = _rawOutputText;
            if (!EndsWithLineBreak(currentText))
            {
                AppendText(OutputLineBreak);
            }

            _thinkingLineActive = false;
            ScrollOutputToEnd();
        }

        private void ResetThinkingState()
        {
            _thinkingTimer?.Stop();
            _thinkingLabelFlushTimer?.Stop();
            _thinkingLineActive = false;
            _thinkingLineStart = 0;
            _thinkingDotCount = 0;
            _thinkingLinePrefix = string.Empty;
            _thinkingLineTimestamp = string.Empty;
            _pendingThinkingLabel = null;
            _lastThinkingLabelRender = DateTimeOffset.MinValue;
        }

        private void ReplaceThinkingLine(string text)
        {
            int currentLength = _rawOutputText.Length;
            if (_thinkingLineStart < 0 || _thinkingLineStart > currentLength)
            {
                return;
            }

            _rawOutputText = _rawOutputText.Substring(0, _thinkingLineStart) + text;
            _outputLength = _thinkingLineStart + text.Length;
            if (_renderedLines.Count > 0)
            {
                SetRenderedLine(_renderedLines.Count - 1, text);
            }
            else
            {
                UpdateRichText(_rawOutputText);
            }
            ScrollOutputToEnd();
        }

        private static int FindLastLineStart(string text)
        {
            int lastLf = text.LastIndexOf('\n');
            if (lastLf >= 0)
            {
                return lastLf + 1;
            }

            int lastCr = text.LastIndexOf('\r');
            return lastCr >= 0 ? lastCr + 1 : 0;
        }

        private static int CountTrailingThinkingDots(string line)
        {
            int count = 0;
            for (int i = line.Length - 1; i >= 0 && line[i] == '.' && count < 3; i--)
            {
                count++;
            }

            return count;
        }

        private static bool EndsWithLineBreak(string text)
        {
            return text.EndsWith("\r\n", StringComparison.Ordinal) ||
                   text.EndsWith("\n", StringComparison.Ordinal) ||
                   text.EndsWith("\r", StringComparison.Ordinal);
        }

        private static bool EndsWithBlankLine(string text)
        {
            return text.EndsWith("\r\n\r\n", StringComparison.Ordinal) ||
                   text.EndsWith("\n\n", StringComparison.Ordinal);
        }

        private static string CollapseExcessBlankLinesForAppend(string existingText, string text)
        {
            string normalized = NormalizeOutputLineBreaks(text);
            normalized = CollapseLineBreakRuns(normalized);

            int trailingBreaks = CountTrailingLineBreaks(NormalizeOutputLineBreaks(existingText));
            int leadingBreaks = CountLeadingLineBreaks(normalized);
            int allowedLeadingBreaks = Math.Max(0, 2 - trailingBreaks);
            if (leadingBreaks > allowedLeadingBreaks)
            {
                normalized = normalized.Substring(leadingBreaks - allowedLeadingBreaks);
            }

            return normalized.Replace("\n", OutputLineBreak, StringComparison.Ordinal);
        }

        private static string NormalizeOutputLineBreaks(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }

        private static string CollapseLineBreakRuns(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            int lineBreakRun = 0;
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    if (lineBreakRun < 2)
                    {
                        builder.Append(ch);
                    }

                    lineBreakRun++;
                }
                else
                {
                    builder.Append(ch);
                    lineBreakRun = 0;
                }
            }

            return builder.ToString();
        }

        private static int CountLeadingLineBreaks(string text)
        {
            int count = 0;
            while (count < text.Length && text[count] == '\n')
            {
                count++;
            }

            return count;
        }

        private static int CountTrailingLineBreaks(string text)
        {
            int count = 0;
            for (int i = text.Length - 1; i >= 0 && text[i] == '\n'; i--)
            {
                count++;
            }

            return count;
        }

        private bool IsControlDown()
        {
            return IsKeyDown(VirtualKey.Control) ||
                   IsKeyDown(VirtualKey.LeftControl) ||
                   IsKeyDown(VirtualKey.RightControl);
        }

        private static bool IsKeyDown(VirtualKey key)
        {
            return (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) &
                    Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }

        private void RequestRunFromPrompt()
        {
            if (_isBusy)
            {
                return;
            }

            RunRequested?.Invoke(AgentPromptInput, new RoutedEventArgs());
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                StopRequested?.Invoke(sender, e);
                return;
            }

            RunRequested?.Invoke(sender, e);
        }

        private void OnNewSessionClick(object sender, RoutedEventArgs e)
        {
            NewSessionRequested?.Invoke(sender, e);
        }

        private void OnRewindSessionClick(object sender, RoutedEventArgs e)
        {
            RewindSessionRequested?.Invoke(sender, e);
        }

        private void OnClearPromptClick(object sender, RoutedEventArgs e)
        {
            AgentPromptInput.Text = string.Empty;
            AgentPromptInput.Focus(FocusState.Programmatic);
        }

        private void OnPromptTextChanged(object sender, TextChangedEventArgs e)
        {
            AgentClearPromptButton.Visibility = string.IsNullOrEmpty(AgentPromptInput.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void OnAgentHistoryFlyoutOpened(object sender, object e)
        {
            RebuildHistoryMenu();
        }

        private void OnAgentOpenSessionsFlyoutOpened(object sender, object e)
        {
            OpenSessionsFlyoutOpened?.Invoke(this, EventArgs.Empty);
            RebuildOpenSessionMenu();
        }

        private void OnDeleteHistoryClick(object sender, RoutedEventArgs e)
        {
            HistoryToolbarDeleteClicked?.Invoke(this, e);
        }

        private void OnInsertOutputClick(object sender, RoutedEventArgs e)
        {
            InsertOutputRequested?.Invoke(sender, e);
        }

        private void OnInsertNewTabOutputClick(object sender, RoutedEventArgs e)
        {
            InsertNewTabOutputRequested?.Invoke(sender, e);
        }

        private void OnOutputPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _outputPointerDownPoint = e.GetCurrentPoint(AgentOutputText).Position;
            _outputPointerSelectionGesture = false;
        }

        private void OnOutputPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_outputPointerDownPoint == null)
            {
                return;
            }

            var currentPoint = e.GetCurrentPoint(AgentOutputText);
            if (!currentPoint.Properties.IsLeftButtonPressed)
            {
                return;
            }

            var startPoint = _outputPointerDownPoint.Value;
            double dx = currentPoint.Position.X - startPoint.X;
            double dy = currentPoint.Position.Y - startPoint.Y;
            if ((dx * dx) + (dy * dy) > 16)
            {
                _outputPointerSelectionGesture = true;
            }
        }

        private void OnOutputPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_outputPointerSelectionGesture)
            {
                _hasExplicitOutputSelection = true;
                QueueCaptureExplicitOutputSelection();
            }
            else
            {
                ClearExplicitOutputSelection();
            }

            _outputPointerDownPoint = null;
            _outputPointerSelectionGesture = false;
        }

        private void OnOutputDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            _hasExplicitOutputSelection = true;
            QueueCaptureExplicitOutputSelection();
        }

        private void QueueCaptureExplicitOutputSelection()
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                CaptureExplicitOutputSelection);
        }

        private void CaptureExplicitOutputSelection()
        {
            if (!_hasExplicitOutputSelection)
            {
                _explicitSelectedOutputText = string.Empty;
                return;
            }

            string selectedText = AgentOutputText.SelectedText;
            _explicitSelectedOutputText = IsTextFromOutput(selectedText)
                ? selectedText
                : string.Empty;
        }

        private void ClearExplicitOutputSelection()
        {
            _explicitSelectedOutputText = string.Empty;
            _hasExplicitOutputSelection = false;
            _outputPointerDownPoint = null;
            _outputPointerSelectionGesture = false;
        }

        private bool IsTextFromOutput(string selectedText)
        {
            if (string.IsNullOrEmpty(selectedText) || string.IsNullOrEmpty(_rawOutputText))
            {
                return false;
            }

            if (_rawOutputText.Contains(selectedText, StringComparison.Ordinal))
            {
                return true;
            }

            string normalizedSelected = NormalizeLineEndings(selectedText);
            string normalizedOutput = NormalizeLineEndings(_rawOutputText);
            return normalizedSelected.Length > 0 &&
                normalizedOutput.Contains(normalizedSelected, StringComparison.Ordinal);
        }

        private static string NormalizeLineEndings(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
        }

        private void OnAddAttachmentClick(object sender, RoutedEventArgs e)
        {
            AddAttachmentRequested?.Invoke(sender, e);
        }

        private void OnAgentAddPresetClickInPanel(object sender, RoutedEventArgs e)
        {
            AgentPresetAddRequested?.Invoke(sender, e);
            AgentPresetFlyout.Hide();
        }

        private void OnAgentExportPresetClickInPanel(object sender, RoutedEventArgs e)
        {
            AgentPresetExportRequested?.Invoke(sender, e);
            AgentPresetFlyout.Hide();
        }

        private void OnAgentImportPresetClickInPanel(object sender, RoutedEventArgs e)
        {
            AgentPresetImportRequested?.Invoke(sender, e);
            AgentPresetFlyout.Hide();
        }

        private void OnAgentAddMcpClickInPanel(object sender, RoutedEventArgs e)
        {
            AgentMcpAddRequested?.Invoke(sender, e);
            AgentMcpFlyout.Hide();
        }

        private void OnAgentExportMcpClickInPanel(object sender, RoutedEventArgs e)
        {
            AgentMcpExportRequested?.Invoke(sender, e);
            AgentMcpFlyout.Hide();
        }

        private void OnAgentImportMcpClickInPanel(object sender, RoutedEventArgs e)
        {
            AgentMcpImportRequested?.Invoke(sender, e);
            AgentMcpFlyout.Hide();
        }

        private void OnAgentMcpFlyoutOpened(object sender, object e)
        {
            AgentMcpFlyoutOpened?.Invoke(this, EventArgs.Empty);
            RebuildAgentMcpMenu();
        }

        private void OnAgentPresetFlyoutOpened(object sender, object e)
        {
            RebuildAgentPresetMenu();
        }

        private void OnAgentSkillFlyoutOpened(object sender, object e)
        {
            AgentSkillFlyoutOpened?.Invoke(this, EventArgs.Empty);
            RebuildAgentSkillMenu();
        }

        private void OnAgentSkillRefreshClick(object sender, RoutedEventArgs e)
        {
            AgentSkillRefreshRequested?.Invoke(this, EventArgs.Empty);
            AgentSkillFlyout.Hide();
        }

        private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is AgentAttachmentItem item)
            {
                RemoveAttachmentRequested?.Invoke(this, item);
            }
        }

        private void OnPromptInputKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            if (!IsControlDown())
            {
                return;
            }

            e.Handled = true;
            RequestRunFromPrompt();
        }

        private void OnPromptRunKeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            RequestRunFromPrompt();
        }

        private void OnDiffApproveClick(object sender, RoutedEventArgs e)
        {
            DiffApproved?.Invoke(sender, e);
        }

        private void OnModelNameClick(object sender, RoutedEventArgs e)
        {
            ModelNameClick?.Invoke(sender, e);
        }

        private void OnDiffCancelClick(object sender, RoutedEventArgs e)
        {
            DiffCancelled?.Invoke(sender, e);
        }

        private void OnOutputTextKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.C && IsControlDown())
            {
                _hasExplicitOutputSelection = true;
                CaptureExplicitOutputSelection();
                string textToCopy = _explicitSelectedOutputText;
                if (string.IsNullOrEmpty(textToCopy))
                {
                    textToCopy = _rawOutputText;
                }

                if (!string.IsNullOrEmpty(textToCopy))
                {
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.SetText(textToCopy);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                    Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                    e.Handled = true;
                }
            }
        }

        private void UpdateRichText(string rawText)
        {
            string normalized = (rawText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');

            List<string> displayLines = new List<string>();
            if (HideHtmlCodeBlocks)
            {
                bool inHtmlBlock = false;
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (!inHtmlBlock)
                    {
                        if (trimmed.StartsWith("```html", StringComparison.OrdinalIgnoreCase))
                        {
                            inHtmlBlock = true;
                            displayLines.Add(_getString?.Invoke("AgentHtmlCodeBlockHidden", "[HTML 코드 블록 숨겨짐 (상세 출력 비활성화)]") ?? "[HTML 코드 블록 숨겨짐 (상세 출력 비활성화)]");
                        }
                        else
                        {
                            displayLines.Add(line);
                        }
                    }
                    else
                    {
                        if (trimmed.StartsWith("```", StringComparison.Ordinal))
                        {
                            inHtmlBlock = false;
                        }
                    }
                }
            }
            else
            {
                displayLines.AddRange(lines);
            }

            // Adjust the number of blocks in AgentOutputText and cache to match displayLines.Count
            while (AgentOutputText.Blocks.Count > displayLines.Count)
            {
                AgentOutputText.Blocks.RemoveAt(AgentOutputText.Blocks.Count - 1);
            }
            while (_renderedLines.Count > displayLines.Count)
            {
                _renderedLines.RemoveAt(_renderedLines.Count - 1);
            }

            while (AgentOutputText.Blocks.Count < displayLines.Count)
            {
                AgentOutputText.Blocks.Add(new Paragraph());
            }
            while (_renderedLines.Count < displayLines.Count)
            {
                _renderedLines.Add(null!);
            }

            // Update only the changed lines
            for (int k = 0; k < displayLines.Count; k++)
            {
                string line = displayLines[k];
                if (_renderedLines[k] != line)
                {
                    if (AgentOutputText.Blocks[k] is Paragraph paragraph)
                    {
                        paragraph.Inlines.Clear();
                        ParseLineToInlines(line, paragraph.Inlines);
                    }
                    _renderedLines[k] = line;
                }
            }
        }

        private void AppendRenderedText(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] parts = normalized.Split('\n');

            EnsureRenderedLineExists();

            int lastIndex = _renderedLines.Count - 1;
            SetRenderedLine(lastIndex, _renderedLines[lastIndex] + parts[0]);

            for (int i = 1; i < parts.Length; i++)
            {
                AddRenderedLine(parts[i]);
            }
        }

        private void EnsureRenderedLineExists()
        {
            if (_renderedLines.Count > 0 && AgentOutputText.Blocks.Count > 0)
            {
                return;
            }

            _renderedLines.Clear();
            AgentOutputText.Blocks.Clear();
            AddRenderedLine(string.Empty);
        }

        private void AddRenderedLine(string line)
        {
            var paragraph = new Paragraph();
            AgentOutputText.Blocks.Add(paragraph);
            _renderedLines.Add(string.Empty);
            SetRenderedLine(_renderedLines.Count - 1, line);
        }

        private void SetRenderedLine(int index, string line)
        {
            if (index < 0)
            {
                return;
            }

            while (AgentOutputText.Blocks.Count <= index)
            {
                AgentOutputText.Blocks.Add(new Paragraph());
            }
            while (_renderedLines.Count <= index)
            {
                _renderedLines.Add(string.Empty);
            }

            if (_renderedLines[index] == line)
            {
                return;
            }

            if (AgentOutputText.Blocks[index] is Paragraph paragraph)
            {
                paragraph.Inlines.Clear();
                ParseLineToInlines(line, paragraph.Inlines);
            }
            _renderedLines[index] = line;
        }

        private void ParseLineToInlines(string line, InlineCollection inlines)
        {
            if (string.IsNullOrEmpty(line))
            {
                inlines.Add(new Run { Text = string.Empty });
                return;
            }

            bool isHeading = TryParseMarkdownHeading(line, out int headingLevel, out string displayLine);
            double headingFontSize = GetMarkdownHeadingFontSize(headingLevel);
            line = displayLine;

            if (string.IsNullOrEmpty(line))
            {
                inlines.Add(CreateTextRun(string.Empty, isHeading, headingFontSize));
                return;
            }

            int i = 0;
            bool isBold = false;
            bool isCode = false;
            var currentSegment = new StringBuilder();

            void FlushCurrentSegment()
            {
                if (currentSegment.Length == 0)
                {
                    return;
                }

                string text = currentSegment.ToString();
                currentSegment.Clear();

                if (isCode)
                {
                    var container = new InlineUIContainer();
                    Brush bgBrush = GetBrushResource("AgentCodeBackground", Microsoft.UI.Colors.LightGray);
                    Brush fgBrush = GetBrushResource("AgentCodeForeground", Microsoft.UI.Colors.Black);

                    var border = new Border
                    {
                        Background = bgBrush,
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(2, 2.5, 2, -2.5)
                    };

                    var textBlock = new TextBlock
                    {
                        Text = text,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = isHeading ? headingFontSize : 12,
                        FontWeight = isHeading
                            ? Microsoft.UI.Text.FontWeights.Bold
                            : Microsoft.UI.Text.FontWeights.Normal,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    textBlock.Foreground = fgBrush;

                    border.Child = textBlock;
                    container.Child = border;
                    inlines.Add(container);
                }
                else
                {
                    var run = CreateTextRun(text, isHeading || isBold, headingFontSize);
                    inlines.Add(run);
                }
            }

            while (i < line.Length)
            {
                if (isCode)
                {
                    if (line[i] == '`')
                    {
                        FlushCurrentSegment();
                        isCode = false;
                        i++;
                    }
                    else
                    {
                        currentSegment.Append(line[i]);
                        i++;
                    }
                }
                else
                {
                    if (line[i] == '`')
                    {
                        if (line.IndexOf('`', i + 1) >= 0)
                        {
                            FlushCurrentSegment();
                            isCode = true;
                            i++;
                        }
                        else
                        {
                            currentSegment.Append(line[i]);
                            i++;
                        }
                    }
                    else if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
                    {
                        if (isBold)
                        {
                            FlushCurrentSegment();
                            isBold = false;
                            i += 2;
                        }
                        else if (line.IndexOf("**", i + 2, StringComparison.Ordinal) >= 0)
                        {
                            FlushCurrentSegment();
                            isBold = true;
                            i += 2;
                        }
                        else
                        {
                            currentSegment.Append("**");
                            i += 2;
                        }
                    }
                    else
                    {
                        currentSegment.Append(line[i]);
                        i++;
                    }
                }
            }

            FlushCurrentSegment();
        }

        private static Run CreateTextRun(string text, bool isBold, double fontSize)
        {
            var run = new Run { Text = text };
            if (isBold)
            {
                run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            }

            if (fontSize > 0)
            {
                run.FontSize = fontSize;
            }

            return run;
        }

        private static bool TryParseMarkdownHeading(string line, out int level, out string displayLine)
        {
            level = 0;
            displayLine = line;

            int hashCount = 0;
            while (hashCount < line.Length && hashCount < 6 && line[hashCount] == '#')
            {
                hashCount++;
            }

            if (hashCount == 0 ||
                hashCount >= line.Length ||
                (line[hashCount] != ' ' && line[hashCount] != '\t'))
            {
                return false;
            }

            level = hashCount;
            displayLine = line.Substring(hashCount).TrimStart(' ', '\t');
            return true;
        }

        private static double GetMarkdownHeadingFontSize(int level)
        {
            return level switch
            {
                1 => 17,
                2 => 16,
                3 => 15,
                >= 4 and <= 6 => 14,
                _ => 0
            };
        }

        private Brush GetBrushResource(string key, Windows.UI.Color fallbackColor)
        {
            if (Resources.TryGetValue(key, out object resource) && resource is Brush localBrush)
            {
                return localBrush;
            }

            if (Application.Current.Resources.TryGetValue(key, out resource) && resource is Brush appBrush)
            {
                return appBrush;
            }

            return new SolidColorBrush(fallbackColor);
        }

        public void UpdateAgentPresetsMenu(
            IReadOnlyList<string> presetNames,
            IReadOnlyCollection<string> selectedPresetNames,
            Func<string, string, string> getString)
        {
            _getString = getString;
            _agentPresetNames = new List<string>(presetNames);
            _selectedAgentPresetNames = new HashSet<string>(selectedPresetNames, StringComparer.OrdinalIgnoreCase);
            RebuildAgentPresetMenu();
            RebuildSelectedAgentPresetChips();
        }

        public void UpdateAgentSkillsMenu(
            IReadOnlyList<AgentSkillItem> skills,
            IReadOnlyCollection<string> selectedSkillNames,
            Func<string, string, string> getString)
        {
            _getString = getString;
            _agentSkillItems = new List<AgentSkillItem>(skills);
            _selectedAgentSkillNames = new HashSet<string>(selectedSkillNames, StringComparer.OrdinalIgnoreCase);
            RebuildAgentSkillMenu();
            RebuildSelectedAgentPresetChips();
        }

        public void UpdateAgentSkillSelection(
            IReadOnlyCollection<string> selectedSkillNames,
            Func<string, string, string> getString)
        {
            _getString = getString;
            _selectedAgentSkillNames = new HashSet<string>(selectedSkillNames, StringComparer.OrdinalIgnoreCase);
            UpdateAgentSkillButtonStates();
            RebuildSelectedAgentPresetChips();
        }

        public void UpdateAgentMcpMenu(
            IReadOnlyList<AgentMcpItem> mcpItems,
            IReadOnlyCollection<string> selectedMcpNames,
            Func<string, string, string> getString)
        {
            _getString = getString;
            _agentMcpItems = new List<AgentMcpItem>(mcpItems);
            _selectedAgentMcpNames = new HashSet<string>(selectedMcpNames, StringComparer.OrdinalIgnoreCase);
            RebuildAgentMcpMenu();
            RebuildSelectedAgentPresetChips();
        }

        private void RebuildAgentMcpMenu()
        {
            if (AgentMcpListPanel == null)
            {
                return;
            }

            AgentMcpListPanel.Children.Clear();
            Style? buttonStyle = Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = _getString ?? _displayText.GetString;

            if (_agentMcpItems.Count == 0)
            {
                AgentMcpListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentMcpEmptyText", "등록된 MCP 없음"),
                    FontSize = 11,
                    Foreground = CreateMcpEmptyTextBrush(),
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            foreach (var item in _agentMcpItems)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                bool isSelected = _selectedAgentMcpNames.Contains(item.Name);
                var selectBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    MinHeight = 34,
                    Padding = new Thickness(8, 3, 8, 3),
                    Style = buttonStyle
                };

                var textStack = new StackPanel { Spacing = 1 };
                textStack.Children.Add(new TextBlock
                {
                    Text = isSelected ? $"✓ {item.Name}" : item.Name,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                });
                textStack.Children.Add(new TextBlock
                {
                    Text = item.Detail,
                    FontSize = 10,
                    Foreground = CreateMcpSecondaryTextBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                });
                selectBtn.Content = textStack;

                string currentName = item.Name;
                selectBtn.Click += (_, _) => AgentMcpToggled?.Invoke(this, currentName);
                Grid.SetColumn(selectBtn, 0);
                rowGrid.Children.Add(selectBtn);

                if (!item.CanEdit && !item.CanDelete)
                {
                    var lockIcon = new FontIcon
                    {
                        Glyph = "\uE72E",
                        FontSize = 10,
                        Foreground = CreateMcpSecondaryTextBrush(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    ToolTipService.SetToolTip(lockIcon, getString("AgentMcpBuiltInLockedTooltip", "내장 플러그인은 수정하거나 삭제할 수 없습니다."));
                    Grid.SetColumn(lockIcon, 1);
                    rowGrid.Children.Add(lockIcon);
                    AgentMcpListPanel.Children.Add(rowGrid);
                    continue;
                }

                var editBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70F", FontSize = 10 },
                    Width = 28,
                    Height = 34,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(editBtn, getString("AgentMcpEditText", "수정"));
                editBtn.IsEnabled = item.CanEdit;
                editBtn.Click += (_, _) =>
                {
                    AgentMcpEdited?.Invoke(this, currentName);
                    AgentMcpFlyout.Hide();
                };
                Grid.SetColumn(editBtn, 1);
                rowGrid.Children.Add(editBtn);

                var deleteBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 34,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(deleteBtn, getString("AgentMcpDeleteText", "삭제"));
                deleteBtn.IsEnabled = item.CanDelete;
                deleteBtn.Click += (_, _) => AgentMcpDeleted?.Invoke(this, currentName);
                Grid.SetColumn(deleteBtn, 2);
                rowGrid.Children.Add(deleteBtn);

                AgentMcpListPanel.Children.Add(rowGrid);
            }
        }

        private Brush CreateMcpEmptyTextBrush()
        {
            return CreateMcpSecondaryTextBrush();
        }

        private Brush CreateMcpSecondaryTextBrush()
        {
            bool isLightTheme = ActualTheme == ElementTheme.Light ||
                (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Light);

            return new SolidColorBrush(isLightTheme
                ? Windows.UI.Color.FromArgb(255, 75, 85, 99)
                : Windows.UI.Color.FromArgb(255, 229, 231, 235));
        }

        private void RebuildAgentSkillMenu()
        {
            if (AgentSkillListPanel == null)
            {
                return;
            }

            AgentSkillListPanel.Children.Clear();
            _agentSkillButtons.Clear();
            Style? buttonStyle = Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = _getString ?? _displayText.GetString;

            if (_agentSkillItems.Count == 0)
            {
                AgentSkillListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentSkillEmptyText", "설치된 스킬 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            foreach (var skill in _agentSkillItems)
            {
                bool isSelected = _selectedAgentSkillNames.Contains(skill.Name);
                var selectBtn = new Button
                {
                    Content = isSelected ? $"✓ {skill.Name}" : skill.Name,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                string currentName = skill.Name;
                selectBtn.Click += (_, _) => AgentSkillToggled?.Invoke(this, currentName);
                _agentSkillButtons[currentName] = selectBtn;
                AgentSkillListPanel.Children.Add(selectBtn);
            }
        }

        private void UpdateAgentSkillButtonStates()
        {
            foreach (var pair in _agentSkillButtons)
            {
                bool isSelected = _selectedAgentSkillNames.Contains(pair.Key);
                pair.Value.Content = isSelected ? $"✓ {pair.Key}" : pair.Key;
            }
        }

        private void RebuildAgentPresetMenu()
        {
            if (AgentPresetListPanel == null)
            {
                return;
            }

            AgentPresetListPanel.Children.Clear();
            Style? buttonStyle = Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = _getString ?? _displayText.GetString;

            if (_agentPresetNames.Count == 0)
            {
                AgentPresetListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentPresetEmptyText", "프리셋 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            foreach (string presetName in _agentPresetNames)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                bool isSelected = _selectedAgentPresetNames.Contains(presetName);
                var selectBtn = new Button
                {
                    Content = isSelected ? $"✓ {presetName}" : presetName,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                string currentName = presetName;
                selectBtn.Click += (_, _) => AgentPresetToggled?.Invoke(this, currentName);
                Grid.SetColumn(selectBtn, 0);
                rowGrid.Children.Add(selectBtn);

                var editBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70F", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(editBtn, getString("AgentPresetEditText", "수정"));
                editBtn.Click += (_, _) =>
                {
                    AgentPresetEdited?.Invoke(this, currentName);
                    AgentPresetFlyout.Hide();
                };
                Grid.SetColumn(editBtn, 1);
                rowGrid.Children.Add(editBtn);

                var deleteBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(deleteBtn, getString("AgentPresetDeleteText", "삭제"));
                deleteBtn.Click += (_, _) => AgentPresetDeleted?.Invoke(this, currentName);
                Grid.SetColumn(deleteBtn, 2);
                rowGrid.Children.Add(deleteBtn);

                AgentPresetListPanel.Children.Add(rowGrid);
            }
        }

        private void RebuildSelectedAgentPresetChips()
        {
            if (AgentSelectedPresetPanel == null)
            {
                return;
            }

            AgentSelectedPresetPanel.Children.Clear();
            Func<string, string, string> getString = _getString ?? _displayText.GetString;

            foreach (string presetName in _agentPresetNames)
            {
                if (!_selectedAgentPresetNames.Contains(presetName))
                {
                    continue;
                }

                AgentSelectedPresetPanel.Children.Add(CreateSelectedChip(
                    presetName,
                    getString("AgentPresetRemoveTooltip", "선택 해제"),
                    () => AgentPresetRemoved?.Invoke(this, presetName)));
            }

            string mcpPrefix = getString("AgentMcpChipPrefix", "MCP: ");
            foreach (var mcp in _agentMcpItems)
            {
                if (!_selectedAgentMcpNames.Contains(mcp.Name))
                {
                    continue;
                }

                string currentName = mcp.Name;
                AgentSelectedPresetPanel.Children.Add(CreateSelectedChip(
                    mcpPrefix + mcp.Name,
                    getString("AgentMcpRemoveTooltip", "MCP 선택 해제"),
                    () => AgentMcpRemoved?.Invoke(this, currentName)));
            }

            string skillPrefix = getString("AgentSkillChipPrefix", "Skill: ");
            var selectedSkillNames = new List<string>(_selectedAgentSkillNames);
            selectedSkillNames.Sort(StringComparer.CurrentCultureIgnoreCase);
            foreach (string skillName in selectedSkillNames)
            {
                string currentName = skillName;
                AgentSelectedPresetPanel.Children.Add(CreateSelectedChip(
                    skillPrefix + skillName,
                    getString("AgentSkillRemoveTooltip", "스킬 선택 해제"),
                    () => AgentSkillRemoved?.Invoke(this, currentName)));
            }

            AgentSelectedPresetScrollViewer.Visibility =
                AgentSelectedPresetPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            QueueSelectedChipScrollButtonsUpdate();
        }

        private void OnSelectedPresetScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            UpdateSelectedChipScrollButtons();
        }

        private void OnSelectedPresetScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            QueueSelectedChipScrollButtonsUpdate();
        }

        private void OnSelectedPresetPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            QueueSelectedChipScrollButtonsUpdate();
        }

        private void OnSelectedPresetScrollLeftClick(object sender, RoutedEventArgs e)
        {
            ScrollSelectedChips(-SelectedChipScrollStep);
        }

        private void OnSelectedPresetScrollRightClick(object sender, RoutedEventArgs e)
        {
            ScrollSelectedChips(SelectedChipScrollStep);
        }

        private void ScrollSelectedChips(double delta)
        {
            if (AgentSelectedPresetScrollViewer.Visibility != Visibility.Visible)
            {
                return;
            }

            double targetOffset = Math.Clamp(
                AgentSelectedPresetScrollViewer.HorizontalOffset + delta,
                0,
                AgentSelectedPresetScrollViewer.ScrollableWidth);
            AgentSelectedPresetScrollViewer.ChangeView(targetOffset, null, null, false);
            UpdateSelectedChipScrollButtons();
        }

        private void QueueSelectedChipScrollButtonsUpdate()
        {
            if (DispatcherQueue?.TryEnqueue(UpdateSelectedChipScrollButtons) == true)
            {
                return;
            }

            UpdateSelectedChipScrollButtons();
        }

        private void UpdateSelectedChipScrollButtons()
        {
            bool hasOverflow =
                AgentSelectedPresetScrollViewer.Visibility == Visibility.Visible &&
                AgentSelectedPresetPanel.Children.Count > 0 &&
                AgentSelectedPresetScrollViewer.ScrollableWidth > 0.5;

            AgentSelectedPresetScrollButtons.Visibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
            if (!hasOverflow)
            {
                return;
            }

            AgentSelectedPresetScrollLeftButton.IsEnabled = AgentSelectedPresetScrollViewer.HorizontalOffset > 0.5;
            AgentSelectedPresetScrollRightButton.IsEnabled =
                AgentSelectedPresetScrollViewer.HorizontalOffset < AgentSelectedPresetScrollViewer.ScrollableWidth - 0.5;
        }

        private Border CreateSelectedChip(string text, string tooltip, Action removeAction)
        {
            Style? buttonStyle = Resources["AgentButtonStyle"] as Style;
            var chip = new Border
            {
                Background = (Brush)Application.Current.Resources["SystemControlBackgroundAltMediumLowBrush"],
                BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 2, 2)
            };

            var chipContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            chipContent.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            var removeBtn = new Button
            {
                Content = "x",
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                FontSize = 10,
                Style = buttonStyle
            };
            ToolTipService.SetToolTip(removeBtn, tooltip);
            removeBtn.Click += (_, _) => removeAction();
            chipContent.Children.Add(removeBtn);

            chip.Child = chipContent;
            return chip;
        }

        public void ShowDiffConfirm(string header, string description)
        {
            AgentDiffConfirmHeader.Text = header;
            AgentDiffConfirmDescription.Text = description;
            AgentPowerShellConfirmCommand.Text = string.Empty;
            AgentPowerShellCommandPanel.Visibility = Visibility.Collapsed;
            AgentDiffConfirmPanel.Visibility = Visibility.Visible;
            UpdateReviewPanelsHostVisibility();
        }

        public void ShowPowerShellConfirm(string header, string description, string command)
        {
            AgentDiffConfirmHeader.Text = header;
            AgentDiffConfirmDescription.Text = description;
            AgentPowerShellConfirmCommand.Text = command;
            AgentPowerShellCommandPanel.Visibility = Visibility.Visible;
            AgentDiffConfirmPanel.Visibility = Visibility.Visible;
            UpdateReviewPanelsHostVisibility();
        }

        public void HideDiffConfirm()
        {
            AgentDiffConfirmPanel.Visibility = Visibility.Collapsed;
            UpdateReviewPanelsHostVisibility();
        }

        public void UpdateModifiedFiles(IReadOnlyList<AgentFileEditPreview> edits)
        {
            AgentModifiedFilesList.ItemsSource = edits;
            AgentModifiedFilesPanel.Visibility = edits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateReviewPanelsHostVisibility();
        }

        public void UpdateAttachments(IReadOnlyList<AgentAttachmentItem> attachments)
        {
            AgentAttachmentsList.ItemsSource = attachments;
            AgentAttachmentsList.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnModifiedFilesListItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is AgentFileEditPreview preview)
            {
                AgentModifiedFilesList.SelectedItem = preview;
                FileDiffRequested?.Invoke(this, preview);
            }
        }

        private void OnRevertFileClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AgentFileEditPreview preview)
            {
                FileRevertRequested?.Invoke(this, preview);
            }
        }

        private void OnModifiedFilesCloseClick(object sender, RoutedEventArgs e)
        {
            AgentModifiedFilesPanel.Visibility = Visibility.Collapsed;
            UpdateReviewPanelsHostVisibility();
        }

        private void UpdateReviewPanelsHostVisibility()
        {
            bool hasVisiblePanel =
                AgentDiffConfirmPanel.Visibility == Visibility.Visible ||
                AgentModifiedFilesPanel.Visibility == Visibility.Visible;
            AgentReviewPanelsHost.Visibility = hasVisiblePanel ? Visibility.Visible : Visibility.Collapsed;
        }

        public void UpdateModelName(string text)
        {
            AgentModelNameText.Text = text;
        }

        public void UpdateHistoryItems(List<AgentHistoryItemViewModel> items, string? selectedId)
        {
            _historyItems = items;
            _selectedHistoryId = selectedId;
            RebuildHistoryMenu();
        }

        public void UpdateOpenSessionItems(List<AgentOpenSessionItemViewModel> items, string? selectedId)
        {
            _openSessionItems = items;
            _selectedOpenSessionId = selectedId;
            UpdateOpenSessionButtonChrome();
            RebuildOpenSessionMenu();
        }

        private void UpdateOpenSessionButtonChrome()
        {
            bool showSessionList = _openSessionItems.Count > 1;
            AgentOpenSessionsButton.Visibility = showSessionList ? Visibility.Visible : Visibility.Collapsed;
            AgentNewSessionButton.CornerRadius = showSessionList
                ? new CornerRadius(4, 0, 0, 4)
                : new CornerRadius(4);
            AgentOpenSessionsButton.CornerRadius = new CornerRadius(0, 4, 4, 0);
            AgentRewindSessionButton.CornerRadius = new CornerRadius(4);
        }

        private void RebuildOpenSessionMenu()
        {
            if (AgentOpenSessionsListPanel == null)
            {
                return;
            }

            UpdateOpenSessionButtonChrome();
            AgentOpenSessionsListPanel.Children.Clear();
            Style? buttonStyle = Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = _getString ?? _displayText.GetString;

            if (_openSessionItems.Count == 0)
            {
                AgentOpenSessionsListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentOpenSessionsEmptyText", "열린 세션 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            foreach (var item in _openSessionItems)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var titlePanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };

                titlePanel.Children.Add(new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(item.IsRunning
                        ? Windows.UI.Color.FromArgb(255, 34, 197, 94)
                        : Windows.UI.Color.FromArgb(255, 156, 163, 175)),
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (item.IsSelected)
                {
                    titlePanel.Children.Add(new FontIcon
                    {
                        Glyph = "\uE73E",
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                titlePanel.Children.Add(new TextBlock
                {
                    Text = item.Title,
                    FontSize = 11,
                    MaxWidth = 190,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var selectBtn = new Button
                {
                    Content = titlePanel,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle,
                    IsEnabled = item.CanSelect
                };
                if (item.IsSelected)
                {
                    selectBtn.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }

                string currentId = item.Id;
                selectBtn.Click += (_, _) =>
                {
                    OpenSessionSelected?.Invoke(this, currentId);
                    AgentOpenSessionsFlyout.Hide();
                };
                Grid.SetColumn(selectBtn, 0);
                rowGrid.Children.Add(selectBtn);

                var closeBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle,
                    IsEnabled = item.CanClose
                };
                ToolTipService.SetToolTip(closeBtn, getString("AgentOpenSessionCloseText", "세션 닫기"));
                closeBtn.Click += (_, _) =>
                {
                    OpenSessionClosed?.Invoke(this, currentId);
                };
                Grid.SetColumn(closeBtn, 1);
                rowGrid.Children.Add(closeBtn);

                AgentOpenSessionsListPanel.Children.Add(rowGrid);
            }
        }

        private void RebuildHistoryMenu()
        {
            if (AgentHistoryListPanel == null)
            {
                return;
            }

            AgentHistoryListPanel.Children.Clear();
            Style? buttonStyle = Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = _getString ?? _displayText.GetString;

            if (_historyItems.Count == 0)
            {
                AgentHistoryListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentHistoryEmptyText", "히스토리 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            foreach (var item in _historyItems)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                bool isSelected = string.Equals(_selectedHistoryId, item.Id, StringComparison.Ordinal);
                var selectBtn = new Button
                {
                    Content = isSelected ? $"✓ {item.Title} ({item.TimeText})" : $"{item.Title} ({item.TimeText})",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                if (isSelected)
                {
                    selectBtn.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }

                string currentId = item.Id;
                selectBtn.Click += (_, _) =>
                {
                    HistorySelected?.Invoke(this, currentId);
                    AgentHistoryFlyout.Hide();
                };
                Grid.SetColumn(selectBtn, 0);
                rowGrid.Children.Add(selectBtn);

                var deleteBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle,
                    IsEnabled = !_isBusy
                };
                ToolTipService.SetToolTip(deleteBtn, getString("AgentHistoryDeleteText", "삭제"));
                deleteBtn.Click += (_, _) =>
                {
                    HistoryDeleted?.Invoke(this, currentId);
                };
                Grid.SetColumn(deleteBtn, 1);
                rowGrid.Children.Add(deleteBtn);

                AgentHistoryListPanel.Children.Add(rowGrid);
            }
        }
    }
}
