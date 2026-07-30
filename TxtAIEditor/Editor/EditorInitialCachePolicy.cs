using System;

namespace TxtAIEditor.Editor
{
    internal static class EditorInitialCachePolicy
    {
        internal const int FullDocumentLineLimit = 1000;

        internal static int GetInitialLineCount(int documentLineCount, int regularWarmupLineCount)
        {
            int safeLineCount = Math.Max(1, documentLineCount);
            int safeWarmupLineCount = Math.Max(1, regularWarmupLineCount);
            return safeLineCount <= FullDocumentLineLimit
                ? safeLineCount
                : Math.Min(safeLineCount, safeWarmupLineCount);
        }
    }
}
