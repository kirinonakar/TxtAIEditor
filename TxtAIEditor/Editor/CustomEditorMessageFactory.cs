using System;
using System.Collections.Generic;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Editor
{
    internal static class CustomEditorMessageFactory
    {
        private const string HexLanguageName = "hex";
        private const string HexEditorFontFamily = "Consolas, \"Cascadia Mono\", \"Courier New\", monospace";

        public static object CreateInitializeModel(
            int lineCount,
            string language,
            EditorSettings settings,
            bool isReadOnly,
            IReadOnlyList<string>? initialLines,
            string? documentId,
            long documentVersion,
            string? viewId,
            bool isSplitView,
            bool? inlineLivePreviewEnabled,
            string? livePreviewBaseHref,
            ILocalizationService? localizationService)
        {
            Dictionary<string, object?> message = CreateOptions(
                "initModel",
                language,
                settings,
                isReadOnly,
                localizationService);
            message["protocolVersion"] = EditorProtocol.CurrentVersion;
            message["documentId"] = documentId;
            message["viewId"] = viewId;
            message["documentVersion"] = documentVersion;
            message["lineCount"] = Math.Max(1, lineCount);
            message["initialStartLine"] = 1;
            message["initialLines"] = initialLines ?? Array.Empty<string>();
            message["language"] = language;
            message["inlineLivePreviewEnabled"] = inlineLivePreviewEnabled;
            message["livePreviewBaseHref"] = livePreviewBaseHref;
            message["isSplitView"] = isSplitView;
            return message;
        }

        public static object CreateUpdateOptions(
            string language,
            EditorSettings settings,
            bool isReadOnly,
            ILocalizationService? localizationService) =>
            CreateOptions("updateOptions", language, settings, isReadOnly, localizationService);

        public static object CreateCsvTableMode(
            bool enabled,
            ILocalizationService? localizationService)
        {
            var message = new Dictionary<string, object?>
            {
                ["action"] = "setCsvTableMode",
                ["enabled"] = enabled
            };
            AddCsvLocalizedText(message, localizationService);
            return message;
        }

        private static Dictionary<string, object?> CreateOptions(
            string action,
            string language,
            EditorSettings settings,
            bool isReadOnly,
            ILocalizationService? localizationService)
        {
            bool isHexLanguage = string.Equals(language, HexLanguageName, StringComparison.OrdinalIgnoreCase);
            var message = new Dictionary<string, object?>
            {
                ["action"] = action,
                ["theme"] = settings.Theme,
                ["wordWrap"] = isHexLanguage ? false : settings.WordWrap,
                ["syntaxHighlighting"] = settings.SyntaxHighlighting,
                ["showDirtyLines"] = settings.ShowDirtyLines,
                ["bracketPairColorization"] = settings.BracketPairColorization,
                ["fontSize"] = settings.FontSize,
                ["fontFamily"] = isHexLanguage ? HexEditorFontFamily : settings.FontFamily,
                ["tabSize"] = settings.TabSize,
                ["customBackgroundColor"] = settings.CustomBackgroundColor,
                ["customForegroundColor"] = settings.CustomForegroundColor,
                ["autocompleteOnEnter"] = settings.AutocompleteOnEnter,
                ["autocompleteOnTab"] = settings.AutocompleteOnTab,
                ["readOnly"] = isReadOnly,
                ["hexEditable"] = isHexLanguage
            };
            AddLocalizedText(message, localizationService);
            return message;
        }

        private static void AddLocalizedText(
            IDictionary<string, object?> message,
            ILocalizationService? localizationService)
        {
            (string Property, string Resource, string Fallback)[] values =
            {
                ("findPlaceholder", "EditorFindPlaceholder", "찾기"),
                ("replacePlaceholder", "EditorReplacePlaceholder", "바꾸기"),
                ("replaceButton", "EditorReplaceButton", "바꾸기"),
                ("replaceAllButton", "EditorReplaceAllButton", "모두 바꾸기"),
                ("findClearTooltip", "EditorFindClearTooltip", "지우기"),
                ("findMatchCaseTooltip", "EditorFindMatchCaseTooltip", "대소문자 구분 (Aa)"),
                ("findRegexTooltip", "EditorFindRegexTooltip", "정규식 사용 (.*)"),
                ("replaceClearTooltip", "EditorReplaceClearTooltip", "지우기"),
                ("findPrevTooltip", "EditorFindPrevTooltip", "이전"),
                ("findNextTooltip", "EditorFindNextTooltip", "다음"),
                ("findCloseTooltip", "EditorFindCloseTooltip", "닫기"),
                ("editorLoadingText", "EditorLoadingText", "로딩 중..."),
                ("longLineProtectionFormat", "EditorLongLineProtectionFormat", "... too long (전체 {0}자)"),
                ("menuCut", "EditorContextMenuCut", "잘라내기"),
                ("menuCopy", "EditorContextMenuCopy", "복사"),
                ("menuPaste", "EditorContextMenuPaste", "붙여넣기"),
                ("menuDelete", "EditorContextMenuDelete", "삭제"),
                ("menuSelectAll", "EditorContextMenuSelectAll", "모두 선택"),
                ("menuToggleComment", "EditorContextMenuToggleComment", "주석 토글"),
                ("menuIndent", "EditorContextMenuIndent", "들여쓰기"),
                ("menuOutdent", "EditorContextMenuOutdent", "내여쓰기"),
                ("menuLineCleanup", "EditorContextMenuLineCleanup", "줄 정리"),
                ("menuSortAsc", "EditorContextMenuSortAsc", "오름차순 정렬"),
                ("menuSortDesc", "EditorContextMenuSortDesc", "내림차순 정렬"),
                ("menuRemoveDuplicates", "EditorContextMenuRemoveDuplicates", "중복 줄 제거"),
                ("menuRemoveEmptyLines", "EditorContextMenuRemoveEmptyLines", "빈 줄 제거"),
                ("menuCollapseConsecutiveEmptyLines", "EditorContextMenuCollapseConsecutiveEmptyLines", "연속 빈줄 하나로 줄이기"),
                ("menuTrimSpaces", "EditorContextMenuTrimSpaces", "앞뒤 공백 제거"),
                ("menuConvert", "EditorContextMenuConvert", "변환"),
                ("menuToUpperCase", "EditorContextMenuToUpperCase", "대문자로"),
                ("menuToLowerCase", "EditorContextMenuToLowerCase", "소문자로"),
                ("menuToSentenceCase", "EditorContextMenuToSentenceCase", "Sentence case"),
                ("menuToTitleCase", "EditorContextMenuToTitleCase", "Title case"),
                ("menuUrlEncode", "EditorContextMenuUrlEncode", "URL Encode"),
                ("menuUrlDecode", "EditorContextMenuUrlDecode", "URL Decode"),
                ("menuBase64Encode", "EditorContextMenuBase64Encode", "Base64 Encode"),
                ("menuBase64Decode", "EditorContextMenuBase64Decode", "Base64 Decode"),
                ("menuHexToDec", "EditorContextMenuHexToDec", "HEX → DEC"),
                ("menuDecToHex", "EditorContextMenuDecToHex", "DEC → HEX"),
                ("menuFormatText", "EditorContextMenuFormatText", "Format text"),
                ("menuScrollSync", "EditorContextMenuScrollSync", "스크롤 동기화"),
                ("autocompleteSnippet", "EditorAutocompleteSnippet", "스니펫"),
                ("autocompleteSnippetPrefix", "EditorAutocompleteSnippetPrefix", "스니펫:")
            };

            foreach ((string property, string resource, string fallback) in values)
            {
                message[property] = localizationService?.GetString(resource, fallback) ?? fallback;
            }

            AddCsvLocalizedText(message, localizationService);
        }

        private static void AddCsvLocalizedText(
            IDictionary<string, object?> message,
            ILocalizationService? localizationService)
        {
            message["csvNameBoxPlaceholder"] = localizationService?.GetString("CsvNameBoxPlaceholder", "셀") ?? "셀";
            message["csvFormulaPlaceholder"] = localizationService?.GetString("CsvFormulaPlaceholder", "선택한 CSV 셀 값") ?? "선택한 CSV 셀 값";
            message["csvJsonKeyHeader"] = localizationService?.GetString("CsvJsonKeyHeader", "키") ?? "키";
            message["csvJsonValueHeader"] = localizationService?.GetString("CsvJsonValueHeader", "값") ?? "값";
        }
    }
}
