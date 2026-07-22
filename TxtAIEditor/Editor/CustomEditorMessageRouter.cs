using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TxtAIEditor.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace TxtAIEditor.Editor
{
    internal sealed class CustomEditorMessageRouter
    {
        private readonly Func<object, Task> _sendMessageAsync;
        private string _documentId = string.Empty;
        private string _viewId = string.Empty;
        private long _lastIncomingSequence;

        public CustomEditorMessageRouter(WebView2 webView, Func<object, Task> sendMessageAsync)
        {
            _sendMessageAsync = sendMessageAsync;
            webView.WebMessageReceived += OnWebMessageReceived;
        }

        public bool IsReady { get; private set; }

        public event Action<bool>? ContentChanged;
        public event Action<string, int, int, long?, long?>? SelectionReceived;
        public event Action<int, int>? CursorChanged;
        public event Action? EditorReady;
        public event Action<string>? ShortcutPressed;
        public event Action<int, int, int>? LinesRequested;
        public event Action<EditorEditRequest>? EditRequested;
        public event Action<long, string>? HexEditRequested;
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
        public event Action? ReadyReceived;
        public event Action<int, long>? FlushCompleted;

        public void SetDocumentContext(string? documentId, string? viewId)
        {
            _documentId = documentId ?? string.Empty;
            _viewId = viewId ?? string.Empty;
            _lastIncomingSequence = 0;
        }

        private void OnWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string json = NormalizeWebMessageJson(args);
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (!EditorProtocol.TryAcceptIncoming(
                    root,
                    _documentId,
                    _viewId,
                    ref _lastIncomingSequence,
                    out string type))
                {
                    return;
                }

                RouteMessage(type, root);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error receiving web message: {ex.Message}");
            }
        }

        private void RouteMessage(string type, JsonElement root)
        {
            switch (type)
            {
                case "ready":
                    IsReady = true;
                    EditorReady?.Invoke();
                    ReadyReceived?.Invoke();
                    break;
                case "initialRenderComplete":
                    EditorRendered?.Invoke();
                    break;
                case "contentChanged":
                    ContentChanged?.Invoke(IsTrue(root, "isComposing"));
                    break;
                case "requestLines":
                    HandleLinesRequested(root);
                    break;
                case "edit":
                    HandleCanonicalEdit(root);
                    break;
                case "lineChanged":
                    HandleLineChanged(root);
                    break;
                case "hexEdit":
                    HandleHexEdit(root);
                    break;
                case "lineEdit":
                    HandleLineEdit(root);
                    break;
                case "rangeEdit":
                    HandleRangeEdit(root);
                    break;
                case "insertLine":
                    HandleInsertLine(root);
                    break;
                case "splitLine":
                    HandleSplitLine(root);
                    break;
                case "mergeLineWithPrevious":
                    HandleLineCommand(root, EditorEditRequestKind.MergeLineWithPrevious);
                    break;
                case "deleteLine":
                    if (!IsTrue(root, "isComposing"))
                    {
                        HandleLineCommand(root, EditorEditRequestKind.DeleteLine);
                    }
                    break;
                case "editTransactionStarted":
                    EditTransactionStarted?.Invoke();
                    break;
                case "editTransactionEnded":
                    EditTransactionEnded?.Invoke();
                    break;
                case "find":
                    HandleFind(root);
                    break;
                case "findAll":
                    HandleFindAll(root);
                    break;
                case "replaceAll":
                    HandleReplaceAll(root);
                    break;
                case "cursorChanged":
                    HandleCursorChanged(root);
                    break;
                case "selectionResult":
                    HandleSelectionResult(root);
                    break;
                case "editorFlushedForSave":
                    HandleFlushCompleted(root);
                    break;
                case "shortcut":
                    HandleShortcut(root);
                    break;
                case "editorScroll":
                    HandleEditorScroll(root);
                    break;
                case "scrollSyncChanged":
                    if (root.TryGetProperty("enabled", out JsonElement enabled))
                    {
                        ScrollSyncChanged?.Invoke(enabled.GetBoolean());
                    }
                    break;
                case "clipboardWrite":
                    if (root.TryGetProperty("text", out JsonElement clipboardText))
                    {
                        WriteClipboardText(clipboardText.GetString() ?? string.Empty);
                    }
                    break;
                case "clipboardRead":
                    _ = SendClipboardReadResultAsync(GetInt32(root, "requestId"));
                    break;
                case "ctrlClick":
                    HandleCtrlClick(root);
                    break;
                case "openableHoverRequest":
                    HandleOpenableHoverRequest(root);
                    break;
            }
        }

        private void HandleLinesRequested(JsonElement root)
        {
            if (root.TryGetProperty("requestId", out JsonElement requestId) &&
                root.TryGetProperty("startLine", out JsonElement startLine) &&
                root.TryGetProperty("count", out JsonElement count))
            {
                LinesRequested?.Invoke(requestId.GetInt32(), startLine.GetInt32(), count.GetInt32());
            }
        }

        private void HandleCanonicalEdit(JsonElement root)
        {
            if (root.TryGetProperty("edit", out JsonElement edit) &&
                edit.ValueKind == JsonValueKind.Object &&
                TryReadTextEdit(edit, out TextEdit textEdit))
            {
                EditRequested?.Invoke(CreateEditRequest(root, EditorEditRequestKind.TextEdit, edit: textEdit));
            }
        }

        private void HandleLineChanged(JsonElement root)
        {
            if (!IsTrue(root, "isComposing") &&
                root.TryGetProperty("lineNumber", out JsonElement lineNumber) &&
                root.TryGetProperty("text", out JsonElement text))
            {
                EditRequested?.Invoke(CreateEditRequest(
                    root,
                    EditorEditRequestKind.ReplaceLine,
                    lineNumber: lineNumber.GetInt32(),
                    after: text.GetString() ?? string.Empty));
            }
        }

        private void HandleHexEdit(JsonElement root)
        {
            if (root.TryGetProperty("offset", out JsonElement offset) &&
                root.TryGetProperty("hex", out JsonElement hex))
            {
                HexEditRequested?.Invoke(offset.GetInt64(), hex.GetString() ?? string.Empty);
            }
        }

        private void HandleLineEdit(JsonElement root)
        {
            if (IsTrue(root, "isComposing") ||
                !root.TryGetProperty("lineNumber", out JsonElement lineNumber) ||
                !root.TryGetProperty("startColumn", out JsonElement startColumn) ||
                !root.TryGetProperty("endColumn", out JsonElement endColumn) ||
                !root.TryGetProperty("text", out JsonElement text))
            {
                return;
            }

            int line = lineNumber.GetInt32();
            EditRequested?.Invoke(CreateEditRequest(
                root,
                EditorEditRequestKind.TextEdit,
                edit: new TextEdit(line, startColumn.GetInt32(), line, endColumn.GetInt32(), text.GetString() ?? string.Empty)));
        }

        private void HandleRangeEdit(JsonElement root)
        {
            if (TryReadTextEdit(root, out TextEdit textEdit))
            {
                EditRequested?.Invoke(CreateEditRequest(root, EditorEditRequestKind.TextEdit, edit: textEdit));
            }
        }

        private void HandleInsertLine(JsonElement root)
        {
            if (root.TryGetProperty("lineNumber", out JsonElement lineNumber) &&
                root.TryGetProperty("text", out JsonElement text))
            {
                EditRequested?.Invoke(CreateEditRequest(
                    root,
                    EditorEditRequestKind.InsertLine,
                    lineNumber: lineNumber.GetInt32(),
                    after: text.GetString() ?? string.Empty));
            }
        }

        private void HandleSplitLine(JsonElement root)
        {
            if (root.TryGetProperty("lineNumber", out JsonElement lineNumber) &&
                root.TryGetProperty("before", out JsonElement before) &&
                root.TryGetProperty("after", out JsonElement after))
            {
                EditRequested?.Invoke(CreateEditRequest(
                    root,
                    EditorEditRequestKind.SplitLine,
                    lineNumber: lineNumber.GetInt32(),
                    before: before.GetString() ?? string.Empty,
                    after: after.GetString() ?? string.Empty));
            }
        }

        private void HandleLineCommand(JsonElement root, EditorEditRequestKind kind)
        {
            if (root.TryGetProperty("lineNumber", out JsonElement lineNumber))
            {
                EditRequested?.Invoke(CreateEditRequest(root, kind, lineNumber: lineNumber.GetInt32()));
            }
        }

        private void HandleFind(JsonElement root)
        {
            if (!root.TryGetProperty("query", out JsonElement query))
            {
                return;
            }

            FindRequested?.Invoke(
                query.GetString() ?? string.Empty,
                GetInt32(root, "startLine", 1),
                GetInt32(root, "startColumn", 1),
                GetBoolean(root, "reverse"),
                GetBoolean(root, "matchCase"),
                GetBoolean(root, "isRegex"));
        }

        private void HandleFindAll(JsonElement root)
        {
            if (root.TryGetProperty("query", out JsonElement query))
            {
                FindAllRequested?.Invoke(
                    query.GetString() ?? string.Empty,
                    GetBoolean(root, "matchCase"),
                    GetBoolean(root, "isRegex"),
                    GetInt32(root, "currentLine", 1));
            }
        }

        private void HandleReplaceAll(JsonElement root)
        {
            if (root.TryGetProperty("query", out JsonElement query) &&
                root.TryGetProperty("replace", out JsonElement replacement))
            {
                ReplaceAllRequested?.Invoke(
                    query.GetString() ?? string.Empty,
                    replacement.GetString() ?? string.Empty,
                    GetBoolean(root, "matchCase"),
                    GetBoolean(root, "isRegex"));
            }
        }

        private void HandleCursorChanged(JsonElement root)
        {
            if (root.TryGetProperty("line", out JsonElement line) &&
                root.TryGetProperty("column", out JsonElement column))
            {
                CursorChanged?.Invoke(line.GetInt32(), column.GetInt32());
            }
        }

        private void HandleSelectionResult(JsonElement root)
        {
            if (root.TryGetProperty("text", out JsonElement text))
            {
                SelectionReceived?.Invoke(
                    text.GetString() ?? string.Empty,
                    GetInt32(root, "startLine"),
                    GetInt32(root, "endLine"),
                    TryGetInt64(root, "hexOffset"),
                    TryGetInt64(root, "hexLength"));
            }
        }

        private void HandleFlushCompleted(JsonElement root)
        {
            int requestId = GetInt32(root, "requestId");
            long documentVersion = TryGetInt64(root, "documentVersion") ?? 0;
            FlushCompleted?.Invoke(requestId, documentVersion);
        }

        private void HandleShortcut(JsonElement root)
        {
            if (root.TryGetProperty("name", out JsonElement name))
            {
                ShortcutPressed?.Invoke(name.GetString() ?? string.Empty);
            }
        }

        private void HandleEditorScroll(JsonElement root)
        {
            if (root.TryGetProperty("firstLine", out JsonElement firstLine) &&
                root.TryGetProperty("offset", out JsonElement offset))
            {
                ScrollChanged?.Invoke(firstLine.GetInt32(), offset.GetDouble());
            }
        }

        private void HandleCtrlClick(JsonElement root)
        {
            if (root.TryGetProperty("text", out JsonElement text))
            {
                CtrlClicked?.Invoke(
                    text.GetString() ?? string.Empty,
                    GetBoolean(root, "isUrl"),
                    GetBoolean(root, "isPath"));
            }
        }

        private void HandleOpenableHoverRequest(JsonElement root)
        {
            if (root.TryGetProperty("requestId", out JsonElement requestId) &&
                root.TryGetProperty("text", out JsonElement text))
            {
                _ = SendOpenableHoverResultAsync(
                    requestId.GetInt32(),
                    text.GetString() ?? string.Empty,
                    GetBoolean(root, "isUrl"),
                    GetBoolean(root, "isPath"));
            }
        }

        private async Task SendOpenableHoverResultAsync(int requestId, string text, bool isUrl, bool isPath)
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
                Debug.WriteLine($"Failed to validate openable hover target: {ex.Message}");
            }

            await _sendMessageAsync(new { action = "openableHoverResult", requestId, isOpenable });
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
                Debug.WriteLine($"Failed to read clipboard text: {ex.Message}");
            }

            await _sendMessageAsync(new { action = "clipboardReadResult", requestId, text });
        }

        private static bool TryReadTextEdit(JsonElement element, out TextEdit edit)
        {
            if (element.TryGetProperty("startLine", out JsonElement startLine) &&
                element.TryGetProperty("startColumn", out JsonElement startColumn) &&
                element.TryGetProperty("endLine", out JsonElement endLine) &&
                element.TryGetProperty("endColumn", out JsonElement endColumn) &&
                element.TryGetProperty("text", out JsonElement text))
            {
                edit = new TextEdit(
                    startLine.GetInt32(),
                    startColumn.GetInt32(),
                    endLine.GetInt32(),
                    endColumn.GetInt32(),
                    text.GetString() ?? string.Empty);
                return true;
            }

            edit = new TextEdit(1, 1, 1, 1, string.Empty);
            return false;
        }

        private static EditorEditRequest CreateEditRequest(
            JsonElement root,
            EditorEditRequestKind kind,
            TextEdit? edit = null,
            int lineNumber = 0,
            string before = "",
            string after = "")
        {
            string documentId = root.GetProperty("documentId").GetString() ?? string.Empty;
            string viewId = root.GetProperty("viewId").GetString() ?? string.Empty;
            long baseVersion = root.GetProperty("baseVersion").GetInt64();
            long editId = root.TryGetProperty("editId", out JsonElement editIdProperty) &&
                editIdProperty.TryGetInt64(out long explicitEditId)
                    ? explicitEditId
                    : root.GetProperty("sequence").GetInt64();
            bool isCompositionCommit = IsTrue(root, "isCompositionCommit");

            return new EditorEditRequest(
                documentId,
                viewId,
                editId,
                baseVersion,
                kind,
                edit ?? new TextEdit(1, 1, 1, 1, string.Empty),
                lineNumber,
                before,
                after,
                isCompositionCommit);
        }

        private static string NormalizeWebMessageJson(CoreWebView2WebMessageReceivedEventArgs args)
        {
            string json = args.WebMessageAsJson;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.String)
                {
                    return document.RootElement.GetString() ?? "{}";
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

        private static bool IsTrue(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static bool GetBoolean(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out JsonElement value) && value.GetBoolean();

        private static int GetInt32(JsonElement root, string propertyName, int fallback = 0) =>
            root.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt32(out int result)
                ? result
                : fallback;

        private static long? TryGetInt64(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long result)
                ? result
                : null;

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
                Debug.WriteLine($"Failed to write clipboard text: {ex.Message}");
            }
        }
    }
}
