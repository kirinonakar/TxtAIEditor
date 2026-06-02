using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TxtAIEditor.Controls
{
    public sealed class GitStatusRefreshController
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly DispatcherTimer _autoRefreshTimer;
        private readonly Func<string> _repoPathProvider;
        private readonly Func<Task> _refreshAsync;
        private int _refreshVersion;

        public GitStatusRefreshController(
            DispatcherQueue dispatcherQueue,
            DispatcherTimer autoRefreshTimer,
            Func<string> repoPathProvider,
            Func<Task> refreshAsync)
        {
            _dispatcherQueue = dispatcherQueue;
            _autoRefreshTimer = autoRefreshTimer;
            _repoPathProvider = repoPathProvider;
            _refreshAsync = refreshAsync;

            _autoRefreshTimer.Tick += OnAutoRefreshTimerTick;
        }

        public Task RefreshAsync()
        {
            return _refreshAsync();
        }

        public void QueueRefresh()
        {
            int version = ++_refreshVersion;

            void RunGitRefresh()
            {
                _ = RefreshQueuedAsync(version);
            }

            if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RunGitRefresh))
            {
                RunGitRefresh();
            }
        }

        private async Task RefreshQueuedAsync(int version)
        {
            try
            {
                if (version != _refreshVersion)
                {
                    return;
                }

                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Queued Git refresh failed: {ex.Message}");
            }
        }

        private async void OnAutoRefreshTimerTick(object? sender, object e)
        {
            if (!string.IsNullOrEmpty(_repoPathProvider()))
            {
                await RefreshAsync();
            }
        }
    }
}
