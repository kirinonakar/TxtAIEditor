using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentToolExecutionGate
    {
        public const int ParallelLimit = 4;
        private readonly SemaphoreSlim _reads = new(ParallelLimit, ParallelLimit);
        private readonly SemaphoreSlim _writes = new(ParallelLimit, ParallelLimit);
        private readonly SemaphoreSlim _web = new(ParallelLimit, ParallelLimit);
        private readonly AccessGate _workspace = new();
        private readonly Dictionary<string, FileGate> _files = new(StringComparer.OrdinalIgnoreCase);

        public Task<IDisposable> EnterWebAsync(CancellationToken token) => EnterSlotAsync(_web, token);

        public Task<IDisposable> EnterFileReadAsync(string? path, CancellationToken token) =>
            EnterWorkspaceAsync(path is null ? Array.Empty<string>() : new[] { path }, path is null ? Access.Scan : Access.Read, _reads, token);

        public Task<IDisposable> EnterFilesAsync(IEnumerable<string> paths, CancellationToken token) =>
            EnterWorkspaceAsync(paths, Access.Write, _writes, token);

        public Task<IDisposable> EnterExclusiveAsync(CancellationToken token = default) =>
            _workspace.EnterAsync(Access.Exclusive, token);

        private async Task<IDisposable> EnterWorkspaceAsync(
            IEnumerable<string> paths, Access access, SemaphoreSlim slots, CancellationToken token)
        {
            var leases = new List<IDisposable>();
            void Release()
            {
                for (int index = leases.Count - 1; index >= 0; index--)
                {
                    leases[index].Dispose();
                }
            }

            try
            {
                // Take path locks in the same order across calls, before entering the workspace gate.
                foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    leases.Add(await EnterFileAsync(path, access == Access.Read ? Access.Read : Access.Exclusive, token));
                }

                leases.Add(await EnterSlotAsync(slots, token));
                leases.Add(await _workspace.EnterAsync(access, token));
                return new Lease(Release);
            }
            catch
            {
                Release();
                throw;
            }
        }

        private static async Task<IDisposable> EnterSlotAsync(SemaphoreSlim slots, CancellationToken token)
        {
            await slots.WaitAsync(token);
            return new Lease(() => slots.Release());
        }

        private async Task<IDisposable> EnterFileAsync(string path, Access access, CancellationToken token)
        {
            FileGate file;
            lock (_files)
            {
                if (!_files.TryGetValue(path, out file!))
                {
                    _files.Add(path, file = new FileGate());
                }
                file.References++;
            }

            try
            {
                IDisposable lease = await file.Gate.EnterAsync(access, token);
                return new Lease(() =>
                {
                    lease.Dispose();
                    ReleaseFile(path, file);
                });
            }
            catch
            {
                ReleaseFile(path, file);
                throw;
            }
        }

        private void ReleaseFile(string path, FileGate file)
        {
            lock (_files)
            {
                if (--file.References == 0)
                {
                    _files.Remove(path);
                    file.Gate.Dispose();
                }
            }
        }

        private enum Access { Read, Write, Scan, Exclusive }

        private sealed class FileGate
        {
            public AccessGate Gate { get; } = new();
            public int References { get; set; }
        }

        private sealed class AccessGate : IDisposable
        {
            private readonly SemaphoreSlim _admission = new(1, 1);
            private readonly int[] _active = new int[4];
            private TaskCompletionSource _changed = NewSignal();

            public async Task<IDisposable> EnterAsync(Access access, CancellationToken token)
            {
                // Once an incompatible caller is waiting, later callers cannot keep it waiting
                // by continuously joining the currently active group.
                await _admission.WaitAsync(token);
                try
                {
                    while (true)
                    {
                        Task changed;
                        lock (_active)
                        {
                            token.ThrowIfCancellationRequested();
                            if (CanEnter(access))
                            {
                                _active[(int)access]++;
                                return new Lease(() => Release(access));
                            }
                            changed = _changed.Task;
                        }
                        await changed.WaitAsync(token);
                    }
                }
                finally
                {
                    _admission.Release();
                }
            }

            private bool CanEnter(Access access) =>
                _active[(int)Access.Exclusive] == 0 && access switch
                {
                    Access.Read => true,
                    Access.Write => _active[(int)Access.Scan] == 0,
                    Access.Scan => _active[(int)Access.Write] == 0,
                    _ => _active.All(count => count == 0)
                };

            private void Release(Access access)
            {
                lock (_active)
                {
                    if (--_active[(int)access] == 0)
                    {
                        _changed.TrySetResult();
                        _changed = NewSignal();
                    }
                }
            }

            private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Dispose() => _admission.Dispose();
        }

        private sealed class Lease(Action release) : IDisposable
        {
            private Action? _release = release;
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
