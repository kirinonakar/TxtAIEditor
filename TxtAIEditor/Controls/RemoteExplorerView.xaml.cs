using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    public sealed partial class RemoteExplorerView : UserControl
    {
        private const double ServerInputHeight = 32;
        private readonly ObservableCollection<RemoteServerProfile> _profiles = new();
        private readonly ObservableCollection<RemoteDirectoryEntry> _entries = new();
        private readonly RemoteServerStore _serverStore = new(new CredentialService());
        private readonly RemoteExplorerService _explorerService = new();
        private readonly WslDistributionService _wslDistributionService = new();
        private Func<string, string, string> _getString = (_, fallback) => fallback;
        private RemoteConnectionSettings? _connection;
        private string _currentPath = "/";
        private string _connectionRootPath = "/";
        private CancellationTokenSource? _operationCancellation;
        private Task? _profileRefreshTask;
        private bool _loaded;

        public RemoteExplorerView()
        {
            InitializeComponent();
            ServerList.ItemsSource = _profiles;
            RemoteEntryList.ItemsSource = _entries;
            Loaded += OnLoaded;
        }

        public event EventHandler<RemoteFileOpenedEventArgs>? RemoteFileOpened;
        public event EventHandler<RemoteServerSelectedEventArgs>? RemoteServerSelected;

        public void Localize(Func<string, string, string> getString)
        {
            _getString = getString;
            TitleText.Text = Get("RemoteExplorerTitle", "리모트 탐색기");
            DescriptionText.Text = Get("RemoteExplorerDescription", "WSL, SSH, SFTP, FTPS 또는 WebDAV 서버를 탐색합니다.");
            AddServerButtonText.Text = Get("RemoteExplorerAddServer", "서버 추가");
            EmptyServersText.Text = Get("RemoteExplorerNoServers", "추가된 서버가 없습니다.");

            string up = Get("ExplorerUpTooltip", "상위 폴더");
            ToolTipService.SetToolTip(RemoteUpButton, up);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RemoteUpButton, up);
            string refresh = Get("ExplorerRefreshTooltip", "새로고침");
            ToolTipService.SetToolTip(RemoteRefreshButton, refresh);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RemoteRefreshButton, refresh);
            string disconnect = Get("RemoteExplorerDisconnect", "연결 끊기");
            ToolTipService.SetToolTip(DisconnectButton, disconnect);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DisconnectButton, disconnect);
            ToolTipService.SetToolTip(AddServerButton, Get("RemoteExplorerAddServerTooltip", "새 리모트 서버 추가"));

            UpdateStatus(_connection == null
                ? Get("RemoteExplorerSelectServer", "서버를 선택하세요.")
                : string.Format(Get("RemoteExplorerConnectedFormat", "{0}에 연결됨"), _connection.Profile.Name));
        }

        public Task RefreshProfilesAsync()
        {
            if (_profileRefreshTask is { IsCompleted: false })
            {
                return _profileRefreshTask;
            }

            _profileRefreshTask = RefreshProfilesCoreAsync();
            return _profileRefreshTask;
        }

        private async Task RefreshProfilesCoreAsync()
        {
            BusyRing.IsActive = true;
            BusyRing.Visibility = Visibility.Visible;
            EmptyServersPanel.Visibility = Visibility.Collapsed;
            UpdateStatus(Get(
                "RemoteExplorerLoadingServers",
                "서버 목록을 불러오는 중..."));
            try
            {
                IReadOnlyList<RemoteServerProfile> profiles =
                    await _serverStore.LoadAsync();
                _profiles.Clear();
                foreach (RemoteServerProfile profile in profiles)
                {
                    _profiles.Add(profile);
                }

                InvalidateProfileListMeasure();

                IReadOnlyList<RemoteServerProfile> wslProfiles =
                    await _wslDistributionService.GetInstalledProfilesAsync();
                foreach (RemoteServerProfile profile in wslProfiles)
                {
                    if (_profiles.All(candidate => candidate.Id != profile.Id))
                    {
                        _profiles.Add(profile);
                    }
                }
            }
            finally
            {
                BusyRing.IsActive = false;
                BusyRing.Visibility = Visibility.Collapsed;
                EmptyServersPanel.Visibility = _profiles.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                UpdateStatus(_connection == null
                    ? Get("RemoteExplorerSelectServer", "서버를 선택하세요.")
                    : string.Format(
                        Get("RemoteExplorerConnectedFormat", "{0}에 연결됨"),
                        _connection.Profile.Name));
                InvalidateProfileListMeasure();
            }
        }

        private void InvalidateProfileListMeasure()
        {
            ServerList.InvalidateMeasure();
            ServerListPanel.InvalidateMeasure();
            InvalidateMeasure();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            await RefreshProfilesAsync();
        }

        private async void OnAddServerClick(object sender, RoutedEventArgs e)
        {
            TextBox nameBox = CreateLiteralTextBox(
                Get("RemoteServerNamePlaceholder", "서버 이름"));
            ComboBox typeBox = new()
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = ServerInputHeight,
                MinHeight = ServerInputHeight,
                MaxHeight = ServerInputHeight,
                VerticalContentAlignment = VerticalAlignment.Center,
                ItemsSource = new[] { "SSH", "SFTP", "FTPS", "WebDAV" },
                SelectedIndex = 1
            };
            TextBox addressBox = new()
            {
                PlaceholderText = Get("RemoteServerAddressPlaceholder", "호스트 또는 HTTPS 주소"),
                Height = ServerInputHeight,
                MinHeight = ServerInputHeight,
                MaxHeight = ServerInputHeight,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                InputScope = new InputScope
                {
                    Names = { new InputScopeName(InputScopeNameValue.Url) }
                }
            };
            TextBox portBox = new()
            {
                Text = "22",
                Height = ServerInputHeight,
                MinHeight = ServerInputHeight,
                MaxHeight = ServerInputHeight,
                VerticalContentAlignment = VerticalAlignment.Center,
                InputScope = new InputScope
                {
                    Names = { new InputScopeName(InputScopeNameValue.Number) }
                }
            };
            TextBox userNameBox = CreateLiteralTextBox(
                Get("RemoteServerUserNamePlaceholder", "ID"));
            PasswordBox passwordBox = new()
            {
                PlaceholderText = Get("RemoteServerPasswordPlaceholder", "비밀번호"),
                Height = ServerInputHeight,
                MinHeight = ServerInputHeight,
                MaxHeight = ServerInputHeight,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            TextBlock validationText = new()
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            typeBox.SelectionChanged += (_, _) =>
            {
                portBox.Text = typeBox.SelectedIndex switch
                {
                    0 or 1 => "22",
                    2 => "21",
                    3 => "443",
                    _ => portBox.Text
                };
                addressBox.PlaceholderText = typeBox.SelectedIndex == 3
                    ? "https://example.com/webdav"
                    : Get("RemoteServerAddressPlaceholder", "호스트 또는 주소");
            };

            StackPanel content = new() { Spacing = 7, MinWidth = 360 };
            AddLabeledControl(content, Get("RemoteServerNameLabel", "서버 이름"), nameBox);
            AddLabeledControl(content, Get("RemoteServerTypeLabel", "서버 종류"), typeBox);
            AddLabeledControl(content, Get("RemoteServerAddressLabel", "주소"), addressBox);
            AddLabeledControl(content, Get("RemoteServerPortLabel", "포트"), portBox);
            AddLabeledControl(content, Get("RemoteServerUserNameLabel", "ID"), userNameBox);
            AddLabeledControl(content, Get("RemoteServerPasswordLabel", "비밀번호"), passwordBox);
            content.Children.Add(validationText);
            content.Children.Add(new TextBlock
            {
                Text = Get("RemoteServerCredentialNotice", "주소와 비밀번호는 Windows 자격 증명 관리자에 일반 자격 증명으로 저장됩니다."),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = Get("RemoteExplorerAddServerTitle", "리모트 서버 추가"),
                Content = content,
                PrimaryButtonText = Get("CommonAdd", "추가"),
                CloseButtonText = Get("CommonCancel", "취소"),
                DefaultButton = ContentDialogButton.Primary
            };

            RemoteServerProfile? profileToSave = null;
            string addressToSave = string.Empty;
            string passwordToSave = string.Empty;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                string validationError = ValidateServer(
                    nameBox.Text,
                    typeBox.SelectedIndex,
                    addressBox.Text,
                    portBox.Text,
                    userNameBox.Text,
                    passwordBox.Password,
                    out RemoteServerProfile? profile);
                if (!string.IsNullOrEmpty(validationError) || profile == null)
                {
                    args.Cancel = true;
                    validationText.Text = validationError;
                    validationText.Visibility = Visibility.Visible;
                    return;
                }

                profileToSave = profile;
                addressToSave = addressBox.Text.Trim();
                passwordToSave = passwordBox.Password;
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || profileToSave == null)
            {
                return;
            }

            try
            {
                await _serverStore.SaveAsync(profileToSave, addressToSave, passwordToSave);
                await RefreshProfilesAsync();
                UpdateStatus(Get("RemoteServerSaved", "서버가 저장되었습니다."));
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(
                    Get("RemoteServerSaveFailedTitle", "서버 저장 실패"),
                    ex.Message);
            }
        }

        private async void OnDeleteServerClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RemoteServerProfile profile })
            {
                return;
            }

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = Get("RemoteServerDeleteTitle", "서버 삭제"),
                Content = string.Format(
                    Get("RemoteServerDeleteMessageFormat", "'{0}' 서버와 저장된 자격 증명을 삭제할까요?"),
                    profile.Name),
                PrimaryButtonText = Get("CommonDelete", "삭제"),
                CloseButtonText = Get("CommonCancel", "취소"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await _serverStore.DeleteAsync(profile);
            await RefreshProfilesAsync();
            UpdateStatus(Get("RemoteServerDeleted", "서버와 자격 증명이 삭제되었습니다."));
        }

        private async void OnServerItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not RemoteServerProfile profile)
            {
                return;
            }

            if (profile.ServerType == RemoteServerType.Wsl)
            {
                SetBusy(true);
                UpdateStatus(Get(
                    "RemoteExplorerResolvingWslHome",
                    "WSL 홈 폴더를 확인하는 중..."));
                try
                {
                    profile.UserName =
                        await WslDistributionService.GetHomePathAsync(profile.Name);
                }
                finally
                {
                    SetBusy(false);
                }
            }

            RemoteConnectionSettings? connection = _serverStore.GetConnection(profile);
            if (connection == null)
            {
                await ShowErrorAsync(
                    Get("RemoteConnectionFailedTitle", "리모트 연결 실패"),
                    Get("RemoteCredentialMissing", "Windows 자격 증명 관리자에서 서버 주소 또는 비밀번호를 찾을 수 없습니다."));
                return;
            }

            RemoteServerSelected?.Invoke(this, new RemoteServerSelectedEventArgs(profile));
            await Task.CompletedTask;
        }

        private async void OnRemoteEntryClick(object sender, ItemClickEventArgs e)
        {
            if (_connection == null || e.ClickedItem is not RemoteDirectoryEntry entry)
            {
                return;
            }

            if (entry.IsDirectory)
            {
                _currentPath = entry.FullPath;
                await LoadCurrentDirectoryAsync();
                return;
            }

            await RunBusyAsync(
                Get("RemoteDownloadingFile", "파일을 여는 중..."),
                async cancellationToken =>
                {
                    string localPath = await _explorerService.DownloadFileAsync(_connection, entry, cancellationToken);
                    RemoteFileOpened?.Invoke(this, new RemoteFileOpenedEventArgs(localPath));
                    UpdateStatus(string.Format(
                        Get("RemoteFileOpenedFormat", "{0}의 임시 로컬 복사본을 열었습니다."),
                        entry.Name));
                });
        }

        private async void OnRemoteUpClick(object sender, RoutedEventArgs e)
        {
            if (_connection == null)
            {
                return;
            }

            string parent = RemoteExplorerService.GetParentPath(_currentPath);
            if (parent == _currentPath ||
                _currentPath.Equals(_connectionRootPath, StringComparison.Ordinal))
            {
                return;
            }

            _currentPath = parent;
            await LoadCurrentDirectoryAsync();
        }

        private async void OnRemoteRefreshClick(object sender, RoutedEventArgs e)
        {
            await LoadCurrentDirectoryAsync();
        }

        private void OnDisconnectClick(object sender, RoutedEventArgs e)
        {
            _operationCancellation?.Cancel();
            _connection = null;
            _entries.Clear();
            BrowserPanel.Visibility = Visibility.Collapsed;
            ServerListPanel.Visibility = Visibility.Visible;
            UpdateStatus(Get("RemoteExplorerSelectServer", "서버를 선택하세요."));
        }

        private async Task LoadCurrentDirectoryAsync()
        {
            if (_connection == null)
            {
                return;
            }

            CurrentPathText.Text = _currentPath;
            RemoteUpButton.IsEnabled = !_currentPath.Equals(_connectionRootPath, StringComparison.Ordinal);
            await RunBusyAsync(
                Get("RemoteLoadingDirectory", "폴더를 불러오는 중..."),
                async cancellationToken =>
                {
                    IReadOnlyList<RemoteDirectoryEntry> items =
                        await _explorerService.ListDirectoryAsync(_connection, _currentPath, cancellationToken);
                    _entries.Clear();
                    foreach (RemoteDirectoryEntry item in items)
                    {
                        _entries.Add(item);
                    }

                    UpdateStatus(string.Format(
                        Get("RemoteItemCountFormat", "{0:N0}개 항목"),
                        _entries.Count));
                });
        }

        private async Task RunBusyAsync(
            string status,
            Func<CancellationToken, Task> operation)
        {
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = _operationCancellation.Token;
            SetBusy(true);
            UpdateStatus(status);
            try
            {
                await operation(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                UpdateStatus(string.Format(
                    Get("RemoteOperationFailedFormat", "작업 실패: {0}"),
                    ex.Message));
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    SetBusy(false);
                }
            }
        }

        private string ValidateServer(
            string name,
            int typeIndex,
            string address,
            string portText,
            string userName,
            string password,
            out RemoteServerProfile? profile)
        {
            profile = null;
            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(address) ||
                string.IsNullOrWhiteSpace(userName) ||
                string.IsNullOrEmpty(password))
            {
                return Get("RemoteServerRequiredFields", "서버 이름, 주소, ID, 비밀번호를 모두 입력하세요.");
            }

            if (!int.TryParse(portText, out int port) || port is < 1 or > 65535)
            {
                return Get("RemoteServerInvalidPort", "포트는 1~65535 사이의 숫자여야 합니다.");
            }

            RemoteServerType serverType = typeIndex switch
            {
                0 => RemoteServerType.Ssh,
                1 => RemoteServerType.Sftp,
                2 => RemoteServerType.Ftps,
                3 => RemoteServerType.WebDav,
                _ => RemoteServerType.Sftp
            };
            if (serverType == RemoteServerType.WebDav &&
                (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? uri) ||
                 !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return Get("RemoteWebDavHttpsRequired", "WebDAV 주소는 https://로 시작해야 합니다.");
            }

            profile = new RemoteServerProfile
            {
                Name = name.Trim(),
                ServerType = serverType,
                Port = port,
                UserName = userName.Trim()
            };
            return string.Empty;
        }

        private static void AddLabeledControl(Panel panel, string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label, FontSize = 12 });
            panel.Children.Add(control);
        }

        private static TextBox CreateLiteralTextBox(string placeholderText)
        {
            return new TextBox
            {
                PlaceholderText = placeholderText,
                Height = ServerInputHeight,
                MinHeight = ServerInputHeight,
                MaxHeight = ServerInputHeight,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                InputScope = new InputScope
                {
                    Names = { new InputScopeName(InputScopeNameValue.Url) }
                }
            };
        }

        private async Task ShowErrorAsync(string title, string message)
        {
            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = Get("CommonClose", "닫기")
            };
            await dialog.ShowAsync();
        }

        private void SetBusy(bool isBusy)
        {
            BusyRing.IsActive = isBusy;
            BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            AddServerButton.IsEnabled = !isBusy;
            ServerList.IsEnabled = !isBusy;
            RemoteEntryList.IsEnabled = !isBusy;
            RemoteUpButton.IsEnabled = !isBusy &&
                !_currentPath.Equals(_connectionRootPath, StringComparison.Ordinal);
            RemoteRefreshButton.IsEnabled = !isBusy;
        }

        private void UpdateStatus(string message)
        {
            StatusText.Text = message;
        }

        private string Get(string key, string fallback) => _getString(key, fallback);
    }
}
