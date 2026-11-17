using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace TxtAIEditor.Controls
{
    internal static class PresetTextBox
    {
        internal static string NormalizePresetContent(string? value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
        }

        internal static string NormalizeTextBoxLineEndings(string? value)
        {
            return NormalizePresetContent(value).Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        internal static async Task PasteClipboardTextAsync(TextBox textBox, string presetKind)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text))
                {
                    return;
                }

                string clipboardText = await content.GetTextAsync();
                InsertTextAtSelection(textBox, NormalizeTextBoxLineEndings(clipboardText));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to paste {presetKind} preset content: {ex.Message}");
            }
        }

        private static void InsertTextAtSelection(TextBox textBox, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string current = textBox.Text ?? string.Empty;
            int selectionStart = Math.Max(0, Math.Min(textBox.SelectionStart, current.Length));
            int selectionLength = Math.Max(0, Math.Min(textBox.SelectionLength, current.Length - selectionStart));

            textBox.Text = current.Substring(0, selectionStart) +
                text +
                current.Substring(selectionStart + selectionLength);
            textBox.SelectionStart = selectionStart + text.Length;
            textBox.SelectionLength = 0;
        }
    }
}
