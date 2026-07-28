using System;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Composition
{
    internal sealed record MainWindowCommonServices(
        ISettingsService SettingsService,
        ILocalizationService LocalizationService,
        ILanguageDetectionService LanguageDetectionService);

    internal sealed record MainWindowDocumentServices(
        IFileService FileService,
        IFileSaveDialogService FileSaveDialogService,
        SecureNoteEncryptionService SecureNoteEncryptionService,
        UnsavedChangesDialogService UnsavedChangesDialogService);

    internal sealed record MainWindowWorkspaceServices(
        IGitService GitService,
        IRecentFilesService RecentFilesService,
        IFileSearchService FileSearchService,
        ExplorerDirectoryService ExplorerDirectoryService,
        RemoteWorkspaceService RemoteWorkspaceService,
        ArchiveExplorerService ArchiveExplorerService);

    internal sealed record MainWindowEditorServices(
        ISnippetService SnippetService,
        PdfTextExtractionService PdfTextExtractionService);

    internal sealed record MainWindowAgentServices(
        ICredentialService CredentialService,
        ILLMService LlmService);

    internal sealed record MainWindowShellServices(
        IStickyNoteService StickyNoteService,
        ISettingsDialogService SettingsDialogService,
        IUiPersonalizationService UiPersonalizationService,
        CompareSelectionDialogService CompareSelectionDialogService);

    internal sealed class MainWindowServices
    {
        private MainWindowServices(
            MainWindowCommonServices common,
            MainWindowDocumentServices documents,
            MainWindowWorkspaceServices workspace,
            MainWindowEditorServices editor,
            MainWindowAgentServices agents,
            MainWindowShellServices shell)
        {
            Common = common;
            Documents = documents;
            Workspace = workspace;
            Editor = editor;
            Agents = agents;
            Shell = shell;
        }

        public MainWindowCommonServices Common { get; }
        public MainWindowDocumentServices Documents { get; }
        public MainWindowWorkspaceServices Workspace { get; }
        public MainWindowEditorServices Editor { get; }
        public MainWindowAgentServices Agents { get; }
        public MainWindowShellServices Shell { get; }

        public static MainWindowServices Create(Func<string, string, string> getString)
        {
            var fileService = new FileService();
            var settingsService = new SettingsService();
            var credentialService = new CredentialService();
            var localizationService = new ResourceLocalizationService(settingsService);
            var llmService = new LLMService(settingsService, credentialService, localizationService);
            var gitService = new GitService();
            var snippetService = new SnippetService();
            var languageDetectionService = new LanguageDetectionService();
            var recentFilesService = new RecentFilesService();
            var fileSearchService = new FileSearchService(fileService);
            var stickyNoteService = new StickyNoteService(getString);
            var settingsDialogService = new SettingsDialogService(llmService);
            var uiPersonalizationService = new UiPersonalizationService();
            var explorerDirectoryService = new ExplorerDirectoryService();
            var remoteWorkspaceService = new RemoteWorkspaceService(credentialService);
            var archiveExplorerService = new ArchiveExplorerService();
            var pdfTextExtractionService = new PdfTextExtractionService();
            var secureNoteEncryptionService = new SecureNoteEncryptionService();
            var fileSaveDialogService = new FileSaveDialogService(getString);
            var compareSelectionDialogService = new CompareSelectionDialogService();
            var unsavedChangesDialogService = new UnsavedChangesDialogService();

            return new MainWindowServices(
                new MainWindowCommonServices(
                    settingsService,
                    localizationService,
                    languageDetectionService),
                new MainWindowDocumentServices(
                    fileService,
                    fileSaveDialogService,
                    secureNoteEncryptionService,
                    unsavedChangesDialogService),
                new MainWindowWorkspaceServices(
                    gitService,
                    recentFilesService,
                    fileSearchService,
                    explorerDirectoryService,
                    remoteWorkspaceService,
                    archiveExplorerService),
                new MainWindowEditorServices(
                    snippetService,
                    pdfTextExtractionService),
                new MainWindowAgentServices(
                    credentialService,
                    llmService),
                new MainWindowShellServices(
                    stickyNoteService,
                    settingsDialogService,
                    uiPersonalizationService,
                    compareSelectionDialogService));
        }
    }
}
