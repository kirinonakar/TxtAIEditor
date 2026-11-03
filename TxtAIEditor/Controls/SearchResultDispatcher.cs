using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    // Keep only one UI callback queued, and yield to input/layout between short slices.
    internal sealed class SearchResultDispatcher<T>
    {
        private const int MaxItemsPerTick = 50;
        private static readonly TimeSpan TickBudget = TimeSpan.FromMilliseconds(4);
        private readonly object _gate = new();
        private readonly Queue<IReadOnlyList<T>> _batches = new();
        private readonly Func<Action, bool> _enqueue;
        private readonly Action<T> _append;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _batchOffset;
        private bool _scheduled;
        private bool _completed;
        private bool _stopped;

        public SearchResultDispatcher(Func<Action, bool> enqueue, Action<T> append, CancellationToken cancellationToken)
        {
            _enqueue = enqueue;
            _append = append;
            _cancellationToken = cancellationToken;
        }

        public void Publish(IReadOnlyList<T> results)
        {
            if (results.Count == 0) return;
            lock (_gate)
            {
                if (_stopped || _completed || _cancellationToken.IsCancellationRequested) return;
                _batches.Enqueue(results);
                if (_scheduled) return;
                _scheduled = true;
            }

            Schedule();
        }

        public Task CompleteAsync()
        {
            lock (_gate)
            {
                _completed = true;
                if (!_scheduled || _stopped) _completion.TrySetResult();
            }

            return _completion.Task;
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _stopped = true;
                _batches.Clear();
                _batchOffset = 0;
                _completion.TrySetResult();
            }
        }

        private void Schedule()
        {
            if (!_enqueue(Drain)) Cancel();
        }

        private void Drain()
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                for (int count = 0; count < MaxItemsPerTick; count++)
                {
                    T item;
                    lock (_gate)
                    {
                        if (_stopped || _cancellationToken.IsCancellationRequested)
                        {
                            Cancel();
                            return;
                        }
                        if (_batches.Count == 0) break;
                        IReadOnlyList<T> batch = _batches.Peek();
                        item = batch[_batchOffset++];
                        if (_batchOffset == batch.Count)
                        {
                            _batches.Dequeue();
                            _batchOffset = 0;
                        }
                    }

                    _append(item);
                    if (Stopwatch.GetElapsedTime(started) >= TickBudget) break;
                }
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
                Cancel();
                return;
            }

            lock (_gate)
            {
                if (_stopped) return;
                if (_batches.Count == 0)
                {
                    _scheduled = false;
                    if (_completed) _completion.TrySetResult();
                    return;
                }
            }

            Schedule();
        }
    }
}
