namespace TxtAIEditor.Core.Interfaces
{
    public interface ILocalizationService
    {
        string GetString(string key, string fallback);
        void ApplyResourceLanguage();
    }
}
