using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace TxtAIEditor.Editor
{
    public readonly record struct CaretState(int LineNumber, int Column);

    public readonly record struct UndoEditRange(int StartLine, int OldLineCount, int NewLineCount);

    public readonly record struct TextLinePatch(int LineNumber, string Text);

    public sealed record UndoResult(
        int StartLine,
        int OldLineCount,
        int DocumentLineCount,
        IReadOnlyList<string> LinesToRefresh,
        CaretState? Caret)
    {
        public int NewLineCount => LinesToRefresh.Count;

        public IReadOnlyList<TextLinePatch>? LinePatches { get; init; }
    }

    public interface IUndoableEdit
    {
        UndoEditRange RedoRange { get; }
        UndoEditRange UndoRange { get; }

        void Apply(ITextModel model);
        void Unapply(ITextModel model);
        bool TryMergeWith(IUndoableEdit next, TimeSpan interval, out IUndoableEdit merged);
    }

    public sealed class UndoManager
    {
        private const int MaxUndoDepth = 200;

        private readonly List<UndoTransaction> _undoStack = new();
        private readonly List<UndoTransaction> _redoStack = new();
        private UndoTransaction? _currentTransaction;
        private int _transactionDepth;
        private DateTime _lastEditTime = DateTime.MinValue;

        public void BeginTransaction(string reason, CaretState? beforeCaret = null)
        {
            if (_transactionDepth == 0)
            {
                _currentTransaction = new UndoTransaction(reason, beforeCaret);
            }

            _transactionDepth++;
        }

        public void AddEdit(IUndoableEdit edit)
        {
            var now = DateTime.UtcNow;
            _redoStack.Clear();

            if (_currentTransaction != null)
            {
                _currentTransaction.AddEdit(edit, LastEditInterval(now));
                _lastEditTime = now;
                return;
            }

            TimeSpan interval = LastEditInterval(now);
            if (_undoStack.Count > 0 && _undoStack[^1].TryMergeWith(edit, interval))
            {
                _lastEditTime = now;
                return;
            }

            var transaction = new UndoTransaction("edit", null);
            transaction.AddEdit(edit, interval);
            Commit(transaction);
            _lastEditTime = now;
        }

        public void EndTransaction(CaretState? afterCaret = null)
        {
            if (_transactionDepth <= 0)
            {
                _transactionDepth = 0;
                _currentTransaction = null;
                return;
            }

            _transactionDepth--;
            if (_transactionDepth > 0)
            {
                return;
            }

            if (_currentTransaction is { HasEdits: true } transaction)
            {
                transaction.AfterCaret = afterCaret;
                Commit(transaction);
            }

            _currentTransaction = null;
            _lastEditTime = DateTime.MinValue;
        }

        public UndoResult? Undo(ITextModel model)
        {
            CloseOpenTransaction();
            if (_undoStack.Count == 0)
            {
                return null;
            }

            UndoTransaction transaction = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            UndoResult result = transaction.Unapply(model);
            _redoStack.Add(transaction);
            _lastEditTime = DateTime.MinValue;
            return result;
        }

        public UndoResult? Redo(ITextModel model)
        {
            CloseOpenTransaction();
            if (_redoStack.Count == 0)
            {
                return null;
            }

            UndoTransaction transaction = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            UndoResult result = transaction.Apply(model);
            _undoStack.Add(transaction);
            _lastEditTime = DateTime.MinValue;
            return result;
        }

        public async Task<UndoResult?> UndoAsync(
            ITextModel model,
            IProgress<TextOperationProgress>? progress = null)
        {
            CloseOpenTransaction();
            if (_undoStack.Count == 0)
            {
                return null;
            }

            UndoTransaction transaction = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            UndoResult result = await transaction.UnapplyAsync(model, progress);
            _redoStack.Add(transaction);
            _lastEditTime = DateTime.MinValue;
            return result;
        }

        public async Task<UndoResult?> RedoAsync(
            ITextModel model,
            IProgress<TextOperationProgress>? progress = null)
        {
            CloseOpenTransaction();
            if (_redoStack.Count == 0)
            {
                return null;
            }

            UndoTransaction transaction = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            UndoResult result = await transaction.ApplyAsync(model, progress);
            _undoStack.Add(transaction);
            _lastEditTime = DateTime.MinValue;
            return result;
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            _currentTransaction = null;
            _transactionDepth = 0;
            _lastEditTime = DateTime.MinValue;
        }

        private void CloseOpenTransaction()
        {
            if (_currentTransaction is { HasEdits: true } transaction)
            {
                Commit(transaction);
            }

            _currentTransaction = null;
            _transactionDepth = 0;
        }

        private void Commit(UndoTransaction transaction)
        {
            if (!transaction.HasEdits)
            {
                return;
            }

            _undoStack.Add(transaction);
            if (_undoStack.Count > MaxUndoDepth)
            {
                _undoStack.RemoveAt(0);
            }
        }

        private TimeSpan LastEditInterval(DateTime now)
        {
            return _lastEditTime == DateTime.MinValue ? TimeSpan.MaxValue : now - _lastEditTime;
        }
    }

    public sealed class UndoTransaction
    {
        private const long ProgressIntervalMilliseconds = 50;
        private static readonly IReadOnlyList<string> EmptyLines = Array.Empty<string>();
        private readonly List<IUndoableEdit> _edits = new();

        public UndoTransaction(string reason, CaretState? beforeCaret)
        {
            Reason = reason;
            BeforeCaret = beforeCaret;
        }

        public string Reason { get; }
        public CaretState? BeforeCaret { get; }
        public CaretState? AfterCaret { get; set; }
        public bool HasEdits => _edits.Count > 0;

        public void AddEdit(IUndoableEdit edit, TimeSpan interval)
        {
            if (_edits.Count > 0 && _edits[^1].TryMergeWith(edit, interval, out IUndoableEdit merged))
            {
                _edits[^1] = merged;
                return;
            }

            _edits.Add(edit);
        }

        public bool TryMergeWith(IUndoableEdit edit, TimeSpan interval)
        {
            if (_edits.Count != 1)
            {
                return false;
            }

            if (!_edits[0].TryMergeWith(edit, interval, out IUndoableEdit merged))
            {
                return false;
            }

            _edits[0] = merged;
            return true;
        }

        public UndoResult Apply(ITextModel model)
        {
            int beforeLineCount = model.LineCount;
            UndoEditRange[] ranges = _edits.Select(edit => edit.RedoRange).ToArray();
            foreach (IUndoableEdit edit in _edits)
            {
                edit.Apply(model);
            }

            return BuildResult(model, beforeLineCount, ranges, AfterCaret);
        }

        public UndoResult Unapply(ITextModel model)
        {
            int beforeLineCount = model.LineCount;
            UndoEditRange[] ranges = _edits.Select(edit => edit.UndoRange).ToArray();
            for (int i = _edits.Count - 1; i >= 0; i--)
            {
                _edits[i].Unapply(model);
            }

            return BuildResult(model, beforeLineCount, ranges, BeforeCaret);
        }

        public Task<UndoResult> ApplyAsync(
            ITextModel model,
            IProgress<TextOperationProgress>? progress)
        {
            progress?.Report(new TextOperationProgress(0, _edits.Count, TimeSpan.Zero));
            return Task.Run(() => ApplyWithProgress(model, progress));
        }

        public Task<UndoResult> UnapplyAsync(
            ITextModel model,
            IProgress<TextOperationProgress>? progress)
        {
            progress?.Report(new TextOperationProgress(0, _edits.Count, TimeSpan.Zero));
            return Task.Run(() => UnapplyWithProgress(model, progress));
        }

        private UndoResult ApplyWithProgress(
            ITextModel model,
            IProgress<TextOperationProgress>? progress)
        {
            var stopwatch = Stopwatch.StartNew();
            long lastProgressReport = 0;
            int beforeLineCount = model.LineCount;
            UndoEditRange[] ranges = _edits.Select(edit => edit.RedoRange).ToArray();
            for (int i = 0; i < _edits.Count; i++)
            {
                _edits[i].Apply(model);
                if ((i + 1) % 256 == 0)
                {
                    ReportProgressIfDue(
                        progress,
                        i + 1,
                        _edits.Count,
                        stopwatch,
                        ref lastProgressReport);
                }
            }

            progress?.Report(new TextOperationProgress(_edits.Count, _edits.Count, stopwatch.Elapsed));
            return BuildResult(model, beforeLineCount, ranges, AfterCaret);
        }

        private UndoResult UnapplyWithProgress(
            ITextModel model,
            IProgress<TextOperationProgress>? progress)
        {
            var stopwatch = Stopwatch.StartNew();
            long lastProgressReport = 0;
            int beforeLineCount = model.LineCount;
            UndoEditRange[] ranges = _edits.Select(edit => edit.UndoRange).ToArray();
            int processed = 0;
            for (int i = _edits.Count - 1; i >= 0; i--)
            {
                _edits[i].Unapply(model);
                processed++;
                if (processed % 256 == 0)
                {
                    ReportProgressIfDue(
                        progress,
                        processed,
                        _edits.Count,
                        stopwatch,
                        ref lastProgressReport);
                }
            }

            progress?.Report(new TextOperationProgress(_edits.Count, _edits.Count, stopwatch.Elapsed));
            return BuildResult(model, beforeLineCount, ranges, BeforeCaret);
        }

        private static void ReportProgressIfDue(
            IProgress<TextOperationProgress>? progress,
            int processed,
            int total,
            Stopwatch stopwatch,
            ref long lastProgressReport)
        {
            long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            if (elapsedMilliseconds - lastProgressReport < ProgressIntervalMilliseconds)
            {
                return;
            }

            lastProgressReport = elapsedMilliseconds;
            progress?.Report(new TextOperationProgress(processed, total, stopwatch.Elapsed));
        }

        private UndoResult BuildResult(
            ITextModel model,
            int beforeLineCount,
            IReadOnlyList<UndoEditRange> ranges,
            CaretState? caret)
        {
            if (ranges.Count == 0)
            {
                return new UndoResult(1, 0, model.LineCount, EmptyLines, caret);
            }

            if (ranges.Count > 1 && ranges.All(range =>
                range.OldLineCount == 1 && range.NewLineCount == 1))
            {
                TextLinePatch[] patches = ranges
                    .Select(range => Math.Clamp(range.StartLine, 1, model.LineCount))
                    .Distinct()
                    .OrderBy(lineNumber => lineNumber)
                    .Select(lineNumber => new TextLinePatch(lineNumber, model.GetLine(lineNumber)))
                    .ToArray();
                return new UndoResult(
                    patches[0].LineNumber,
                    0,
                    model.LineCount,
                    EmptyLines,
                    caret)
                {
                    LinePatches = patches
                };
            }

            int startLine = ranges.Min(range => Math.Max(1, range.StartLine));
            bool exactRange = ranges.Count == 1 || ranges.All(range => range.OldLineCount == range.NewLineCount);
            int oldLineCount;
            int newLineCount;

            if (ranges.Count == 1)
            {
                oldLineCount = Math.Max(0, ranges[0].OldLineCount);
                newLineCount = Math.Max(0, ranges[0].NewLineCount);
            }
            else if (exactRange)
            {
                int oldEndLine = ranges.Max(range => range.StartLine + Math.Max(0, range.OldLineCount) - 1);
                int newEndLine = ranges.Max(range => range.StartLine + Math.Max(0, range.NewLineCount) - 1);
                oldLineCount = Math.Max(0, oldEndLine - startLine + 1);
                newLineCount = Math.Max(0, newEndLine - startLine + 1);
            }
            else
            {
                oldLineCount = Math.Max(0, beforeLineCount - startLine + 1);
                newLineCount = Math.Max(0, model.LineCount - startLine + 1);
            }

            oldLineCount = Math.Min(oldLineCount, Math.Max(0, beforeLineCount - startLine + 1));
            newLineCount = Math.Min(newLineCount, Math.Max(0, model.LineCount - startLine + 1));

            IReadOnlyList<string> lines = newLineCount > 0
                ? model.GetLines(startLine, newLineCount)
                : EmptyLines;

            return new UndoResult(startLine, oldLineCount, model.LineCount, lines, caret);
        }
    }

    public sealed record ReplaceLineEdit(int LineNumber, string BeforeText, string AfterText) : IUndoableEdit
    {
        public UndoEditRange RedoRange => new(LineNumber, 1, 1);
        public UndoEditRange UndoRange => new(LineNumber, 1, 1);

        public void Apply(ITextModel model) => model.ReplaceLine(LineNumber, AfterText);
        public void Unapply(ITextModel model) => model.ReplaceLine(LineNumber, BeforeText);

        public bool TryMergeWith(IUndoableEdit next, TimeSpan interval, out IUndoableEdit merged)
        {
            if (interval.TotalMilliseconds < 1200 &&
                next is ReplaceLineEdit edit &&
                edit.LineNumber == LineNumber &&
                string.Equals(AfterText, edit.BeforeText, StringComparison.Ordinal))
            {
                merged = this with { AfterText = edit.AfterText };
                return true;
            }

            merged = this;
            return false;
        }
    }

    public sealed record RangeTextEdit(TextEdit Forward, TextEdit Reverse) : IUndoableEdit
    {
        private static int LineSpan(TextEdit edit) => Math.Max(1, edit.EndLine - edit.StartLine + 1);
        private static int ReplacementLineCount(TextEdit edit) =>
            Math.Max(1, (edit.Text ?? string.Empty).Count(character => character == '\n') + 1);

        public UndoEditRange RedoRange =>
            new(Forward.StartLine, LineSpan(Forward), ReplacementLineCount(Forward));

        public UndoEditRange UndoRange =>
            new(Reverse.StartLine, LineSpan(Reverse), ReplacementLineCount(Reverse));

        public void Apply(ITextModel model) => model.ApplyEdit(Forward);
        public void Unapply(ITextModel model) => model.ApplyEdit(Reverse);

        public bool TryMergeWith(IUndoableEdit next, TimeSpan interval, out IUndoableEdit merged)
        {
            merged = this;
            return false;
        }
    }

    public sealed record InsertLineEdit(int LineNumber, string InsertedText) : IUndoableEdit
    {
        public UndoEditRange RedoRange => new(LineNumber, 0, 1);
        public UndoEditRange UndoRange => new(LineNumber, 1, 0);

        public void Apply(ITextModel model) => model.InsertLine(LineNumber, InsertedText);
        public void Unapply(ITextModel model) => model.DeleteLine(LineNumber);

        public bool TryMergeWith(IUndoableEdit next, TimeSpan interval, out IUndoableEdit merged)
        {
            merged = this;
            return false;
        }
    }

    public sealed record DeleteLineEdit(int LineNumber, string DeletedText, bool LeavesEmptyLine) : IUndoableEdit
    {
        public UndoEditRange RedoRange => LeavesEmptyLine ? new(LineNumber, 1, 1) : new(LineNumber, 1, 0);
        public UndoEditRange UndoRange => LeavesEmptyLine ? new(LineNumber, 1, 1) : new(LineNumber, 0, 1);

        public void Apply(ITextModel model)
        {
            if (LeavesEmptyLine)
            {
                model.ReplaceLine(LineNumber, string.Empty);
                return;
            }

            model.DeleteLine(LineNumber);
        }

        public void Unapply(ITextModel model)
        {
            if (LeavesEmptyLine)
            {
                model.ReplaceLine(LineNumber, DeletedText);
                return;
            }

            model.InsertLine(LineNumber, DeletedText);
        }

        public bool TryMergeWith(IUndoableEdit next, TimeSpan interval, out IUndoableEdit merged)
        {
            merged = this;
            return false;
        }
    }

    public sealed record SplitLineEdit(int LineNumber, string OriginalText, string Before, string After) : IUndoableEdit
    {
        public UndoEditRange RedoRange => new(LineNumber, 1, 2);
        public UndoEditRange UndoRange => new(LineNumber, 2, 1);

        public void Apply(ITextModel model) => model.SplitLine(LineNumber, Before, After);

        public void Unapply(ITextModel model)
        {
            model.DeleteLine(LineNumber + 1);
            model.ReplaceLine(LineNumber, OriginalText);
        }

        public bool TryMergeWith(IUndoableEdit next, TimeSpan interval, out IUndoableEdit merged)
        {
            merged = this;
            return false;
        }
    }

    public sealed record MergeLineEdit(
        int LineNumber,
        string PreviousBefore,
        string CurrentBefore) : IUndoableEdit
    {
        private int PreviousLineNumber => Math.Max(1, LineNumber - 1);

        public UndoEditRange RedoRange => new(PreviousLineNumber, 2, 1);
        public UndoEditRange UndoRange => new(PreviousLineNumber, 1, 2);

        public void Apply(ITextModel model) => model.MergeLineWithPrevious(LineNumber);

        public void Unapply(ITextModel model)
        {
            model.ReplaceLine(PreviousLineNumber, PreviousBefore);
            model.InsertLine(LineNumber, CurrentBefore);
        }

        public bool TryMergeWith(IUndoableEdit next, TimeSpan interval, out IUndoableEdit merged)
        {
            merged = this;
            return false;
        }
    }
}
