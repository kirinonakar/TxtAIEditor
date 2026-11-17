using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed record AgentPluginEntry(string Id, string Type, string Name, string Description, bool Active,
        string? Status = null, int? ToolCount = null);

    internal sealed record AgentPluginInstallResult(
        IReadOnlyList<AgentPluginEntry> Plugins, IReadOnlyList<string> Warnings, bool AlreadyInstalled = false);

    internal sealed class AgentMcpPluginManagerTool
    {
        public const string ServerId = "builtin-plugin-manager";
        public const string ServerName = "Plugin manager";
        private const string AliasPrefix = "mcp_plugin_manager_";
        public const string InstallPluginAlias = AliasPrefix + "install_plugin";
        public const string InstallMcpAlias = AliasPrefix + "install_mcp";
        public const string InstallSkillAlias = AliasPrefix + "install_skill";
        private static readonly JsonSerializerOptions ResultJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        private const string ListSchema = """
            {"type":"object","properties":{"type":{"type":"string","enum":["all","plugin","mcp","skill"],"default":"all"}},"additionalProperties":false}
            """;
        private const string ChangeSchema = """
            {"type":"object","properties":{"name":{"type":"string","description":"Exact name or ID."},"type":{"type":"string","enum":["plugin","mcp","skill"]}},"required":["name"],"additionalProperties":false}
            """;
        private const string SearchSchema = """
            {"type":"object","properties":{"keywords":{"type":"array","items":{"type":"string"},"minItems":1},"regex":{"type":"boolean","default":false},"match":{"type":"string","enum":["any","all"],"default":"any"},"ignoreCase":{"type":"boolean","default":true},"type":{"type":"string","enum":["all","plugin","mcp","skill"],"default":"all"}},"required":["keywords"],"additionalProperties":false}
            """;
        private const string InstallPluginSchema = """
            {"type":"object","properties":{"source":{"type":"string","description":"GitHub repo or plugin folder URL."},"activate":{"type":"boolean"}},"required":["source"],"additionalProperties":false}
            """;
        private const string InstallSkillSchema = """
            {
              "type":"object",
              "properties":{
                "source":{"type":"string","description":"GitHub repo, skill folder, or SKILL.md URL."},
                "name":{"type":"string","description":"Exact folder name to select a skill."},
                "activate":{"type":"boolean"}
              },
              "required":["source"],
              "additionalProperties":false
            }
            """;
        private const string InstallMcpSchema = """
            {
              "type":"object",
              "properties":{
                "name":{"type":"string"},
                "url":{"type":"string","description":"Streamable HTTP endpoint."},
                "command":{"type":"string","description":"Stdio executable."},
                "args":{"type":"array","items":{"type":"string"}},
                "workingDirectory":{"type":"string","description":"Existing absolute path."},
                "env":{"type":"object","additionalProperties":{"type":"string"}},
                "stateless":{"type":"boolean","description":"HTTP only."},
                "description":{"type":"string"},
                "activate":{"type":"boolean"}
              },
              "required":["name"],
              "additionalProperties":false
            }
            """;
        private readonly Func<Task<IReadOnlyList<AgentPluginEntry>>> _getEntriesAsync;
        private readonly Action<AgentPluginEntry, bool> _setActive;
        private readonly Func<string, JsonElement, CancellationToken, Task<AgentPluginInstallResult>> _installAsync;
        private readonly Dictionary<string, (AgentPluginEntry Entry, bool Active, long Order)> _pending = new(StringComparer.OrdinalIgnoreCase);
        private long _changeOrder;

        public AgentMcpPluginManagerTool(
            Func<Task<IReadOnlyList<AgentPluginEntry>>> getEntriesAsync,
            Action<AgentPluginEntry, bool> setActive,
            Func<string, JsonElement, CancellationToken, Task<AgentPluginInstallResult>> installAsync)
        {
            _getEntriesAsync = getEntriesAsync;
            _setActive = setActive;
            _installAsync = installAsync;
        }

        public static bool IsServerName(string name) => ServerName.Equals(name, StringComparison.OrdinalIgnoreCase);

        public static IReadOnlyList<AgentMcpToolAlias> CreateAliases()
        {
            return new[]
            {
                CreateAlias("list_plugin", "List installed items and MCP connection status.", ListSchema),
                CreateAlias("activate_plugin", "Enable an installed item.", ChangeSchema),
                CreateAlias("deactivate_plugin", "Disable an installed item.", ChangeSchema),
                CreateAlias("search_plugin", "Search installed names, IDs, and descriptions.", SearchSchema),
                CreateAlias("install_plugin", "Install a public GitHub Agent Plugin.", InstallPluginSchema),
                CreateAlias("install_skill", "Install a public GitHub skill with its resources.", InstallSkillSchema),
                CreateAlias("install_mcp", "Register an HTTP or stdio MCP.", InstallMcpSchema)
            };
        }

        private static AgentMcpToolAlias CreateAlias(string name, string description, string schema) => new()
        {
            Alias = AliasPrefix + name,
            ServerId = ServerId,
            ServerName = ServerName,
            ToolName = name,
            Description = description,
            InputSchemaJson = schema,
            IsBuiltIn = true
        };

        public static void AppendPromptContext(StringBuilder builder, bool includeTextTools, bool planningMode)
        {
            builder.AppendLine($"## {ServerName}");
            builder.AppendLine("Activation changes apply next model turn.");
            if (!planningMode)
            {
                builder.AppendLine("""
                    For requested installs, check installed items and read official setup via web_search/web_fetch. Reuse an existing match. Pass activate=true, then check MCP connection and cite the source.
                    MCP: supply url or the verified local command with separate args. Check PATH and documented Windows paths with Get-Command/Test-Path. Resolve placeholders, limit file grants to the task, and keep secrets out of calls.
                    GitHub: select the folder containing plugin.json or SKILL.md; a SKILL.md file URL also works for skills.
                    """);
            }

            if (includeTextTools)
            {
                builder.AppendLine($"Text tool arguments (? = optional; prefix all names with {AliasPrefix}):");
                builder.AppendLine("""
                    list_plugin(type?)
                    search_plugin(keywords:string[], type?, match?:any|all, regex?:bool, ignoreCase?:bool)
                    activate_plugin(name, type?) / deactivate_plugin(name, type?)
                    Use exact name or ID for activation. type=plugin|mcp|skill (list/search default all). Search defaults: any, literal, case-insensitive.
                    """);
                if (!planningMode)
                {
                    builder.AppendLine("""
                        install_plugin(source, activate?:bool)
                        install_skill(source, name?, activate?:bool)
                        install_mcp(name, url?, command?, args?:string[], workingDirectory?, env?:{string:string}, stateless?:bool, description?, activate?:bool)
                        source is a public GitHub URL. Skill name selects a folder. workingDirectory is an existing absolute path. stateless applies to HTTP.
                        """);
                }
            }

            builder.AppendLine();
        }

        public static AgentMcpItem CreateMenuItem(bool selected, Func<string, string, string> getString) => new()
        {
            Name = ServerName,
            Detail = getString("AgentMcpPluginManagerDetail", "내장 플러그인 · Agent Plugin, MCP, Skill 설치 및 관리"),
            IsSelected = selected,
            IsBuiltIn = true,
            CanEdit = false,
            CanDelete = false,
            CanConfigure = false
        };

        public async Task<string> ExecuteAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (arguments.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException("Arguments must be a JSON object.");
                }

                if (toolName is "install_plugin" or "install_mcp" or "install_skill")
                {
                    string installType = toolName switch
                    {
                        "install_mcp" => "mcp",
                        "install_skill" => "skill",
                        _ => "plugin"
                    };

                    bool activate = GetBoolean(arguments, "activate", false);
                    AgentPluginInstallResult installed = await _installAsync(installType, arguments, cancellationToken);
                    if (activate)
                    {
                        foreach (AgentPluginEntry entry in installed.Plugins)
                        {
                            QueueActiveChange(entry, true);
                        }
                    }

                    return "MCP tool result: " + JsonSerializer.Serialize(new
                    {
                        installed = true,
                        alreadyInstalled = installed.AlreadyInstalled,
                        plugins = installed.Plugins.Select(DescribeEntry),
                        warnings = installed.Warnings,
                        activation = activate ? "next_model_turn" : null
                    }, ResultJson);
                }

                string type = GetString(arguments, "type", "all").ToLowerInvariant();
                if (type is not ("all" or "plugin" or "mcp" or "skill"))
                {
                    throw new ArgumentException("type must be all, plugin, mcp, or skill.");
                }

                IReadOnlyList<AgentPluginEntry> entries = await _getEntriesAsync();
                cancellationToken.ThrowIfCancellationRequested();
                var filtered = entries.Where(entry => type == "all" || entry.Type == type).ToList();
                if (toolName == "search_plugin")
                {
                    filtered = Search(filtered, arguments, cancellationToken);
                }
                else if (toolName is "activate_plugin" or "deactivate_plugin")
                {
                    string name = GetString(arguments, "name").Trim();
                    if (name.Length == 0)
                    {
                        throw new ArgumentException("Provide an exact installed name or id in name.");
                    }

                    var matches = filtered.Where(entry => entry.Id.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0)
                    {
                        matches = filtered.Where(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    }
                    if (matches.Count != 1)
                    {
                        throw new ArgumentException(matches.Count == 0
                            ? $"Installed plugin not found: {name}. Use list_plugin or search_plugin."
                            : $"Ambiguous plugin name: {name}. Specify type or use the exact id.");
                    }

                    AgentPluginEntry target = matches[0];
                    bool active = toolName == "activate_plugin";
                    QueueActiveChange(target, active);
                    return "MCP tool result: " + JsonSerializer.Serialize(new
                    {
                        id = target.Id, type = target.Type, name = target.Name,
                        active = target.Active, pendingActive = active, applies = "next_model_turn"
                    }, ResultJson);
                }
                else if (toolName != "list_plugin")
                {
                    throw new ArgumentException($"Unknown Plugin manager tool: {toolName}.");
                }

                return "MCP tool result: " + JsonSerializer.Serialize(new
                {
                    count = filtered.Count,
                    plugins = filtered.OrderBy(entry => entry.Type).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(DescribeEntry)
                }, ResultJson);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return "MCP tool failed: Plugin manager: The download timed out.";
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException or
                IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or JsonException)
            {
                return $"MCP tool failed: Plugin manager: {ex.Message}";
            }
        }

        internal static bool IsInstallAlias(string alias) => alias is InstallPluginAlias or InstallMcpAlias or InstallSkillAlias;

        private void QueueActiveChange(AgentPluginEntry entry, bool active) =>
            _pending[entry.Id] = (entry, active, ++_changeOrder);

        private object DescribeEntry(AgentPluginEntry entry) => new
        {
            id = entry.Id, type = entry.Type, name = entry.Name, description = entry.Description,
            active = entry.Active, status = entry.Status, toolCount = entry.ToolCount,
            pendingActive = _pending.TryGetValue(entry.Id, out var change) ? (bool?)change.Active : null
        };

        private static List<AgentPluginEntry> Search(List<AgentPluginEntry> entries, JsonElement arguments, CancellationToken cancellationToken)
        {
            if (!arguments.TryGetProperty("keywords", out var keywords) || keywords.ValueKind != JsonValueKind.Array ||
                keywords.GetArrayLength() is 0 or > 32 || keywords.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
            {
                throw new ArgumentException("keywords must contain 1 to 32 non-empty strings.");
            }
            string[] patterns = keywords.EnumerateArray().Select(value => value.GetString()!.Trim()).ToArray();
            if (patterns.Any(pattern => pattern.Length is 0 or > 2000))
            {
                throw new ArgumentException("Each keyword must contain 1 to 2000 characters.");
            }
            string match = GetString(arguments, "match", "any").ToLowerInvariant();
            if (match is not ("any" or "all"))
            {
                throw new ArgumentException("match must be any or all.");
            }
            bool regex = GetBoolean(arguments, "regex", false);
            bool ignoreCase = GetBoolean(arguments, "ignoreCase", true);
            Regex[] expressions = regex
                ? patterns.Select(pattern => new Regex(pattern,
                    RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                    TimeSpan.FromMilliseconds(100))).ToArray()
                : Array.Empty<Regex>();
            var results = new List<AgentPluginEntry>();
            foreach (AgentPluginEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text = $"{entry.Name}\n{entry.Id}\n{entry.Description}";
                bool Matches(int index) => regex ? expressions[index].IsMatch(text) :
                    text.Contains(patterns[index], ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                bool found = match == "all"
                    ? Enumerable.Range(0, patterns.Length).All(Matches)
                    : Enumerable.Range(0, patterns.Length).Any(Matches);
                if (found)
                {
                    results.Add(entry);
                }
            }
            return results;
        }

        // Applying only at the request boundary keeps aliases valid for the entire current tool batch,
        // including a batch that disables Plugin manager itself.
        public bool ApplyPendingChanges(CancellationToken cancellationToken)
        {
            bool changed = _pending.Count > 0;
            foreach (var change in _pending.Values.OrderBy(change => change.Order).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                _setActive(change.Entry, change.Active);
                _pending.Remove(change.Entry.Id);
            }
            return changed;
        }

        internal static string GetString(JsonElement arguments, string name, string fallback = "")
        {
            if (!arguments.TryGetProperty(name, out var value)) return fallback;
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback :
                throw new ArgumentException($"{name} must be a string.");
        }

        internal static bool GetBoolean(JsonElement arguments, string name, bool fallback)
        {
            if (!arguments.TryGetProperty(name, out var value)) return fallback;
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() :
                throw new ArgumentException($"{name} must be a boolean.");
        }
    }
}
