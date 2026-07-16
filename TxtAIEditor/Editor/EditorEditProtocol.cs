using System;

namespace TxtAIEditor.Editor
{
    public sealed record EditorEditCommand(
        string DocumentId,
        string ViewId,
        long EditId,
        long BaseVersion,
        TextEdit Edit,
        bool IsCompositionCommit);

    public sealed record EditorEditCommandResult(
        long EditId,
        bool IsAccepted,
        long CurrentVersion,
        long ResyncFromVersion,
        EditorDocumentChange? Change);

    public enum EditorEditRequestKind
    {
        TextEdit,
        ReplaceLine,
        InsertLine,
        SplitLine,
        MergeLineWithPrevious,
        DeleteLine
    }

    public sealed record EditorEditRequest(
        string DocumentId,
        string ViewId,
        long EditId,
        long BaseVersion,
        EditorEditRequestKind Kind,
        TextEdit Edit,
        int LineNumber,
        string Before,
        string After,
        bool IsCompositionCommit)
    {
        public bool TryNormalize(ITextModel model, out EditorEditCommand command)
        {
            command = default!;
            if (model.LineCount <= 0 || string.IsNullOrEmpty(DocumentId) || string.IsNullOrEmpty(ViewId))
            {
                return false;
            }

            TextEdit normalized;
            switch (Kind)
            {
                case EditorEditRequestKind.TextEdit:
                    normalized = Edit;
                    break;

                case EditorEditRequestKind.ReplaceLine:
                    if (!IsValidLine(model, LineNumber)) return false;
                    normalized = new TextEdit(
                        LineNumber,
                        1,
                        LineNumber,
                        model.GetLine(LineNumber).Length + 1,
                        After);
                    break;

                case EditorEditRequestKind.InsertLine:
                    {
                        int requestedLine = Math.Clamp(LineNumber, 1, model.LineCount + 1);
                        if (requestedLine <= model.LineCount)
                        {
                            normalized = new TextEdit(
                                requestedLine,
                                1,
                                requestedLine,
                                1,
                                NormalizeSingleLine(After) + "\n");
                        }
                        else
                        {
                            int lastLine = model.LineCount;
                            int lastColumn = model.GetLine(lastLine).Length + 1;
                            normalized = new TextEdit(
                                lastLine,
                                lastColumn,
                                lastLine,
                                lastColumn,
                                "\n" + NormalizeSingleLine(After));
                        }
                        break;
                    }

                case EditorEditRequestKind.SplitLine:
                    if (!IsValidLine(model, LineNumber)) return false;
                    normalized = new TextEdit(
                        LineNumber,
                        1,
                        LineNumber,
                        model.GetLine(LineNumber).Length + 1,
                        NormalizeSingleLine(Before) + "\n" + NormalizeSingleLine(After));
                    break;

                case EditorEditRequestKind.MergeLineWithPrevious:
                    if (LineNumber <= 1 || LineNumber > model.LineCount) return false;
                    normalized = new TextEdit(
                        LineNumber - 1,
                        model.GetLine(LineNumber - 1).Length + 1,
                        LineNumber,
                        1,
                        string.Empty);
                    break;

                case EditorEditRequestKind.DeleteLine:
                    if (!IsValidLine(model, LineNumber)) return false;
                    if (model.LineCount == 1)
                    {
                        normalized = new TextEdit(
                            1,
                            1,
                            1,
                            model.GetLine(1).Length + 1,
                            string.Empty);
                    }
                    else if (LineNumber < model.LineCount)
                    {
                        normalized = new TextEdit(
                            LineNumber,
                            1,
                            LineNumber + 1,
                            1,
                            string.Empty);
                    }
                    else
                    {
                        int previousLine = LineNumber - 1;
                        normalized = new TextEdit(
                            previousLine,
                            model.GetLine(previousLine).Length + 1,
                            LineNumber,
                            model.GetLine(LineNumber).Length + 1,
                            string.Empty);
                    }
                    break;

                default:
                    return false;
            }

            command = new EditorEditCommand(
                DocumentId,
                ViewId,
                EditId,
                BaseVersion,
                normalized,
                IsCompositionCommit);
            return true;
        }

        private static bool IsValidLine(ITextModel model, int lineNumber) =>
            lineNumber >= 1 && lineNumber <= model.LineCount;

        private static string NormalizeSingleLine(string text) =>
            (text ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
    }
}
