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
        private const string HexLanguageName = "hex";
        private const string HexEditorFontFamily = "Consolas, \"Cascadia Mono\", \"Courier New\", monospace";
        private readonly WebView2 _webView;
        private readonly ILocalizationService? _localizationService;
        private bool _isReady = false;
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
        private string _documentId = string.Empty;
        private string _viewId = string.Empty;
        private long _lastIncomingSequence;
        private IReadOnlyList<string>? _currentOriginalLines;
        private Dictionary<int, string>? _currentDirtyLines;

        public event Action<bool>? ContentChanged;
        public event Action<string, int, int, long?, long?>? SelectionReceived;
        public event Action<int, int>? CursorChanged;
        public event Action? EditorReady;
        public event Action<string>? ShortcutPressed;
        public event Action<int, int, int>? LinesRequested;
        public event Action<int, string, bool>? LineChanged;
        public event Action<long, string>? HexEditRequested;
        public event Action<int, int, int, string, bool>? LineEditRequested;
        public event Action<int, int, int, int, string>? RangeEditRequested;
        public event Action<int, string>? LineInsertRequested;
        public event Action<int, string, string>? LineSplitRequested;
        public event Action<int>? MergeLineWithPreviousRequested;
        public event Action<int, bool>? DeleteLineRequested;
        public event Action? EditTransactionStarted;
        public event Action? EditTransactionEnded;
        public event Action<string, int, int, bool, bool, bool>? FindRequested;
        public event Action<string, bool, bool, int>? FindAllRequested;
        public event Action<string, string, bool, bool>? ReplaceAllRequested;
        public event Action<int, double>? ScrollChanged;
        public event Action<bool>? ScrollSyncChanged;
        public event Action? EditorRendered;
        public event Action<string, bool, bool>? CtrlClicked;
        public event Func<string, bool, bool, Task<bool>>? OpenableHoverRequested;

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
                System.IO.Directory.CreateDirectory(cacheFolder);
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, cacheFolder, null);
                lock (_envLock)
                {
                    _sharedEnvironment ??= env;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create shared WebView2 environment: {ex.Message}");
                string fallbackCacheFolder = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "TxtAIEditor",
                    "WebView2Cache");
                System.IO.Directory.CreateDirectory(fallbackCacheFolder);
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, fallbackCacheFolder, null);
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

        public async Task SetTextAsync(
            string text,
            bool shouldFocus = true,
            string? documentId = null,
            long? documentVersion = null,
            string? viewId = null)
        {
            if (!_isReady)
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
                protocolVersion = 1,
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
            string? viewId = null)
        {
            _documentId = documentId ?? string.Empty;
            _viewId = viewId ?? string.Empty;
            _lastIncomingSequence = 0;
            _currentLanguage = string.IsNullOrWhiteSpace(language) ? "plaintext" : language;
            bool isHexLanguage = IsHexLanguage(_currentLanguage);
            var msg = new
            {
                protocolVersion = 1,
                action = "initModel",
                documentId,
                viewId,
                documentVersion,
                lineCount = Math.Max(1, lineCount),
                initialStartLine = 1,
                initialLines = initialLines ?? Array.Empty<string>(),
                language = _currentLanguage,
                theme = settings.Theme,
                wordWrap = isHexLanguage ? false : settings.WordWrap,
                syntaxHighlighting = settings.SyntaxHighlighting,
                bracketPairColorization = settings.BracketPairColorization,
                fontSize = settings.FontSize,
                fontFamily = isHexLanguage ? HexEditorFontFamily : settings.FontFamily,
                tabSize = settings.TabSize,
                customBackgroundColor = settings.CustomBackgroundColor,
                customForegroundColor = settings.CustomForegroundColor,
                autocompleteOnEnter = settings.AutocompleteOnEnter,
                autocompleteOnTab = settings.AutocompleteOnTab,
                readOnly = isReadOnly,
                hexEditable = isHexLanguage,
                isSplitView = _isSplitView,
                findPlaceholder = _localizationService?.GetString("EditorFindPlaceholder", "찾기") ?? "찾기",
                replacePlaceholder = _localizationService?.GetString("EditorReplacePlaceholder", "바꾸기") ?? "바꾸기",
                replaceButton = _localizationService?.GetString("EditorReplaceButton", "바꾸기") ?? "바꾸기",
                replaceAllButton = _localizationService?.GetString("EditorReplaceAllButton", "모두 바꾸기") ?? "모두 바꾸기",
                findClearTooltip = _localizationService?.GetString("EditorFindClearTooltip", "지우기") ?? "지우기",
                findMatchCaseTooltip = _localizationService?.GetString("EditorFindMatchCaseTooltip", "대소문자 구분 (Aa)") ?? "대소문자 구분 (Aa)",
                findRegexTooltip = _localizationService?.GetString("EditorFindRegexTooltip", "정규식 사용 (.*)") ?? "정규식 사용 (.*)",
                replaceClearTooltip = _localizationService?.GetString("EditorReplaceClearTooltip", "지우기") ?? "지우기",
                findPrevTooltip = _localizationService?.GetString("EditorFindPrevTooltip", "이전") ?? "이전",
                findNextTooltip = _localizationService?.GetString("EditorFindNextTooltip", "다음") ?? "다음",
                findCloseTooltip = _localizationService?.GetString("EditorFindCloseTooltip", "닫기") ?? "닫기",
                editorLoadingText = _localizationService?.GetString("EditorLoadingText", "로딩 중...") ?? "로딩 중...",
                longLineProtectionFormat = _localizationService?.GetString("EditorLongLineProtectionFormat", "... too long (전체 {0}자)") ?? "... too long (전체 {0}자)",
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
                csvFormulaPlaceholder = _localizationService?.GetString("CsvFormulaPlaceholder", "선택한 CSV 셀 값") ?? "선택한 CSV 셀 값",
                csvJsonKeyHeader = _localizationService?.GetString("CsvJsonKeyHeader", "키") ?? "키",
                csvJsonValueHeader = _localizationService?.GetString("CsvJsonValueHeader", "값") ?? "값"
            };
            await SendMessageAsync(msg);

            // A split view can inherit its saved baseline and dirty markers before its
            // WebView has finished initializing. Reapply the latest state after initModel,
            // which clears both collections on the JavaScript side.
            if (_currentOriginalLines != null)
            {
                await SendMessageAsync(new { action = "resetOriginalLines", lines = _currentOriginalLines });
            }
            if (_currentDirtyLines != null)
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
                protocolVersion = 1,
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
            long? documentVersion = null)
        {
            await SendMessageAsync(new
            {
                protocolVersion = 1,
                action = "applyEditResult",
                documentId,
                baseVersion,
                documentVersion,
                startLine = Math.Max(1, result.StartLine),
                oldLineCount = Math.Max(0, result.OldLineCount),
                lineCount = Math.Max(1, result.DocumentLineCount),
                lines = result.LinesToRefresh,
                caret = result.Caret is CaretState caret
                    ? new { line = Math.Max(1, caret.LineNumber), column = Math.Max(1, caret.Column) }
                    : null
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
                _currentLanguage = filePath;
                await SendMessageAsync(new { action = "setLanguage", language = filePath });
                return;
            }

            string name = System.IO.Path.GetFileName(filePath);
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
            {
                _currentLanguage = "dockerfile";
                await SendMessageAsync(new { action = "setLanguage", language = "dockerfile" });
                return;
            }
            if (name.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
            {
                _currentLanguage = "makefile";
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
                ".resw" => "xml",
                ".appxmanifest" => "xml",
                ".csproj" => "xml",
                ".manifest" => "xml",
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

            _currentLanguage = lang;
            var msg = new { action = "setLanguage", language = lang };
            await SendMessageAsync(msg);
        }

        public async Task UpdateOptionsAsync(EditorSettings settings, bool isReadOnly = false)
        {
            bool isHexLanguage = IsHexLanguage(_currentLanguage);
            var msg = new
            {
                action = "updateOptions",
                theme = settings.Theme,
                wordWrap = isHexLanguage ? false : settings.WordWrap,
                syntaxHighlighting = settings.SyntaxHighlighting,
                bracketPairColorization = settings.BracketPairColorization,
                fontSize = settings.FontSize,
                fontFamily = isHexLanguage ? HexEditorFontFamily : settings.FontFamily,
                tabSize = settings.TabSize,
                customBackgroundColor = settings.CustomBackgroundColor,
                customForegroundColor = settings.CustomForegroundColor,
                autocompleteOnEnter = settings.AutocompleteOnEnter,
                autocompleteOnTab = settings.AutocompleteOnTab,
                readOnly = isReadOnly,
                hexEditable = isHexLanguage,
                findPlaceholder = _localizationService?.GetString("EditorFindPlaceholder", "찾기") ?? "찾기",
                replacePlaceholder = _localizationService?.GetString("EditorReplacePlaceholder", "바꾸기") ?? "바꾸기",
                replaceButton = _localizationService?.GetString("EditorReplaceButton", "바꾸기") ?? "바꾸기",
                replaceAllButton = _localizationService?.GetString("EditorReplaceAllButton", "모두 바꾸기") ?? "모두 바꾸기",
                findClearTooltip = _localizationService?.GetString("EditorFindClearTooltip", "지우기") ?? "지우기",
                findMatchCaseTooltip = _localizationService?.GetString("EditorFindMatchCaseTooltip", "대소문자 구분 (Aa)") ?? "대소문자 구분 (Aa)",
                findRegexTooltip = _localizationService?.GetString("EditorFindRegexTooltip", "정규식 사용 (.*)") ?? "정규식 사용 (.*)",
                replaceClearTooltip = _localizationService?.GetString("EditorReplaceClearTooltip", "지우기") ?? "지우기",
                findPrevTooltip = _localizationService?.GetString("EditorFindPrevTooltip", "이전") ?? "이전",
                findNextTooltip = _localizationService?.GetString("EditorFindNextTooltip", "다음") ?? "다음",
                findCloseTooltip = _localizationService?.GetString("EditorFindCloseTooltip", "닫기") ?? "닫기",
                editorLoadingText = _localizationService?.GetString("EditorLoadingText", "로딩 중...") ?? "로딩 중...",
                longLineProtectionFormat = _localizationService?.GetString("EditorLongLineProtectionFormat", "... too long (전체 {0}자)") ?? "... too long (전체 {0}자)",
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
                csvFormulaPlaceholder = _localizationService?.GetString("CsvFormulaPlaceholder", "선택한 CSV 셀 값") ?? "선택한 CSV 셀 값",
                csvJsonKeyHeader = _localizationService?.GetString("CsvJsonKeyHeader", "키") ?? "키",
                csvJsonValueHeader = _localizationService?.GetString("CsvJsonValueHeader", "값") ?? "값"
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

        public async Task<long?> FlushPendingEditForSaveAsync(int timeoutMs = 700)
        {
            if (!_isReady || _webView.CoreWebView2 == null)
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
            var msg = new
            {
                action = "setCsvTableMode",
                enabled = enabled,
                csvNameBoxPlaceholder = _localizationService?.GetString("CsvNameBoxPlaceholder", "셀") ?? "셀",
                csvFormulaPlaceholder = _localizationService?.GetString("CsvFormulaPlaceholder", "선택한 CSV 셀 값") ?? "선택한 CSV 셀 값",
                csvJsonKeyHeader = _localizationService?.GetString("CsvJsonKeyHeader", "키") ?? "키",
                csvJsonValueHeader = _localizationService?.GetString("CsvJsonValueHeader", "값") ?? "값"
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

        private static bool IsHexLanguage(string? language)
        {
            return string.Equals(language, HexLanguageName, StringComparison.OrdinalIgnoreCase);
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
                    if (root.TryGetProperty("documentId", out JsonElement documentIdProp) &&
                        documentIdProp.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(_documentId) &&
                        !string.Equals(documentIdProp.GetString(), _documentId, StringComparison.Ordinal))
                    {
                        return;
                    }
                    if (root.TryGetProperty("viewId", out JsonElement viewIdProp) &&
                        viewIdProp.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(_viewId) &&
                        !string.Equals(viewIdProp.GetString(), _viewId, StringComparison.Ordinal))
                    {
                        return;
                    }
                    if (root.TryGetProperty("sequence", out JsonElement sequenceProp) &&
                        sequenceProp.TryGetInt64(out long sequence))
                    {
                        if (sequence <= _lastIncomingSequence) return;
                        _lastIncomingSequence = sequence;
                    }

                    switch (type)
                    {
                        case "ready":
                            _isReady = true;
                            EditorReady?.Invoke();
                            _ = SetSplitViewAsync(_isSplitView);
                            if (_pendingText != null)
                            {
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

                        case "hexEdit":
                            if (root.TryGetProperty("offset", out JsonElement hexEditOffsetProp) &&
                                root.TryGetProperty("hex", out JsonElement hexEditTextProp))
                            {
                                HexEditRequested?.Invoke(
                                    hexEditOffsetProp.GetInt64(),
                                    hexEditTextProp.GetString() ?? string.Empty);
                            }
                            break;

                        case "lineEdit":
                            if (root.TryGetProperty("lineNumber", out JsonElement editLineNumberProp) &&
                                root.TryGetProperty("startColumn", out JsonElement editStartColumnProp) &&
                                root.TryGetProperty("endColumn", out JsonElement editEndColumnProp) &&
                                root.TryGetProperty("text", out JsonElement editTextProp))
                            {
                                bool isComposing = root.TryGetProperty("isComposing", out JsonElement editComposingProp) &&
                                    editComposingProp.ValueKind == JsonValueKind.True;
                                LineEditRequested?.Invoke(
                                    editLineNumberProp.GetInt32(),
                                    editStartColumnProp.GetInt32(),
                                    editEndColumnProp.GetInt32(),
                                    editTextProp.GetString() ?? string.Empty,
                                    isComposing);
                            }
                            break;

                        case "rangeEdit":
                            if (root.TryGetProperty("startLine", out JsonElement rangeStartLineProp) &&
                                root.TryGetProperty("startColumn", out JsonElement rangeStartColumnProp) &&
                                root.TryGetProperty("endLine", out JsonElement rangeEndLineProp) &&
                                root.TryGetProperty("endColumn", out JsonElement rangeEndColumnProp) &&
                                root.TryGetProperty("text", out JsonElement rangeTextProp))
                            {
                                RangeEditRequested?.Invoke(
                                    rangeStartLineProp.GetInt32(),
                                    rangeStartColumnProp.GetInt32(),
                                    rangeEndLineProp.GetInt32(),
                                    rangeEndColumnProp.GetInt32(),
                                    rangeTextProp.GetString() ?? string.Empty);
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
                                bool isComposing = root.TryGetProperty("isComposing", out JsonElement delComposingProp) &&
                                    delComposingProp.ValueKind == JsonValueKind.True;
                                DeleteLineRequested?.Invoke(deleteLineProp.GetInt32(), isComposing);
                            }
                            break;

                        case "editTransactionStarted":
                            EditTransactionStarted?.Invoke();
                            break;

                        case "editTransactionEnded":
                            EditTransactionEnded?.Invoke();
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
                                int currentLine = root.TryGetProperty("currentLine", out JsonElement currentLineProp) && currentLineProp.TryGetInt32(out int cl)
                                    ? cl
                                    : 1;
                                FindAllRequested?.Invoke(
                                    findAllQueryProp.GetString() ?? string.Empty,
                                    findAllMatchCase,
                                    isRegex,
                                    currentLine);
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
                                int selStartLine = root.TryGetProperty("startLine", out JsonElement selStartProp) && selStartProp.TryGetInt32(out int sl) ? sl : 0;
                                int selEndLine = root.TryGetProperty("endLine", out JsonElement selEndProp) && selEndProp.TryGetInt32(out int el) ? el : 0;
                                long? hexOffset = TryGetInt64(root, "hexOffset");
                                long? hexLength = TryGetInt64(root, "hexLength");
                                SelectionReceived?.Invoke(selectionProp.GetString() ?? string.Empty, selStartLine, selEndLine, hexOffset, hexLength);
                            }
                            break;

                        case "editorFlushedForSave":
                            {
                                int requestId = root.TryGetProperty("requestId", out JsonElement flushRequestIdProp)
                                    ? flushRequestIdProp.GetInt32()
                                    : 0;
                                long documentVersion = root.TryGetProperty("documentVersion", out JsonElement flushVersionProp) &&
                                    flushVersionProp.TryGetInt64(out long parsedVersion)
                                        ? parsedVersion
                                        : 0;
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

                        case "ctrlClick":
                            if (root.TryGetProperty("text", out JsonElement ctrlClickTextProp))
                            {
                                bool isUrl = root.TryGetProperty("isUrl", out JsonElement isUrlProp) && isUrlProp.GetBoolean();
                                bool isPath = root.TryGetProperty("isPath", out JsonElement isPathProp) && isPathProp.GetBoolean();
                                CtrlClicked?.Invoke(ctrlClickTextProp.GetString() ?? string.Empty, isUrl, isPath);
                            }
                            break;

                        case "openableHoverRequest":
                            if (root.TryGetProperty("requestId", out JsonElement hoverRequestIdProp) &&
                                root.TryGetProperty("text", out JsonElement hoverTextProp))
                            {
                                bool isUrl = root.TryGetProperty("isUrl", out JsonElement isUrlProp) && isUrlProp.GetBoolean();
                                bool isPath = root.TryGetProperty("isPath", out JsonElement isPathProp) && isPathProp.GetBoolean();
                                _ = HandleOpenableHoverRequestAsync(
                                    hoverRequestIdProp.GetInt32(),
                                    hoverTextProp.GetString() ?? string.Empty,
                                    isUrl,
                                    isPath);
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

        private static long? TryGetInt64(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt64(out long result))
            {
                return null;
            }

            return result;
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

        private async Task HandleOpenableHoverRequestAsync(int requestId, string text, bool isUrl, bool isPath)
        {
            bool isOpenable = false;
            try
            {
                var handler = OpenableHoverRequested;
                if (handler != null)
                {
                    isOpenable = await handler(text, isUrl, isPath);
                }
                else if (isUrl)
                {
                    isOpenable = Uri.TryCreate(text, UriKind.Absolute, out _);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to validate openable hover target: {ex.Message}");
            }

            await SendMessageAsync(new
            {
                action = "openableHoverResult",
                requestId,
                isOpenable
            });
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
