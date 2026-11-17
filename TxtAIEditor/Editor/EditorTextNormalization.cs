namespace TxtAIEditor.Editor
{
    internal static class EditorTextNormalization
    {
        internal static string NormalizeSingleLine(string? text) =>
            (text ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
    }
}
