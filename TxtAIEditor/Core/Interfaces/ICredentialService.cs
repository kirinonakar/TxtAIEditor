namespace TxtAIEditor.Core.Interfaces
{
    public interface ICredentialService
    {
        void WriteCredential(string targetName, string userName, string password);
        string? ReadCredential(string targetName);
        void DeleteCredential(string targetName);
    }
}
