using System;
using System.Collections.Generic;
using System.Linq;

namespace TxtAIEditor.Core.Models
{
    public sealed record ToolbarButtonOption(string Id, string ResourceKey, bool IsRequired = false);

    public static class ToolbarButtonCatalog
    {
        public static IReadOnlyList<ToolbarButtonOption> All { get; } = new[]
        {
            new ToolbarButtonOption("openFile", "OpenFile"),
            new ToolbarButtonOption("saveFile", "SaveFile"),
            new ToolbarButtonOption("saveAsFile", "SaveAsFile"),
            new ToolbarButtonOption("print", "Print"),
            new ToolbarButtonOption("search", "Search"),
            new ToolbarButtonOption("compare", "Compare"),
            new ToolbarButtonOption("terminal", "Terminal"),
            new ToolbarButtonOption("topMost", "TopMost"),
            new ToolbarButtonOption("stickyNote", "StickyNote"),
            new ToolbarButtonOption("wordWrap", "WordWrap"),
            new ToolbarButtonOption("markdown", "Markdown"),
            new ToolbarButtonOption("livePreview", "LivePreview"),
            new ToolbarButtonOption("csvTable", "CsvTable"),
            new ToolbarButtonOption("theme", "Theme"),
            new ToolbarButtonOption("split", "Split"),
            new ToolbarButtonOption("settings", "Settings", IsRequired: true)
        };

        public static IReadOnlyList<string> DefaultOrder { get; } = All
            .Where(option => !option.IsRequired)
            .Select(option => option.Id)
            .ToList();

        public static IReadOnlyList<string> DefaultLeftAlignedButtons { get; } = new[]
        {
            "openFile",
            "saveFile",
            "saveAsFile",
            "print",
            "search"
        };

        public static IReadOnlyList<IReadOnlyList<string>> DefaultGroups { get; } = new[]
        {
            new[] { "openFile", "saveFile", "saveAsFile", "print" },
            new[] { "search" },
            new[] { "compare", "terminal", "topMost", "stickyNote" },
            new[] { "wordWrap" },
            new[] { "markdown", "livePreview", "csvTable" },
            new[] { "theme" },
            new[] { "split" },
            new[] { "settings" }
        };

        public static string NormalizeId(string value)
        {
            return value switch
            {
                "파일 열기" or "Open File" or "ファイルを開く" or "OpenFileButton" => "openFile",
                "저장" or "Save" or "保存" or "SaveFileButton" => "saveFile",
                "다른 이름으로 저장" or "Save As" or "名前を付けて保存" or "SaveAsFileButton" => "saveAsFile",
                "비교" or "Compare" or "比較" or "CompareButton" => "compare",
                "터미널" or "Terminal" or "ターミナル" or "TerminalToggleButton" => "terminal",
                "인쇄" or "Print" or "印刷" or "PrintButton" => "print",
                "항상위" or "TopMost" or "常に手前" or "TopMostToggleButton" => "topMost",
                "스티커" or "Sticky" or "付箋" or "StickyNoteButton" => "stickyNote",
                "Word Wrap" or "右端で折り返す" or "WordWrapToggle" => "wordWrap",
                "검색" or "Search" or "検索" or "SearchButton" => "search",
                "Markdown" or "MarkdownToolbarToggle" => "markdown",
                "라이브" or "라이브 프리뷰" or "Live" or "Live Preview" or "ライブ" or "ライブプレビュー" or "LivePreviewToggle" or "livePreview" => "livePreview",
                "CSV 테이블" or "CSV Table" or "CSVテーブル" or "CsvTableToggle" => "csvTable",
                "테마" or "Theme" or "テーマ" or "ThemeButton" => "theme",
                "분할" or "Split" or "分割" or "SplitButton" => "split",
                "설정" or "Settings" or "設定" or "SettingsButton" => "settings",
                _ => value
            };
        }
    }
}
