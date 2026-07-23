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
            TitleText.Text = tab.TabHeaderTitle;
            TitleText.SetBinding(TextBlock.TextProperty, new Binding
            {
                Path = new PropertyPath(nameof(OpenedTab.TabHeaderTitle)),
                Mode = BindingMode.OneWay,
                Source = tab
            });
            ToolTipService.SetToolTip(LockIcon, encryptedTooltip);
            UpdateDirtyIndicator();
            UpdateLockIcon();
            UpdateArchiveIcon();
            UpdateExternalPathIndicator();

            _tab.PropertyChanged += OnTabPropertyChanged;
        }

        public void SetWorkspaceFolderPath(string? workspaceFolderPath)
        {
            _workspaceFolderPath = workspaceFolderPath ?? string.Empty;
            UpdateExternalPathIndicator();
        }

        private static bool IsPathUnderWorkspace(OpenedTab tab, string workspaceFolderPath)
        {
            if (string.IsNullOrWhiteSpace(workspaceFolderPath))
            {
                return true;
            }

            if (tab.IsRemoteFile && !string.IsNullOrWhiteSpace(tab.RemotePath))
            {
                if (!RemotePath.IsRemote(workspaceFolderPath))
                {
                    return false;
                }

                if (!RemotePath.TryParse(tab.RemotePath, out Guid fileServerId, out string filePath) ||
                    !RemotePath.TryParse(workspaceFolderPath, out Guid wsServerId, out string wsPath))
                {
                    return false;
                }

                if (fileServerId != wsServerId)
                {
                    return false;
                }

                string normalizedFilePath = NormalizeRemotePath(filePath);
                string normalizedWsPath = NormalizeRemotePath(wsPath);

                string fileDir = GetRemoteDirectoryName(normalizedFilePath);
                return string.Equals(fileDir, normalizedWsPath, StringComparison.OrdinalIgnoreCase);
            }

            string? localFilePath = !string.IsNullOrWhiteSpace(tab.FilePath) ? tab.FilePath : tab.HexSourceFilePath;
            if (string.IsNullOrWhiteSpace(localFilePath))
            {
                return true;
            }

            if (RemotePath.IsRemote(workspaceFolderPath))
            {
                return false;
            }

            var fileDirectory = Path.GetDirectoryName(localFilePath);
            if (string.IsNullOrEmpty(fileDirectory))
            {
                return false;
            }

            string normalizedLocalDir = fileDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedLocalWs = workspaceFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normalizedLocalDir, normalizedLocalWs, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRemotePath(string path)
        {
            string normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
            return string.IsNullOrWhiteSpace(normalized) ? "/" : "/" + normalized.Trim('/');
        }

        private static string GetRemoteDirectoryName(string remotePath)
        {
            string normalized = NormalizeRemotePath(remotePath);
            int separator = normalized.LastIndexOf('/');
            return separator <= 0 ? "/" : normalized[..separator];
        }

        private void UpdateExternalPathIndicator()
        {
            if (string.IsNullOrEmpty(_workspaceFolderPath) || _tab == null)
            {
                TitleText.ClearValue(TextBlock.ForegroundProperty);
                return;
            }

            if (IsPathUnderWorkspace(_tab, _workspaceFolderPath))
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
            else if (args.PropertyName == nameof(OpenedTab.IsArchiveEntry))
            {
                UpdateArchiveIcon();
            }
            else if (args.PropertyName == nameof(OpenedTab.FilePath) || args.PropertyName == nameof(OpenedTab.RemotePath))
            {
                UpdateExternalPathIndicator();
            }
        }

        private void UpdateDirtyIndicator()
        {
            // Keep the indicator's layout slot stable. The first dirty transition
            // happens on the IME commit path; collapsing/expanding both split-tab
            // headers there forces a native layout pass between Korean syllables.
            DirtyIndicator.Opacity = _tab?.IsDirty == true ? 1 : 0;
        }

        private void UpdateLockIcon()
        {
            LockIcon.Visibility = _tab?.IsEncrypted == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateArchiveIcon()
        {
            ArchiveIcon.Visibility = _tab?.IsArchiveEntry == true
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
