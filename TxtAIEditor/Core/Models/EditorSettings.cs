using System;
using System.Collections.Generic;

namespace TxtAIEditor.Core.Models
{
    public class EditorSettings
    {
        public string Theme { get; set; } = "Dark";
        public string FontFamily { get; set; } = "Consolas, 'Courier New', monospace";
        public double FontSize { get; set; } = 14.0;
        public bool WordWrap { get; set; } = true;
        public int TabSize { get; set; } = 4;
        public bool AutocompleteOnEnter { get; set; } = true;
        public bool AutocompleteOnTab { get; set; } = true;
        public bool BracketPairColorization { get; set; } = true;
        public long LargeFileThresholdMB { get; set; } = 50;
        
        // Personalization Settings
        public string CustomBackgroundColor { get; set; } = string.Empty;
        public string CustomForegroundColor { get; set; } = string.Empty;
        public string UiFontFamily { get; set; } = "Segoe UI, Malgun Gothic";
        public string MarkdownToolbarBackgroundColor { get; set; } = string.Empty;
        public string PreviewFontFamily { get; set; } = "Segoe UI, Malgun Gothic, Arial, sans-serif";
        public double PreviewFontSize { get; set; } = 15.0;
        public string PreviewCustomBackgroundColor { get; set; } = string.Empty;
        public string PreviewCustomForegroundColor { get; set; } = string.Empty;
        public bool AutoSave { get; set; } = false;
        public string PreviewMode { get; set; } = "Markdown";
        public bool LeftSidebarVisible { get; set; } = true;
        public bool RightSidebarVisible { get; set; } = true;
        public bool DefaultMarkdownEnabled { get; set; } = true;
        public bool DefaultMarkdownToolbarEnabled { get; set; } = false;
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowWidth { get; set; } = 1200;
        public int WindowHeight { get; set; } = 800;
        public double TerminalPanelHeight { get; set; } = 220;
        public string TerminalProfile { get; set; } = "PowerShell";
        public string TerminalFontFamily { get; set; } = "Consolas";
        public double TerminalFontSize { get; set; } = 13.0;

        // LLM Config
        public string LlmProvider { get; set; } = "OpenAI";
        public string LlmEndpoint { get; set; } = "https://api.openai.com/v1";
        public string LlmModel { get; set; } = "gpt-5.5";
        public string LlmModelGemini { get; set; } = "gemini-flash-lite-latest";
        public string LlmModelOpenAI { get; set; } = "gpt-5.5";
        public string LlmModelOpenRouter { get; set; } = "moonshotai/kimi-k2.6:free";
        public string LlmModelLmStudio { get; set; } = "";
        public bool LlmConfirmBeforeSending { get; set; } = false;
        public string LlmSourceLanguage { get; set; } = "Auto";
        public string LlmTargetLanguage { get; set; } = "Korean";

        // Exa Config
        public string ExaEndpoint { get; set; } = "https://mcp.exa.ai/mcp";

        // Git Config
        public bool AutoGitDetect { get; set; } = true;

        // Favorites
        public List<string> FavoritePaths { get; set; } = new List<string>();
        public List<string> PinnedFavoritePaths { get; set; } = new List<string>();

        // Toolbar Customization
        public List<string> ToolbarButtonOrder { get; set; } = new List<string>();
        public bool ToolbarShowLabels { get; set; } = true;
        public List<string> ToolbarHiddenButtons { get; set; } = new List<string>();

        // Language
        public string Language { get; set; } = "Default";
    }
}
