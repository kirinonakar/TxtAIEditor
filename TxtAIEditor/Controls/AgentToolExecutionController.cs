using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Services.LLM;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentToolExecutionController
    {
        private readonly ILLMService _llmService;
        private readonly AgentFileToolService _fileTools;
        private readonly AgentFileToolController _fileToolController;
        private readonly AgentTabToolController _tabToolController;
        private readonly AgentSkillController _skillController;
        private readonly AgentMcpController _mcpController;
        private readonly Action<LlmMessageAttachment> _addImageAttachment;
        private readonly Func<JsonElement, CancellationToken, Task<string>>? _makePlanAsync;
        private readonly Action<string> _appendActivity;
        private readonly Func<string, string, string> _getString;

        public AgentToolExecutionController(
            ILLMService llmService,
            AgentFileToolService fileTools,
            AgentFileToolController fileToolController,
            AgentTabToolController tabToolController,
            AgentSkillController skillController,
            AgentMcpController mcpController,
            Action<LlmMessageAttachment> addImageAttachment,
            Func<JsonElement, CancellationToken, Task<string>>? makePlanAsync,
            Action<string> appendActivity,
            Func<string, string, string> getString)
        {
            _llmService = llmService;
            _fileTools = fileTools;
            _fileToolController = fileToolController;
            _tabToolController = tabToolController;
            _skillController = skillController;
            _mcpController = mcpController;
            _addImageAttachment = addImageAttachment;
            _makePlanAsync = makePlanAsync;
            _appendActivity = appendActivity;
            _getString = getString;
        }

        public async Task<string> ExecuteAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedToolName = NormalizeToolName(toolName);
                bool isMcpTool = _mcpController.TryGetToolAlias(normalizedToolName, out _);

                _appendActivity(GetToolStartMessage(normalizedToolName, arguments));

                string result;
                if (normalizedToolName == "replace_in_file")
                {
                    result = await _fileToolController.ReplaceInFileAsync(arguments);
                }
                else if (normalizedToolName == "search_replace")
                {
                    result = await _fileToolController.SearchReplaceAsync(arguments);
                }
                else if (isMcpTool)
                {
                    result = await _mcpController.ExecuteToolAsync(normalizedToolName, arguments, cancellationToken);
                }
                else
                {
                    result = normalizedToolName switch
                    {
                        "list_files" => await _fileTools.ListFilesAsync(
                            GetStringArgument(arguments, "glob"),
                            GetIntArgument(arguments, "maxResults", 80)),
                        "search_text" => await _fileTools.SearchTextAsync(
                            GetStringArgument(arguments, "query"),
                            GetStringArgument(arguments, "glob"),
                            GetIntArgument(arguments, "maxResults", 80)),
                        "run_rg" => await _fileTools.RunRgAsync(
                            GetStringArgument(arguments, "arguments"),
                            GetIntArgument(arguments, "timeoutMs", 10000),
                            cancellationToken),
                        "run_rga" => await _fileTools.RunRgaAsync(
                            GetStringArgument(arguments, "arguments"),
                            GetIntArgument(arguments, "timeoutMs", 10000),
                            cancellationToken),
                        "run_powershell" => await _fileTools.RunPowerShellAsync(
                            GetStringArgument(arguments, "command"),
                            GetIntArgument(arguments, "timeoutMs", 10000),
                            cancellationToken),
                        "read_file" => await _fileTools.ReadFileAsync(
                            GetStringArgument(arguments, "path"),
                            GetIntArgument(arguments, "startLine", 1),
                            GetIntArgument(arguments, "lineCount", 160)),
                        "skill_use" => await _skillController.UseSkillAsync(
                            GetFirstStringArgument(arguments, "name", "skill", "skillName", "skill_name", "path", "filePath", "file_path")),
                        "read_image" => await ReadImageToolAsync(arguments),
                        "extract_document" => await _fileTools.ExtractDocumentAsync(
                            GetExtractDocumentInputPathArgument(arguments),
                            GetExtractDocumentOutputPathArgument(arguments),
                            GetIntArgument(arguments, "maxChars", 5000000)),
                        "create_file" => await _fileToolController.CreateFileAsync(arguments),
                        "overwrite_file" => await _fileToolController.OverwriteFileAsync(arguments),
                        "append_to_file" => await _fileToolController.AppendToFileAsync(arguments),
                        "merge_files" => await _fileToolController.MergeFilesAsync(arguments),
                        "split_file" => await _fileToolController.SplitFileAsync(arguments),
                        "replace_range" => await _fileToolController.ReplaceRangeAsync(arguments),
                        "apply_patch" => await _fileToolController.ApplyPatchAsync(arguments),
                        "insert_to_file" => await _fileToolController.InsertIntoFileAsync(arguments),
                        "insert_text" => await _tabToolController.InsertTextAsync(
                            GetFirstStringArgument(arguments, "content", "text", "newText", "new_text")),
                        "create_tab" => await _tabToolController.CreateTabAsync(arguments),
                        "edit_tab" => await _tabToolController.EditTabAsync(arguments),
                        "save_tab" => await _tabToolController.SaveTabAsync(arguments),
                        "open_file" => await _fileToolController.OpenFileAsync(arguments),
                        "make_plan" => _makePlanAsync != null
                            ? await _makePlanAsync(arguments, cancellationToken)
                            : "make_plan failed: planning tool is not available.",
                        "web_search_exa" => await _llmService.SearchExaAsync(
                            GetStringArgument(arguments, "query"),
                            GetIntArgument(arguments, "numResults", 5),
                            cancellationToken),
                        "web_fetch" => await _llmService.FetchExaAsync(
                            GetUrlsArgument(arguments),
                            cancellationToken),
                        "web_fetch_exa" => await _llmService.FetchExaAsync(
                            GetUrlsArgument(arguments),
                            cancellationToken),
                        _ => $"Unknown tool: {toolName}"
                    };
                }
                cancellationToken.ThrowIfCancellationRequested();
                _fileToolController.TrackSuccessfulFileToolPath(normalizedToolName, arguments, result);

                return result;
            }
            catch (OperationCanceledException)
            {
                _appendActivity(_getString("AgentActivityToolCancelled", "도구 실행 중단됨"));
                throw;
            }
            catch (Exception ex)
            {
                string result = $"Tool failed: {ex.Message}";
                _appendActivity(string.Format(
                    _getString("AgentActivityToolFailedFormat", "도구 실패: {0}"),
                    toolName));
                return result;
            }
        }

        public string FormatDisplayResult(
            string normalizedToolName,
            JsonElement arguments,
            string toolResult,
            bool skippedDuplicateTool,
            bool verbose)
        {
            if (skippedDuplicateTool)
            {
                return _getString(
                    "AgentDuplicateToolReused",
                    "동일한 도구 호출이 이미 성공해 재실행하지 않았습니다.");
            }

            if (normalizedToolName == "read_file")
            {
                string path = GetStringArgument(arguments, "path");
                string? skillName = TryGetSkillNameFromPath(path);
                if (skillName != null)
                {
                    return string.Format(_getString("AgentVerboseReadSkillOnly", "{0} 스킬을 참고합니다."), skillName);
                }
            }
            else if (normalizedToolName == "skill_use")
            {
                string skillName = _skillController.GetSkillDisplayName(
                    GetFirstStringArgument(arguments, "name", "skill", "skillName", "skill_name", "path", "filePath", "file_path"));
                return string.Format(_getString("AgentVerboseReadSkillOnly", "{0} 스킬을 참고합니다."), skillName);
            }
            else if (normalizedToolName == "run_powershell")
            {
                string command = GetStringArgument(arguments, "command");
                string? path = TryGetPathFromGetContent(command);
                if (path != null)
                {
                    string? skillName = TryGetSkillNameFromPath(path);
                    if (skillName != null)
                    {
                        return string.Format(_getString("AgentVerboseReadSkillOnly", "{0} 스킬을 참고합니다."), skillName);
                    }
                }
            }

            if (_mcpController.TryGetToolAlias(normalizedToolName, out _))
            {
                if (verbose || toolResult.StartsWith("MCP tool failed:", StringComparison.OrdinalIgnoreCase))
                {
                    return toolResult;
                }

                return _getString("AgentVerboseMcpToolOnly", "MCP 도구를 실행했습니다");
            }

            if (verbose || toolResult.StartsWith("Tool failed:", StringComparison.OrdinalIgnoreCase))
            {
                return toolResult;
            }

            if (normalizedToolName == "read_file")
            {
                return _getString("AgentVerboseReadFileOnly", "파일을 읽었습니다");
            }

            if (normalizedToolName == "run_powershell" && !verbose)
            {
                string command = GetStringArgument(arguments, "command");
                string? path = TryGetPathFromGetContent(command);
                if (path != null)
                {
                    return _getString("AgentVerboseReadFileOnly", "파일을 읽었습니다");
                }
                return _getString("AgentVerboseRunPowerShellOnly", "PowerShell 명령을 실행했습니다");
            }

            if (normalizedToolName == "extract_document")
            {
                return _getString("AgentVerboseExtractDocumentOnly", "문서 텍스트 추출을 완료했습니다");
            }

            if (normalizedToolName == "append_to_file")
            {
                return _getString("AgentVerboseAppendFileOnly", "파일에 내용을 덧붙였습니다");
            }

            if (normalizedToolName == "search_replace")
            {
                return _getString("AgentVerboseSearchReplaceOnly", "검색/치환을 완료했습니다");
            }

            if (normalizedToolName == "merge_files")
            {
                return _getString("AgentVerboseMergeFilesOnly", "파일들을 합쳤습니다");
            }

            if (normalizedToolName == "split_file")
            {
                return _getString("AgentVerboseSplitFileOnly", "파일을 분리했습니다");
            }

            if (normalizedToolName == "list_files")
            {
                return _getString("AgentVerboseListFilesOnly", "폴더를 읽었습니다");
            }

            if (normalizedToolName == "search_text")
            {
                return _getString("AgentVerboseSearchTextOnly", "텍스트 검색을 완료했습니다");
            }

            if (normalizedToolName == "run_rg")
            {
                return _getString("AgentVerboseRunRgOnly", "Ripgrep 검색을 완료했습니다");
            }

            if (normalizedToolName == "run_rga")
            {
                return _getString("AgentVerboseRunRgaOnly", "Ripgrep All 검색을 완료했습니다");
            }

            if (normalizedToolName == "web_search_exa")
            {
                return _getString("AgentVerboseWebSearchOnly", "웹 검색을 완료했습니다");
            }

            if (normalizedToolName == "web_fetch" || normalizedToolName == "web_fetch_exa")
            {
                return _getString("AgentVerboseWebFetchOnly", "웹페이지를 읽었습니다");
            }

            if (normalizedToolName == "open_file")
            {
                string resourceKey = toolResult.StartsWith("open_file activated_existing:", StringComparison.OrdinalIgnoreCase)
                    ? "AgentVerboseOpenFileExistingOnly"
                    : "AgentVerboseOpenFileOnly";
                string fallback = toolResult.StartsWith("open_file activated_existing:", StringComparison.OrdinalIgnoreCase)
                    ? "이미 열려 있던 파일을 활성화했습니다"
                    : "파일을 열었습니다";
                return _getString(resourceKey, fallback);
            }

            if (normalizedToolName == "make_plan")
            {
                return toolResult.StartsWith("make_plan saved:", StringComparison.OrdinalIgnoreCase)
                    ? _getString("AgentVerboseMakePlanOnly", "계획서를 저장하고 열었습니다.")
                    : toolResult;
            }

            if (normalizedToolName == "save_tab")
            {
                return _getString("AgentVerboseSaveTabOnly", "탭을 저장했습니다");
            }

            if (normalizedToolName == "edit_tab")
            {
                return _getString("AgentVerboseEditTabOnly", "탭 내용을 수정했습니다");
            }

            return toolResult;
        }

        private async Task<string> ReadImageToolAsync(JsonElement arguments)
        {
            AgentReadImageResult imageResult = await _fileTools.ReadImageAsync(
                GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path"));

            if (imageResult.Attachment != null)
            {
                _addImageAttachment(imageResult.Attachment);
            }

            return imageResult.TranscriptText;
        }

        private string GetToolStartMessage(string toolName, JsonElement arguments)
        {
            if (_mcpController.TryGetToolAlias(toolName, out var mcpAlias))
            {
                return string.Format(
                    _getString("AgentActivityMcpToolFormat", "MCP 도구 실행 중: {0} ({1})"),
                    mcpAlias.ToolName,
                    mcpAlias.ServerName);
            }

            return toolName switch
            {
                "list_files" => string.Format(
                    _getString("AgentActivityListFilesFormat", "파일 목록 조회 중: {0}"),
                    GetStringArgument(arguments, "glob")),
                "search_text" => string.Format(
                    _getString("AgentActivitySearchTextFormat", "텍스트 검색 중: {0}"),
                    GetStringArgument(arguments, "query")),
                "run_rg" => string.Format(
                    _getString("AgentActivityRunRgFormat", "rg 실행 중: {0}"),
                    TruncateForActivity(GetStringArgument(arguments, "arguments"))),
                "run_rga" => string.Format(
                    _getString("AgentActivityRunRgaFormat", "rga 실행 중: {0}"),
                    TruncateForActivity(GetStringArgument(arguments, "arguments"))),
                "run_powershell" => string.Format(
                    _getString("AgentActivityRunPowerShellFormat", "PowerShell 실행 중: {0}"),
                    TruncateForActivity(GetStringArgument(arguments, "command"))),
                "read_file" => string.Format(
                    _getString("AgentActivityReadFileFormat", "파일 읽는 중: {0} ({1}줄부터 {2}줄)"),
                    GetStringArgument(arguments, "path"),
                    GetIntArgument(arguments, "startLine", 1),
                    GetIntArgument(arguments, "lineCount", 160)),
                "skill_use" => string.Format(
                    _getString("AgentActivitySkillUseFormat", "스킬 참고 중: {0}"),
                    _skillController.GetSkillDisplayName(GetFirstStringArgument(arguments, "name", "skill", "skillName", "skill_name", "path", "filePath", "file_path"))),
                "read_image" => string.Format(
                    _getString("AgentActivityReadImageFormat", "이미지 읽는 중: {0}"),
                    GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path")),
                "extract_document" => string.Format(
                    _getString("AgentActivityExtractDocumentFormat", "문서 텍스트 추출 중: {0}"),
                    GetExtractDocumentInputPathArgument(arguments)),
                "create_file" => string.Format(
                    _getString("AgentActivityCreateFileFormat", "파일 만드는 중: {0}"),
                    GetStringArgument(arguments, "path")),
                "replace_in_file" => string.Format(
                    _getString("AgentActivityReplaceFileFormat", "파일 수정 중: {0}"),
                    _fileToolController.GetEditPathArgument(arguments)),
                "search_replace" => string.Format(
                    _getString("AgentActivitySearchReplaceFormat", "검색/치환 중: {0}"),
                    _fileToolController.GetEditPathArgument(arguments)),
                "replace_range" => string.Format(
                    _getString("AgentActivityReplaceRangeFormat", "파일 범위 수정 중: {0} ({1}줄부터 {2}줄)"),
                    _fileToolController.GetEditPathArgument(arguments),
                    _fileToolController.GetReplaceRangeStartLineArgument(arguments, _fileToolController.GetEditPathArgument(arguments)),
                    _fileToolController.GetReplaceRangeEndLineArgument(arguments, _fileToolController.GetEditPathArgument(arguments))),
                "insert_to_file" => string.Format(
                    _getString("AgentActivityInsertIntoFileFormat", "파일에 내용 삽입 중: {0}"),
                    _fileToolController.GetEditPathArgument(arguments)),
                "apply_patch" => string.Format(
                    _getString("AgentActivityApplyPatchFormat", "파일 패치 적용 중: {0}"),
                    _fileToolController.GetEditPathArgument(arguments)),
                "overwrite_file" => string.Format(
                    _getString("AgentActivityOverwriteFileFormat", "파일 덮어쓰는 중: {0}"),
                    _fileToolController.GetEditPathArgument(arguments)),
                "append_to_file" => string.Format(
                    _getString("AgentActivityAppendFileFormat", "파일 덧붙이는 중: {0}"),
                    _fileToolController.GetEditPathArgument(arguments)),
                "merge_files" => string.Format(
                    _getString("AgentActivityMergeFilesFormat", "파일 합치는 중: {0}"),
                    GetFirstStringArgument(arguments, "targetPath", "target_path", "path", "target")),
                "split_file" => string.Format(
                    _getString("AgentActivitySplitFileFormat", "파일 분리하는 중: {0}"),
                    _fileToolController.GetEditPathArgument(arguments)),
                "insert_text" => _getString("AgentActivityInsertText", "현재 편집기에 입력 중"),
                "create_tab" => string.Format(
                    _getString("AgentActivityCreateTabFormat", "새 탭에 입력 중: {0}"),
                    TruncateForActivity(GetFirstStringArgument(arguments, "title", "name", "fileName", "file_name"))),
                "save_tab" => string.Format(
                    _getString("AgentActivitySaveTabFormat", "탭 저장 중: {0}"),
                    TruncateForActivity(GetFirstStringArgument(arguments, "title", "id", "tabId", "tab_id", "path", "filePath", "file_path"))),
                "edit_tab" => string.Format(
                    _getString("AgentActivityEditTabFormat", "탭 수정 중: {0}"),
                    TruncateForActivity(GetFirstStringArgument(arguments, "title", "id", "tabId", "tab_id"))),
                "open_file" => string.Format(
                    _getString("AgentActivityOpenFileFormat", "파일 여는 중: {0}"),
                    GetStringArgument(arguments, "path")),
                "make_plan" => _getString("AgentActivityMakePlan", "계획서 저장 중"),
                "web_search_exa" => string.Format(
                    _getString("AgentActivityWebSearchExaFormat", "Exa 웹 검색 중: {0}"),
                    GetStringArgument(arguments, "query")),
                "web_fetch" => string.Format(
                    _getString("AgentActivityWebFetchFormat", "웹 페이지 읽는 중: {0}"),
                    string.Join(", ", GetUrlsArgument(arguments))),
                "web_fetch_exa" => string.Format(
                    _getString("AgentActivityWebFetchExaFormat", "Exa 웹 페이지 읽는 중: {0}"),
                    string.Join(", ", GetUrlsArgument(arguments))),
                _ => string.Format(
                    _getString("AgentActivityUnknownToolFormat", "도구 실행 중: {0}"),
                    toolName)
            };
        }

        private string GetExtractDocumentInputPathArgument(JsonElement arguments)
        {
            string explicitPath = GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path", "source", "input");
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return explicitPath;
            }

            string legacyArguments = GetStringArgument(arguments, "arguments");
            if (string.IsNullOrWhiteSpace(legacyArguments))
            {
                return string.Empty;
            }

            foreach (string token in SplitCommandLineArguments(legacyArguments))
            {
                if (IsSupportedDocumentPathToken(token))
                {
                    return token;
                }
            }

            return string.Empty;
        }

        private string GetExtractDocumentOutputPathArgument(JsonElement arguments)
        {
            string explicitOutput = GetFirstStringArgument(arguments, "outputPath", "output_path", "targetPath", "target_path", "target", "output");
            if (!string.IsNullOrWhiteSpace(explicitOutput))
            {
                return explicitOutput;
            }

            string legacyArguments = GetStringArgument(arguments, "arguments");
            if (string.IsNullOrWhiteSpace(legacyArguments))
            {
                return string.Empty;
            }

            var tokens = SplitCommandLineArguments(legacyArguments);
            int sourceIndex = tokens.FindIndex(IsSupportedDocumentPathToken);
            if (sourceIndex < 0)
            {
                return string.Empty;
            }

            for (int i = sourceIndex + 1; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (string.IsNullOrWhiteSpace(token) ||
                    token.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                return token;
            }

            return string.Empty;
        }

        private static List<string> SplitCommandLineArguments(string arguments)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return tokens;
            }

            var builder = new StringBuilder();
            char quote = '\0';

            foreach (char ch in arguments)
            {
                if ((ch == '"' || ch == '\'') && (quote == '\0' || quote == ch))
                {
                    quote = quote == '\0' ? ch : '\0';
                    continue;
                }

                if (char.IsWhiteSpace(ch) && quote == '\0')
                {
                    if (builder.Length > 0)
                    {
                        tokens.Add(builder.ToString());
                        builder.Clear();
                    }
                    continue;
                }

                builder.Append(ch);
            }

            if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
            }

            return tokens;
        }

        private static bool IsSupportedDocumentPathToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.StartsWith("-", StringComparison.Ordinal))
            {
                return false;
            }

            string extension = Path.GetExtension(token);
            return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase);
        }

        private string? TryGetSkillNameFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string normPath = path.Replace('/', '\\');

            int skillIdx = normPath.LastIndexOf(@"\skills\", StringComparison.OrdinalIgnoreCase);
            if (skillIdx >= 0)
            {
                string sub = normPath.Substring(skillIdx + @"\skills\".Length);
                string[] parts = sub.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    string candidate = parts[0];
                    if (candidate.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        return Path.GetFileNameWithoutExtension(candidate);
                    }
                    return candidate;
                }
            }

            int agentIdx = normPath.LastIndexOf(@"\.agents\", StringComparison.OrdinalIgnoreCase);
            if (agentIdx >= 0)
            {
                string sub = normPath.Substring(agentIdx + @"\.agents\".Length);
                string[] parts = sub.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    return Path.GetFileNameWithoutExtension(parts[0]);
                }
            }

            if (Path.GetFileName(normPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                string? dirName = Path.GetFileName(Path.GetDirectoryName(normPath));
                if (!string.IsNullOrEmpty(dirName))
                {
                    return dirName;
                }
            }

            return null;
        }

        private static string? TryGetPathFromGetContent(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            string lower = command.ToLowerInvariant();
            
            bool hasGetContent = Regex.IsMatch(lower, @"\b(get-content|gc|cat)\b");
            if (!hasGetContent) return null;

            var matches = Regex.Matches(command, @"[""']([^""']+)[""']");
            foreach (Match match in matches)
            {
                string path = match.Groups[1].Value;
                if (path.Contains('\\') || path.Contains('/') || path.Contains('.'))
                {
                    return path;
                }
            }

            string[] tokens = command.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                if (token.StartsWith('-')) continue;
                if (token.Contains('\\') || token.Contains('/') || token.Contains('.'))
                {
                    return token;
                }
            }

            return null;
        }
    }
}
