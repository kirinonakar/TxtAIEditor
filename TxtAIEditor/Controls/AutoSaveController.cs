using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class AutoSaveController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly Func<EditorSettings?> _settingsProvider;
        private readonly Func<string> _repoPathProvider;
        private readonly Func<OpenedTab, Task<bool>> _saveTabAsync;
        private readonly DispatcherTimer _timer;
        private bool _enabled;

        public AutoSaveController(
            MainWindowViewModel viewModel,
            Func<EditorSettings?> settingsProvider,
            Func<string> repoPathProvider,
            Func<OpenedTab, Task<bool>> saveTabAsync)
        {
            _viewModel = viewModel;
            _settingsProvider = settingsProvider;
            _repoPathProvider = repoPathProvider;
            _saveTabAsync = saveTabAsync;
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _timer.Tick += OnTimerTick;
        }

        public void UpdateStatus()
        {
            var settings = _settingsProvider();
            if (settings == null) return;

            _enabled = settings.AutoSave && !string.IsNullOrEmpty(_repoPathProvider());
            if (_enabled)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }

        public void Stop()
        {
            _timer.Stop();
        }

        private async void OnTimerTick(object? sender, object e)
        {
            if (!_enabled) return;

            string currentRepoPath = _repoPathProvider();
            if (string.IsNullOrEmpty(currentRepoPath)) return;

            string repoPath;
            try
            {
                repoPath = Path.GetFullPath(currentRepoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            }
            catch
            {
                return;
            }

            var dirtyTabs = _viewModel.Tabs.Where(t =>
            {
                if (!t.IsDirty || string.IsNullOrEmpty(t.FilePath)) return false;
                try
                {
                    string fullPath = Path.GetFullPath(t.FilePath);
                    return fullPath.StartsWith(repoPath, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }).ToList();

            foreach (var tab in dirtyTabs)
            {
                await _saveTabAsync(tab);
            }
        }
    }
}
