using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    internal enum ComfyUiLaunchFailure
    {
        None,
        MissingPath,
        FileNotFound,
        Error
    }

    internal sealed class AgentMcpComfyUiTool
    {
        private const string BuiltInComfyUiId = "builtin-comfyui";
        private const string BuiltInComfyUiName = "ComfyUI";
        private const string BuiltInComfyUiGenerateAlias = "mcp_comfyui_generate_image";
        private const string BuiltInComfyUiReadWorkflowAlias = "mcp_comfyui_read_workflow";
        private const string BuiltInComfyUiGenerateToolName = "generate_image";
        private const string BuiltInComfyUiReadWorkflowToolName = "read_workflow";
        private const string DefaultComfyUiEndpoint = "http://127.0.0.1:8188";
        private const int DefaultComfyUiTimeoutSeconds = 300;
        private const int DefaultComfyUiPollIntervalMs = 1000;
        private const int WorkflowContextLimit = 50;
        private const int ReadWorkflowCharacterLimit = 200_000;
        private const string BuiltInComfyUiInputSchemaJson = """
        {
          "type": "object",
          "properties": {
            "apiJson": {
              "type": "string",
              "description": "ComfyUI workflow API JSON. Pass either apiJson or workflowFile. IMPORTANT: DO NOT write local file paths (e.g. D:\\path\\img.png) inside LoadImage nodes in apiJson. Instead, pass local paths to 'inputImagePath' or 'inputImages', and set the target 'image' values inside apiJson to empty string (\"\"). The system uploads files and automatically maps them to empty LoadImage nodes in order."
            },
            "workflowFile": {
              "type": "string",
              "description": "Optional ComfyUI API workflow JSON file. Relative paths resolve under the configured ComfyUI API workflow folder shown in the MCP context. Use this when choosing from the provided workflow list."
            },
            "parameters": {
              "type": "object",
              "description": "Optional replacements. Keys replace {{key}} placeholders and dot paths such as 6.inputs.text. You can set 6.inputs.image to \"\" to clear default workflow image names so that uploaded input images can be mapped."
            },
            "prompt": {
              "type": "string",
              "description": "Positive image prompt. If no explicit parameter path is provided, TxtAIEditor analyzes the workflow and fills a positive prompt text slot such as an empty StringConcatenate inputs.string_a linked from CLIPTextEncode."
            },
            "inputImagePath": {
              "type": "string",
              "description": "Optional local image path to upload to the ComfyUI input folder before running the workflow. TxtAIEditor uploads the file and automatically populates the first LoadImage node's image parameter that has an empty string value (\"\") in the workflow. DO NOT put local file paths inside the apiJson."
            },
            "inputImages": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional local image paths to upload to the ComfyUI input folder. Empty LoadImage inputs (those with \"\" as the value in apiJson) are filled with the uploaded filenames in workflow order. DO NOT put local file paths inside the apiJson."
            },
            "outputFileName": {
              "type": "string",
              "description": "File name or path for the generated image. Relative paths are saved under the current workspace."
            },
            "endpoint": {
              "type": "string",
              "description": "ComfyUI base URL. Defaults to http://127.0.0.1:8188."
            },
            "timeoutSeconds": {
              "type": "integer",
              "default": 300
            },
            "outputNodeId": {
              "type": "string",
              "description": "Optional ComfyUI output node id to prefer when several image outputs exist."
            }
          },
          "required": ["outputFileName"]
        }
        """;
        private const string BuiltInComfyUiReadWorkflowInputSchemaJson = """
        {
          "type": "object",
          "properties": {
            "workflowFile": {
              "type": "string",
              "description": "ComfyUI API workflow JSON file to read. Relative paths resolve under the configured ComfyUI API workflow folder."
            }
          },
          "required": ["workflowFile"]
        }
        """;

        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly Func<string> _workspaceRootProvider;
        private readonly Func<EditorSettings> _settingsProvider;
        private readonly Func<string, Task>? _fileModifiedAsync;

        public AgentMcpComfyUiTool(
            Func<string> workspaceRootProvider,
            Func<EditorSettings> settingsProvider,
            Func<string, Task>? fileModifiedAsync)
        {
            _workspaceRootProvider = workspaceRootProvider;
            _settingsProvider = settingsProvider;
            _fileModifiedAsync = fileModifiedAsync;
        }

        public string ServerId => BuiltInComfyUiId;

        public string ServerName => BuiltInComfyUiName;

        public bool IsServerName(string serverName)
        {
            return serverName.Equals(BuiltInComfyUiName, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsServerId(string serverId)
        {
            return serverId.Equals(BuiltInComfyUiId, StringComparison.OrdinalIgnoreCase);
        }

        public bool CanHandleAlias(AgentMcpToolAlias alias)
        {
            return alias.Alias.Equals(BuiltInComfyUiGenerateAlias, StringComparison.OrdinalIgnoreCase) ||
                alias.Alias.Equals(BuiltInComfyUiReadWorkflowAlias, StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<AgentMcpToolAlias> CreateAliases()
        {
            return new[]
            {
                CreateGenerateAlias(),
                CreateReadWorkflowAlias()
            };
        }

        public AgentMcpToolAlias CreateGenerateAlias()
        {
            return new AgentMcpToolAlias
            {
                Alias = BuiltInComfyUiGenerateAlias,
                ServerId = BuiltInComfyUiId,
                ServerName = BuiltInComfyUiName,
                ToolName = BuiltInComfyUiGenerateToolName,
                Description = "Generate an image through a local or remote ComfyUI HTTP API workflow, then download and save the produced image file. Pass either apiJson or workflowFile. IMPORTANT for image inputs: if input images are used, do NOT modify apiJson directly to include local absolute paths. Instead, set the target image properties inside apiJson (or via parameters replacement) to \"\" and pass local image paths through inputImagePath or inputImages so the tool can upload and map them automatically.",
                InputSchemaJson = BuiltInComfyUiInputSchemaJson,
                IsBuiltIn = true
            };
        }

        public AgentMcpToolAlias CreateReadWorkflowAlias()
        {
            return new AgentMcpToolAlias
            {
                Alias = BuiltInComfyUiReadWorkflowAlias,
                ServerId = BuiltInComfyUiId,
                ServerName = BuiltInComfyUiName,
                ToolName = BuiltInComfyUiReadWorkflowToolName,
                Description = "Read a configured ComfyUI API workflow JSON file so you can inspect node ids, inputs, placeholders, and parameter paths before calling mcp_comfyui_generate_image.",
                InputSchemaJson = BuiltInComfyUiReadWorkflowInputSchemaJson,
                IsBuiltIn = true
            };
        }

        public AgentMcpItem CreateMenuItem(bool isSelected, Func<string, string, string> getString, string status)
        {
            string detail = getString("AgentMcpComfyUiDetail", "내장 플러그인 - API JSON/파라미터로 이미지 생성");
            if (!string.IsNullOrWhiteSpace(status))
            {
                detail += " - " + status;
            }

            return new AgentMcpItem
            {
                Name = BuiltInComfyUiName,
                Endpoint = DefaultComfyUiEndpoint,
                Detail = detail,
                IsSelected = isSelected,
                IsBuiltIn = true,
                CanEdit = false,
                CanDelete = false
            };
        }

        public async Task<string> ExecuteAsync(
            AgentMcpToolAlias alias,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            if (alias.Alias.Equals(BuiltInComfyUiReadWorkflowAlias, StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteReadWorkflowAsync(arguments, cancellationToken);
            }

            return await ExecuteGenerateImageAsync(arguments, cancellationToken);
        }

        private async Task<string> ExecuteGenerateImageAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            string apiJson = GetJsonArgument(arguments, "apiJson", "api_json", "workflowJson", "workflow_json", "promptJson", "prompt_json");
            string workflowFile = AgentToolHelpers.GetFirstStringArgument(
                arguments,
                "workflowFile",
                "workflow_file",
                "workflowPath",
                "workflow_path",
                "apiWorkflowFile",
                "api_workflow_file",
                "apiWorkflowPath",
                "api_workflow_path");
            string workflowDisplayPath = string.Empty;
            if (string.IsNullOrWhiteSpace(apiJson))
            {
                if (string.IsNullOrWhiteSpace(workflowFile))
                {
                    return "MCP tool failed: pass either ComfyUI apiJson or workflowFile.";
                }

                string workflowPath = ResolveComfyWorkflowPath(workflowFile);
                apiJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);
                workflowDisplayPath = GetComfyWorkflowDisplayPath(workflowPath);
            }

            string outputPath = AgentToolHelpers.GetFirstStringArgument(
                arguments,
                "outputFileName",
                "output_file_name",
                "fileName",
                "filename",
                "saveAs",
                "save_as",
                "outputPath",
                "output_path",
                "path");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return "MCP tool failed: ComfyUI outputFileName is empty.";
            }

            string endpoint = AgentToolHelpers.GetFirstStringArgument(arguments, "endpoint", "baseUrl", "base_url", "server", "url");
            endpoint = NormalizeComfyEndpoint(endpoint);
            await EnsureDefaultEndpointStartedForExecutionAsync(endpoint, cancellationToken);
            string outputNodeId = AgentToolHelpers.GetFirstStringArgument(arguments, "outputNodeId", "output_node_id", "nodeId", "node_id");
            string promptText = AgentToolHelpers.GetFirstStringArgument(
                arguments,
                "prompt",
                "positivePrompt",
                "positive_prompt",
                "imagePrompt",
                "image_prompt",
                "text",
                "string_a");
            int timeoutSeconds = Math.Max(1, AgentToolHelpers.GetIntArgument(arguments, "timeoutSeconds", DefaultComfyUiTimeoutSeconds));
            int pollIntervalMs = Math.Clamp(
                AgentToolHelpers.GetIntArgument(arguments, "pollIntervalMs", DefaultComfyUiPollIntervalMs),
                250,
                10_000);

            JsonObject parameters = ExtractJsonObjectArgument(arguments, "parameters", "params");
            IReadOnlyList<ComfyInputImageRef> inputImages = await UploadComfyInputImagesAsync(
                endpoint,
                ExtractComfyInputImagePaths(arguments),
                cancellationToken);
            JsonObject promptPayload = BuildComfyPromptPayload(apiJson, parameters, promptText, inputImages);
            using JsonDocument promptResponse = await PostComfyJsonAsync(endpoint, "prompt", promptPayload, cancellationToken);
            string promptId = TryGetStringProperty(promptResponse.RootElement, "prompt_id");
            if (string.IsNullOrWhiteSpace(promptId))
            {
                return "MCP tool failed: ComfyUI did not return prompt_id.";
            }

            ComfyImageRef image = await WaitForComfyImageAsync(
                endpoint,
                promptId,
                outputNodeId,
                TimeSpan.FromSeconds(timeoutSeconds),
                pollIntervalMs,
                cancellationToken);
            byte[] imageBytes = await DownloadComfyImageAsync(endpoint, image, cancellationToken);
            string fullPath = ResolveComfyOutputPath(outputPath, image);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(fullPath, imageBytes, cancellationToken);
            if (_fileModifiedAsync != null)
            {
                await _fileModifiedAsync(fullPath);
            }

            string root = ResolveWorkspaceRoot();
            string displayPath = AgentWorkspaceFileResolver.IsInsideRoot(root, fullPath)
                ? AgentWorkspaceFileResolver.RelativePath(root, fullPath)
                : fullPath;
            string uploadedText = inputImages.Count == 0
                ? string.Empty
                : "\ninput_images: " + string.Join(", ", inputImages.Select(item => item.FileName));
            string workflowText = string.IsNullOrWhiteSpace(workflowDisplayPath)
                ? string.Empty
                : $"\nworkflow: {workflowDisplayPath}";
            return $"MCP tool result: ComfyUI image saved: {displayPath}\nprompt_id: {promptId}\nsource_image: {image.FileName}{workflowText}{uploadedText}";
        }

        private async Task<string> ExecuteReadWorkflowAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            string workflowFile = AgentToolHelpers.GetFirstStringArgument(
                arguments,
                "workflowFile",
                "workflow_file",
                "workflowPath",
                "workflow_path",
                "apiWorkflowFile",
                "api_workflow_file",
                "apiWorkflowPath",
                "api_workflow_path",
                "path",
                "file");
            if (string.IsNullOrWhiteSpace(workflowFile))
            {
                return "MCP tool failed: ComfyUI workflowFile is empty.";
            }

            string workflowPath = ResolveComfyWorkflowPath(workflowFile);
            string content = await File.ReadAllTextAsync(workflowPath, cancellationToken);
            bool truncated = content.Length > ReadWorkflowCharacterLimit;
            if (truncated)
            {
                content = content.Substring(0, ReadWorkflowCharacterLimit);
            }

            string truncatedNote = truncated
                ? $"\n[truncated after {ReadWorkflowCharacterLimit:N0} characters]"
                : string.Empty;
            return $"MCP tool result: ComfyUI workflow JSON: {GetComfyWorkflowDisplayPath(workflowPath)}\n{content}{truncatedNote}";
        }

        public void AppendWorkflowContext(StringBuilder builder)
        {
            string workflowDirectory;
            try
            {
                workflowDirectory = ResolveComfyWorkflowDirectory(createIfMissing: true);
            }
            catch (Exception ex)
            {
                builder.AppendLine($"ComfyUI API workflow folder: unavailable ({ex.Message})");
                builder.AppendLine();
                return;
            }

            builder.AppendLine($"ComfyUI API workflow folder: {workflowDirectory}");
            builder.AppendLine("Use mcp_comfyui_read_workflow with workflowFile to inspect a listed JSON file, then call mcp_comfyui_generate_image with either workflowFile or apiJson.");

            IReadOnlyList<ComfyWorkflowFile> workflows = GetWorkflowFiles(workflowDirectory, out bool hasMore, out string error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                builder.AppendLine($"Workflow list unavailable: {error}");
                builder.AppendLine();
                return;
            }

            if (workflows.Count == 0)
            {
                builder.AppendLine("Available workflow API JSON files: none found.");
                builder.AppendLine();
                return;
            }

            builder.AppendLine("Available workflow API JSON files:");
            foreach (var workflow in workflows)
            {
                builder.AppendLine($"- {workflow.RelativePath} ({FormatByteSize(workflow.SizeBytes)}, modified {workflow.ModifiedAt:yyyy-MM-dd HH:mm})");
            }

            if (hasMore)
            {
                builder.AppendLine($"- ... more than {WorkflowContextLimit:N0} workflow files; ask the user to narrow the folder or use a specific relative path.");
            }

            builder.AppendLine();
        }

        public async Task<bool> IsServerRunningAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildComfyUrl(DefaultComfyUiEndpoint, "system_stats"));
                using HttpResponseMessage response = await HttpClient.SendAsync(request, timeout.Token);
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public async Task<bool> WaitForServerRunningAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsServerRunningAsync(cancellationToken))
                {
                    return true;
                }

                await Task.Delay(1000, cancellationToken);
            }

            return false;
        }

        public bool TryStartConfiguredComfyUi(out ComfyUiLaunchFailure failure, out string detail)
        {
            failure = ComfyUiLaunchFailure.None;
            detail = string.Empty;
            string launchPath = GetSettings().ComfyUiLaunchPath?.Trim().Trim('"') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(launchPath))
            {
                failure = ComfyUiLaunchFailure.MissingPath;
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(launchPath));
                if (!File.Exists(fullPath))
                {
                    failure = ComfyUiLaunchFailure.FileNotFound;
                    detail = fullPath;
                    return false;
                }

                string? workingDirectory = Path.GetDirectoryName(fullPath);
                var startInfo = new ProcessStartInfo
                {
                    FileName = fullPath,
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Minimized
                };

                Process? process = Process.Start(startInfo);
                if (process == null)
                {
                    failure = ComfyUiLaunchFailure.Error;
                    detail = "Process.Start returned null.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                failure = ComfyUiLaunchFailure.Error;
                detail = ex.Message;
                return false;
            }
        }

        private async Task EnsureDefaultEndpointStartedForExecutionAsync(string endpoint, CancellationToken cancellationToken)
        {
            if (!NormalizeComfyEndpoint(endpoint).Equals(DefaultComfyUiEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (await IsServerRunningAsync(cancellationToken))
            {
                return;
            }

            if (!TryStartConfiguredComfyUi(out ComfyUiLaunchFailure failure, out string detail))
            {
                string message = failure switch
                {
                    ComfyUiLaunchFailure.MissingPath => "ComfyUI is not running and no ComfyUI launch path is configured.",
                    ComfyUiLaunchFailure.FileNotFound => $"ComfyUI is not running and the configured launch file was not found: {detail}",
                    _ => $"ComfyUI is not running and automatic launch failed: {detail}"
                };
                throw new InvalidOperationException(message);
            }

            if (!await WaitForServerRunningAsync(TimeSpan.FromSeconds(45), cancellationToken))
            {
                throw new TimeoutException("ComfyUI was launched but did not become reachable at http://127.0.0.1:8188 within 45 seconds.");
            }
        }

        private EditorSettings GetSettings()
        {
            try
            {
                return _settingsProvider() ?? new EditorSettings();
            }
            catch
            {
                return new EditorSettings();
            }
        }

        private string ResolveComfyWorkflowDirectory(bool createIfMissing)
        {
            string directory = GetSettings().ComfyUiWorkflowDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = EditorSettings.GetDefaultComfyUiWorkflowDirectory();
            }

            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (createIfMissing)
            {
                Directory.CreateDirectory(fullPath);
            }

            return fullPath;
        }

        private string ResolveComfyWorkflowPath(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                throw new InvalidOperationException("ComfyUI workflowFile is empty.");
            }

            string root = ResolveComfyWorkflowDirectory(createIfMissing: true);
            string path = requestedPath.Trim().Trim('"');
            string fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
                : Path.GetFullPath(Path.Combine(root, path));

            if (!File.Exists(fullPath) && string.IsNullOrWhiteSpace(Path.GetExtension(fullPath)))
            {
                string jsonPath = fullPath + ".json";
                if (File.Exists(jsonPath))
                {
                    fullPath = jsonPath;
                }
            }

            if (!AgentWorkspaceFileResolver.IsInsideRoot(root, fullPath))
            {
                throw new InvalidOperationException("ComfyUI workflowFile must stay inside the configured workflow API folder.");
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("ComfyUI workflow file was not found.", requestedPath);
            }

            return fullPath;
        }

        private string GetComfyWorkflowDisplayPath(string workflowPath)
        {
            string root = ResolveComfyWorkflowDirectory(createIfMissing: false);
            return AgentWorkspaceFileResolver.IsInsideRoot(root, workflowPath)
                ? Path.GetRelativePath(root, workflowPath)
                : workflowPath;
        }

        private static IReadOnlyList<ComfyWorkflowFile> GetWorkflowFiles(
            string workflowDirectory,
            out bool hasMore,
            out string error)
        {
            hasMore = false;
            error = string.Empty;
            try
            {
                if (!Directory.Exists(workflowDirectory))
                {
                    return Array.Empty<ComfyWorkflowFile>();
                }

                var files = Directory
                    .EnumerateFiles(workflowDirectory, "*.json", SearchOption.AllDirectories)
                    .OrderBy(path => Path.GetRelativePath(workflowDirectory, path), StringComparer.OrdinalIgnoreCase)
                    .Take(WorkflowContextLimit + 1)
                    .ToList();
                hasMore = files.Count > WorkflowContextLimit;
                if (hasMore)
                {
                    files.RemoveAt(files.Count - 1);
                }

                return files.Select(path =>
                {
                    var info = new FileInfo(path);
                    return new ComfyWorkflowFile
                    {
                        RelativePath = Path.GetRelativePath(workflowDirectory, path),
                        SizeBytes = info.Length,
                        ModifiedAt = info.LastWriteTime
                    };
                }).ToList();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return Array.Empty<ComfyWorkflowFile>();
            }
        }

        private static string FormatByteSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B";
            }

            double value = bytes / 1024.0;
            if (value < 1024)
            {
                return value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB";
            }

            value /= 1024.0;
            return value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        private static string GetJsonArgument(JsonElement arguments, params string[] names)
        {
            if (arguments.ValueKind == JsonValueKind.String)
            {
                return arguments.GetString() ?? string.Empty;
            }

            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (string name in names)
            {
                if (!arguments.TryGetProperty(name, out var value))
                {
                    continue;
                }

                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? string.Empty,
                    JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                    _ => string.Empty
                };
            }

            return string.Empty;
        }

        private static JsonObject ExtractJsonObjectArgument(JsonElement arguments, params string[] names)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return new JsonObject();
            }

            foreach (string name in names)
            {
                if (!arguments.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Object)
                {
                    return JsonNode.Parse(value.GetRawText()) as JsonObject ?? new JsonObject();
                }

                if (value.ValueKind == JsonValueKind.String)
                {
                    string json = value.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return new JsonObject();
                    }

                    return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
                }
            }

            return new JsonObject();
        }

        private static JsonObject BuildComfyPromptPayload(
            string apiJson,
            JsonObject parameters,
            string promptText,
            IReadOnlyList<ComfyInputImageRef> inputImages)
        {
            JsonNode workflowNode = JsonNode.Parse(apiJson) ??
                throw new InvalidOperationException("ComfyUI apiJson is not valid JSON.");

            if (workflowNode is not JsonObject workflowObject)
            {
                throw new InvalidOperationException("ComfyUI apiJson must be a JSON object.");
            }

            string clientId = Guid.NewGuid().ToString("N");
            if (workflowObject.TryGetPropertyValue("prompt", out JsonNode? promptNode) && promptNode != null)
            {
                ApplyComfyParameters(promptNode, parameters);
                ApplyComfyPromptText(promptNode, promptText);
                ApplyComfyInputImages(promptNode, inputImages);
                if (!workflowObject.ContainsKey("client_id"))
                {
                    workflowObject["client_id"] = clientId;
                }

                return workflowObject;
            }

            ApplyComfyParameters(workflowObject, parameters);
            ApplyComfyPromptText(workflowObject, promptText);
            ApplyComfyInputImages(workflowObject, inputImages);
            return new JsonObject
            {
                ["prompt"] = workflowObject,
                ["client_id"] = clientId
            };
        }

        private static int ApplyComfyParameters(JsonNode workflowNode, JsonObject parameters)
        {
            if (parameters.Count == 0)
            {
                return 0;
            }

            int replacementCount = ReplaceComfyPlaceholders(workflowNode, parameters);
            foreach (var parameter in parameters.ToList())
            {
                if (!parameter.Key.Contains('.', StringComparison.Ordinal))
                {
                    continue;
                }

                if (TrySetJsonPath(workflowNode, parameter.Key, parameter.Value))
                {
                    replacementCount++;
                }
            }

            return replacementCount;
        }

        private static bool ApplyComfyPromptText(JsonNode workflowNode, string promptText)
        {
            if (string.IsNullOrWhiteSpace(promptText) || workflowNode is not JsonObject workflowObject)
            {
                return false;
            }

            foreach (var node in workflowObject)
            {
                if (node.Value is not JsonObject nodeObject ||
                    !IsPositiveClipTextEncodeNode(nodeObject))
                {
                    continue;
                }

                if (TryApplyPromptToClipTextInput(workflowObject, nodeObject, promptText))
                {
                    return true;
                }
            }

            foreach (var node in workflowObject)
            {
                if (node.Value is JsonObject nodeObject &&
                    TryApplyPromptToStringA(nodeObject, promptText))
                {
                    return true;
                }
            }

            foreach (var node in workflowObject)
            {
                if (node.Value is JsonObject nodeObject &&
                    !IsNegativePromptNode(nodeObject) &&
                    TryApplyPromptToTextInput(nodeObject, promptText))
                {
                    return true;
                }
            }

            return false;
        }

        private static int ApplyComfyInputImages(JsonNode workflowNode, IReadOnlyList<ComfyInputImageRef> inputImages)
        {
            if (inputImages.Count == 0 || workflowNode is not JsonObject workflowObject)
            {
                return 0;
            }

            int imageIndex = 0;
            foreach (var node in workflowObject)
            {
                if (imageIndex >= inputImages.Count)
                {
                    return imageIndex;
                }

                if (node.Value is JsonObject nodeObject &&
                    IsLoadImageNode(nodeObject) &&
                    TryApplyInputImageToNode(nodeObject, inputImages[imageIndex]))
                {
                    imageIndex++;
                }
            }

            foreach (var node in workflowObject)
            {
                if (imageIndex >= inputImages.Count)
                {
                    return imageIndex;
                }

                if (node.Value is JsonObject nodeObject &&
                    !IsLoadImageNode(nodeObject) &&
                    TryApplyInputImageToNode(nodeObject, inputImages[imageIndex]))
                {
                    imageIndex++;
                }
            }

            return imageIndex;
        }

        private static bool TryApplyInputImageToNode(JsonObject nodeObject, ComfyInputImageRef image)
        {
            if (!TryGetInputsObject(nodeObject, out var inputs) ||
                !inputs.TryGetPropertyValue("image", out JsonNode? imageNode) ||
                !IsEmptyJsonString(imageNode))
            {
                return false;
            }

            inputs["image"] = JsonValue.Create(image.FileName);
            return true;
        }

        private static bool IsLoadImageNode(JsonObject nodeObject)
        {
            string classType = GetJsonObjectString(nodeObject, "class_type");
            string title = GetNodeTitle(nodeObject);
            return classType.Contains("LoadImage", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Load Image", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryApplyPromptToClipTextInput(JsonObject workflowObject, JsonObject clipNode, string promptText)
        {
            if (!TryGetInputsObject(clipNode, out var inputs) ||
                !inputs.TryGetPropertyValue("text", out JsonNode? textNode))
            {
                return false;
            }

            if (textNode is JsonArray link &&
                link.Count > 0 &&
                TryGetJsonString(link[0], out string linkedNodeId) &&
                !string.IsNullOrWhiteSpace(linkedNodeId) &&
                workflowObject.TryGetPropertyValue(linkedNodeId, out JsonNode? linkedNode) &&
                linkedNode is JsonObject linkedObject)
            {
                return TryApplyPromptToStringA(linkedObject, promptText) ||
                    TryApplyPromptToTextInput(linkedObject, promptText);
            }

            return TryApplyPromptToTextInput(clipNode, promptText);
        }

        private static bool TryApplyPromptToStringA(JsonObject nodeObject, string promptText)
        {
            if (!TryGetInputsObject(nodeObject, out var inputs) ||
                !inputs.TryGetPropertyValue("string_a", out JsonNode? stringANode) ||
                !IsEmptyJsonString(stringANode))
            {
                return false;
            }

            inputs["string_a"] = JsonValue.Create(promptText);
            return true;
        }

        private static bool TryApplyPromptToTextInput(JsonObject nodeObject, string promptText)
        {
            if (!TryGetInputsObject(nodeObject, out var inputs) ||
                !inputs.TryGetPropertyValue("text", out JsonNode? textNode) ||
                !IsEmptyJsonString(textNode))
            {
                return false;
            }

            inputs["text"] = JsonValue.Create(promptText);
            return true;
        }

        private static bool TryGetInputsObject(JsonObject nodeObject, out JsonObject inputs)
        {
            inputs = null!;
            if (!nodeObject.TryGetPropertyValue("inputs", out JsonNode? inputsNode) ||
                inputsNode is not JsonObject inputsObject)
            {
                return false;
            }

            inputs = inputsObject;
            return true;
        }

        private static bool IsPositiveClipTextEncodeNode(JsonObject nodeObject)
        {
            string classType = GetJsonObjectString(nodeObject, "class_type");
            if (!classType.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string title = GetNodeTitle(nodeObject);
            if (title.Contains("negative", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return title.Contains("positive", StringComparison.OrdinalIgnoreCase) ||
                TryGetInputsObject(nodeObject, out var inputs) &&
                inputs.ContainsKey("text");
        }

        private static bool IsNegativePromptNode(JsonObject nodeObject)
        {
            string title = GetNodeTitle(nodeObject);
            string classType = GetJsonObjectString(nodeObject, "class_type");
            return title.Contains("negative", StringComparison.OrdinalIgnoreCase) ||
                classType.Contains("negative", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetNodeTitle(JsonObject nodeObject)
        {
            if (nodeObject.TryGetPropertyValue("_meta", out JsonNode? metaNode) &&
                metaNode is JsonObject metaObject)
            {
                return GetJsonObjectString(metaObject, "title");
            }

            return string.Empty;
        }

        private static string GetJsonObjectString(JsonObject obj, string propertyName)
        {
            return obj.TryGetPropertyValue(propertyName, out JsonNode? value) &&
                TryGetJsonString(value, out string text)
                    ? text ?? string.Empty
                    : string.Empty;
        }

        private static bool IsEmptyJsonString(JsonNode? node)
        {
            return TryGetJsonString(node, out string value) &&
                string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetJsonString(JsonNode? node, out string value)
        {
            value = string.Empty;
            if (node is not JsonValue jsonValue ||
                !jsonValue.TryGetValue<string>(out string? text))
            {
                return false;
            }

            value = text ?? string.Empty;
            return true;
        }

        private static int ReplaceComfyPlaceholders(JsonNode? node, JsonObject parameters)
        {
            if (node is JsonObject obj)
            {
                int count = 0;
                foreach (var property in obj.ToList())
                {
                    count += ReplaceChildPlaceholder(obj, property.Key, property.Value, parameters);
                }

                return count;
            }

            if (node is JsonArray array)
            {
                int count = 0;
                for (int i = 0; i < array.Count; i++)
                {
                    count += ReplaceArrayPlaceholder(array, i, array[i], parameters);
                }

                return count;
            }

            return 0;
        }

        private static int ReplaceChildPlaceholder(JsonObject parent, string key, JsonNode? child, JsonObject parameters)
        {
            if (TryBuildPlaceholderReplacement(child, parameters, out JsonNode? replacement))
            {
                parent[key] = replacement;
                return 1;
            }

            return ReplaceComfyPlaceholders(child, parameters);
        }

        private static int ReplaceArrayPlaceholder(JsonArray parent, int index, JsonNode? child, JsonObject parameters)
        {
            if (TryBuildPlaceholderReplacement(child, parameters, out JsonNode? replacement))
            {
                parent[index] = replacement;
                return 1;
            }

            return ReplaceComfyPlaceholders(child, parameters);
        }

        private static bool TryBuildPlaceholderReplacement(JsonNode? node, JsonObject parameters, out JsonNode? replacement)
        {
            replacement = null;
            if (node == null ||
                !TryGetJsonString(node, out string text) ||
                string.IsNullOrEmpty(text))
            {
                return false;
            }

            string replacedText = text;
            bool changed = false;
            foreach (var parameter in parameters)
            {
                string placeholder = "{{" + parameter.Key + "}}";
                if (!text.Contains(placeholder, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(text, placeholder, StringComparison.Ordinal))
                {
                    replacement = parameter.Value?.DeepClone();
                    return true;
                }

                replacedText = replacedText.Replace(
                    placeholder,
                    ConvertParameterToString(parameter.Value),
                    StringComparison.Ordinal);
                changed = true;
            }

            if (!changed)
            {
                return false;
            }

            replacement = JsonValue.Create(replacedText);
            return true;
        }

        private static bool TrySetJsonPath(JsonNode root, string path, JsonNode? value)
        {
            string[] segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            JsonNode? current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                current = GetPathChild(current, segments[i]);
                if (current == null)
                {
                    return false;
                }
            }

            return SetPathChild(current, segments[^1], value?.DeepClone());
        }

        private static JsonNode? GetPathChild(JsonNode? node, string segment)
        {
            if (node is JsonObject obj)
            {
                return obj.TryGetPropertyValue(segment, out JsonNode? value) ? value : null;
            }

            if (node is JsonArray array &&
                int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) &&
                index >= 0 &&
                index < array.Count)
            {
                return array[index];
            }

            return null;
        }

        private static bool SetPathChild(JsonNode? node, string segment, JsonNode? value)
        {
            if (node is JsonObject obj)
            {
                obj[segment] = value;
                return true;
            }

            if (node is JsonArray array &&
                int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) &&
                index >= 0 &&
                index < array.Count)
            {
                array[index] = value;
                return true;
            }

            return false;
        }

        private static string ConvertParameterToString(JsonNode? value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return TryGetJsonString(value, out string text)
                ? text ?? string.Empty
                : value.ToJsonString();
        }

        private async Task<IReadOnlyList<ComfyInputImageRef>> UploadComfyInputImagesAsync(
            string endpoint,
            IReadOnlyList<string> imagePaths,
            CancellationToken cancellationToken)
        {
            if (imagePaths.Count == 0)
            {
                return Array.Empty<ComfyInputImageRef>();
            }

            var uploadedImages = new List<ComfyInputImageRef>();
            foreach (string imagePath in imagePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullPath = ResolveComfyInputImagePath(imagePath);
                uploadedImages.Add(await UploadComfyInputImageAsync(endpoint, fullPath, cancellationToken));
            }

            return uploadedImages;
        }

        private async Task<ComfyInputImageRef> UploadComfyInputImageAsync(
            string endpoint,
            string fullPath,
            CancellationToken cancellationToken)
        {
            string fileName = Path.GetFileName(fullPath);
            using var content = new MultipartFormDataContent();
            await using var fileStream = File.OpenRead(fullPath);
            using var imageContent = new StreamContent(fileStream);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(GetImageMediaType(fullPath));
            content.Add(imageContent, "image", fileName);
            content.Add(new StringContent("input"), "type");
            content.Add(new StringContent("true"), "overwrite");

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildComfyUrl(endpoint, "upload/image"))
            {
                Content = content
            };
            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"ComfyUI input image upload failed: {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            string uploadedName = fileName;
            string uploadedSubfolder = string.Empty;
            string uploadedType = "input";
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using JsonDocument uploadResponse = JsonDocument.Parse(body);
                    uploadedName = TryGetStringProperty(uploadResponse.RootElement, "name");
                    uploadedSubfolder = TryGetStringProperty(uploadResponse.RootElement, "subfolder");
                    uploadedType = TryGetStringProperty(uploadResponse.RootElement, "type");
                }
                catch (JsonException)
                {
                }
            }

            return new ComfyInputImageRef
            {
                FileName = string.IsNullOrWhiteSpace(uploadedName) ? fileName : uploadedName,
                LocalPath = fullPath,
                Subfolder = uploadedSubfolder,
                Type = string.IsNullOrWhiteSpace(uploadedType) ? "input" : uploadedType
            };
        }

        private string ResolveComfyInputImagePath(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                throw new InvalidOperationException("ComfyUI input image path is empty.");
            }

            string path = requestedPath.Trim();
            string fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(ResolveWorkspaceRoot(), path));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("ComfyUI input image file was not found.", requestedPath);
            }

            return fullPath;
        }

        private static IReadOnlyList<string> ExtractComfyInputImagePaths(JsonElement arguments)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            var paths = new List<string>();
            foreach (string name in new[]
            {
                "inputImagePath",
                "input_image_path",
                "imagePath",
                "image_path",
                "initImagePath",
                "init_image_path",
                "sourceImage",
                "source_image",
                "inputImage",
                "input_image"
            })
            {
                AddComfyInputImageArgument(arguments, name, paths);
            }

            foreach (string name in new[]
            {
                "inputImages",
                "input_images",
                "imagePaths",
                "image_paths",
                "images",
                "sourceImages",
                "source_images"
            })
            {
                AddComfyInputImageArgument(arguments, name, paths);
            }

            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddComfyInputImageArgument(JsonElement arguments, string propertyName, List<string> paths)
        {
            if (!arguments.TryGetProperty(propertyName, out JsonElement value))
            {
                return;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                string path = value.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
                return;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                string path = TryGetStringProperty(value, "path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = TryGetStringProperty(value, "file");
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
                return;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string path = item.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    string path = TryGetStringProperty(item, "path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        path = TryGetStringProperty(item, "file");
                    }

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
            }
        }

        private static string GetImageMediaType(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".avif" => "image/avif",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".tif" or ".tiff" => "image/tiff",
                _ => "image/png"
            };
        }

        private async Task<JsonDocument> PostComfyJsonAsync(
            string endpoint,
            string route,
            JsonObject payload,
            CancellationToken cancellationToken)
        {
            string json = payload.ToJsonString();
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildComfyUrl(endpoint, route))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"ComfyUI {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            return JsonDocument.Parse(body);
        }

        private async Task<ComfyImageRef> WaitForComfyImageAsync(
            string endpoint,
            string promptId,
            string outputNodeId,
            TimeSpan timeout,
            int pollIntervalMs,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
            string historyRoute = "history/" + Uri.EscapeDataString(promptId);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildComfyUrl(endpoint, historyRoute));
                using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
                {
                    using JsonDocument history = JsonDocument.Parse(body);
                    if (TryGetComfyImageFromHistory(history.RootElement, promptId, outputNodeId, out var image))
                    {
                        return image;
                    }
                }

                await Task.Delay(pollIntervalMs, cancellationToken);
            }

            throw new TimeoutException($"ComfyUI image generation timed out after {timeout.TotalSeconds:N0} seconds.");
        }

        private static bool TryGetComfyImageFromHistory(
            JsonElement root,
            string promptId,
            string outputNodeId,
            out ComfyImageRef image)
        {
            image = new ComfyImageRef();
            JsonElement entry = root;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(promptId, out JsonElement promptEntry))
            {
                entry = promptEntry;
            }

            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("outputs", out JsonElement outputs) ||
                outputs.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var output in outputs.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(outputNodeId) &&
                    !output.Name.Equals(outputNodeId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryGetComfyImageFromOutput(output.Value, out image))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetComfyImageFromOutput(JsonElement output, out ComfyImageRef image)
        {
            image = new ComfyImageRef();
            if (output.ValueKind != JsonValueKind.Object ||
                !output.TryGetProperty("images", out JsonElement images) ||
                images.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in images.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string fileName = TryGetStringProperty(item, "filename");
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                image = new ComfyImageRef
                {
                    FileName = fileName,
                    Subfolder = TryGetStringProperty(item, "subfolder"),
                    Type = TryGetStringProperty(item, "type")
                };
                return true;
            }

            return false;
        }

        private async Task<byte[]> DownloadComfyImageAsync(string endpoint, ComfyImageRef image, CancellationToken cancellationToken)
        {
            string query =
                "filename=" + Uri.EscapeDataString(image.FileName) +
                "&type=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(image.Type) ? "output" : image.Type) +
                "&subfolder=" + Uri.EscapeDataString(image.Subfolder ?? string.Empty);
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildComfyUrl(endpoint, "view?" + query));
            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string body = Encoding.UTF8.GetString(bytes);
                throw new InvalidOperationException($"ComfyUI image download failed: {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            return bytes;
        }

        private string ResolveComfyOutputPath(string requestedPath, ComfyImageRef image)
        {
            string path = requestedPath.Trim();
            if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            {
                path = Path.Combine(path, Path.GetFileName(image.FileName));
            }

            string extension = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(extension))
            {
                string sourceExtension = Path.GetExtension(image.FileName);
                path += string.IsNullOrWhiteSpace(sourceExtension) ? ".png" : sourceExtension;
            }

            string root = ResolveWorkspaceRoot();
            string fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));

            if (!IsAllowedComfyOutputPath(root, fullPath))
            {
                throw new InvalidOperationException("ComfyUI output path must stay inside the workspace, C:\\tmp, or the system temp directory.");
            }

            return fullPath;
        }

        private string ResolveWorkspaceRoot()
        {
            string root = _workspaceRootProvider();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsAllowedComfyOutputPath(string workspaceRoot, string fullPath)
        {
            if (AgentWorkspaceFileResolver.IsInsideRoot(workspaceRoot, fullPath))
            {
                return true;
            }

            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (AgentWorkspaceFileResolver.IsInsideRoot(tempRoot, fullPath))
            {
                return true;
            }

            string cTmpRoot = Path.GetFullPath(@"C:\tmp").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return AgentWorkspaceFileResolver.IsInsideRoot(cTmpRoot, fullPath);
        }

        private static string NormalizeComfyEndpoint(string endpoint)
        {
            string normalized = string.IsNullOrWhiteSpace(endpoint)
                ? DefaultComfyUiEndpoint
                : endpoint.Trim();
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("ComfyUI endpoint must be an http or https URL.");
            }

            return normalized.TrimEnd('/');
        }

        private static Uri BuildComfyUrl(string endpoint, string route)
        {
            return new Uri(NormalizeComfyEndpoint(endpoint) + "/" + route.TrimStart('/'));
        }

        private static string TryGetStringProperty(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property))
            {
                return string.Empty;
            }

            return property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : property.GetRawText();
        }

        private sealed class ComfyImageRef
        {
            public string FileName { get; set; } = string.Empty;
            public string Subfolder { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
        }

        private sealed class ComfyInputImageRef
        {
            public string FileName { get; set; } = string.Empty;
            public string LocalPath { get; set; } = string.Empty;
            public string Subfolder { get; set; } = string.Empty;
            public string Type { get; set; } = "input";
        }

        private sealed class ComfyWorkflowFile
        {
            public string RelativePath { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public DateTime ModifiedAt { get; set; }
        }
    }
}
