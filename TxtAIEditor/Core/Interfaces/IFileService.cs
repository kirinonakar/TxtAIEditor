using System.Threading.Tasks;

namespace TxtAIEditor.Core.Interfaces
{
    public interface IFileService
    {
        // 일반 파일 읽기 (인코딩 자동 감지)
        Task<string> ReadTextFileAsync(string filePath);
        Task<TextFileReadResult> ReadTextFileAsync(string filePath, string encodingName);
        
        // 안전한 파일 쓰기 (임시 파일 쓰기 후 덮어쓰기 기법 적용)
        Task SaveTextFileAsync(string filePath, string content);
        Task SaveTextFileAsync(string filePath, string content, string encodingName);

        // 대용량 파일 정보 추출
        Task<LargeFileInfo> GetLargeFileInfoAsync(string filePath);

        // 대용량 파일용 Chunk 로더
        Task<string> ReadChunkAsync(string filePath, long offset, int length);

        // 대용량 파일 인덱싱 및 라인 뷰어 연동
        Task InitializeLargeFileAsync(string filePath);
        Task<int> GetLargeFileLineCountAsync(string filePath);
        Task<System.Collections.Generic.List<string>> GetLargeFileLinesAsync(string filePath, int startLine, int count);
        Task<System.Collections.Generic.List<LargeFileSearchResult>> SearchLargeFileAsync(string filePath, string query, bool isRegex, bool matchCase = false, bool wholeWord = false);
        Task SaveLargeFileWithPatchesAsync(string filePath, System.Collections.Generic.Dictionary<int, string> patches);
    }

    public class LargeFileInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public long FileSize { get; set; } // Bytes
        public bool IsLargeFile => FileSize >= 50 * 1024 * 1024; // 50MB 이상
        public bool IsUltraLargeFile => FileSize >= 200 * 1024 * 1024; // 200MB 이상
    }

    public class TextFileReadResult
    {
        public string Content { get; set; } = string.Empty;
        public string EncodingName { get; set; } = "UTF-8";
        public bool WasAutoDetected { get; set; } = true;
    }

    public class LargeFileSearchResult
    {
        public int LineNumber { get; set; }
        public string LineContent { get; set; } = string.Empty;
        public int IndexOfMatch { get; set; }
        public int MatchLength { get; set; }
    }
}
