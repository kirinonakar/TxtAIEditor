using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace TxtAIEditor.Editor
{
    public class MonacoBridge
    {
        private readonly WebView2 _webView;
        private readonly ILocalizationService? _localizationService;
        private bool _isReady = false;
        private string? _pendingText = null;
        private bool _pendingSetTextShouldFocus = true;
        private readonly object _flushLock = new object();
        private readonly Dictionary<int, TaskCompletionSource<bool>> _pendingFlushRequests = new Dictionary<int, TaskCompletionSource<bool>>();
        private int _flushRequestSeq = 0;

        public event Action<bool>? ContentChanged;
        public event Action<string>? SelectionReceived;
        public event Action<int, int>? CursorChanged;
        public event Action? EditorReady;
        public event Action<string>? ShortcutPressed;
        public event Action<int, int, int>? LinesRequested;
        public event Action<int, string, bool>? LineChanged;
        public event Action<int, string>? LineInsertRequested;
        public event Action<int, string, string>? LineSplitRequested;
        public event Action<int>? MergeLineWithPreviousRequested;
        public event Action<int>? DeleteLineRequested;
        public event Action<string, int, int, bool, bool, bool>? FindRequested;
        public event Action<string, bool, bool>? FindAllRequested;
        public event Action<string, string, bool, bool>? ReplaceAllRequested;
        public event Action<int, double>? ScrollChanged;
        public event Action<bool>? ScrollSyncChanged;
        public event Action? EditorRendered;

        public MonacoBridge(WebView2 webView, ILocalizationService? localizationService = null)
        {
            _webView = webView;
            _localizationService = localizationService;
            _webView.WebMessageReceived += OnWebMessageReceived;
        }

        private static CoreWebView2Environment? _sharedEnvironment;
        private static readonly object _envLock = new object();

        public static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
        {
            if (_sharedEnvironment != null)
            {
                return _sharedEnvironment;
            }

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string cacheFolder = System.IO.Path.Combine(localAppData, "TxtAIEditor", "WebView2Cache");
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, cacheFolder, null);
                lock (_envLock)
                {
                    _sharedEnvironment ??= env;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create shared WebView2 environment: {ex.Message}");
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, null, null);
                lock (_envLock)
                {
                    _sharedEnvironment ??= env;
                }
            }

            return _sharedEnvironment!;
        }

        public async Task InitializeAsync()
        {
            try
            {
                var env = await GetSharedEnvironmentAsync();
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

        public async Task SetTextAsync(string text, bool shouldFocus = true)
        {
            if (!_isReady)
            {
                _pendingText = text;
                _pendingSetTextShouldFocus = shouldFocus;
                return;
            }

            var msg = new { action = "setText", text = text, shouldFocus = shouldFocus };
            await SendMessageAsync(msg);
        }

        public async Task ResetOriginalLinesAsync(IReadOnlyList<string> lines)
        {
            var msg = new { action = "resetOriginalLines", lines = lines };
            await SendMessageAsync(msg);
        }

        public async Task UpdateDirtyLinesAsync(Dictionary<int, string> dirtyLines)
        {
            var msg = new { action = "updateDirtyLines", dirtyLines = dirtyLines };
            await SendMessageAsync(msg);
        }

        public async Task InitializeModelAsync(
            int lineCount,
            string language,
            EditorSettings settings,
            bool isReadOnly = false,
            IReadOnlyList<string>? initialLines = null)
        {
            var msg = new
            {
                action = "initModel",
                lineCount = Math.Max(1, lineCount),
                initialStartLine = 1,
                initialLines = initialLines ?? Array.Empty<string>(),
                language = language,
                theme = settings.Theme,
                wordWrap = settings.WordWrap,
                bracketPairColorization = settings.BracketPairColorization,
                fontSize = settings.FontSize,
                fontFamily = settings.FontFamily,
                tabSize = settings.TabSize,
                customBackgroundColor = settings.CustomBackgroundColor,
                customForegroundColor = settings.CustomForegroundColor,
                autocompleteOnEnter = settings.AutocompleteOnEnter,
                autocompleteOnTab = settings.AutocompleteOnTab,
                readOnly = isReadOnly,
                findPlaceholder = _localizationService?.GetString("EditorFindPlaceholder", "찾기") ?? "찾기",
                replacePlaceholder = _localizationService?.GetString("EditorReplacePlaceholder", "바꾸기") ?? "바꾸기",
                replaceButton = _localizationService?.GetString("EditorReplaceButton", "바꾸기") ?? "바꾸기",
                replaceAllButton = _localizationService?.GetString("EditorReplaceAllButton", "모두 바꾸기") ?? "모두 바꾸기",
                findPrevTooltip = _localizationService?.GetString("EditorFindPrevTooltip", "이전") ?? "이전",
                findNextTooltip = _localizationService?.GetString("EditorFindNextTooltip", "다음") ?? "다음",
                findCloseTooltip = _localizationService?.GetString("EditorFindCloseTooltip", "닫기") ?? "닫기",
                menuCut = _localizationService?.GetString("EditorContextMenuCut", "잘라내기") ?? "잘라내기",
                menuCopy = _localizationService?.GetString("EditorContextMenuCopy", "복사") ?? "복사",
                menuPaste = _localizationService?.GetString("EditorContextMenuPaste", "붙여넣기") ?? "붙여넣기",
                menuDelete = _localizationService?.GetString("EditorContextMenuDelete", "삭제") ?? "삭제",
                menuSelectAll = _localizationService?.GetString("EditorContextMenuSelectAll", "모두 선택") ?? "모두 선택",
                menuToggleComment = _localizationService?.GetString("EditorContextMenuToggleComment", "주석 토글") ?? "주석 토글",
                menuIndent = _localizationService?.GetString("EditorContextMenuIndent", "들여쓰기") ?? "들여쓰기",
                menuOutdent = _localizationService?.GetString("EditorContextMenuOutdent", "내여쓰기") ?? "내여쓰기",
                menuLineCleanup = _localizationService?.GetString("EditorContextMenuLineCleanup", "줄 정리") ?? "줄 정리",
                menuSortAsc = _localizationService?.GetString("EditorContextMenuSortAsc", "오름차순 정렬") ?? "오름차순 정렬",
                menuSortDesc = _localizationService?.GetString("EditorContextMenuSortDesc", "내림차순 정렬") ?? "내림차순 정렬",
                menuRemoveDuplicates = _localizationService?.GetString("EditorContextMenuRemoveDuplicates", "중복 줄 제거") ?? "중복 줄 제거",
                menuRemoveEmptyLines = _localizationService?.GetString("EditorContextMenuRemoveEmptyLines", "빈 줄 제거") ?? "빈 줄 제거",
                menuCollapseConsecutiveEmptyLines = _localizationService?.GetString("EditorContextMenuCollapseConsecutiveEmptyLines", "연속 빈줄 하나로 줄이기") ?? "연속 빈줄 하나로 줄이기",
                menuTrimSpaces = _localizationService?.GetString("EditorContextMenuTrimSpaces", "앞뒤 공백 제거") ?? "앞뒤 공백 제거",
                menuConvert = _localizationService?.GetString("EditorContextMenuConvert", "변환") ?? "변환",
                menuToUpperCase = _localizationService?.GetString("EditorContextMenuToUpperCase", "대문자로") ?? "대문자로",
                menuToLowerCase = _localizationService?.GetString("EditorContextMenuToLowerCase", "소문자로") ?? "소문자로",
                menuToSentenceCase = _localizationService?.GetString("EditorContextMenuToSentenceCase", "Sentence case") ?? "Sentence case",
                menuToTitleCase = _localizationService?.GetString("EditorContextMenuToTitleCase", "Title case") ?? "Title case",
                menuUrlEncode = _localizationService?.GetString("EditorContextMenuUrlEncode", "URL Encode") ?? "URL Encode",
                menuUrlDecode = _localizationService?.GetString("EditorContextMenuUrlDecode", "URL Decode") ?? "URL Decode",
                menuBase64Encode = _localizationService?.GetString("EditorContextMenuBase64Encode", "Base64 Encode") ?? "Base64 Encode",
                menuBase64Decode = _localizationService?.GetString("EditorContextMenuBase64Decode", "Base64 Decode") ?? "Base64 Decode",
                menuHexToDec = _localizationService?.GetString("EditorContextMenuHexToDec", "HEX → DEC") ?? "HEX → DEC",
                menuDecToHex = _localizationService?.GetString("EditorContextMenuDecToHex", "DEC → HEX") ?? "DEC → HEX",
                menuFormatText = _localizationService?.GetString("EditorContextMenuFormatText", "Format text") ?? "Format text",
                menuScrollSync = _localizationService?.GetString("EditorContextMenuScrollSync", "스크롤 동기화") ?? "스크롤 동기화",
                autocompleteSnippet = _localizationService?.GetString("EditorAutocompleteSnippet", "스니펫") ?? "스니펫",
                autocompleteSnippetPrefix = _localizationService?.GetString("EditorAutocompleteSnippetPrefix", "스니펫:") ?? "스니펫:",
                csvNameBoxPlaceholder = _localizationService?.GetString("CsvNameBoxPlaceholder", "셀") ?? "셀",
                csvFormulaPlaceholder = _localizationService?.GetString("CsvFormulaPlaceholder", "선택한 CSV 셀 값") ?? "선택한 CSV 셀 값"
            };
            await SendMessageAsync(msg);
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

        public async Task UpdateLineAsync(int lineNumber, string text, bool isComposing = false)
        {
            await SendMessageAsync(new
            {
                action = "updateLine",
                lineNumber = Math.Max(1, lineNumber),
                text = text ?? string.Empty,
                isComposing = isComposing
            });
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

        public async Task SetLanguageAsync(string filePath)
        {
            if (!filePath.Contains(System.IO.Path.DirectorySeparatorChar) &&
                !filePath.Contains(System.IO.Path.AltDirectorySeparatorChar) &&
                !filePath.Contains('.') &&
                !string.IsNullOrWhiteSpace(filePath))
            {
                await SendMessageAsync(new { action = "setLanguage", language = filePath });
                return;
            }

            string name = System.IO.Path.GetFileName(filePath);
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
            {
                await SendMessageAsync(new { action = "setLanguage", language = "dockerfile" });
                return;
            }
            if (name.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
            {
                await SendMessageAsync(new { action = "setLanguage", language = "makefile" });
                return;
            }

            string ext = System.IO.Path.GetExtension(filePath).ToLower();
            string lang = ext switch
            {
                ".cs" => "csharp",
                ".fs" => "fsharp",
                ".vb" => "vb",
                ".js" => "javascript",
                ".jsx" => "javascript",
                ".mjs" => "javascript",
                ".cjs" => "javascript",
                ".ts" => "typescript",
                ".tsx" => "typescript",
                ".mts" => "typescript",
                ".cts" => "typescript",
                ".html" => "html",
                ".htm" => "html",
                ".css" => "css",
                ".scss" => "scss",
                ".less" => "less",
                ".json" => "json",
                ".jsonc" => "json",
                ".md" => "markdown",
                ".markdown" => "markdown",
                ".py" => "python",
                ".cpp" => "cpp",
                ".cxx" => "cpp",
                ".cc" => "cpp",
                ".c" => "cpp",
                ".h" => "cpp",
                ".hpp" => "cpp",
                ".xml" => "xml",
                ".xaml" => "xml",
                ".sql" => "sql",
                ".sh" => "shell",
                ".bash" => "shell",
                ".zsh" => "shell",
                ".ps1" => "powershell",
                ".psm1" => "powershell",
                ".psd1" => "powershell",
                ".tex" => "latex",
                ".diff" => "diff",
                ".rs" => "rust",
                ".go" => "go",
                ".java" => "java",
                ".kt" => "kotlin",
                ".kts" => "kotlin",
                ".swift" => "swift",
                ".php" => "php",
                ".rb" => "ruby",
                ".dart" => "dart",
                ".lua" => "lua",
                ".r" => "r",
                ".rprofile" => "r",
                ".dockerfile" => "dockerfile",
                ".toml" => "toml",
                ".ini" => "ini",
                ".yml" => "yaml",
                ".yaml" => "yaml",
                ".reg" => "reg",
                _ => "plaintext"
            };

            var msg = new { action = "setLanguage", language = lang };
            await SendMessageAsync(msg);
        }

        public async Task UpdateOptionsAsync(EditorSettings settings, bool isReadOnly = false)
        {
            var msg = new
            {
                action = "updateOptions",
                theme = settings.Theme,
                wordWrap = settings.WordWrap,
                bracketPairColorization = settings.BracketPairColorization,
                fontSize = settings.FontSize,
                fontFamily = settings.FontFamily,
                tabSize = settings.TabSize,
                customBackgroundColor = settings.CustomBackgroundColor,
                customForegroundColor = settings.CustomForegroundColor,
                autocompleteOnEnter = settings.AutocompleteOnEnter,
                autocompleteOnTab = settings.AutocompleteOnTab,
                readOnly = isReadOnly,
                findPlaceholder = _localizationService?.GetString("EditorFindPlaceholder", "찾기") ?? "찾기",
                replacePlaceholder = _localizationService?.GetString("EditorReplacePlaceholder", "바꾸기") ?? "바꾸기",
                replaceButton = _localizationService?.GetString("EditorReplaceButton", "바꾸기") ?? "바꾸기",
                replaceAllButton = _localizationService?.GetString("EditorReplaceAllButton", "모두 바꾸기") ?? "모두 바꾸기",
                findPrevTooltip = _localizationService?.GetString("EditorFindPrevTooltip", "이전") ?? "이전",
                findNextTooltip = _localizationService?.GetString("EditorFindNextTooltip", "다음") ?? "다음",
                findCloseTooltip = _localizationService?.GetString("EditorFindCloseTooltip", "닫기") ?? "닫기",
                menuCut = _localizationService?.GetString("EditorContextMenuCut", "잘라내기") ?? "잘라내기",
                menuCopy = _localizationService?.GetString("EditorContextMenuCopy", "복사") ?? "복사",
                menuPaste = _localizationService?.GetString("EditorContextMenuPaste", "붙여넣기") ?? "붙여넣기",
                menuDelete = _localizationService?.GetString("EditorContextMenuDelete", "삭제") ?? "삭제",
                menuSelectAll = _localizationService?.GetString("EditorContextMenuSelectAll", "모두 선택") ?? "모두 선택",
                menuToggleComment = _localizationService?.GetString("EditorContextMenuToggleComment", "주석 토글") ?? "주석 토글",
                menuIndent = _localizationService?.GetString("EditorContextMenuIndent", "들여쓰기") ?? "들여쓰기",
                menuOutdent = _localizationService?.GetString("EditorContextMenuOutdent", "내여쓰기") ?? "내여쓰기",
                menuLineCleanup = _localizationService?.GetString("EditorContextMenuLineCleanup", "줄 정리") ?? "줄 정리",
                menuSortAsc = _localizationService?.GetString("EditorContextMenuSortAsc", "오름차순 정렬") ?? "오름차순 정렬",
                menuSortDesc = _localizationService?.GetString("EditorContextMenuSortDesc", "내림차순 정렬") ?? "내림차순 정렬",
                menuRemoveDuplicates = _localizationService?.GetString("EditorContextMenuRemoveDuplicates", "중복 줄 제거") ?? "중복 줄 제거",
                menuRemoveEmptyLines = _localizationService?.GetString("EditorContextMenuRemoveEmptyLines", "빈 줄 제거") ?? "빈 줄 제거",
                menuCollapseConsecutiveEmptyLines = _localizationService?.GetString("EditorContextMenuCollapseConsecutiveEmptyLines", "연속 빈줄 하나로 줄이기") ?? "연속 빈줄 하나로 줄이기",
                menuTrimSpaces = _localizationService?.GetString("EditorContextMenuTrimSpaces", "앞뒤 공백 제거") ?? "앞뒤 공백 제거",
                menuConvert = _localizationService?.GetString("EditorContextMenuConvert", "변환") ?? "변환",
                menuToUpperCase = _localizationService?.GetString("EditorContextMenuToUpperCase", "대문자로") ?? "대문자로",
                menuToLowerCase = _localizationService?.GetString("EditorContextMenuToLowerCase", "소문자로") ?? "소문자로",
                menuToSentenceCase = _localizationService?.GetString("EditorContextMenuToSentenceCase", "Sentence case") ?? "Sentence case",
                menuToTitleCase = _localizationService?.GetString("EditorContextMenuToTitleCase", "Title case") ?? "Title case",
                menuUrlEncode = _localizationService?.GetString("EditorContextMenuUrlEncode", "URL Encode") ?? "URL Encode",
                menuUrlDecode = _localizationService?.GetString("EditorContextMenuUrlDecode", "URL Decode") ?? "URL Decode",
                menuBase64Encode = _localizationService?.GetString("EditorContextMenuBase64Encode", "Base64 Encode") ?? "Base64 Encode",
                menuBase64Decode = _localizationService?.GetString("EditorContextMenuBase64Decode", "Base64 Decode") ?? "Base64 Decode",
                menuHexToDec = _localizationService?.GetString("EditorContextMenuHexToDec", "HEX → DEC") ?? "HEX → DEC",
                menuDecToHex = _localizationService?.GetString("EditorContextMenuDecToHex", "DEC → HEX") ?? "DEC → HEX",
                menuFormatText = _localizationService?.GetString("EditorContextMenuFormatText", "Format text") ?? "Format text",
                menuScrollSync = _localizationService?.GetString("EditorContextMenuScrollSync", "스크롤 동기화") ?? "스크롤 동기화",
                autocompleteSnippet = _localizationService?.GetString("EditorAutocompleteSnippet", "스니펫") ?? "스니펫",
                autocompleteSnippetPrefix = _localizationService?.GetString("EditorAutocompleteSnippetPrefix", "스니펫:") ?? "스니펫:",
                csvNameBoxPlaceholder = _localizationService?.GetString("CsvNameBoxPlaceholder", "셀") ?? "셀",
                csvFormulaPlaceholder = _localizationService?.GetString("CsvFormulaPlaceholder", "선택한 CSV 셀 값") ?? "선택한 CSV 셀 값"
            };
            await SendMessageAsync(msg);
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

        public async Task FlushPendingEditForSaveAsync(int timeoutMs = 700)
        {
            if (!_isReady || _webView.CoreWebView2 == null)
            {
                return;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    await tcs.Task;
                }
                else
                {
                    lock (_flushLock)
                    {
                        _pendingFlushRequests.Remove(requestId);
                    }
                }

                // JS의 compositionend/input 후속 task와 WebMessageReceived의 lineChanged 처리가
                // 저장 로직보다 먼저 끝날 수 있도록 아주 짧게 양보한다.
                await Task.Delay(30);
            }
            catch (Exception ex)
            {
                lock (_flushLock)
                {
                    _pendingFlushRequests.Remove(requestId);
                }
                System.Diagnostics.Debug.WriteLine($"Failed to flush editor before save: {ex.Message}");
            }
        }

        public async Task RevealLineAsync(int lineNum, int indexOfMatch = 0, int matchLength = 0, string query = "")
        {
            var msg = new { action = "revealLine", lineNumber = lineNum, indexOfMatch = indexOfMatch, matchLength = matchLength, query = query };
            await SendMessageAsync(msg);
        }

        public async Task InsertTextAsync(string text)
        {
            var msg = new { action = "insertText", text = text };
            await SendMessageAsync(msg);
        }

        public async Task UpdateSnippetsAsync(IReadOnlyList<SnippetItem> snippets)
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
                }).ToArray()
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
            var msg = new
            {
                action = "setCsvTableMode",
                enabled = enabled,
                csvNameBoxPlaceholder = _localizationService?.GetString("CsvNameBoxPlaceholder", "셀") ?? "셀",
                csvFormulaPlaceholder = _localizationService?.GetString("CsvFormulaPlaceholder", "선택한 CSV 셀 값") ?? "선택한 CSV 셀 값"
            };
            await SendMessageAsync(msg);
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
            if (!_isReady) return;

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

        private void OnWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string json = NormalizeWebMessageJson(args);
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("type", out JsonElement typeProp)) return;

                    string type = typeProp.GetString() ?? string.Empty;

                    switch (type)
                    {
                        case "ready":
                            _isReady = true;
                            EditorReady?.Invoke();
                            if (_pendingText != null)
                            {
                                _ = SetTextAsync(_pendingText, _pendingSetTextShouldFocus);
                                _pendingText = null;
                                _pendingSetTextShouldFocus = true;
                            }
                            break;

                        case "initialRenderComplete":
                            EditorRendered?.Invoke();
                            break;

                        case "contentChanged":
                            {
                                bool isComposing = root.TryGetProperty("isComposing", out JsonElement contentComposingProp) &&
                                    contentComposingProp.ValueKind == JsonValueKind.True;
                                ContentChanged?.Invoke(isComposing);
                            }
                            break;

                        case "requestLines":
                            if (root.TryGetProperty("requestId", out JsonElement requestIdProp) &&
                                root.TryGetProperty("startLine", out JsonElement startLineProp) &&
                                root.TryGetProperty("count", out JsonElement countProp))
                            {
                                LinesRequested?.Invoke(
                                    requestIdProp.GetInt32(),
                                    startLineProp.GetInt32(),
                                    countProp.GetInt32());
                            }
                            break;

                        case "lineChanged":
                            if (root.TryGetProperty("lineNumber", out JsonElement lineNumberProp) &&
                                root.TryGetProperty("text", out JsonElement textProp))
                            {
                                bool isComposing = root.TryGetProperty("isComposing", out JsonElement isComposingProp) &&
                                    isComposingProp.ValueKind == JsonValueKind.True;
                                LineChanged?.Invoke(lineNumberProp.GetInt32(), textProp.GetString() ?? string.Empty, isComposing);
                            }
                            break;

                        case "insertLine":
                            if (root.TryGetProperty("lineNumber", out JsonElement insertLineProp) &&
                                root.TryGetProperty("text", out JsonElement insertTextProp))
                            {
                                LineInsertRequested?.Invoke(insertLineProp.GetInt32(), insertTextProp.GetString() ?? string.Empty);
                            }
                            break;

                        case "splitLine":
                            if (root.TryGetProperty("lineNumber", out JsonElement splitLineProp) &&
                                root.TryGetProperty("before", out JsonElement beforeProp) &&
                                root.TryGetProperty("after", out JsonElement afterProp))
                            {
                                LineSplitRequested?.Invoke(
                                    splitLineProp.GetInt32(),
                                    beforeProp.GetString() ?? string.Empty,
                                    afterProp.GetString() ?? string.Empty);
                            }
                            break;

                        case "mergeLineWithPrevious":
                            if (root.TryGetProperty("lineNumber", out JsonElement mergeLineProp))
                            {
                                MergeLineWithPreviousRequested?.Invoke(mergeLineProp.GetInt32());
                            }
                            break;

                        case "deleteLine":
                            if (root.TryGetProperty("lineNumber", out JsonElement deleteLineProp))
                            {
                                DeleteLineRequested?.Invoke(deleteLineProp.GetInt32());
                            }
                            break;

                        case "find":
                            if (root.TryGetProperty("query", out JsonElement queryProp))
                            {
                                int startLine = root.TryGetProperty("startLine", out JsonElement findLineProp)
                                    ? findLineProp.GetInt32()
                                    : 1;
                                int startColumn = root.TryGetProperty("startColumn", out JsonElement findColumnProp)
                                    ? findColumnProp.GetInt32()
                                    : 1;
                                bool reverse = root.TryGetProperty("reverse", out JsonElement reverseProp) &&
                                    reverseProp.GetBoolean();
                                bool matchCase = root.TryGetProperty("matchCase", out JsonElement matchCaseProp) &&
                                    matchCaseProp.GetBoolean();
                                bool isRegex = root.TryGetProperty("isRegex", out JsonElement isRegexProp) &&
                                    isRegexProp.GetBoolean();

                                FindRequested?.Invoke(
                                    queryProp.GetString() ?? string.Empty,
                                    startLine,
                                    startColumn,
                                    reverse,
                                    matchCase,
                                    isRegex);
                            }
                            break;

                        case "findAll":
                            if (root.TryGetProperty("query", out JsonElement findAllQueryProp))
                            {
                                bool findAllMatchCase = root.TryGetProperty("matchCase", out JsonElement findAllMatchCaseProp) &&
                                    findAllMatchCaseProp.GetBoolean();
                                bool isRegex = root.TryGetProperty("isRegex", out JsonElement isRegexProp) &&
                                    isRegexProp.GetBoolean();
                                FindAllRequested?.Invoke(
                                    findAllQueryProp.GetString() ?? string.Empty,
                                    findAllMatchCase,
                                    isRegex);
                            }
                            break;

                        case "replaceAll":
                            if (root.TryGetProperty("query", out JsonElement replaceAllQueryProp) &&
                                root.TryGetProperty("replace", out JsonElement replaceValProp))
                            {
                                bool replaceMatchCase = root.TryGetProperty("matchCase", out JsonElement replaceMatchCaseProp) &&
                                    replaceMatchCaseProp.GetBoolean();
                                bool replaceIsRegex = root.TryGetProperty("isRegex", out JsonElement replaceIsRegexProp) &&
                                    replaceIsRegexProp.GetBoolean();
                                ReplaceAllRequested?.Invoke(
                                    replaceAllQueryProp.GetString() ?? string.Empty,
                                    replaceValProp.GetString() ?? string.Empty,
                                    replaceMatchCase,
                                    replaceIsRegex);
                            }
                            break;

                        case "cursorChanged":
                            if (root.TryGetProperty("line", out JsonElement lineProp) &&
                                root.TryGetProperty("column", out JsonElement colProp))
                            {
                                CursorChanged?.Invoke(lineProp.GetInt32(), colProp.GetInt32());
                            }
                            break;

                        case "selectionResult":
                            if (root.TryGetProperty("text", out JsonElement selectionProp))
                            {
                                SelectionReceived?.Invoke(selectionProp.GetString() ?? string.Empty);
                            }
                            break;

                        case "editorFlushedForSave":
                            {
                                int requestId = root.TryGetProperty("requestId", out JsonElement flushRequestIdProp)
                                    ? flushRequestIdProp.GetInt32()
                                    : 0;
                                TaskCompletionSource<bool>? pending = null;
                                lock (_flushLock)
                                {
                                    if (_pendingFlushRequests.TryGetValue(requestId, out pending))
                                    {
                                        _pendingFlushRequests.Remove(requestId);
                                    }
                                }
                                pending?.TrySetResult(true);
                            }
                            break;

                        case "shortcut":
                            if (root.TryGetProperty("name", out JsonElement nameProp))
                            {
                                ShortcutPressed?.Invoke(nameProp.GetString() ?? string.Empty);
                            }
                            break;

                        case "editorScroll":
                            if (root.TryGetProperty("firstLine", out JsonElement editorFirstLineProp) &&
                                root.TryGetProperty("offset", out JsonElement editorOffsetProp))
                            {
                                ScrollChanged?.Invoke(editorFirstLineProp.GetInt32(), editorOffsetProp.GetDouble());
                            }
                            break;

                        case "scrollSyncChanged":
                            if (root.TryGetProperty("enabled", out JsonElement enabledProp))
                            {
                                ScrollSyncChanged?.Invoke(enabledProp.GetBoolean());
                            }
                            break;

                        case "clipboardWrite":
                            if (root.TryGetProperty("text", out JsonElement clipboardTextProp))
                            {
                                WriteClipboardText(clipboardTextProp.GetString() ?? string.Empty);
                            }
                            break;

                        case "clipboardRead":
                            {
                                int clipboardRequestId = root.TryGetProperty("requestId", out JsonElement clipboardRequestIdProp)
                                    ? clipboardRequestIdProp.GetInt32()
                                    : 0;
                                _ = SendClipboardReadResultAsync(clipboardRequestId);
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error receiving web message: {ex.Message}");
            }
        }

        private static string NormalizeWebMessageJson(CoreWebView2WebMessageReceivedEventArgs args)
        {
            string json = args.WebMessageAsJson;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.String)
                {
                    return doc.RootElement.GetString() ?? "{}";
                }
            }
            catch
            {
                string? asString = args.TryGetWebMessageAsString();
                if (!string.IsNullOrWhiteSpace(asString))
                {
                    return asString;
                }
            }

            return json;
        }

        private static void WriteClipboardText(string text)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text ?? string.Empty);
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write clipboard text: {ex.Message}");
            }
        }

        private async Task SendClipboardReadResultAsync(int requestId)
        {
            string text = string.Empty;

            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Text))
                {
                    text = await content.GetTextAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read clipboard text: {ex.Message}");
            }

            await SendMessageAsync(new
            {
                action = "clipboardReadResult",
                requestId = requestId,
                text = text
            });
        }
    }
}
