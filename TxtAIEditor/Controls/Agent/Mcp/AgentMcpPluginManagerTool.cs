using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed record AgentPluginEntry(string Id, string Type, string Name, string Description, bool Active);

    internal sealed class AgentMcpPluginManagerTool
    {
        public const string ServerId = "builtin-plugin-manager";
        public const string ServerName = "Plugin manager";
        private const string ListSchema = """
            {"type":"object","properties":{"type":{"type":"string","enum":["all","mcp","skill"],"default":"all"}},"additionalProperties":false}
            """;
        private const string ChangeSchema = """
            {"type":"object","properties":{"name":{"type":"string","description":"Exact installed name or id returned by list_plugin or search_plugin."},"type":{"type":"string","enum":["mcp","skill"],"description":"Optional type to disambiguate identical MCP and skill names."}},"required":["name"],"additionalProperties":false}
            """;
        private const string SearchSchema = """
            {"type":"object","properties":{"keywords":{"type":"array","items":{"type":"string"},"minItems":1,"description":"Keywords or regular expressions to search in installed MCP/skill names, ids, and descriptions, including disabled skills."},"regex":{"type":"boolean","default":false},"match":{"type":"string","enum":["any","all"],"default":"any","description":"Match any keyword (OR) or all keywords (AND)."},"ignoreCase":{"type":"boolean","default":true},"type":{"type":"string","enum":["all","mcp","skill"],"default":"all"}},"required":["keywords"],"additionalProperties":false}
            """;
        private readonly Func<Task<IReadOnlyList<AgentPluginEntry>>> _getEntriesAsync;
        private readonly Action<AgentPluginEntry, bool> _setActive;
        private readonly Dictionary<string, (AgentPluginEntry Entry, bool Active, long Order)> _pending = new(StringComparer.OrdinalIgnoreCase);
        private long _changeOrder;

        public AgentMcpPluginManagerTool(
            Func<Task<IReadOnlyList<AgentPluginEntry>>> getEntriesAsync,
            Action<AgentPluginEntry, bool> setActive)
        {
            _getEntriesAsync = getEntriesAsync;
            _setActive = setActive;
        }

        public static bool IsServerName(string name) => ServerName.Equals(name, StringComparison.OrdinalIgnoreCase);

        public static IReadOnlyList<AgentMcpToolAlias> CreateAliases()
        {
            return new[]
            {
                CreateAlias("list_plugin", "List all installed MCPs and skills, including inactive entries, their descriptions and activation state.", ListSchema),
                CreateAlias("activate_plugin", "Activate an installed MCP or skill. The change, tool catalog, skill context, AgentPanel selection and chips apply before the next model turn.", ChangeSchema),
                CreateAlias("deactivate_plugin", "Deactivate an installed MCP or skill. The change, tool catalog, skill context, AgentPanel selection and chips apply before the next model turn.", ChangeSchema),
                CreateAlias("search_plugin", "Search installed MCPs and skills, including inactive entries and skill descriptions. Supports multiple keywords, AND/OR matching and regular expressions.", SearchSchema)
            };
        }

        private static AgentMcpToolAlias CreateAlias(string name, string description, string schema) => new()
        {
            Alias = "mcp_plugin_manager_" + name,
            ServerId = ServerId,
            ServerName = ServerName,
            ToolName = name,
            Description = description,
            InputSchemaJson = schema,
            IsBuiltIn = true
        };

        public static AgentMcpItem CreateMenuItem(bool selected, Func<string, string, string> getString) => new()
        {
            Name = ServerName,
            Detail = getString("AgentMcpPluginManagerDetail", "내장 플러그인 - MCP·Skill 조회, 검색 및 활성화 관리"),
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

                IReadOnlyList<AgentPluginEntry> entries = await _getEntriesAsync();
                cancellationToken.ThrowIfCancellationRequested();
                string type = GetString(arguments, "type", "all").ToLowerInvariant();
                if (type is not ("all" or "mcp" or "skill"))
                {
                    throw new ArgumentException("type must be all, mcp, or skill.");
                }

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
                    _pending[target.Id] = (target, active, ++_changeOrder);
                    return "MCP tool result: " + JsonSerializer.Serialize(new
                    {
                        id = target.Id, type = target.Type, name = target.Name,
                        active = target.Active, pendingActive = active, applies = "next_model_turn"
                    });
                }
                else if (toolName != "list_plugin")
                {
                    throw new ArgumentException($"Unknown Plugin manager tool: {toolName}.");
                }

                return "MCP tool result: " + JsonSerializer.Serialize(new
                {
                    count = filtered.Count,
                    plugins = filtered.OrderBy(entry => entry.Type).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(entry => new
                        {
                            id = entry.Id, type = entry.Type, name = entry.Name,
                            description = entry.Description, active = entry.Active,
                            pendingActive = _pending.TryGetValue(entry.Id, out var change) ? (bool?)change.Active : null
                        })
                });
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                return $"MCP tool failed: Plugin manager: {ex.Message}";
            }
        }

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

        private static string GetString(JsonElement arguments, string name, string fallback = "")
        {
            if (!arguments.TryGetProperty(name, out var value)) return fallback;
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback :
                throw new ArgumentException($"{name} must be a string.");
        }

        private static bool GetBoolean(JsonElement arguments, string name, bool fallback)
        {
            if (!arguments.TryGetProperty(name, out var value)) return fallback;
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() :
                throw new ArgumentException($"{name} must be a boolean.");
        }
    }
}
