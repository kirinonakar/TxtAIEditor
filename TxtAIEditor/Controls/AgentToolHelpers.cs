using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Controls
{
    internal static class AgentToolHelpers
    {
        public static bool IsUnchangedEditCompletionResult(string toolResult)
        {
            return !string.IsNullOrWhiteSpace(toolResult) &&
                toolResult.Contains(" unchanged:", StringComparison.OrdinalIgnoreCase) &&
                !toolResult.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
                !toolResult.Contains("cancelled", StringComparison.OrdinalIgnoreCase);
        }

        public static string TruncateForActivity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "(empty)";
            }

            value = value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
            return value.Length > 120 ? value.Substring(0, 120) + "..." : value;
        }

        public static string TruncateForConfirmation(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "(empty)";
            }

            value = value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
            const int maxConfirmationChars = 40;
            return value.Length > maxConfirmationChars ? value.Substring(0, maxConfirmationChars) + "..." : value;
        }

        public static string GetStringArgument(JsonElement arguments, string name)
        {
            return arguments.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
        }

        public static string[] GetUrlsArgument(JsonElement arguments)
        {
            if (arguments.TryGetProperty("urls", out var urlsProp))
            {
                if (urlsProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in urlsProp.EnumerateArray())
                    {
                        string val = item.GetString() ?? "";
                        if (!string.IsNullOrEmpty(val))
                        {
                            list.Add(val);
                        }
                    }
                    if (list.Count > 0)
                    {
                        return list.ToArray();
                    }
                }
                else if (urlsProp.ValueKind == JsonValueKind.String)
                {
                    string singleUrl = urlsProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(singleUrl))
                    {
                        return new[] { singleUrl };
                    }
                }
            }

            string fallbackUrl = GetFirstStringArgument(arguments, "url", "uri", "address");
            if (!string.IsNullOrEmpty(fallbackUrl))
            {
                return new[] { fallbackUrl };
            }

            return Array.Empty<string>();
        }

        public static bool ShouldSkipDuplicateSuccessfulTool(string normalizedToolName)
        {
            return normalizedToolName is "read_file"
                or "run_powershell"
                or "run_rg"
                or "run_rga"
                or "run_pdftotext"
                or "search_text"
                or "create_file"
                or "overwrite_file"
                or "replace_in_file"
                or "replace_range"
                or "append_to_file"
                or "merge_files"
                or "split_file"
                or "apply_patch"
                or "insert_text"
                or "web_search_exa"
                or "web_fetch"
                or "web_fetch_exa";
        }

        public static bool IsMutatingTool(string normalizedToolName)
        {
            return normalizedToolName is "create_file"
                or "overwrite_file"
                or "replace_in_file"
                or "replace_range"
                or "apply_patch"
                or "insert_text"
                or "create_tab"
                or "edit_tab"
                or "save_tab"
                or "append_to_file"
                or "merge_files"
                or "split_file";
        }

        public static void ClearCachedToolResults(Dictionary<string, string> toolResults, string normalizedToolName)
        {
            string prefix = normalizedToolName + ":";
            foreach (string key in toolResults.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                toolResults.Remove(key);
            }
        }

        public static bool IsFileEditingTool(string normalizedToolName)
        {
            return normalizedToolName is "overwrite_file"
                or "replace_in_file"
                or "replace_range"
                or "apply_patch"
                or "append_to_file"
                or "merge_files"
                or "split_file";
        }

        public static bool UserRequestAllowsEditsOutsideSelection(string instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction))
            {
                return false;
            }

            return Regex.IsMatch(
                instruction,
                @"\b(whole|entire|full)\s+(file|document|workspace|project|repository)\b|\b(all|every)\s+(file|document)\b|전체\s*(파일|문서|워크스페이스|작업공간|프로젝트|저장소)|(?:파일|문서|프로젝트|저장소)\s*전체|すべての(?:ファイル|文書)|(?:ファイル|文書|ワークスペース|プロジェクト|リポジトリ)全体",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static bool IsSuccessfulToolResult(string result)
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                return false;
            }

            if (result.StartsWith("Tool failed:", StringComparison.OrdinalIgnoreCase) ||
                result.Contains(" failed:", StringComparison.OrdinalIgnoreCase) ||
                result.Contains(" cancelled", StringComparison.OrdinalIgnoreCase) ||
                result.Contains(" timed out", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var exitCodeMatch = Regex.Match(result, @"\[exit_code\]\s+(-?\d+)", RegexOptions.IgnoreCase);
            return !exitCodeMatch.Success ||
                (int.TryParse(exitCodeMatch.Groups[1].Value, out int exitCode) && exitCode == 0);
        }

        public static string AppendToolStatusMessage(string result, string message)
        {
            string trimmedResult = (result ?? string.Empty).TrimEnd();
            if (string.IsNullOrWhiteSpace(message) ||
                trimmedResult.EndsWith(message, StringComparison.Ordinal))
            {
                return trimmedResult;
            }

            return $"{trimmedResult}\n{message}";
        }

        public static string BuildToolInvocationKey(string normalizedToolName, JsonElement arguments)
        {
            if (normalizedToolName == "run_powershell")
            {
                return normalizedToolName + ":" + NormalizePowerShellForDuplicateCheck(GetStringArgument(arguments, "command"));
            }

            var builder = new StringBuilder(normalizedToolName);
            builder.Append(':');
            AppendCanonicalJson(builder, arguments);
            return builder.ToString();
        }

        public static string GetFirstStringArgument(JsonElement arguments, params string[] names)
        {
            foreach (string name in names)
            {
                string value = GetStringArgument(arguments, name);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        public static string NormalizeToolName(string toolName)
        {
            string normalized = (toolName ?? string.Empty)
                .Trim()
                .Replace("-", "_", StringComparison.Ordinal)
                .Replace(" ", "_", StringComparison.Ordinal)
                .ToLowerInvariant();

            return normalized switch
            {
                "replace_text" => "replace_in_file",
                "replace" => "replace_in_file",
                "edit_file" => "replace_in_file",
                "write_file" => "overwrite_file",
                "append" => "append_to_file",
                "append_file" => "append_to_file",
                "append_to_file" => "append_to_file",
                "merge" => "merge_files",
                "merge_file" => "merge_files",
                "merge_files" => "merge_files",
                "split" => "split_file",
                "split_file" => "split_file",
                "split_files" => "split_file",
                "write_text" => "overwrite_file",
                "insert" => "insert_text",
                "insert_into_editor" => "insert_text",
                "insert_text_into_editor" => "insert_text",
                "paste_text" => "insert_text",
                "new_tab" => "create_tab",
                "open_new_tab" => "create_tab",
                "create_new_tab" => "create_tab",
                "insert_text_new_tab" => "create_tab",
                "insert_into_new_tab" => "create_tab",
                "paste_text_new_tab" => "create_tab",
                "edit_tab" => "edit_tab",
                "modify_tab" => "edit_tab",
                "update_tab" => "edit_tab",
                "overwrite_tab" => "edit_tab",
                "save" => "save_tab",
                "save_file" => "save_tab",
                "save_tab" => "save_tab",
                "read" => "read_file",
                "search" => "search_text",
                "powershell" => "run_powershell",
                "rg" => "run_rg",
                "rga" => "run_rga",
                "pdftotext" => "run_pdftotext",
                "search_exa" => "web_search_exa",
                "exa_search" => "web_search_exa",
                "exa" => "web_search_exa",
                "fetch_exa" => "web_fetch_exa",
                "exa_fetch" => "web_fetch_exa",
                "fetch" => "web_fetch",
                "web_fetch" => "web_fetch",
                "patch" => "apply_patch",
                "apply_diff" => "apply_patch",
                "open" => "open_file",
                "open_in_editor" => "open_file",
                "open_tab" => "open_file",
                _ => normalized
            };
        }

        public static int GetIntArgument(JsonElement arguments, string name, int fallback)
        {
            return TryGetIntArgument(arguments, name, out int value) ? value : fallback;
        }

        public static bool TryGetIntArgument(JsonElement arguments, string name, out int value)
        {
            value = 0;
            if (!arguments.TryGetProperty(name, out var prop))
            {
                return false;
            }

            if (prop.ValueKind == JsonValueKind.Number)
            {
                return prop.TryGetInt32(out value);
            }

            if (prop.ValueKind == JsonValueKind.String)
            {
                return int.TryParse(prop.GetString(), out value);
            }

            return false;
        }

        private static string NormalizePowerShellForDuplicateCheck(string command)
        {
            string normalized = Regex.Replace(command ?? string.Empty, @"\s+", " ").Trim();
            normalized = Regex.Replace(normalized, @"\s+2>\s*&\s*1\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
            return normalized.ToLowerInvariant();
        }

        private static void AppendCanonicalJson(StringBuilder builder, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    builder.Append('{');
                    bool firstProperty = true;
                    foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (!firstProperty)
                        {
                            builder.Append(',');
                        }

                        firstProperty = false;
                        builder.Append(JsonSerializer.Serialize(property.Name));
                        builder.Append(':');
                        AppendCanonicalJson(builder, property.Value);
                    }
                    builder.Append('}');
                    break;

                case JsonValueKind.Array:
                    builder.Append('[');
                    bool firstItem = true;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (!firstItem)
                        {
                            builder.Append(',');
                        }

                        firstItem = false;
                        AppendCanonicalJson(builder, item);
                    }
                    builder.Append(']');
                    break;

                case JsonValueKind.String:
                    builder.Append(JsonSerializer.Serialize(element.GetString() ?? string.Empty));
                    break;

                default:
                    builder.Append(element.GetRawText());
                    break;
            }
        }
    }
}
