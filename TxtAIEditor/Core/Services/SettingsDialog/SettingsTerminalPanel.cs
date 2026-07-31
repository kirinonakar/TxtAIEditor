using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    internal sealed class SettingsTerminalPanel : UserControl
    {
        private sealed record TerminalProfileChoice(string Id, string Label);

        private readonly ComboBox _terminalProfileCombo;
        private readonly ComboBox _terminalFontFamilyCombo;
        private readonly Slider _terminalSizeSlider;

        public SettingsTerminalPanel(
            EditorSettings settings,
            IReadOnlyList<string> fontFamilies,
            Func<string, string, string> getString)
        {
            _terminalProfileCombo = CreateProfileCombo(settings, getString);
            _terminalFontFamilyCombo = SettingsDialogUi.CreateFontComboBox(settings.TerminalFontFamily, fontFamilies);
            _terminalSizeSlider = new Slider { Minimum = 8, Maximum = 36, Value = Math.Clamp(settings.TerminalFontSize, 8, 36), StepFrequency = 1 };

            var cardContent = new StackPanel { Spacing = 6 };
            SettingsDialogUi.AddLabel(cardContent, getString("SettingsTerminalProfile", "터미널 셸"));
            cardContent.Children.Add(_terminalProfileCombo);
            SettingsDialogUi.AddLabel(cardContent, getString("SettingsTerminalFontFamily", "터미널 폰트"));
            cardContent.Children.Add(_terminalFontFamilyCombo);
            var terminalSizeLabel = new TextBlock { Text = getString("SettingsTerminalFontSize", "터미널 글자 크기") + $" ({settings.TerminalFontSize:0}pt)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            cardContent.Children.Add(terminalSizeLabel);
            cardContent.Children.Add(_terminalSizeSlider);
            _terminalSizeSlider.ValueChanged += (_, args) => terminalSizeLabel.Text = getString("SettingsTerminalFontSize", "터미널 글자 크기") + $" ({args.NewValue:0}pt)";
            cardContent.Children.Add(SettingsDialogUi.CreateMutedTextBlock(
                getString("SettingsTerminalProfileInfo", "PowerShell은 PowerShell 7(pwsh)이 설치되어 있으면 자동으로 PowerShell 7을 사용합니다. CMD, Git Bash, WSL도 선택할 수 있으며 새 설정은 다음 새 터미널부터 적용됩니다.")));

            var card = SettingsDialogUi.CreateCard(
                getString("SettingsTerminalGroup", "터미널 셸 & 폰트"),
                cardContent,
                "\uE756");

            var section = new StackPanel { Spacing = 10, Width = 460, Padding = new Thickness(2, 6, 2, 2) };
            section.Children.Add(card);
            Content = section;
        }

        public void ApplyToSettings(EditorSettings settings)
        {
            settings.TerminalProfile = (_terminalProfileCombo.SelectedItem as TerminalProfileChoice)?.Id ?? "PowerShell";
            settings.TerminalFontFamily = SettingsDialogUi.GetSelectedComboText(_terminalFontFamilyCombo, settings.TerminalFontFamily);
            settings.TerminalFontSize = _terminalSizeSlider.Value;
        }

        private static ComboBox CreateProfileCombo(EditorSettings settings, Func<string, string, string> getString)
        {
            var terminalProfileCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var profile in TerminalShellProfile.GetProfiles())
            {
                string label = profile.IsAvailable
                    ? profile.DisplayName
                    : $"{profile.DisplayName} ({getString("SettingsTerminalNotFound", "설치되지 않음")})";
                terminalProfileCombo.Items.Add(new TerminalProfileChoice(profile.Id, label));
            }

            terminalProfileCombo.DisplayMemberPath = nameof(TerminalProfileChoice.Label);
            string selectedTerminalProfile = TerminalShellProfile.NormalizeId(settings.TerminalProfile);
            terminalProfileCombo.SelectedItem = terminalProfileCombo.Items
                .OfType<TerminalProfileChoice>()
                .FirstOrDefault(choice => choice.Id.Equals(selectedTerminalProfile, StringComparison.OrdinalIgnoreCase))
                ?? terminalProfileCombo.Items.OfType<TerminalProfileChoice>().FirstOrDefault();

            return terminalProfileCombo;
        }
    }
}
