using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Editor
{
    public sealed class CustomEditorBridge
    {
        private const int LinePatchBatchMaxLines = 256;
        private const int LinePatchBatchMaxBytes = 256 * 1024;
        private readonly WebView2 _webView;
        private readonly ILocalizationService? _localizationService;
        private readonly CustomEditorMessageRouter _messageRouter;
        private string? _pendingText = null;
        private bool _pendingSetTextShouldFocus = true;
        private string? _pendingSetTextDocumentId;
        private long? _pendingSetTextDocumentVersion;
        private string? _pendingSetTextViewId;
        private bool _isSplitView = false;
        private string _currentLanguage = "plaintext";
        private readonly object _flushLock = new object();
        private readonly Dictionary<int, TaskCompletionSource<long>> _pendingFlushRequests = new Dictionary<int, TaskCompletionSource<long>>();
        private int _flushRequestSeq = 0;
        private IReadOnlyList<string>? _currentOriginalLines;
        private Dictionary<int, string>? _currentDirtyLines;

        public event Action<bool>? ContentChanged { add => _messageRouter.ContentChanged += value; remove => _messageRouter.ContentChanged -= value; }
        public event Action<string, int, int, long?, long?>? SelectionReceived { add => _messageRouter.SelectionReceived += value; remove => _messageRouter.SelectionReceived -= value; }
        public event Action<int, int>? CursorChanged { add => _messageRouter.CursorChanged += value; remove => _messageRouter.CursorChanged -= value; }
        public event Action? EditorReady { add => _messageRouter.EditorReady += value; remove => _messageRouter.EditorReady -= value; }
        public event Action<string>? ShortcutPressed { add => _messageRouter.ShortcutPressed += value; remove => _messageRouter.ShortcutPressed -= value; }
        public event Action<int, int, int>? LinesRequested { add => _messageRouter.LinesRequested += value; remove => _messageRouter.LinesRequested -= value; }
        public event Action<EditorEditRequest>? EditRequested { add => _messageRouter.EditRequested += value; remove => _messageRouter.EditRequested -= value; }
        public event Action<long, string>? HexEditRequested { add => _messageRouter.HexEditRequested += value; remove => _messageRouter.HexEditRequested -= value; }
        public event Action? EditTransactionStarted { add => _messageRouter.EditTransactionStarted += value; remove => _messageRouter.EditTransactionStarted -= value; }
        public event Action? EditTransactionEnded { add => _messageRouter.EditTransactionEnded += value; remove => _messageRouter.EditTransactionEnded -= value; }
        public event Action<string, int, int, bool, bool, bool>? FindRequested { add => _messageRouter.FindRequested += value; remove => _messageRouter.FindRequested -= value; }
        public event Action<string, bool, bool, int>? FindAllRequested { add => _messageRouter.FindAllRequested += value; remove => _messageRouter.FindAllRequested -= value; }
        public event Action<string, string, bool, bool>? ReplaceAllRequested { add => _messageRouter.ReplaceAllRequested += value; remove => _messageRouter.ReplaceAllRequested -= value; }
        public event Action<int, double>? ScrollChanged { add => _messageRouter.ScrollChanged += value; remove => _messageRouter.ScrollChanged -= value; }
        public event Action<bool>? ScrollSyncChanged { add => _messageRouter.ScrollSyncChanged += value; remove => _messageRouter.ScrollSyncChanged -= value; }
        public event Action? EditorRendered { add => _messageRouter.EditorRendered += value; remove => _messageRouter.EditorRendered -= value; }
        public event Action<string, bool, bool>? CtrlClicked { add => _messageRouter.CtrlClicked += value; remove => _messageRouter.CtrlClicked -= value; }
        public event Func<string, bool, bool, Task<bool>>? OpenableHoverRequested { add => _messageRouter.OpenableHoverRequested += value; remove => _messageRouter.OpenableHoverRequested -= value; }

        public CustomEditorBridge(WebView2 webView, ILocalizationService? localizationService = null)
        {
            _webView = webView;
            _localizationService = localizationService;
            _messageRouter = new CustomEditorMessageRouter(webView, SendMessageAsync);
            _messageRouter.ReadyReceived += HandleEditorReady;
            _messageRouter.FlushCompleted += HandleFlushCompleted;
        }

        public async Task InitializeAsync()
        {
            try
            {
                var env = await WebViewEnvironmentProvider.GetSharedAsync();
                await _webView.EnsureCoreWebView2Async(env);
                
                _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                _webView.CoreWebView2.Settings.IsScriptEnabled = true;
                
                // Disable browser context menu to keep it premium
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebView2 initialization failed: {ex.Message}");
            }
        }

        public void LoadEditor(string hostUrl)
        {
            _webView.Source = new Uri(hostUrl);
        }

        public async Task SetTextAsync(
            string text,
            bool shouldFocus = true,
            string? documentId = null,
            long? documentVersion = null,
            string? viewId = null)
        {
            if (!_messageRouter.IsReady)
            {
                _pendingText = text;
                _pendingSetTextShouldFocus = shouldFocus;
                _pendingSetTextDocumentId = documentId;
                _pendingSetTextDocumentVersion = documentVersion;
                _pendingSetTextViewId = viewId;
                return;
            }

            var msg = new
            {
                protocolVersion = EditorProtocol.CurrentVersion,
                action = "setText",
                documentId,
                viewId,
                documentVersion,
                text,
                shouldFocus
            };
            await SendMessageAsync(msg);
        }

        public async Task ResetOriginalLinesAsync(IReadOnlyList<string> lines)
        {
            _currentOriginalLines = lines.ToArray();
            _currentDirtyLines = new Dictionary<int, string>();
            var msg = new { action = "resetOriginalLines", lines = _currentOriginalLines };
            await SendMessageAsync(msg);
        }

        public async Task UpdateDirtyLinesAsync(Dictionary<int, string> dirtyLines)
        {
            _currentDirtyLines = new Dictionary<int, string>(dirtyLines);
            var msg = new { action = "updateDirtyLines", dirtyLines = _currentDirtyLines };
            await SendMessageAsync(msg);
        }

        public async Task InitializeModelAsync(
            int lineCount,
            string language,
            EditorSettings settings,
            bool isReadOnly = false,
            IReadOnlyList<string>? initialLines = null,
            string? documentId = null,
            long documentVersion = 0,
            string? viewId = null,
            bool? inlineLivePreviewEnabled = null,
            string? livePreviewBaseHref = null)
        {
            _currentLanguage = string.IsNullOrWhiteSpace(language) ? "plaintext" : language;
            _messageRouter.SetDocumentContext(documentId, viewId);
            object message = CustomEditorMessageFactory.CreateInitializeModel(
                lineCount,
                _currentLanguage,
                settings,
                isReadOnly,
                initialLines,
                documentId,
                documentVersion,
                viewId,
                _isSplitView,
                inlineLivePreviewEnabled,
                livePreviewBaseHref,
                _localizationService);
            await SendMessageAsync(message);

            // A split view can inherit its saved baseline and dirty markers before its
            // WebView has finished initializing. Reapply the latest state after initModel,
            // which clears both collections on the JavaScript side.
            if (_currentOriginalLines != null)
            {
                await SendMessageAsync(new { action = "resetOriginalLines", lines = _currentOriginalLines });
            }
            if (settings.ShowDirtyLines && _currentDirtyLines != null)
            {
                await SendMessageAsync(new { action = "updateDirtyLines", dirtyLines = _currentDirtyLines });
            }
        }

        public async Task SendLinesAsync(int requestId, int startLine, IReadOnlyList<string> lines)
        {
            var msg = new
            {
                action = "receiveLines",
                requestId = requestId,
                startLine = startLine,
                lines = lines
            };
            await SendMessageAsync(msg);
        }

        public async Task UpdateLineCountAsync(int lineCount)
        {
            await SendMessageAsync(new { action = "lineCountChanged", lineCount = Math.Max(1, lineCount) });
        }

        public async Task UpdateLineAsync(
            int lineNumber,
            string text,
            bool isComposing = false,
            string? documentId = null,
            long? baseVersion = null,
            long? documentVersion = null)
        {
            await SendMessageAsync(new
            {
                protocolVersion = EditorProtocol.CurrentVersion,
                action = "updateLine",
                documentId,
                baseVersion,
                documentVersion,
                lineNumber = Math.Max(1, lineNumber),
                text = text ?? string.Empty,
                isComposing = isComposing
            });
        }

        public async Task SetSplitViewAsync(bool enabled)
        {
            _isSplitView = enabled;
            await SendMessageAsync(new
            {
                action = "setSplitView",
                enabled = enabled
            });
        }

        public async Task ApplyEditResultAsync(
            UndoResult result,
            string? documentId = null,
            long? baseVersion = null,
            long? documentVersion = null,
            string? sourceViewId = null)
        {
            if (result.LinePatches is { Count: > 0 } patches)
            {
                await ApplyLinePatchesAsync(
                    patches,
                    documentId,
                    baseVersion,
                    documentVersion,
                    sourceViewId);
                return;
            }

            await SendMessageAsync(new
            {
                protocolVersion = EditorProtocol.CurrentVersion,
                action = "applyEditResult",
                documentId,
                baseVersion,
                documentVersion,
                sourceViewId,
                startLine = Math.Max(1, result.StartLine),
                oldLineCount = Math.Max(0, result.OldLineCount),
                lineCount = Math.Max(1, result.DocumentLineCount),
                lines = result.LinesToRefresh,
                caret = result.Caret is CaretState caret
                    ? new { line = Math.Max(1, caret.LineNumber), column = Math.Max(1, caret.Column) }
                    : null
            });
        }

        public Task SendEditAcceptedAsync(EditorEditCommandResult result)
        {
            EditorDocumentChange? change = result.Change;
            return SendMessageAsync(new
            {
                protocolVersion = EditorProtocol.CurrentVersion,
                action = "editAccepted",
                editId = result.EditId,
                newVersion = result.CurrentVersion,
                changePatch = change == null ? null : new
                {
                    startLine = change.StartLine,
                    oldLineCount = change.OldLineCount,
                    lineCount = change.DocumentLineCount,
                    lines = change.Lines,
                    linePatches = change.LinePatches
                }
            });
        }

        public Task SendEditRejectedAsync(EditorEditCommandResult result)
        {
            return SendMessageAsync(new
            {
                protocolVersion = EditorProtocol.CurrentVersion,
                action = "editRejected",
                editId = result.EditId,
                currentVersion = result.CurrentVersion,
                resyncFromVersion = result.ResyncFromVersion
            });
        }

        public Task SetTextOperationLockAsync(bool locked)
        {
            return SendMessageAsync(new
            {
                action = "setTextOperationLock",
                locked
            });
        }

        public async Task ApplyLineReplacementsAsync(
            IReadOnlyList<LineReplacement> replacements,
            string documentId,
            long baseVersion,
            long documentVersion,
            string sourceViewId)
        {
            await SendLinePatchBatchesAsync(
                replacements.Select(item => new TextLinePatch(item.LineNumber, item.AfterText)),
                documentId,
                baseVersion,
                documentVersion,
                sourceViewId);
        }

        public async Task ApplyLinePatchesAsync(
            IReadOnlyList<TextLinePatch> patches,
            string? documentId,
            long? baseVersion,
            long? documentVersion,
            string? sourceViewId)
        {
            await SendLinePatchBatchesAsync(
                patches,
                documentId,
                baseVersion,
                documentVersion,
                sourceViewId);
        }

        private async Task SendLinePatchBatchesAsync(
            IEnumerable<TextLinePatch> patches,
            string? documentId,
            long? baseVersion,
            long? documentVersion,
            string? sourceViewId)
        {
            var latestPatchByLine = new Dictionary<int, TextLinePatch>();
            foreach (TextLinePatch patch in patches)
            {
                latestPatchByLine[patch.LineNumber] = patch;
            }

            TextLinePatch[] coalescedPatches = latestPatchByLine.Values
                .OrderBy(patch => patch.LineNumber)
                .ToArray();
            if (coalescedPatches.Length == 0)
            {
                return;
            }

            string batchId = Guid.NewGuid().ToString("N");
            int batchIndex = 0;
            int patchIndex = 0;
            while (patchIndex < coalescedPatches.Length)
            {
                int batchBytes = 0;
                var batch = new List<TextLinePatch>(LinePatchBatchMaxLines);
                while (patchIndex < coalescedPatches.Length && batch.Count < LinePatchBatchMaxLines)
                {
                    TextLinePatch patch = coalescedPatches[patchIndex];
                    int patchBytes = Encoding.UTF8.GetByteCount(patch.Text ?? string.Empty);
                    if (batch.Count > 0 && batchBytes + patchBytes > LinePatchBatchMaxBytes)
                    {
                        break;
                    }

                    batch.Add(patch);
                    batchBytes += patchBytes;
                    patchIndex++;
                }

                bool isFinal = patchIndex >= coalescedPatches.Length;
                await SendRequiredMessageAsync(new
                {
                    protocolVersion = EditorProtocol.CurrentVersion,
                    action = "applyLineReplacements",
                    documentId,
                    baseVersion,
                    documentVersion,
                    sourceViewId,
                    batchId,
                    batchIndex,
                    isFinal,
                    replacements = batch.Select(item => new
                    {
                        lineNumber = item.LineNumber,
                        text = item.Text
                    }).ToArray()
                });
                batchIndex++;
                if (!isFinal)
                {
                    await Task.Yield();
                }
            }
        }

        public async Task SendFindResultAsync(TextSearchResult? result, string query)
        {
            if (result == null)
            {
                await SendMessageAsync(new { action = "findResult", found = false, query = query });
                return;
            }

            await SendMessageAsync(new
            {
                action = "findResult",
                found = true,
                query = query,
                lineNumber = result.LineNumber,
                indexOfMatch = result.IndexOfMatch,
                matchLength = result.MatchLength
            });
        }

        public async Task SendFindAllResultsAsync(IReadOnlyList<TextSearchResult> results, string query)
        {
            var matches = results.Select(r => new
            {
                lineNumber = r.LineNumber,
                indexOfMatch = r.IndexOfMatch,
                matchLength = r.MatchLength
            }).ToArray();

            await SendMessageAsync(new
            {
                action = "findAllResult",
                query = query,
                matches = matches
            });
        }

        public async Task SetLanguageAsync(string filePathOrLanguage)
        {
            _currentLanguage = CustomEditorLanguageResolver.Resolve(filePathOrLanguage);
            await SendMessageAsync(new { action = "setLanguage", language = _currentLanguage });
        }

        public async Task UpdateOptionsAsync(EditorSettings settings, bool isReadOnly = false)
        {
            if (!settings.ShowDirtyLines)
            {
                _currentDirtyLines = null;
            }

            object message = CustomEditorMessageFactory.CreateUpdateOptions(
                _currentLanguage,
                settings,
                isReadOnly,
                _localizationService);
            await SendMessageAsync(message);
        }

        public async Task TriggerFindAsync()
        {
            var msg = new { action = "triggerFind" };
            await SendMessageAsync(msg);
        }

        public async Task FocusAsync()
        {
            var msg = new { action = "focus" };
            await SendMessageAsync(msg);
        }

        public async Task RequestSelectionAsync()
        {
            var msg = new { action = "getSelection" };
            await SendMessageAsync(msg);
        }

        public async Task<long?> FlushPendingEditForSaveAsync(int timeoutMs = 700)
        {
            if (!_messageRouter.IsReady || _webView.CoreWebView2 == null)
            {
                return null;
            }

            var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            int requestId;
            lock (_flushLock)
            {
                requestId = ++_flushRequestSeq;
                _pendingFlushRequests[requestId] = tcs;
            }

            try
            {
                await SendMessageAsync(new { action = "flushForSave", requestId = requestId });
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(Math.Max(80, timeoutMs)));
                if (completed == tcs.Task)
                {
                    return await tcs.Task;
                }

                lock (_flushLock)
                {
                    _pendingFlushRequests.Remove(requestId);
                }
                return null;
            }
            catch (Exception ex)
            {
                lock (_flushLock)
                {
                    _pendingFlushRequests.Remove(requestId);
                }
                System.Diagnostics.Debug.WriteLine($"Failed to flush editor before save: {ex.Message}");
                return null;
            }
        }

        public async Task RevealLineAsync(int lineNum, int indexOfMatch = 0, int matchLength = 0, string query = "")
        {
            var msg = new { action = "revealLine", lineNumber = lineNum, indexOfMatch = indexOfMatch, matchLength = matchLength, query = query };
            await SendMessageAsync(msg);
        }

        public async Task RevealHexOffsetAsync(long offset)
        {
            var msg = new { action = "revealHexOffset", offset = Math.Max(0, offset) };
            await SendMessageAsync(msg);
        }

        public async Task InsertTextAsync(string text)
        {
            var msg = new { action = "insertText", text = text };
            await SendMessageAsync(msg);
        }

        public async Task BeginStreamInsertAsync()
        {
            await SendMessageAsync(new { action = "beginStreamInsert" });
        }

        public async Task InsertStreamTextAsync(string text)
        {
            var msg = new { action = "insertStreamText", text = text };
            await SendMessageAsync(msg);
        }

        public async Task EndStreamInsertAsync()
        {
            await SendMessageAsync(new { action = "endStreamInsert" });
        }

        public async Task UpdateSnippetsAsync(IReadOnlyList<SnippetItem> snippets, IReadOnlyList<string>? autocompleteWords = null)
        {
            var msg = new
            {
                action = "updateSnippets",
                snippets = snippets.Select(s => new
                {
                    title = s.Title ?? string.Empty,
                    keyword = s.Keyword ?? string.Empty,
                    description = s.Description ?? string.Empty,
                    content = s.Content ?? string.Empty
                }).ToArray(),
                autocompleteWords = autocompleteWords ?? Array.Empty<string>()
            };
            await SendMessageAsync(msg);
        }

        public async Task ApplyMarkdownCommandAsync(string command, string? color = null)
        {
            object msg = color != null
                ? (object)new { action = "markdownCommand", command = command, color = color }
                : (object)new { action = "markdownCommand", command = command };
            await SendMessageAsync(msg);
        }

        public async Task SetCsvTableModeAsync(bool enabled)
        {
            await SendMessageAsync(CustomEditorMessageFactory.CreateCsvTableMode(enabled, _localizationService));
        }

        public async Task SyncScrollFromPreviewAsync(int firstLine, double offset)
        {
            var msg = new
            {
                action = "syncScroll",
                firstLine = firstLine,
                offset = offset
            };
            await SendMessageAsync(msg);
        }

        public async Task UpdateScrollSyncStateAsync(bool enabled)
        {
            var msg = new
            {
                action = "scrollSyncChanged",
                enabled = enabled
            };
            await SendMessageAsync(msg);
        }

        public async Task SetInlineLivePreviewAsync(bool enabled, string baseHref)
        {
            var msg = new
            {
                action = "setInlineLivePreview",
                enabled = enabled,
                baseHref = baseHref ?? string.Empty
            };
            await SendMessageAsync(msg);
        }

        private async Task SendMessageAsync(object obj)
        {
            if (!_messageRouter.IsReady) return;

            string json = JsonSerializer.Serialize(obj);
            try
            {
                if (_webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.PostWebMessageAsJson(json);
                }
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to execute script on WebView2: {ex.Message}");
            }
        }

        private async Task SendRequiredMessageAsync(object obj)
        {
            if (!_messageRouter.IsReady || _webView.CoreWebView2 == null)
            {
                throw new InvalidOperationException("편집기 WebView가 준비되지 않았습니다.");
            }

            string json = JsonSerializer.Serialize(obj);
            _webView.CoreWebView2.PostWebMessageAsJson(json);
            await Task.CompletedTask;
        }

        private void HandleEditorReady()
        {
            _ = SetSplitViewAsync(_isSplitView);
            if (_pendingText == null)
            {
                return;
            }

            _ = SetTextAsync(
                _pendingText,
                _pendingSetTextShouldFocus,
                _pendingSetTextDocumentId,
                _pendingSetTextDocumentVersion,
                _pendingSetTextViewId);
            _pendingText = null;
            _pendingSetTextShouldFocus = true;
            _pendingSetTextDocumentId = null;
            _pendingSetTextDocumentVersion = null;
            _pendingSetTextViewId = null;
        }

        private void HandleFlushCompleted(int requestId, long documentVersion)
        {
            TaskCompletionSource<long>? pending = null;
            lock (_flushLock)
            {
                if (_pendingFlushRequests.TryGetValue(requestId, out pending))
                {
                    _pendingFlushRequests.Remove(requestId);
                }
            }

            pending?.TrySetResult(documentVersion);
        }
    }
}
