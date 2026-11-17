using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentToolExecutionGate
    {
        private const int ParallelLimit = 4;
        private readonly SemaphoreSlim _reads = new(ParallelLimit, ParallelLimit);
        private readonly SemaphoreSlim _mutationAdmission = new(1, 1);
        private readonly SemaphoreSlim _mutationSlots = new(ParallelLimit, ParallelLimit);
        private readonly Dictionary<string, FileGate> _files = new(StringComparer.OrdinalIgnoreCase);

        public async Task<IDisposable> EnterReadAsync(CancellationToken token)
        {
            await _reads.WaitAsync(token);
            return new Lease(() => _reads.Release());
        }

        public async Task<IDisposable> EnterFilesAsync(IEnumerable<string> paths, CancellationToken token)
        {
            var leases = new List<IDisposable>();
            try
            {
                // Acquire in the same order even when two patches list their files differently.
                foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    leases.Add(await EnterFileAsync(path, token));
                }

                await _mutationAdmission.WaitAsync(token);
                try
                {
                    await _mutationSlots.WaitAsync(token);
                }
                finally
                {
                    _mutationAdmission.Release();
                }

                return new Lease(() =>
                {
                    _mutationSlots.Release();
                    foreach (IDisposable lease in leases)
                    {
                        lease.Dispose();
                    }
                });
            }
            catch
            {
                foreach (IDisposable lease in leases)
                {
                    lease.Dispose();
                }
                throw;
            }
        }

        public async Task<IDisposable> EnterExclusiveAsync(CancellationToken token = default)
        {
            await _mutationAdmission.WaitAsync(token);
            int acquired = 0;
            try
            {
                while (acquired < ParallelLimit)
                {
                    await _mutationSlots.WaitAsync(token);
                    acquired++;
                }

                return new Lease(() =>
                {
                    _mutationSlots.Release(ParallelLimit);
                    _mutationAdmission.Release();
                });
            }
            catch
            {
                if (acquired > 0)
                {
                    _mutationSlots.Release(acquired);
                }
                _mutationAdmission.Release();
                throw;
            }
        }

        private async Task<IDisposable> EnterFileAsync(string path, CancellationToken token)
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
                await file.Semaphore.WaitAsync(token);
                return new Lease(() => ReleaseFile(path, file, acquired: true));
            }
            catch
            {
                ReleaseFile(path, file, acquired: false);
                throw;
            }
        }

        private void ReleaseFile(string path, FileGate file, bool acquired)
        {
            lock (_files)
            {
                if (acquired)
                {
                    file.Semaphore.Release();
                }
                if (--file.References == 0)
                {
                    _files.Remove(path);
                    file.Semaphore.Dispose();
                }
            }
        }

        private sealed class FileGate
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);
            public int References { get; set; }
        }

        private sealed class Lease(Action release) : IDisposable
        {
            private Action? _release = release;
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
