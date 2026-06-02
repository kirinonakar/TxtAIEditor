namespace TxtAIEditor.Core.Interfaces
{
    public interface ILanguageDetectionService
    {
        string GetMonacoLanguageName(string filePath);
        string DetectLanguageFromContent(string text, string defaultLanguage = "plaintext");
    }
}
