using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;

namespace TxtAIEditor.Controls
{
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    internal sealed class AgentSessionSelections
    {
        public HashSet<string> Presets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Skills { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> McpServers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> McpPlugins { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DisabledPluginSkills { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DisabledBuiltInSkills { get; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public Dictionary<string, AgentSkill> ActivePluginSkills { get; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonIgnore]
        public Dictionary<string, HashSet<string>> BuiltInRelatedSkills { get; } = new(StringComparer.OrdinalIgnoreCase);

        public AgentSessionSelections Copy()
        {
            var copy = new AgentSessionSelections();
            copy.Presets.UnionWith(Presets);
            copy.Skills.UnionWith(Skills);
            copy.McpServers.UnionWith(McpServers);
            copy.McpPlugins.UnionWith(McpPlugins);
            copy.DisabledPluginSkills.UnionWith(DisabledPluginSkills);
            copy.DisabledBuiltInSkills.UnionWith(DisabledBuiltInSkills);
            return copy;
        }

        public bool EquivalentTo(AgentSessionSelections? other) => other != null &&
            Presets.SetEquals(other.Presets) && Skills.SetEquals(other.Skills) && McpServers.SetEquals(other.McpServers) &&
            McpPlugins.SetEquals(other.McpPlugins) && DisabledPluginSkills.SetEquals(other.DisabledPluginSkills) &&
            DisabledBuiltInSkills.SetEquals(other.DisabledBuiltInSkills);
    }

    internal sealed class AgentSessionSelectionContext
    {
        private readonly AsyncLocal<AgentSessionSelections?> _runSelection = new();

        public AgentSessionSelections Visible { get; set; } = new();
        public AgentSessionSelections Current => _runSelection.Value ?? Visible;
        public Func<string, bool>? IsMcpServerInOpenSession { get; set; }

        public bool IsMcpServerInUse(string id) => IsMcpServerInOpenSession?.Invoke(id) ?? Visible.McpServers.Contains(id);

        public IDisposable Use(AgentSessionSelections selection)
        {
            var scope = new Scope(_runSelection, _runSelection.Value);
            _runSelection.Value = selection;
            return scope;
        }

        private sealed class Scope(AsyncLocal<AgentSessionSelections?> current, AgentSessionSelections? previous) : IDisposable
        {
            public void Dispose() => current.Value = previous;
        }
    }
}
