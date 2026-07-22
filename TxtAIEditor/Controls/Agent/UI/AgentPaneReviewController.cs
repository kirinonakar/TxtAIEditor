using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentPaneReviewController
    {
        private readonly FrameworkElement _resourceOwner;
        private readonly StackPanel _reviewPanelsHost;
        private readonly Border _diffConfirmPanel;
        private readonly TextBlock _diffConfirmHeader;
        private readonly TextBlock _diffConfirmDescription;
        private readonly Border _powerShellCommandPanel;
        private readonly TextBlock _powerShellConfirmCommand;
        private readonly Border _modifiedFilesPanel;
        private readonly ListView _modifiedFilesList;
        private readonly Action<AgentFileEditPreview> _fileDiffRequested;
        private readonly Action<AgentFileEditPreview> _fileRevertRequested;

        public AgentPaneReviewController(
            FrameworkElement resourceOwner,
            StackPanel reviewPanelsHost,
            Border diffConfirmPanel,
            TextBlock diffConfirmHeader,
            TextBlock diffConfirmDescription,
            Border powerShellCommandPanel,
            TextBlock powerShellConfirmCommand,
            Border modifiedFilesPanel,
            ListView modifiedFilesList,
            Action<AgentFileEditPreview> fileDiffRequested,
            Action<AgentFileEditPreview> fileRevertRequested)
        {
            _resourceOwner = resourceOwner;
            _reviewPanelsHost = reviewPanelsHost;
            _diffConfirmPanel = diffConfirmPanel;
            _diffConfirmHeader = diffConfirmHeader;
            _diffConfirmDescription = diffConfirmDescription;
            _powerShellCommandPanel = powerShellCommandPanel;
            _powerShellConfirmCommand = powerShellConfirmCommand;
            _modifiedFilesPanel = modifiedFilesPanel;
            _modifiedFilesList = modifiedFilesList;
            _fileDiffRequested = fileDiffRequested;
            _fileRevertRequested = fileRevertRequested;
        }

        public void ShowDiffConfirm(string header, string description)
        {
            _diffConfirmHeader.Text = header;
            _diffConfirmDescription.Text = description;
            _powerShellConfirmCommand.Text = string.Empty;
            _powerShellCommandPanel.Visibility = Visibility.Collapsed;
            _diffConfirmPanel.Visibility = Visibility.Visible;
            UpdateHostVisibility();
        }

        public void ShowPowerShellConfirm(string header, string description, string command)
        {
            _diffConfirmHeader.Text = header;
            _diffConfirmDescription.Text = description;
            _powerShellConfirmCommand.Text = command;
            _powerShellConfirmCommand.Foreground = AgentToolHelpers.IsDangerousPowerShellCommand(command)
                ? GetAgentBrush("AgentPowerShellConfirmDangerForeground", Microsoft.UI.Colors.Red)
                : GetAgentBrush("AgentOutputForeground", Microsoft.UI.Colors.Black);
            _powerShellCommandPanel.Visibility = Visibility.Visible;
            _diffConfirmPanel.Visibility = Visibility.Visible;
            UpdateHostVisibility();
        }

        public void HideDiffConfirm()
        {
            _diffConfirmPanel.Visibility = Visibility.Collapsed;
            UpdateHostVisibility();
        }

        public void UpdateModifiedFiles(IReadOnlyList<AgentFileEditPreview> edits)
        {
            _modifiedFilesList.ItemsSource = edits;
            _modifiedFilesPanel.Visibility = edits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateHostVisibility();
        }

        public void OpenFileDiff(object clickedItem)
        {
            if (clickedItem is not AgentFileEditPreview preview)
            {
                return;
            }

            _modifiedFilesList.SelectedItem = preview;
            _fileDiffRequested(preview);
        }

        public void RevertFile(object sender)
        {
            if (sender is Button button && button.Tag is AgentFileEditPreview preview)
            {
                _fileRevertRequested(preview);
            }
        }

        public void CloseModifiedFiles()
        {
            _modifiedFilesPanel.Visibility = Visibility.Collapsed;
            UpdateHostVisibility();
        }

        private void UpdateHostVisibility()
        {
            bool hasVisiblePanel =
                _diffConfirmPanel.Visibility == Visibility.Visible ||
                _modifiedFilesPanel.Visibility == Visibility.Visible;
            _reviewPanelsHost.Visibility = hasVisiblePanel ? Visibility.Visible : Visibility.Collapsed;
        }

        private Brush GetAgentBrush(string key, Windows.UI.Color fallbackColor)
        {
            string themeName = _resourceOwner.ActualTheme == ElementTheme.Default
                ? (Application.Current.RequestedTheme == ApplicationTheme.Dark ? "Dark" : "Light")
                : (_resourceOwner.ActualTheme == ElementTheme.Dark ? "Dark" : "Light");

            if (_resourceOwner.Resources.ThemeDictionaries.TryGetValue(themeName, out object? dictionary) &&
                dictionary is ResourceDictionary themeDictionary &&
                themeDictionary.TryGetValue(key, out object? resource) &&
                resource is Brush localThemeBrush)
            {
                return localThemeBrush;
            }

            if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeName, out dictionary) &&
                dictionary is ResourceDictionary appThemeDictionary &&
                appThemeDictionary.TryGetValue(key, out resource) &&
                resource is Brush appThemeBrush)
            {
                return appThemeBrush;
            }

            if (_resourceOwner.Resources.TryGetValue(key, out resource) && resource is Brush localBrush)
            {
                return localBrush;
            }

            if (Application.Current.Resources.TryGetValue(key, out resource) && resource is Brush appBrush)
            {
                return appBrush;
            }

            return new SolidColorBrush(fallbackColor);
        }
    }
}
