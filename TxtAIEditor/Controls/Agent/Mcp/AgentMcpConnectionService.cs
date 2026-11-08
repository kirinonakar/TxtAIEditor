using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpConnectionService : IDisposable
    {
        private readonly AgentMcpRuntime _runtime;
        private readonly Func<string, bool> _isSelected;
        private readonly Func<string, string, string> _getString;
        private readonly Dictionary<string, ServerStartOperation> _serverStartOperations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _serverStatus = new(StringComparer.OrdinalIgnoreCase);

        public event Action? Changed;

        public AgentMcpConnectionService(
            AgentMcpRuntime runtime,
            Func<string, bool> isSelected,
            Func<string, string, string> getString)
        {
            _runtime = runtime;
            _isSelected = isSelected;
            _getString = getString;
        }

        public string GetStatus(string serverId)
        {
            return _serverStatus.TryGetValue(serverId, out string? status) ? status : string.Empty;
        }

        public IReadOnlyList<AgentMcpTool> GetTools(string serverId) => _runtime.GetTools(serverId);

        public Task<JsonDocument> ExecuteToolAsync(
            AgentMcpServer server, string toolName, JsonElement arguments, CancellationToken cancellationToken)
            => _runtime.ExecuteToolAsync(server, toolName, arguments, cancellationToken);

        public void Reset(string serverId)
        {
            _runtime.RemoveSession(serverId);
            _serverStatus.Remove(serverId);
        }

        public async Task StartAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            if (!_serverStartOperations.TryGetValue(server.Id, out var operation))
            {
                operation = new ServerStartOperation();
                _serverStartOperations[server.Id] = operation;
                operation.Task = RunServerStartAsync(server, operation);
            }

            await operation.Task.WaitAsync(cancellationToken);
        }

        private async Task RunServerStartAsync(AgentMcpServer server, ServerStartOperation operation)
        {
            try
            {
                await RefreshServerToolsAsync(server, operation.Token);
            }
            finally
            {
                if (_serverStartOperations.TryGetValue(server.Id, out var current) &&
                    ReferenceEquals(current, operation))
                {
                    _serverStartOperations.Remove(server.Id);
                }

                operation.Dispose();
            }
        }

        private async Task RefreshServerToolsAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            try
            {
                _serverStatus[server.Id] = _getString("AgentMcpStatusConnecting", "연결 중");
                Changed?.Invoke();
                IReadOnlyList<AgentMcpTool> tools = await _runtime.RefreshToolsAsync(server, cancellationToken);

                if (_isSelected(server.Id) && !cancellationToken.IsCancellationRequested)
                {
                    _serverStatus[server.Id] = string.Format(
                        _getString("AgentMcpStatusToolCount", "{0:N0}개 도구"),
                        tools.Count);
                }
                else
                {
                    _runtime.RemoveSession(server.Id);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (_isSelected(server.Id) && !cancellationToken.IsCancellationRequested)
                {
                    _serverStatus[server.Id] = string.Format(
                        _getString("AgentMcpStatusFailedFormat", "연결 실패: {0}"),
                        ex.Message);
                }
            }

            Changed?.Invoke();
        }

        private void CancelServerStart(string serverId)
        {
            if (_serverStartOperations.Remove(serverId, out var operation))
            {
                operation.Cancel();
            }
        }

        public void Stop(string serverId)
        {
            CancelServerStart(serverId);
            _runtime.RemoveSession(serverId);
            _serverStatus.Remove(serverId);
        }

        public void Dispose()
        {
            foreach (var operation in _serverStartOperations.Values)
            {
                operation.Cancel();
            }
            _serverStartOperations.Clear();
            _runtime.Dispose();
        }

        private sealed class ServerStartOperation : IDisposable
        {
            private readonly CancellationTokenSource _cancellation = new();

            public CancellationToken Token => _cancellation.Token;
            public Task Task { get; set; } = Task.CompletedTask;

            public void Cancel()
            {
                _cancellation.Cancel();
            }

            public void Dispose()
            {
                _cancellation.Dispose();
            }
        }
    }
}
