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
                    Description = "List files and folders in the workspace matching a glob pattern.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            glob = new { type = "string", description = "Glob pattern to match files and folders, e.g. **/* or **/*.cs" },
                            maxResults = new { type = "integer", description = "Maximum number of files or folders to return (default: 80)" }
                        }
                    }
                },
                new LlmTool
                {
                    Name = "search_text",
                    Description = "Search for text within files in the workspace matching a glob pattern.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "The text query to search for" },
                            glob = new { type = "string", description = "Glob pattern to filter files, e.g. **/*" },
                            maxResults = new { type = "integer", description = "Maximum number of search results to return (default: 80)" }
                        },
                        required = new[] { "query" }
                    }
                },
                new LlmTool
                {
                    Name = "run_rg",
                    Description = "Run ripgrep search with raw arguments.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            arguments = new { type = "string", description = "Ripgrep arguments, e.g. -n \"pattern\" FolderName" },
                            timeoutMs = new { type = "integer", description = "Execution timeout in milliseconds (default: 10000)" }
                        },
                        required = new[] { "arguments" }
                    }
                },
                new LlmTool
                {
                    Name = "run_rga",
                    Description = "Run ripgrep-all search for text inside PDFs/documents with raw arguments.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            arguments = new { type = "string", description = "Ripgrep-all arguments" },
                            timeoutMs = new { type = "integer", description = "Timeout in milliseconds" }
                        },
                        required = new[] { "arguments" }
                    }
                },
                new LlmTool
                {
                    Name = "run_powershell",
                    Description = "Run a PowerShell command on the system.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            command = new { type = "string", description = "The PowerShell command to run, e.g. git status --short" },
                            timeoutMs = new { type = "integer", description = "Timeout in milliseconds" }
                        },
                        required = new[] { "command" }
                    }
                },
                new LlmTool
                {
                    Name = "read_file",
                    Description = "Read a specific line range from a file.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Relative path to the file in the workspace" },
                            startLine = new { type = "integer", description = "Start line number, 1-indexed (default: 1)" },
                            lineCount = new { type = "integer", description = "Number of lines to read (default: 160)" }
                        },
                        required = new[] { "path" }
                    }
                },
                new LlmTool
                {
                    Name = "read_image",
                    Description = "Read an image file.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Relative path to the image" }
                        },
                        required = new[] { "path" }
                    }
                },
                new LlmTool
                {
                    Name = "extract_document",
                    Description = "Extract text from documents (PDF, DOCX, HWPX, etc.).",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Path to the document file" },
                            outputPath = new { type = "string", description = "Optional output text file path" },
                            maxChars = new { type = "integer", description = "Maximum number of characters to extract" }
                        },
                        required = new[] { "path" }
                    }
                },
                new LlmTool
                {
                    Name = "create_file",
                    Description = "Create a new file with specified content.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Relative path to the new file" },
                            content = new { type = "string", description = "Content of the file" },
                            openAfterCreate = new { type = "boolean", description = "Whether to open the file in the editor after creation" }
                        },
                        required = new[] { "path", "content" }
                    }
                },
                new LlmTool
                {
                    Name = "overwrite_file",
                    Description = "Overwrite an existing file completely with new content.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Relative path to the file" },
                            content = new { type = "string", description = "New content of the file" }
                        },
                        required = new[] { "path", "content" }
                    }
                },
                new LlmTool
                {
                    Name = "append_to_file",
                    Description = "Append content to the end of a file.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Relative path to the file" },
                            content = new { type = "string", description = "Content to append" }
                        },
                        required = new[] { "path", "content" }
                    }
                },
                new LlmTool
                {
                    Name = "merge_files",
                    Description = "Merge multiple source files into one target file.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            targetPath = new { type = "string", description = "Relative output path for the merged file" },
                            paths = new { type = "array", items = new { type = "string" }, description = "Relative source file paths to merge in order" }
                        },
                        required = new[] { "targetPath", "paths" }
                    }
                },
                new LlmTool
                {
                    Name = "split_file",
                    Description = "Split a file into generated chunks or explicit line ranges.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Relative path to the source file" },
                            linesPerFile = new { type = "integer", description = "Number of lines per generated split file" },
                            ranges = new
                            {
                                type = "array",
                                description = "Explicit target ranges to write",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        path = new { type = "string", description = "Relative output path for this split range" },
                                        startLine = new { type = "integer", description = "Start line number, 1-indexed" },
                                        endLine = new { type = "integer", description = "End line number, inclusive" },
                                        lineCount = new { type = "integer", description = "Optional number of lines to include from startLine" }
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
                    Name = "replace_range",
                    Description = "Replace a range of lines in a file.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Relative path to the file" },
                            startLine = new { type = "integer", description = "Start line number to replace" },
                            endLine = new { type = "integer", description = "End line number to replace" },
                            newText = new { type = "string", description = "New text to insert" },
                            expectedSnippet = new { type = "string", description = "The exact full text expected at the range to verify correctness. Required for ranges < 5 lines; for ranges >= 5 lines, provide either this full-range snippet or expectedStartLines plus expectedEndLines." },
                            expectedStartLines = new { anyOf = new object[] { new { type = "array", items = new { type = "string" } }, new { type = "string" } }, description = "For ranges >= 5 lines, the exact content of the first 2 or more lines inside the range. Pass as a string array or as a newline-separated string when expectedSnippet is not provided." },
                            expectedEndLines = new { anyOf = new object[] { new { type = "array", items = new { type = "string" } }, new { type = "string" } }, description = "For ranges >= 5 lines, the exact content of the last 2 or more lines inside the range. Pass as a string array or as a newline-separated string when expectedSnippet is not provided." }
                        },
                        required = new[] { "path", "startLine", "endLine", "newText" }
                    }
                },
                new LlmTool
                {
                    Name = "apply_patch",
                    Description = "Apply a unified diff patch to a file.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Relative path to the file" },
                            patch = new { type = "string", description = "Unified diff patch content" }
                        },
                        required = new[] { "path", "patch" }
                    }
                },
                new LlmTool
                {
                    Name = "insert_to_file",
                    Description = "Insert text relative to unique context lines.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Relative path to the file" },
                            content = new { type = "string", description = "Content to insert" },
                            insert_after = new { type = "string", description = "Unique context lines to insert after" },
                            insert_before = new { type = "string", description = "Unique context lines to insert before" }
                        },
                        required = new[] { "path", "content" }
                    }
                },
                new LlmTool
                {
                    Name = "insert_text",
                    Description = "Insert text at the current cursor position in the active editor tab.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            content = new { type = "string", description = "The text to insert" }
                        },
                        required = new[] { "content" }
                    }
                },
                new LlmTool
                {
                    Name = "create_tab",
                    Description = "Create a new editor tab with content.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string", description = "Title of the tab" },
                            content = new { type = "string", description = "Content of the tab" }
                        },
                        required = new[] { "title", "content" }
                    }
                },
                new LlmTool
                {
                    Name = "edit_tab",
                    Description = "Modify the content of an editor tab.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string", description = "Tab title or ID" },
                            content = new { type = "string", description = "New content of the tab" }
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
                            title = new { type = "string", description = "Optional tab title/ID" },
                            path = new { type = "string", description = "Optional workspace path to save to" }
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
                            path = new { type = "string", description = "Relative path to the file" }
                        },
                        required = new[] { "path" }
                    }
                },
                new LlmTool
                {
                    Name = "web_search_exa",
                    Description = "Search the web using Exa/DuckDuckGo.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "Search query" },
                            numResults = new { type = "integer", description = "Number of results to return (default: 5)" }
                        },
                        required = new[] { "query" }
                    }
                },
                new LlmTool
                {
                    Name = "web_fetch",
                    Description = "Fetch the content of web pages.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            urls = new { type = "array", items = new { type = "string" }, description = "List of URLs to fetch" }
                        },
                        required = new[] { "urls" }
                    }
                }
            };

            tools.Add(new LlmTool
            {
                Name = "skill_use",
                Description = "Read the full SKILL.md for an enabled skill by name or path. The result includes the SKILL.md path and Skill directory; relative scripts/assets in the skill are rooted there.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Name of the skill, e.g., skill-name" }
                    },
                    required = new[] { "name" }
                }
            });

            if (planningMode)
            {
                tools.Add(new LlmTool
                {
                    Name = "make_plan",
                    Description = "Save the implementation plan (Markdown). Use only in planning mode.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            markdown = new { type = "string", description = "Markdown plan content" }
                        },
                        required = new[] { "markdown" }
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
