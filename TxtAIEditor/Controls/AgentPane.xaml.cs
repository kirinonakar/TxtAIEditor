using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
        public int CompletedNotificationCount { get; set; }
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
        private readonly StringBuilder _pendingOutputText = new StringBuilder();
        private readonly AgentOutputRenderer _outputRenderer;
        private bool _outputScrollQueued;
        private bool _outputFlushQueued;
        private bool _userScrolledUp;
        private double _lastVerticalOffset;
        private DispatcherTimer? _outputFlushTimer;
        private AgentDisplayLocalizer _displayText = AgentDisplayLocalizer.CreateWithResourceLoader();
        private readonly AgentPaneMenuCoordinator _menuCoordinator;
        private string _explicitSelectedOutputText = string.Empty;
        private Windows.Foundation.Point? _outputPointerDownPoint;
        private bool _outputPointerSelectionGesture;
        private bool _hasExplicitOutputSelection;
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
            _menuCoordinator = new AgentPaneMenuCoordinator(
                this,
                AgentMcpListPanel,
                AgentMcpFlyout,
                AgentSkillListPanel,
                AgentPresetListPanel,
                AgentPresetFlyout,
                AgentSelectedPresetScrollViewer,
                AgentSelectedPresetPanel,
                AgentSelectedPresetScrollButtons,
                AgentSelectedPresetScrollLeftButton,
                AgentSelectedPresetScrollRightButton,
                new AgentPaneMenuCallbacks
                {
                    AgentPresetToggled = name => AgentPresetToggled?.Invoke(this, name),
                    AgentPresetEdited = name => AgentPresetEdited?.Invoke(this, name),
                    AgentPresetDeleted = name => AgentPresetDeleted?.Invoke(this, name),
                    AgentPresetRemoved = name => AgentPresetRemoved?.Invoke(this, name),
                    AgentSkillToggled = name => AgentSkillToggled?.Invoke(this, name),
                    AgentSkillRemoved = name => AgentSkillRemoved?.Invoke(this, name),
                    AgentMcpToggled = name => AgentMcpToggled?.Invoke(this, name),
                    AgentMcpEdited = name => AgentMcpEdited?.Invoke(this, name),
                    AgentMcpSettingsRequested = name => AgentMcpSettingsRequested?.Invoke(this, name),
                    AgentMcpDeleted = name => AgentMcpDeleted?.Invoke(this, name),
                    AgentMcpRemoved = name => AgentMcpRemoved?.Invoke(this, name)
                });
            _outputRenderer = new AgentOutputRenderer(
                AgentOutputText,
                this,
                text =>
                {
                    _hasExplicitOutputSelection = true;
                    _explicitSelectedOutputText = text;
                });
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

            ActualThemeChanged += (sender, args) => UpdateRichText(_rawOutputText);

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
        public event EventHandler<IEnumerable<string>>? FilesDropped;
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
        public event EventHandler<string>? AgentMcpSettingsRequested;
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
        public ToggleButton StreamToTabToggleButton => AgentStreamToTabToggleButton;
        public bool StreamToTab => AgentStreamToTabToggleButton.IsChecked == true;

        public bool HideHtmlCodeBlocks
        {
            get => _outputRenderer.HideHtmlCodeBlocks;
            set => _outputRenderer.HideHtmlCodeBlocks = value;
        }
        public bool IsThinkingActivityActive => _thinkingLineActive;

        private bool _isBusy;
        private bool _canRewindSession;
        private int _completedSessionNotificationCount;
        private string _newSessionButtonText = string.Empty;
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
        private const int MaxOutputFlushChars = 8_000;
        private const int OutputFlushIntervalMs = 100;
        private const int ThinkingLabelMinIntervalMs = 200;

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
            _menuCoordinator.Localize(getString);
            _outputRenderer.Localize(getString);
            string outputText = _rawOutputText.TrimStart();
            if (_displayText.IsOutputPlaceholder(outputText))
            {
                ResetOutput(_displayText.OutputPlaceholder);
            }

            AgentContextStatsText.Text = getString("AgentContextStatsDefault", "현재 탭과 선택 영역을 맥락으로 사용");
            AgentPlanningModeCheckBox.Content = getString("AgentIncludeActiveFile", "계획 모드 (Planning mode)");
            string streamToTabText = getString("AgentStreamToTab", "탭에 스트리밍");
            ToolTipService.SetToolTip(AgentStreamToTabToggleButton, streamToTabText);
            AutomationProperties.SetName(AgentStreamToTabToggleButton, streamToTabText);
            AgentPromptInput.PlaceholderText = getString("AgentPromptPlaceholder", "Agent에게 맡길 작업 입력...");
            ToolTipService.SetToolTip(AgentMcpButton, getString("AgentMcpButtonTooltip", "MCP 서버"));
            AgentAddMcpText.Text = getString("AgentMcpAddText", "MCP 추가");
            AgentExportMcpText.Text = getString("PresetExportText", "내보내기");
            AgentImportMcpText.Text = getString("PresetImportText", "가져오기");
            ToolTipService.SetToolTip(AgentAddAttachmentButton, getString("AgentAddAttachmentTooltip", "이미지 또는 파일 추가"));
            ToolTipService.SetToolTip(AgentSkillButton, getString("AgentSkillButtonTooltip", "스킬"));
            AgentSkillTitleText.Text = getString("AgentSkillTitle", "스킬");
            ToolTipService.SetToolTip(AgentSkillOpenFolderButton, getString("AgentSkillOpenFolderTooltip", "스킬 폴더 열기"));
            ToolTipService.SetToolTip(AgentPresetButton, getString("AgentPresetButtonTooltip", "페르소나/지침 프리셋"));
            ToolTipService.SetToolTip(AgentSkillRefreshButton, getString("AgentSkillRefreshTooltip", "스킬 디렉터리를 다시 스캔"));
            AgentAddPresetText.Text = getString("AgentPresetAddText", "프리셋 추가");
            AgentExportPresetText.Text = getString("PresetExportText", "내보내기");
            AgentImportPresetText.Text = getString("PresetImportText", "가져오기");
            _runButtonText = getString("AgentRunButton", "실행");
            _stopButtonText = getString("AgentStopButton", "중단");
            AgentRunButton.Content = _isBusy ? _stopButtonText : _runButtonText;
            _newSessionButtonText = getString("AgentNewSessionButton", "새 세션");
            AgentNewSessionButtonText.Text = _newSessionButtonText;
            ToolTipService.SetToolTip(AgentRewindSessionButton, getString("AgentRewindSessionTooltip", "이전 프롬프트 입력 전으로 되감기"));
            ToolTipService.SetToolTip(AgentOpenSessionsButton, getString("AgentOpenSessionsTooltip", "열린 세션"));
            AgentOpenSessionsTitleText.Text = getString("AgentOpenSessionsTitle", "열린 세션");
            ToolTipService.SetToolTip(AgentHistoryButton, getString("AgentHistoryTooltip", "세션 히스토리"));
            AgentHistoryTitleText.Text = getString("AgentHistoryTitle", "세션 히스토리 (최근 20개)");
            ToolTipService.SetToolTip(AgentDeleteHistoryButton, getString("AgentDeleteHistoryTooltip", "히스토리 삭제"));
            AgentInsertOutputButton.Content = getString("AgentLastAnswerButtonText", "마지막 답변");
            AgentActivityHeaderText.Text = getString("AgentActivityHeader", "진행 상황");
            if (_displayText.IsActivityIdle(AgentActivityText.Text))
            {
                AgentActivityText.Text = _displayText.ActivityIdle;
            }
            ToolTipService.SetToolTip(AgentInsertOutputButton, getString("AgentLastAnswerNewTabTooltip", "마지막 Agent 답변을 새 탭에 입력"));
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
            _menuCoordinator.RebuildAll();
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
            AgentStreamToTabToggleButton.IsEnabled = !isBusy;
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
                Interval = TimeSpan.FromMilliseconds(OutputFlushIntervalMs)
            };
            timer.Tick += (_, _) => QueuePendingOutputFlush();
            return timer;
        }

        private void QueuePendingOutputFlush()
        {
            if (_outputFlushQueued)
            {
                return;
            }

            if (_pendingOutputText.Length == 0)
            {
                _outputFlushTimer?.Stop();
                return;
            }

            _outputFlushQueued = true;
            if (DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    _outputFlushQueued = false;
                    FlushPendingOutputText();
                }))
            {
                return;
            }

            _outputFlushQueued = false;
            FlushPendingOutputText();
        }

        private void FlushPendingOutputText()
        {
            _outputFlushQueued = false;
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
            _outputFlushQueued = false;
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
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
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
            if (!_outputRenderer.TrySetLastLine(text))
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
            Visibility visibility = string.IsNullOrEmpty(AgentPromptInput.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (AgentClearPromptButton.Visibility != visibility)
            {
                AgentClearPromptButton.Visibility = visibility;
            }
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

            _explicitSelectedOutputText = AgentOutputText.SelectedText;
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

        private void OnAgentGridDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.Caption = _getString?.Invoke("AgentAttachFileCaption", "파일 첨부") ?? "파일 첨부";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsContentVisible = true;
                e.Handled = true;
            }
            else
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                e.Handled = true;
            }
        }

        private async void OnAgentGridDrop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                return;
            }

            e.Handled = true;
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var filePaths = new List<string>();
                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item.Path) && System.IO.File.Exists(item.Path))
                    {
                        filePaths.Add(item.Path);
                    }
                }

                if (filePaths.Count > 0)
                {
                    FilesDropped?.Invoke(this, filePaths);
                }
            }
            catch
            {
                // 드롭 처리 중 오류 무시
            }
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
            _menuCoordinator.RebuildAgentMcpMenu();
        }

        private void OnAgentPresetFlyoutOpened(object sender, object e)
        {
            _menuCoordinator.RebuildAgentPresetMenu();
        }

        private void OnAgentSkillFlyoutOpened(object sender, object e)
        {
            AgentSkillFlyoutOpened?.Invoke(this, EventArgs.Empty);
            _menuCoordinator.RebuildAgentSkillMenu();
        }

        private void OnAgentSkillRefreshClick(object sender, TappedRoutedEventArgs e)
        {
            AgentSkillRefreshRequested?.Invoke(this, EventArgs.Empty);
            AgentSkillFlyout.Hide();
        }

        private void OnAgentSkillOpenFolderClick(object sender, TappedRoutedEventArgs e)
        {
            try
            {
                string path = AgentSkillDirectories.UserSkillsDirectory;
                if (!System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(path);
                }

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add(path);
                System.Diagnostics.Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open skills directory: {ex.Message}");
            }
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
            _outputRenderer.UpdateRichText(rawText);
        }

        private void AppendRenderedText(string text)
        {
            _outputRenderer.AppendText(text);
        }

        public void UpdateAgentPresetsMenu(
            IReadOnlyList<string> presetNames,
            IReadOnlyCollection<string> selectedPresetNames,
            Func<string, string, string> getString)
        {
            _menuCoordinator.UpdateAgentPresetsMenu(presetNames, selectedPresetNames, getString);
        }

        public void UpdateAgentSkillsMenu(
            IReadOnlyList<AgentSkillItem> skills,
            IReadOnlyCollection<string> selectedSkillNames,
            Func<string, string, string> getString)
        {
            _menuCoordinator.UpdateAgentSkillsMenu(skills, selectedSkillNames, getString);
        }

        public void UpdateAgentSkillSelection(
            IReadOnlyCollection<string> selectedSkillNames,
            Func<string, string, string> getString)
        {
            _menuCoordinator.UpdateAgentSkillSelection(selectedSkillNames, getString);
        }

        public void UpdateAgentMcpMenu(
            IReadOnlyList<AgentMcpItem> mcpItems,
            IReadOnlyCollection<string> selectedMcpNames,
            Func<string, string, string> getString)
        {
            _menuCoordinator.UpdateAgentMcpMenu(mcpItems, selectedMcpNames, getString);
        }

        private void OnSelectedPresetScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            _menuCoordinator.OnSelectedPresetScrollViewerViewChanged();
        }

        private void OnSelectedPresetScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _menuCoordinator.OnSelectedPresetScrollViewerSizeChanged();
        }

        private void OnSelectedPresetPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _menuCoordinator.OnSelectedPresetPanelSizeChanged();
        }

        private void OnSelectedPresetScrollLeftClick(object sender, RoutedEventArgs e)
        {
            _menuCoordinator.OnSelectedPresetScrollLeftClick();
        }

        private void OnSelectedPresetScrollRightClick(object sender, RoutedEventArgs e)
        {
            _menuCoordinator.OnSelectedPresetScrollRightClick();
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

            bool isDanger = AgentToolHelpers.IsDangerousPowerShellCommand(command);

            if (isDanger)
            {
                AgentPowerShellConfirmCommand.Foreground = GetAgentBrush("AgentPowerShellConfirmDangerForeground", Microsoft.UI.Colors.Red);
            }
            else
            {
                AgentPowerShellConfirmCommand.Foreground = GetAgentBrush("AgentOutputForeground", Microsoft.UI.Colors.Black);
            }
            AgentPowerShellCommandPanel.Visibility = Visibility.Visible;
            AgentDiffConfirmPanel.Visibility = Visibility.Visible;
            UpdateReviewPanelsHostVisibility();
        }

        private Brush GetAgentBrush(string key, Windows.UI.Color fallbackColor)
        {
            string themeName = ActualTheme == ElementTheme.Default
                ? (Application.Current.RequestedTheme == ApplicationTheme.Dark ? "Dark" : "Light")
                : (ActualTheme == ElementTheme.Dark ? "Dark" : "Light");

            object? dictObj;
            object? resource;

            if (Resources.ThemeDictionaries.TryGetValue(themeName, out dictObj) &&
                dictObj is ResourceDictionary themeDict &&
                themeDict.TryGetValue(key, out resource) &&
                resource is Brush brush1)
            {
                return brush1;
            }

            if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeName, out dictObj) &&
                dictObj is ResourceDictionary appThemeDict &&
                appThemeDict.TryGetValue(key, out resource) &&
                resource is Brush brush2)
            {
                return brush2;
            }

            if (Resources.TryGetValue(key, out resource) && resource is Brush brush3)
            {
                return brush3;
            }
            if (Application.Current.Resources.TryGetValue(key, out resource) && resource is Brush brush4)
            {
                return brush4;
            }

            return new SolidColorBrush(fallbackColor);
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
            _completedSessionNotificationCount = _openSessionItems.Sum(item => Math.Max(0, item.CompletedNotificationCount));
            UpdateOpenSessionButtonChrome();
            RebuildOpenSessionMenu();
        }

        private void UpdateOpenSessionButtonChrome()
        {
            bool showSessionList = _openSessionItems.Count > 1;
            AgentOpenSessionsButton.Visibility = showSessionList ? Visibility.Visible : Visibility.Collapsed;
            string newSessionText = string.IsNullOrWhiteSpace(_newSessionButtonText)
                ? "새 세션"
                : _newSessionButtonText;
            AgentNewSessionButtonText.Text = newSessionText;

            if (_completedSessionNotificationCount > 0)
            {
                AgentNewSessionBadgeText.Text = FormatCompletionBadgeText(_completedSessionNotificationCount);
                AgentNewSessionBadge.Visibility = Visibility.Visible;
            }
            else
            {
                AgentNewSessionBadge.Visibility = Visibility.Collapsed;
            }

            string automationName = newSessionText;
            if (_completedSessionNotificationCount > 0)
            {
                automationName += " " + FormatCompletionBadgeText(_completedSessionNotificationCount);
            }
            AutomationProperties.SetName(AgentNewSessionButton, automationName);
            Func<string, string, string> getString = _getString ?? _displayText.GetString;
            string? completionTooltip = _completedSessionNotificationCount > 0
                ? string.Format(
                    getString("AgentCompletedSessionsBadgeTooltip", "{0:N0} background session(s) completed"),
                    _completedSessionNotificationCount)
                : null;
            ToolTipService.SetToolTip(AgentNewSessionButton, completionTooltip);
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
                    Fill = new SolidColorBrush(item.CompletedNotificationCount > 0
                        ? Windows.UI.Color.FromArgb(255, 220, 38, 38)
                        : (item.IsRunning
                            ? Windows.UI.Color.FromArgb(255, 34, 197, 94)
                            : Windows.UI.Color.FromArgb(255, 156, 163, 175))),
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

        private static Border CreateCompletionBadge(int count, Func<string, string, string> getString)
        {
            var text = new TextBlock
            {
                Text = FormatCompletionBadgeText(count),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var badge = new Border
            {
                MinWidth = 18,
                Height = 18,
                Padding = new Thickness(4, 0, 4, 1),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 38, 38)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = text
            };
            ToolTipService.SetToolTip(
                badge,
                string.Format(
                    getString("AgentCompletedSessionsBadgeTooltip", "{0:N0} background session(s) completed"),
                    Math.Max(0, count)));
            return badge;
        }

        private static string FormatCompletionBadgeText(int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            return count.ToString();
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
