using System;
using System.Collections.Generic;
using System.IO;

namespace TxtAIEditor.Core.Models
{
    public class EditorSettings
    {
        public const string DefaultExternalViewerPath = "uviewer";
        public const int DefaultWindowWidth = 1200;
        public const int DefaultWindowHeight = 800;

        public string Theme { get; set; } = "Light";
        public string FontFamily { get; set; } = "Consolas, 'Courier New', monospace";
        public double FontSize { get; set; } = 14.0;
        public bool WordWrap { get; set; } = true;
        public bool SyntaxHighlighting { get; set; } = true;
        public bool ShowDirtyLines { get; set; } = true;
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
        public bool AutoSaveAllowNonGitFolders { get; set; } = false;
        public string PreviewMode { get; set; } = "Markdown";
        public string ExternalViewerPath { get; set; } = DefaultExternalViewerPath;
        public string ExternalViewerArguments { get; set; } = string.Empty;
        public bool LeftSidebarVisible { get; set; } = true;
        public bool RightSidebarVisible { get; set; } = true;
        public string RightSidebarSelectedTab { get; set; } = "LivePreview";
        public double LeftSidebarWidth { get; set; } = 260;
        public double RightSidebarWidth { get; set; } = 400;
        public bool ScrollSyncEnabled { get; set; } = true;
        public bool DefaultMarkdownEnabled { get; set; } = true;
        public bool DefaultMarkdownToolbarEnabled { get; set; } = true;
        public bool StartInTreeMode { get; set; } = false;
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowWidth { get; set; } = DefaultWindowWidth;
        public int WindowHeight { get; set; } = DefaultWindowHeight;
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
        public string LlmModelCerebras { get; set; } = "gemma-4-31b";
        public string LlmModelOpenRouter { get; set; } = "moonshotai/kimi-k2.6:free";
        public string LlmModelLmStudio { get; set; } = "";
        public string LlmModelOpenCodeGo { get; set; } = "";
        public string LlmModelOpenCodeZen { get; set; } = "";
        public string LlmModelOllama { get; set; } = "";
        public string LlmModelOllamaCloud { get; set; } = "";
        public string LlmVisionFallbackProvider { get; set; } = "";
        public string LlmVisionFallbackModel { get; set; } = "";
        public string LlmThinkingLevel { get; set; } = "";
        public bool LlmConfirmBeforeSending { get; set; } = false;
        public bool LlmAgentVerbose { get; set; } = false;
        public bool LlmAgentAutoApproveGitEdits { get; set; } = false;
        public bool LlmAgentAutoApprovePowerShell { get; set; } = false;
        public bool LlmAgentAutoApprovePlanning { get; set; } = false;
        public string LlmSourceLanguage { get; set; } = "Auto";
        public string LlmTargetLanguage { get; set; } = "Korean";
        public int LlmMaxToolCalls { get; set; } = 100;

        // Exa Config
        public string ExaEndpoint { get; set; } = "https://mcp.exa.ai/mcp";

        // ComfyUI built-in MCP plugin
        public string ComfyUiLaunchPath { get; set; } = string.Empty;
        public string ComfyUiWorkflowDirectory { get; set; } = GetDefaultComfyUiWorkflowDirectory();

        // Browser Use built-in MCP plugin
        public bool BrowserUseAllowInteraction { get; set; } = true;
        public bool BrowserUseCaptureEnabled { get; set; } = false;
        public bool BrowserUseComputerUseEnabled { get; set; } = true;

        // Git Config
        public bool AutoGitDetect { get; set; } = true;

        // Favorites
        public List<string> FavoritePaths { get; set; } = new List<string>();
        public List<string> PinnedFavoritePaths { get; set; } = new List<string>();

        // Toolbar Customization
        public List<string> ToolbarButtonOrder { get; set; } = new List<string>();
        public List<string> ToolbarLeftAlignedButtons { get; set; } = new List<string>(ToolbarButtonCatalog.DefaultLeftAlignedButtons);
        public bool ToolbarShowLabels { get; set; } = true;
        public List<string> ToolbarHiddenButtons { get; set; } = new List<string>();

        // Startup
        public string HomeFolderPath { get; set; } = string.Empty;

        // Language
        public string Language { get; set; } = "Default";

        public static string GetDefaultComfyUiWorkflowDirectory()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".TxtAIEditor", "ComfyUI_API_workflow");
        }

        public string ResolveTargetLanguage()
        {
            string tgtLang = LlmTargetLanguage;
            if (string.IsNullOrEmpty(tgtLang) || tgtLang.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                string lang = Language;
                if (string.IsNullOrEmpty(lang) || lang.Equals("Default", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        lang = System.Globalization.CultureInfo.CurrentUICulture.Name;
                    }
                    catch
                    {
                        lang = "en-US";
                    }
                }

                if (lang != null)
                {
                    if (lang.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "Korean";
                    if (lang.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "Japanese";
                    if (lang.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
                        lang.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                        lang.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                        lang.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase))
                    {
                        return "Chinese Traditional";
                    }
                    if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "Chinese Simplified";
                }
                return "English";
            }
            return tgtLang;
        }
    }
}
