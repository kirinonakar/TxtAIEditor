namespace TxtAIEditor.Core.Interfaces
{
    public interface ILanguageDetectionService
    {
        string GetEditorLanguageName(string filePath);
        string DetectLanguageFromContent(string text, string defaultLanguage = "plaintext");
    }
}
