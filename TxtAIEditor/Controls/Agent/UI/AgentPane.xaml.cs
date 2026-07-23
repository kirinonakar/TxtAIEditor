using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
        private readonly AgentPaneOutputController _outputController;
        private AgentDisplayLocalizer _displayText = AgentDisplayLocalizer.CreateWithResourceLoader();
        private readonly AgentPaneMenuCoordinator _menuCoordinator;
        private readonly AgentPaneSessionMenuCoordinator _sessionMenuCoordinator;
        private readonly AgentPaneReviewController _reviewController;
        private Func<string, string, string>? _getString;

        public string RawOutputText => GetRawOutputText();
        public string SelectedOutputText => _outputController.SelectedOutputText;

        public AgentPane()
        {
            InitializeComponent();
            AgentMcpFlyout.OverlayInputPassThroughElement = AgentPromptInput;
            AgentSkillFlyout.OverlayInputPassThroughElement = AgentPromptInput;
            AgentPresetFlyout.OverlayInputPassThroughElement = AgentPromptInput;
            AgentPromptInput.GotFocus += (_, _) =>
            {
                if (AgentMcpFlyout.IsOpen)
                {
                    AgentMcpFlyout.Hide();
                }

                if (AgentSkillFlyout.IsOpen)
                {
                    AgentSkillFlyout.Hide();
                }

                if (AgentPresetFlyout.IsOpen)
                {
                    AgentPresetFlyout.Hide();
                }
            };
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
            _outputController = new AgentPaneOutputController(
                AgentOutputText,
                AgentOutputScrollViewer,
                this);
            _sessionMenuCoordinator = new AgentPaneSessionMenuCoordinator(
                this,
                AgentHistoryListPanel,
                AgentHistoryFlyout,
                AgentOpenSessionsListPanel,
                AgentOpenSessionsFlyout,
                AgentNewSessionButton,
                AgentNewSessionButtonText,
                AgentNewSessionBadge,
                AgentNewSessionBadgeText,
                AgentOpenSessionsButton,
                AgentRewindSessionButton,
                new AgentPaneSessionMenuCallbacks
                {
                    HistorySelected = id => HistorySelected?.Invoke(this, id),
                    HistoryDeleted = id => HistoryDeleted?.Invoke(this, id),
                    OpenSessionSelected = id => OpenSessionSelected?.Invoke(this, id),
                    OpenSessionClosed = id => OpenSessionClosed?.Invoke(this, id)
                });
            _reviewController = new AgentPaneReviewController(
                this,
                AgentReviewPanelsHost,
                AgentDiffConfirmPanel,
                AgentDiffConfirmHeader,
                AgentDiffConfirmDescription,
                AgentPowerShellCommandPanel,
                AgentPowerShellConfirmCommand,
                AgentModifiedFilesPanel,
                AgentModifiedFilesList,
                preview => FileDiffRequested?.Invoke(this, preview),
                preview => FileRevertRequested?.Invoke(this, preview));

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

        public AgentOutputWrapper Output => new AgentOutputWrapper(this);
        public TextBox Prompt => AgentPromptInput;
        public bool IsPromptInputFocused => AgentPromptInput.FocusState != FocusState.Unfocused;
        public TextBox Activity => AgentActivityText;
        public TextBlock ContextStats => AgentContextStatsText;
        public TextBlock TokenCount => AgentTokenCountText;
        public CheckBox PlanningModeCheckBox => AgentPlanningModeCheckBox;
        public bool PlanningMode => AgentPlanningModeCheckBox.IsChecked == true;
        public ToggleButton StreamToTabToggleButton => AgentStreamToTabToggleButton;
        public bool StreamToTab => AgentStreamToTabToggleButton.IsChecked == true;

        public bool HideHtmlCodeBlocks
        {
            get => _outputController.HideHtmlCodeBlocks;
            set => _outputController.HideHtmlCodeBlocks = value;
        }
        public bool IsThinkingActivityActive => _outputController.IsThinkingActivityActive;

        private bool _isBusy;
        private bool _canRewindSession;
        private string _runButtonText = string.Empty;
        private string _stopButtonText = string.Empty;
        public void Localize(Func<string, string, string> getString)
        {
            _getString = getString;
            _displayText = new AgentDisplayLocalizer(getString);
            _menuCoordinator.Localize(getString);
            _outputController.Localize(getString);

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
            AgentSkillFilterTextBox.PlaceholderText = getString("AgentSkillFilterPlaceholder", "스킬 검색...");
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
            string newSessionButtonText = getString("AgentNewSessionButton", "새 세션");
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
            _sessionMenuCoordinator.Localize(getString, newSessionButtonText);
        }

        public void SetBusy(bool isBusy)
        {
            if (!isBusy)
            {
                _outputController.FlushPendingOutput();
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
            _sessionMenuCoordinator.SetBusy(isBusy);

            if (!isBusy)
            {
                _outputController.ScrollToEnd(true);
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
            _outputController.AppendOutputText(text);
        }

        public void AppendOutputLine(string line)
        {
            _outputController.AppendOutputLine(line);
        }

        public void BeginOutputBlock(string title)
        {
            _outputController.BeginOutputBlock(title);
        }

        public void BeginThinkingActivity(string label)
        {
            _outputController.BeginThinkingActivity(label);
        }

        public void UpdateThinkingActivity(string label)
        {
            _outputController.UpdateThinkingActivity(label);
        }

        public void StopThinkingActivity()
        {
            _outputController.StopThinkingActivity();
        }

        public void ResumeThinkingActivityFromOutput()
        {
            _outputController.ResumeThinkingActivityFromOutput();
        }

        public void ResetOutput(string text)
        {
            _outputController.ResetOutput(text);
        }

        public string GetRawOutputText()
        {
            return _outputController.RawOutputText;
        }

        private void OnAgentOutputScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            _outputController.OnScrollViewerViewChanged();
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
            _sessionMenuCoordinator.RebuildHistoryMenu();
        }

        private void OnAgentOpenSessionsFlyoutOpened(object sender, object e)
        {
            OpenSessionsFlyoutOpened?.Invoke(this, EventArgs.Empty);
            _sessionMenuCoordinator.RebuildOpenSessionMenu();
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

        private void OnOutputDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            _outputController.OnOutputDoubleTapped();
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

        private void OnAgentSkillFilterTextChanged(object sender, TextChangedEventArgs e)
        {
            _menuCoordinator.SetAgentSkillFilter(AgentSkillFilterTextBox.Text);
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
            _outputController.CopySelectionOrAll(e);
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
            _reviewController.ShowDiffConfirm(header, description);
        }

        public void ShowPowerShellConfirm(string header, string description, string command)
        {
            _reviewController.ShowPowerShellConfirm(header, description, command);
        }

        public void HideDiffConfirm()
        {
            _reviewController.HideDiffConfirm();
        }

        public void UpdateModifiedFiles(IReadOnlyList<AgentFileEditPreview> edits)
        {
            _reviewController.UpdateModifiedFiles(edits);
        }

        public void UpdateAttachments(IReadOnlyList<AgentAttachmentItem> attachments)
        {
            AgentAttachmentsList.ItemsSource = attachments;
            AgentAttachmentsList.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnModifiedFilesListItemClick(object sender, ItemClickEventArgs e)
        {
            _reviewController.OpenFileDiff(e.ClickedItem);
        }

        private void OnRevertFileClick(object sender, RoutedEventArgs e)
        {
            _reviewController.RevertFile(sender);
        }

        private void OnModifiedFilesCloseClick(object sender, RoutedEventArgs e)
        {
            _reviewController.CloseModifiedFiles();
        }
        public void UpdateModelName(string text)
        {
            if (!string.Equals(AgentModelNameText.Text, text, StringComparison.Ordinal))
            {
                AgentModelNameText.Text = text;
            }
        }

        public void UpdateHistoryItems(List<AgentHistoryItemViewModel> items, string? selectedId)
        {
            _sessionMenuCoordinator.UpdateHistoryItems(items, selectedId);
        }

        public void UpdateOpenSessionItems(List<AgentOpenSessionItemViewModel> items, string? selectedId)
        {
            _sessionMenuCoordinator.UpdateOpenSessionItems(items);
        }

    }
}
