using System;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentPaneOutputController
    {
        private const string OutputLineBreak = "\r\n";
        private const int MaxOutputFlushChars = 8_000;
        private const int OutputFlushIntervalMs = 100;
        private const int ThinkingLabelMinIntervalMs = 200;

        private readonly RichTextBlock _outputText;
        private readonly ScrollViewer _outputScrollViewer;
        private readonly FrameworkElement _resourceOwner;
        private readonly AgentOutputRenderer _renderer;
        private readonly StringBuilder _pendingOutputText = new StringBuilder();
        private AgentDisplayLocalizer _displayText = AgentDisplayLocalizer.CreateWithResourceLoader();
        private int _outputLength;
        private string _rawOutputText = string.Empty;
        private bool _outputScrollQueued;
        private bool _outputFlushQueued;
        private bool _userScrolledUp;
        private double _lastVerticalOffset;
        private DispatcherTimer? _outputFlushTimer;
        private string _explicitSelectedOutputText = string.Empty;
        private Windows.Foundation.Point? _outputPointerDownPoint;
        private bool _outputPointerSelectionGesture;
        private bool _hasExplicitOutputSelection;
        private DispatcherTimer? _thinkingTimer;
        private DispatcherTimer? _thinkingLabelFlushTimer;
        private bool _thinkingLineActive;
        private int _thinkingLineStart;
        private int _thinkingDotCount;
        private string _thinkingLinePrefix = string.Empty;
        private string _thinkingLineTimestamp = string.Empty;
        private string? _pendingThinkingLabel;
        private DateTimeOffset _lastThinkingLabelRender = DateTimeOffset.MinValue;

        public AgentPaneOutputController(
            RichTextBlock outputText,
            ScrollViewer outputScrollViewer,
            FrameworkElement resourceOwner)
        {
            _outputText = outputText;
            _outputScrollViewer = outputScrollViewer;
            _resourceOwner = resourceOwner;
            _renderer = new AgentOutputRenderer(
                outputText,
                resourceOwner,
                text =>
                {
                    _hasExplicitOutputSelection = true;
                    _explicitSelectedOutputText = text;
                });

            _outputText.SizeChanged += (_, _) => QueueOutputScrollToEnd();
            _outputText.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(OnOutputPointerPressed),
                true);
            _outputText.AddHandler(
                UIElement.PointerMovedEvent,
                new PointerEventHandler(OnOutputPointerMoved),
                true);
            _outputText.AddHandler(
                UIElement.PointerReleasedEvent,
                new PointerEventHandler(OnOutputPointerReleased),
                true);
            _resourceOwner.ActualThemeChanged += (_, _) => _renderer.UpdateRichText(_rawOutputText);
        }

        public string RawOutputText
        {
            get
            {
                FlushAllPendingOutputText();
                return _rawOutputText;
            }
        }

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

        public bool HideHtmlCodeBlocks
        {
            get => _renderer.HideHtmlCodeBlocks;
            set => _renderer.HideHtmlCodeBlocks = value;
        }

        public bool IsThinkingActivityActive => _thinkingLineActive;

        public void Localize(Func<string, string, string> getString)
        {
            _displayText = new AgentDisplayLocalizer(getString);
            _renderer.Localize(getString);
            if (_displayText.IsOutputPlaceholder(_rawOutputText.TrimStart()))
            {
                ResetOutput(_displayText.OutputPlaceholder);
            }
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

            if (!string.IsNullOrEmpty(_rawOutputText) && !EndsWithLineBreak(_rawOutputText))
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

            if (!string.IsNullOrWhiteSpace(_rawOutputText) && !EndsWithBlankLine(_rawOutputText))
            {
                AppendText(EndsWithLineBreak(_rawOutputText)
                    ? OutputLineBreak
                    : OutputLineBreak + OutputLineBreak);
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
            if (!string.IsNullOrEmpty(currentText) && !EndsWithLineBreak(currentText))
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

        public void ResetOutput(string text)
        {
            ResetThinkingState();
            ClearPendingOutputText();
            ClearExplicitOutputSelection();
            _rawOutputText = text ?? string.Empty;
            _renderer.UpdateRichText(_rawOutputText);
            _outputLength = _rawOutputText.Length;
        }

        public void FlushPendingOutput()
        {
            FlushAllPendingOutputText();
        }

        public void ScrollToEnd(bool force = false)
        {
            ScrollOutputToEnd(force);
        }

        public void OnScrollViewerViewChanged()
        {
            double offset = _outputScrollViewer.VerticalOffset;
            double maxOffset = _outputScrollViewer.ScrollableHeight;

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

        public void OnOutputDoubleTapped()
        {
            _hasExplicitOutputSelection = true;
            QueueCaptureExplicitOutputSelection();
        }

        public void CopySelectionOrAll(KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.C || !IsControlDown())
            {
                return;
            }

            _hasExplicitOutputSelection = true;
            CaptureExplicitOutputSelection();
            string textToCopy = string.IsNullOrEmpty(_explicitSelectedOutputText)
                ? _rawOutputText
                : _explicitSelectedOutputText;
            if (string.IsNullOrEmpty(textToCopy))
            {
                return;
            }

            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(textToCopy);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            e.Handled = true;
        }

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

            if (_outputLength == currentLength)
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
                    _renderer.AppendText(text);
                }
                else
                {
                    _renderer.UpdateRichText(_rawOutputText);
                }
            }
            else
            {
                _rawOutputText = _rawOutputText.Insert(_outputLength, text);
                _outputLength += text.Length;
                _renderer.UpdateRichText(_rawOutputText);
            }
        }

        private void ClearOutputPlaceholder()
        {
            if (_rawOutputText.Length > 100)
            {
                return;
            }

            if (_displayText.IsOutputPlaceholder(_rawOutputText.TrimStart()))
            {
                _rawOutputText = string.Empty;
                _renderer.UpdateRichText(_rawOutputText);
                _outputLength = 0;
            }
            else
            {
                _outputLength = _rawOutputText.Length;
            }
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
            if (_resourceOwner.DispatcherQueue.TryEnqueue(
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
            _resourceOwner.DispatcherQueue.TryEnqueue(
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
            _resourceOwner.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                ChangeOutputViewToEnd);
        }

        private void ChangeOutputViewToEnd()
        {
            _outputScrollViewer.ChangeView(null, double.MaxValue, null, true);
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
            if (!EndsWithLineBreak(_rawOutputText))
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
            if (!_renderer.TrySetLastLine(text))
            {
                _renderer.UpdateRichText(_rawOutputText);
            }
            ScrollOutputToEnd();
        }

        private void OnOutputPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _outputPointerDownPoint = e.GetCurrentPoint(_outputText).Position;
            _outputPointerSelectionGesture = false;
        }

        private void OnOutputPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_outputPointerDownPoint == null)
            {
                return;
            }

            var currentPoint = e.GetCurrentPoint(_outputText);
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

        private void QueueCaptureExplicitOutputSelection()
        {
            _resourceOwner.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                CaptureExplicitOutputSelection);
        }

        private void CaptureExplicitOutputSelection()
        {
            _explicitSelectedOutputText = _hasExplicitOutputSelection
                ? _outputText.SelectedText
                : string.Empty;
        }

        private void ClearExplicitOutputSelection()
        {
            _explicitSelectedOutputText = string.Empty;
            _hasExplicitOutputSelection = false;
            _outputPointerDownPoint = null;
            _outputPointerSelectionGesture = false;
        }

        private static bool IsControlDown()
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
            string normalized = CollapseLineBreakRuns(NormalizeOutputLineBreaks(text));
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
            return (text ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
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
    }
}
