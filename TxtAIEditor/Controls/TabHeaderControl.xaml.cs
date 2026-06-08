using System;
using System.ComponentModel;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed partial class TabHeaderControl : UserControl
    {
        private OpenedTab? _tab;
        private string _workspaceFolderPath = string.Empty;

        public TabHeaderControl()
        {
            InitializeComponent();
            HeaderPanel.Transitions = new TransitionCollection();
            LockIcon.Transitions = new TransitionCollection();
            DirtyIndicator.Transitions = new TransitionCollection();
            TitleText.Transitions = new TransitionCollection();
        }

        public event EventHandler<TabEncryptionMenuRequestedEventArgs>? EncryptionMenuRequested;

        public void Configure(OpenedTab tab, string encryptedTooltip, string? workspaceFolderPath = null)
        {
            if (_tab != null)
            {
                _tab.PropertyChanged -= OnTabPropertyChanged;
            }

            _tab = tab;
            _workspaceFolderPath = workspaceFolderPath ?? string.Empty;
            TitleText.Text = tab.Title;
            TitleText.SetBinding(TextBlock.TextProperty, new Binding
            {
                Path = new PropertyPath(nameof(OpenedTab.Title)),
                Mode = BindingMode.OneWay,
                Source = tab
            });
            ToolTipService.SetToolTip(LockIcon, encryptedTooltip);
            UpdateDirtyIndicator();
            UpdateLockIcon();
            UpdateExternalPathIndicator();

            _tab.PropertyChanged += OnTabPropertyChanged;
        }

        public void SetWorkspaceFolderPath(string? workspaceFolderPath)
        {
            _workspaceFolderPath = workspaceFolderPath ?? string.Empty;
            UpdateExternalPathIndicator();
        }

        private bool IsPathUnderWorkspace(string filePath)
        {
            var normalizedFolder = _workspaceFolderPath.TrimEnd(Path.DirectorySeparatorChar);
            var normalizedFile = filePath.TrimEnd(Path.DirectorySeparatorChar);

            if (normalizedFile.Length > normalizedFolder.Length)
            {
                return normalizedFile.StartsWith(
                    normalizedFolder + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(normalizedFile, normalizedFolder, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateExternalPathIndicator()
        {
            if (string.IsNullOrEmpty(_workspaceFolderPath) ||
                string.IsNullOrEmpty(_tab?.FilePath))
            {
                TitleText.ClearValue(TextBlock.ForegroundProperty);
                return;
            }

            if (IsPathUnderWorkspace(_tab.FilePath))
            {
                TitleText.ClearValue(TextBlock.ForegroundProperty);
            }
            else
            {
                TitleText.Foreground = new SolidColorBrush(Colors.DodgerBlue);
            }
        }

        private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(OpenedTab.IsDirty))
            {
                UpdateDirtyIndicator();
            }
            else if (args.PropertyName == nameof(OpenedTab.IsEncrypted))
            {
                UpdateLockIcon();
            }
        }

        private void UpdateDirtyIndicator()
        {
            DirtyIndicator.Visibility = _tab?.IsDirty == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateLockIcon()
        {
            LockIcon.Visibility = _tab?.IsEncrypted == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnLockIconRightTapped(object sender, RightTappedRoutedEventArgs args)
        {
            if (_tab == null)
            {
                return;
            }

            EncryptionMenuRequested?.Invoke(
                this,
                new TabEncryptionMenuRequestedEventArgs(_tab, LockIcon, args));
        }
    }

    public sealed class TabEncryptionMenuRequestedEventArgs : EventArgs
    {
        public TabEncryptionMenuRequestedEventArgs(
            OpenedTab tab,
            FrameworkElement target,
            RightTappedRoutedEventArgs routedArgs)
        {
            Tab = tab;
            Target = target;
            RoutedArgs = routedArgs;
        }

        public OpenedTab Tab { get; }
        public FrameworkElement Target { get; }
        public RightTappedRoutedEventArgs RoutedArgs { get; }
    }
}
