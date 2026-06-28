using System;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Composition
{
    public sealed class MainWindowServices
    {
        private MainWindowServices(
            IFileService fileService,
            ISettingsService settingsService,
            ICredentialService credentialService,
            ILocalizationService localizationService,
            ILLMService llmService,
            IGitService gitService,
            ISnippetService snippetService,
            ILanguageDetectionService languageDetectionService,
            IRecentFilesService recentFilesService,
            IFileSearchService fileSearchService,
            IStickyNoteService stickyNoteService,
            ISettingsDialogService settingsDialogService,
            IUiPersonalizationService uiPersonalizationService,
            ExplorerDirectoryService explorerDirectoryService,
            PdfTextExtractionService pdfTextExtractionService,
            SecureNoteEncryptionService secureNoteEncryptionService,
            IFileSaveDialogService fileSaveDialogService,
            CompareSelectionDialogService compareSelectionDialogService,
            UnsavedChangesDialogService unsavedChangesDialogService)
        {
            FileService = fileService;
            SettingsService = settingsService;
            CredentialService = credentialService;
            LocalizationService = localizationService;
            LlmService = llmService;
            GitService = gitService;
            SnippetService = snippetService;
            LanguageDetectionService = languageDetectionService;
            RecentFilesService = recentFilesService;
            FileSearchService = fileSearchService;
            StickyNoteService = stickyNoteService;
            SettingsDialogService = settingsDialogService;
            UiPersonalizationService = uiPersonalizationService;
            ExplorerDirectoryService = explorerDirectoryService;
            PdfTextExtractionService = pdfTextExtractionService;
            SecureNoteEncryptionService = secureNoteEncryptionService;
            FileSaveDialogService = fileSaveDialogService;
            CompareSelectionDialogService = compareSelectionDialogService;
            UnsavedChangesDialogService = unsavedChangesDialogService;
        }

        public IFileService FileService { get; }
        public ISettingsService SettingsService { get; }
        public ICredentialService CredentialService { get; }
        public ILocalizationService LocalizationService { get; }
        public ILLMService LlmService { get; }
        public IGitService GitService { get; }
        public ISnippetService SnippetService { get; }
        public ILanguageDetectionService LanguageDetectionService { get; }
        public IRecentFilesService RecentFilesService { get; }
        public IFileSearchService FileSearchService { get; }
        public IStickyNoteService StickyNoteService { get; }
        public ISettingsDialogService SettingsDialogService { get; }
        public IUiPersonalizationService UiPersonalizationService { get; }
        public ExplorerDirectoryService ExplorerDirectoryService { get; }
        public PdfTextExtractionService PdfTextExtractionService { get; }
        public SecureNoteEncryptionService SecureNoteEncryptionService { get; }
        public IFileSaveDialogService FileSaveDialogService { get; }
        public CompareSelectionDialogService CompareSelectionDialogService { get; }
        public UnsavedChangesDialogService UnsavedChangesDialogService { get; }

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
            var pdfTextExtractionService = new PdfTextExtractionService();
            var secureNoteEncryptionService = new SecureNoteEncryptionService();
            var fileSaveDialogService = new FileSaveDialogService(getString);
            var compareSelectionDialogService = new CompareSelectionDialogService();
            var unsavedChangesDialogService = new UnsavedChangesDialogService();

            return new MainWindowServices(
                fileService,
                settingsService,
                credentialService,
                localizationService,
                llmService,
                gitService,
                snippetService,
                languageDetectionService,
                recentFilesService,
                fileSearchService,
                stickyNoteService,
                settingsDialogService,
                uiPersonalizationService,
                explorerDirectoryService,
                pdfTextExtractionService,
                secureNoteEncryptionService,
                fileSaveDialogService,
                compareSelectionDialogService,
                unsavedChangesDialogService);
        }
    }
}
