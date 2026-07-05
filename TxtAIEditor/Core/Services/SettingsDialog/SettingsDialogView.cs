using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    internal sealed class SettingsDialogView : UserControl
    {
        private const int LlmTabIndex = 4;

        private readonly SettingsAppearancePanel _appearancePanel;
        private readonly SettingsEditingPanel _editingPanel;
        private readonly SettingsTerminalPanel _terminalPanel;
        private readonly SettingsToolbarPanel _toolbarPanel;
        private readonly SettingsLlmPanel _llmPanel;
        private readonly SettingsAboutPanel _aboutPanel;

        private SettingsDialogView(
            SettingsAppearancePanel appearancePanel,
            SettingsEditingPanel editingPanel,
            SettingsTerminalPanel terminalPanel,
            SettingsToolbarPanel toolbarPanel,
            SettingsLlmPanel llmPanel,
            SettingsShortcutsPanel shortcutsPanel,
            SettingsAboutPanel aboutPanel,
            Func<string, string, string> getString,
            string? initialTab)
        {
            _appearancePanel = appearancePanel;
            _editingPanel = editingPanel;
            _terminalPanel = terminalPanel;
            _toolbarPanel = toolbarPanel;
            _llmPanel = llmPanel;
            _aboutPanel = aboutPanel;

            Pivot = new Pivot { Width = 500, Height = 440, FontSize = 12 };
            Pivot.Items.Add(CreateTab(getString("SettingsAppearance", "모양"), appearancePanel));
            Pivot.Items.Add(CreateTab(getString("SettingsEditing", "편집"), editingPanel));
            Pivot.Items.Add(CreateTab(getString("SettingsTerminal", "터미널"), terminalPanel));
            Pivot.Items.Add(CreateTab(getString("SettingsToolbarCustomization", "툴바"), toolbarPanel));
            Pivot.Items.Add(CreateTab(getString("SettingsLLM", "LLM"), llmPanel));
            Pivot.Items.Add(CreateTab(getString("SettingsShortcuts", "단축키"), shortcutsPanel));
            Pivot.Items.Add(CreateTab(getString("SettingsAbout", "정보"), aboutPanel));

            if (initialTab != null && initialTab.Equals("LLM", StringComparison.OrdinalIgnoreCase))
            {
                Pivot.SelectedIndex = LlmTabIndex;
            }

            Content = Pivot;
        }

        public Pivot Pivot { get; }
        public event EventHandler? SettingsImported;
        public event Action<string, string>? OpenTextInEditorRequested;

        public static async Task<SettingsDialogView> CreateAsync(
            EditorSettings settings,
            ILLMService llmService,
            Func<string, string, string> getString,
            Action<object>? initializePickerWindow,
            string? initialTab)
        {
            var fontFamilies = SettingsFontCatalog.GetInstalledFontFamilies();
            var appearancePanel = new SettingsAppearancePanel(settings, fontFamilies, getString);
            var editingPanel = new SettingsEditingPanel(settings, getString, initializePickerWindow);
            var terminalPanel = new SettingsTerminalPanel(settings, fontFamilies, getString);
            var toolbarPanel = new SettingsToolbarPanel(settings, getString);
            var llmPanel = await SettingsLlmPanel.CreateAsync(settings, llmService, getString);
            var shortcutsPanel = new SettingsShortcutsPanel(getString);
            var aboutPanel = new SettingsAboutPanel(getString);

            var view = new SettingsDialogView(
                appearancePanel,
                editingPanel,
                terminalPanel,
                toolbarPanel,
                llmPanel,
                shortcutsPanel,
                aboutPanel,
                getString,
                initialTab);
            editingPanel.SettingsImported += (_, _) => view.SettingsImported?.Invoke(view, EventArgs.Empty);
            llmPanel.OpenTextInEditorRequested += (title, content) => view.OpenTextInEditorRequested?.Invoke(title, content);
            return view;
        }

        public void ApplyToSettings(EditorSettings settings)
        {
            _appearancePanel.ApplyToSettings(settings);
            _editingPanel.ApplyToSettings(settings);
            _terminalPanel.ApplyToSettings(settings);
            _llmPanel.ApplyToSettings(settings);
            _toolbarPanel.ApplyToSettings(settings);
        }

        public Task SaveSecretsAsync(EditorSettings settings)
        {
            return _llmPanel.SaveSecretsAsync(settings);
        }

        public string CreateApiKeyStatusMessage(EditorSettings settings)
        {
            return _llmPanel.CreateApiKeyStatusMessage(settings);
        }

        public void RestoreCustomFontSizes()
        {
            _aboutPanel.RestoreCustomFontSizes();
        }

        private static PivotItem CreateTab(string header, UserControl content)
        {
            return new PivotItem
            {
                Header = new TextBlock { Text = header, FontSize = 13 },
                Content = new ScrollViewer
                {
                    Content = content,
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollMode = ScrollMode.Enabled
                }
            };
        }
    }
}
