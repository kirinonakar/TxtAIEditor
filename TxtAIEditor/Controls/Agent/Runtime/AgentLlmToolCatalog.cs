using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentLlmToolCatalog
    {
        public IReadOnlyList<LlmTool> Build(
            bool planningMode,
            IReadOnlyList<AgentMcpToolAlias> mcpAliases,
            bool hasEnabledSkills = false)
        {
            var tools = new List<LlmTool>
            {
                new LlmTool
                {
                    Name = "list_files",
                    Description = "List workspace files and folders.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            glob = new { type = "string", description = "Leading / anchors at the root. /* lists top-level entries, **/*.cs matches recursively." },
                            maxResults = new { type = "integer", description = "Default 80." }
                        }
                    }
                },
                new LlmTool
                {
                    Name = "search_text",
                    Description = "Search workspace paths and file contents.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "Case-insensitive regex. Invalid patterns fall back to literal text." },
                            glob = new { type = "string", description = "Workspace file filter, e.g. **/*.cs." },
                            maxResults = new { type = "integer", description = "Default 80." }
                        },
                        required = new[] { "query" }
                    }
                },
                new LlmTool
                {
                    Name = "run_rg",
                    Description = "Search with ripgrep without a shell. Executable and config options are blocked.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            arguments = new { type = "string", description = "Quote regexes and globs. Supports head/tail [N or -n N], sort [-t CHAR] [-k N] [-n] [-r], wc -l, and Select-Object -First/-Last N. Rank rg -c counts with | sort -t: -k2 -nr." },
                            timeoutMs = new
                            {
                                type = "integer",
                                minimum = AgentProcessToolService.MinimumProcessTimeoutMs,
                                maximum = AgentProcessToolService.MaximumProcessTimeoutMs,
                                description = "Default 10000."
                            }
                        },
                        required = new[] { "arguments" }
                    }
                },
                new LlmTool
                {
                    Name = "run_rga",
                    Description = "Search documents, text, and source with ripgrep-all. Uses run_rg filters without a shell. Blocks --rga-* and executable options.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            arguments = new { type = "string", description = "Quote regexes and globs. Use -g to filter files. Long lines default to 300-byte previews. Override with -M and --[no-]max-columns-preview." },
                            includeNotebooks = new { type = "boolean", description = "Search raw .ipynb JSON, including saved outputs. Default false." },
                            includeArchives = new { type = "boolean", description = "Search archives such as ZIP. Default false." },
                            timeoutMs = new
                            {
                                type = "integer",
                                minimum = AgentProcessToolService.MinimumProcessTimeoutMs,
                                maximum = AgentProcessToolService.MaximumProcessTimeoutMs,
                                description = "Default 10000."
                            }
                        },
                        required = new[] { "arguments" }
                    }
                },
                new LlmTool
                {
                    Name = "run_powershell",
                    Description = "Run a PowerShell script from the workspace root.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            command = new { type = "string", description = "Script body with full cmdlets such as Get-ChildItem. Omit the powershell.exe -Command wrapper." },
                            timeoutMs = new
                            {
                                type = "integer",
                                minimum = AgentProcessToolService.MinimumProcessTimeoutMs,
                                maximum = AgentProcessToolService.MaximumProcessTimeoutMs,
                                @default = AgentProcessToolService.DefaultPowerShellTimeoutMs
                            }
                        },
                        required = new[] { "command" }
                    }
                },
                new LlmTool
                {
                    Name = "read_file",
                    Description = "Read file lines. Truncates lines at 1000 characters.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative path." },
                            startLine = new { type = "integer", description = "1-based. Default 1." },
                            lineCount = new { type = "integer", description = "Default 160." }
                        },
                        required = new[] { "path" }
                    }
                },
                new LlmTool
                {
                    Name = "read_image",
                    Description = "Attach an image up to 25 MiB. By default, downscale proportionally if either side exceeds 2048 px.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative path." },
                            original = new { type = "boolean", description = "Keep original bytes. Default false." }
                        },
                        required = new[] { "path" }
                    }
                },
                new LlmTool
                {
                    Name = "extract_document",
                    Description = "Extract PDF, Word, PowerPoint, Excel, or HWPX content to TXT or CSV.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string" },
                            outputPath = new { type = "string", description = "Workspace destination. Omit for automatic naming." },
                            maxChars = new { type = "integer", description = "Default 5000000." }
                        },
                        required = new[] { "path" }
                    }
                },
                new LlmTool
                {
                    Name = "create_file",
                    Description = "Create a new file.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative path." },
                            content = new { type = "string" },
                            openAfterCreate = new { type = "boolean" }
                        },
                        required = new[] { "path", "content" }
                    }
                },
                new LlmTool
                {
                    Name = "overwrite_file",
                    Description = "Replace the entire contents of an existing file.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative path." },
                            content = new { type = "string" }
                        },
                        required = new[] { "path", "content" }
                    }
                },
                new LlmTool
                {
                    Name = "append_to_file",
                    Description = "Append text to a file.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative path." },
                            content = new { type = "string" }
                        },
                        required = new[] { "path", "content" }
                    }
                },
                new LlmTool
                {
                    Name = "merge_files",
                    Description = "Merge files in input order.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            targetPath = new { type = "string", description = "Workspace-relative destination." },
                            paths = new { type = "array", items = new { type = "string" }, description = "Workspace-relative inputs." }
                        },
                        required = new[] { "targetPath", "paths" }
                    }
                },
                new LlmTool
                {
                    Name = "split_file",
                    Description = "Split a file using linesPerFile or explicit ranges.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative input." },
                            linesPerFile = new { type = "integer" },
                            ranges = new
                            {
                                type = "array",
                                description = "Use endLine or lineCount per range. Omit both to read through EOF.",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        path = new { type = "string", description = "Workspace-relative output." },
                                        startLine = new { type = "integer", description = "1-based." },
                                        endLine = new { type = "integer", description = "Inclusive." },
                                        lineCount = new { type = "integer" }
                                    },
                                    required = new[] { "path", "startLine" }
                                }
                            }
                        },
                        required = new[] { "path" }
                    }
                },
                new LlmTool
                {
                    Name = "search_replace",
                    Description = "Replace literal or regex matches in a file.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative path." },
                            search = new { type = "string" },
                            replacement = new { type = "string" },
                            useRegex = new { type = "boolean", @default = false },
                            matchCase = new { type = "boolean", @default = true },
                            wholeWord = new { type = "boolean", @default = false },
                            maxReplacements = new { type = "integer", minimum = 0, @default = 0, description = "0 replaces all matches." },
                            startLine = new { type = "integer", minimum = 0, description = "1-based. 0 leaves the start unset." },
                            endLine = new { type = "integer", minimum = 0, description = "Inclusive. 0 leaves the end unset." }
                        },
                        required = new[] { "path", "search", "replacement" }
                    }
                },
                new LlmTool
                {
                    Name = "replace_range",
                    Description = "Replace an inclusive line range using original boundary text.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative path." },
                            startLine = new { type = "integer", description = "1-based." },
                            endLine = new { type = "integer" },
                            newText = new { type = "string" },
                            expectedStartLine = new { type = "string", description = "One or more original lines at the start, joined with \\n." },
                            expectedEndLine = new { type = "string", description = "One or more original lines at the end, joined with \\n." }
                        },
                        required = new[] { "path", "startLine", "endLine", "newText", "expectedStartLine", "expectedEndLine" }
                    }
                },
                new LlmTool
                {
                    Name = "apply_patch",
                    Description = "Apply a unified diff to existing files.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative path for one file. Omit for multi-file patches." },
                            patch = new { type = "string", description = "Each hunk needs @@ -oldStart,oldCount +newStart,newCount @@. For multiple files, wrap *** Update File: relative/path sections between *** Begin Patch and *** End Patch. End at the closing marker." }
                        },
                        required = new[] { "patch" }
                    }
                },
                new LlmTool
                {
                    Name = "insert_to_file",
                    Description = "Insert text between two anchors.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative path." },
                            content = new { type = "string" },
                            insert_after = new { type = "string", description = "2+ unique lines before the insertion." },
                            insert_before = new { type = "string", description = "2+ unique lines after the insertion." }
                        },
                        required = new[] { "path", "content", "insert_after", "insert_before" }
                    }
                },
                new LlmTool
                {
                    Name = "insert_text",
                    Description = "Insert text at the active tab's cursor.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            content = new { type = "string" }
                        },
                        required = new[] { "content" }
                    }
                },
                new LlmTool
                {
                    Name = "create_tab",
                    Description = "Create an unsaved editor tab.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string" },
                            content = new { type = "string" }
                        },
                        required = new[] { "title", "content" }
                    }
                },
                new LlmTool
                {
                    Name = "edit_tab",
                    Description = "Replace an editor tab's contents.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string", description = "Tab title or ID" },
                            content = new { type = "string" }
                        },
                        required = new[] { "title", "content" }
                    }
                },
                new LlmTool
                {
                    Name = "save_tab",
                    Description = "Save an editor tab to disk.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string", description = "Tab title or ID. Defaults to the active tab." },
                            path = new { type = "string", description = "Workspace destination. Required for unsaved tabs." }
                        }
                    }
                },
                new LlmTool
                {
                    Name = "open_file",
                    Description = "Open a file in the editor.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Workspace-relative path." }
                        },
                        required = new[] { "path" }
                    }
                },
                new LlmTool
                {
                    Name = "web_search",
                    Description = "Search the web.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string" },
                            numResults = new { type = "integer", description = "Default 5." }
                        },
                        required = new[] { "query" }
                    }
                },
                new LlmTool
                {
                    Name = "web_fetch",
                    Description = "Fetch web pages by URL.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            urls = new { type = "array", items = new { type = "string" } }
                        },
                        required = new[] { "urls" }
                    }
                }
            };

            tools.Add(new LlmTool
            {
                Name = "skill_use",
                Description = "Read an enabled skill's SKILL.md and return its resource directory.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Skill name or SKILL.md path." }
                    },
                    required = new[] { "name" }
                }
            });

            if (planningMode)
            {
                tools.Add(new LlmTool
                {
                    Name = "make_plan",
                    Description = "Submit a plan for approval.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            markdown = new { type = "string", description = "Plan body. Required for new plans. With path, replaces the saved plan body." },
                            path = new { type = "string", description = "Existing plan file to resubmit." }
                        },
                        required = new string[0]
                    }
                });
            }

            foreach (var alias in mcpAliases.OrderBy(item => item.Alias, System.StringComparer.Ordinal))
            {
                object parametersObj = new { type = "object", properties = new { } };
                try
                {
                    if (!string.IsNullOrWhiteSpace(alias.InputSchemaJson))
                    {
                        var parsed = JsonSerializer.Deserialize<object>(alias.InputSchemaJson);
                        if (parsed != null)
                        {
                            parametersObj = parsed;
                        }
                    }
                }
                catch
                {
                }

                tools.Add(new LlmTool
                {
                    Name = alias.Alias,
                    Description = string.IsNullOrEmpty(alias.Description)
                        ? $"MCP tool '{alias.ToolName}' from server '{alias.ServerName}'."
                        : alias.Description,
                    Parameters = parametersObj
                });
            }

            return tools;
        }
    }
}
