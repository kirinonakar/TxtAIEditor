using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentSkill
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SkillFilePath { get; set; } = string.Empty;
    }

    internal sealed class AgentSkillController
    {
        private readonly AgentPane _agentPane;
        private readonly Func<string, string, string> _getString;
        private readonly Action _contextChanged;
        private readonly IReadOnlyList<string> _skillDirectories;
        private readonly List<AgentSkill> _skills = new();
        private readonly Dictionary<string, AgentSkill> _activePluginSkills = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AgentSkill> _installedPluginSkills = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _removedPluginSkills = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _builtInRelatedSkills = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _removedBuiltInRelatedSkills = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedSkillNames = new(StringComparer.OrdinalIgnoreCase);
        private Task? _loadTask;
        private bool _hasLoaded;

        public AgentSkillController(
            AgentPane agentPane,
            Func<string, string, string> getString,
            Action contextChanged)
        {
            _agentPane = agentPane;
            _getString = getString;
            _contextChanged = contextChanged;
            _skillDirectories = AgentSkillDirectories.GetSkillSearchDirectories();
        }

        public Task LoadAsync()
        {
            _loadTask = LoadCoreAsync();
            return _loadTask;
        }

        public Task LoadIfNeededAsync()
        {
            if (_hasLoaded)
            {
                return Task.CompletedTask;
            }

            _loadTask ??= LoadCoreAsync();
            return _loadTask;
        }

        private async Task LoadCoreAsync()
        {
            var loadedByName = new Dictionary<string, AgentSkill>(StringComparer.OrdinalIgnoreCase);
            foreach (string skillsDirectory in _skillDirectories)
            {
                try
                {
                    if (!Directory.Exists(skillsDirectory))
                    {
                        continue;
                    }

                    foreach (string skillFilePath in EnumerateSkillFiles(skillsDirectory))
                    {
                        try
                        {
                            string content = await File.ReadAllTextAsync(skillFilePath);
                            string name = GetSkillName(skillFilePath);
                            if (string.IsNullOrWhiteSpace(name) || loadedByName.ContainsKey(name))
                            {
                                continue;
                            }

                            loadedByName.Add(name, new AgentSkill
                            {
                                Name = name,
                                Description = ExtractDescription(content),
                                SkillFilePath = skillFilePath
                            });
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to load agent skill '{skillFilePath}': {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load agent skills from '{skillsDirectory}': {ex.Message}");
                }
            }

            _skills.Clear();
            _skills.AddRange(loadedByName.Values
                .Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
                .OrderBy(skill => skill.Name, StringComparer.CurrentCultureIgnoreCase));

            _selectedSkillNames.RemoveWhere(name => FindSkill(name) == null);
            _hasLoaded = true;
            UpdateUI();
        }

        public string BuildSelectedSkillSection()
        {
            var selectedSkills = GetSelectedSkills();
            if (selectedSkills.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.AppendLine("[Enabled agent skills]");
            foreach (var skill in selectedSkills)
            {
                builder.AppendLine($"## {skill.Name}");
                builder.AppendLine($"Description: {GetDescriptionForPrompt(skill)}");
                builder.AppendLine($"Use tool: skill_use {{\"name\":\"{skill.Name}\"}} to read the full SKILL.md before applying this skill.");
                builder.AppendLine();
            }

            return builder.ToString().Trim();
        }

        public async Task<string> UseSkillAsync(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
            {
                return "skill_use failed: provide a skill name or SKILL.md path.";
            }

            if (_skills.Count == 0 && _activePluginSkills.Count == 0)
            {
                await LoadAsync();
            }

            AgentSkill? skill = FindSkill(nameOrPath) ?? FindSkillByPath(nameOrPath);
            if (skill == null)
            {
                string availableSkills = string.Join(", ", _skills
                    .Select(item => item.Name)
                    .Concat(_activePluginSkills.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                return string.IsNullOrWhiteSpace(availableSkills)
                    ? $"skill_use failed: skill not found: {nameOrPath}. No installed skills were found."
                    : $"skill_use failed: skill not found: {nameOrPath}. Available skills: {availableSkills}";
            }

            if (!IsSkillActive(skill.Name))
            {
                return $"skill_use failed: skill is inactive: {skill.Name}. Enable it in AgentPanel or with Plugin manager before reading it.";
            }

            if (!File.Exists(skill.SkillFilePath))
            {
                return $"skill_use failed: SKILL.md not found for {skill.Name}: {skill.SkillFilePath}";
            }

            string content = await File.ReadAllTextAsync(skill.SkillFilePath);
            var builder = new StringBuilder();
            builder.AppendLine($"[Skill: {skill.Name}]");
            builder.AppendLine($"SKILL.md: {skill.SkillFilePath}");
            builder.AppendLine($"Skill directory: {GetSkillDirectory(skill)}");
            builder.AppendLine("Path guidance: Resolve relative files and scripts from the Skill directory unless the skill says otherwise. For example, scripts/recalc.py means Skill directory/scripts/recalc.py.");
            builder.AppendLine("When running a skill script that imports sibling modules or data, set PowerShell's location to the Skill directory first, or use absolute paths for both the script and workspace files.");
            builder.AppendLine("Example command shape: python \"<Skill directory>\\scripts\\recalc.py\" \"<absolute workspace file>\".");
            builder.AppendLine();
            builder.Append(content);
            return builder.ToString();
        }

        public string GetSkillDisplayName(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
            {
                return string.Empty;
            }

            AgentSkill? skill = FindSkill(nameOrPath) ?? FindSkillByPath(nameOrPath);
            return skill?.Name ?? nameOrPath;
        }

        public string GetSelectedSkillLabel()
        {
            return string.Join(", ", GetSelectedSkills().Select(skill => skill.Name));
        }

        public bool HasSelectedSkills()
        {
            return GetSelectedSkills().Count > 0;
        }

        public async Task<IReadOnlyList<AgentMcpSkillOption>> GetInstalledSkillOptionsAsync()
        {
            await LoadIfNeededAsync();
            return _skills
                .Select(skill => new AgentMcpSkillOption
                {
                    Name = skill.Name,
                    Description = skill.Description
                })
                .ToList();
        }

        public async Task<IReadOnlyList<AgentPluginEntry>> GetPluginManagerEntriesAsync()
        {
            await LoadIfNeededAsync();
            // Registry metadata may have been installed by an older description parser.
            foreach (AgentSkill skill in _installedPluginSkills.Values.ToList())
            {
                try
                {
                    skill.Description = ExtractDescription(await File.ReadAllTextAsync(skill.SkillFilePath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to read plugin skill description '{skill.SkillFilePath}': {ex.Message}");
                }
            }
            return EnumerateInstalledSkills()
                .Select(skill => new AgentPluginEntry("skill:" + skill.Name, "skill", skill.Name,
                    skill.Description, IsSkillActive(skill.Name)))
                .ToList();
        }

        private IEnumerable<AgentSkill> EnumerateInstalledSkills()
        {
            return _skills.Concat(_installedPluginSkills.Values)
                .GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last());
        }

        public void SetInstalledPluginSkills(IEnumerable<AgentPluginSkill> pluginSkills)
        {
            _installedPluginSkills.Clear();
            foreach (AgentPluginSkill skill in pluginSkills)
            {
                if (string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.SkillFilePath))
                {
                    continue;
                }
                _installedPluginSkills[skill.Name] = new AgentSkill
                {
                    Name = skill.Name,
                    Description = skill.Description,
                    SkillFilePath = skill.SkillFilePath
                };
            }
            _selectedSkillNames.RemoveWhere(name => !_installedPluginSkills.ContainsKey(name) &&
                _skills.All(skill => !skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
            _removedPluginSkills.RemoveWhere(name => !_installedPluginSkills.ContainsKey(name));
            UpdateUI();
        }

        public void SetBuiltInRelatedSkills(string pluginId, IReadOnlyList<string> skillNames)
        {
            string key = pluginId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var names = new HashSet<string>(
                (skillNames ?? Array.Empty<string>())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (!_builtInRelatedSkills.TryGetValue(key, out HashSet<string>? previousNames) || previousNames == null)
            {
                previousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (!previousNames.SetEquals(names))
            {
                // The configured list changed (ComfyUI activated, deactivated, or
                // re-saved), so re-apply it and drop deselection overrides.
                foreach (string name in names.Concat(previousNames))
                {
                    _removedBuiltInRelatedSkills.Remove(name);
                }
            }

            if (names.Count == 0)
            {
                _builtInRelatedSkills.Remove(key);
            }
            else
            {
                _builtInRelatedSkills[key] = names;
            }

            UpdateUI();
        }

        public void ToggleSkill(string skillName)
        {
            SetSkillActive(skillName, !IsSkillActive(skillName));
        }

        public void SetSkillActive(string skillName, bool active)
        {
            if (FindSkill(skillName) == null || IsSkillActive(skillName) == active)
            {
                return;
            }
            if (!active)
            {
                _selectedSkillNames.Remove(skillName);
                if (_installedPluginSkills.ContainsKey(skillName))
                {
                    _removedPluginSkills.Add(skillName);
                }
                if (IsBuiltInRelatedSkill(skillName))
                {
                    _removedBuiltInRelatedSkills.Add(skillName);
                }
            }
            else
            {
                _selectedSkillNames.Add(skillName);
                _removedBuiltInRelatedSkills.Remove(skillName);
                _removedPluginSkills.Remove(skillName);
            }

            UpdateSelectionUI();
        }

        public void RemoveSelectedSkill(string skillName)
        {
            SetSkillActive(skillName, false);
        }

        public void ClearSelectedSkills()
        {
            _selectedSkillNames.Clear();
            _removedPluginSkills.UnionWith(_activePluginSkills.Keys);
            foreach (string name in EnumerateBuiltInRelatedSkillNames())
            {
                _removedBuiltInRelatedSkills.Add(name);
            }

            UpdateSelectionUI();
        }

        public async Task DeleteSkillAsync(string skillName)
        {
            AgentSkill? skill = _skills.FirstOrDefault(item =>
                item.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
            if (skill == null || !AgentSkillDirectories.IsInsideUserSkillsDirectory(skill.SkillFilePath))
            {
                return;
            }

            try
            {
                string skillFilePath = Path.GetFullPath(skill.SkillFilePath);
                if (Path.GetFileName(skillFilePath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                {
                    string? skillDirectory = Path.GetDirectoryName(skillFilePath);
                    if (!string.IsNullOrWhiteSpace(skillDirectory) &&
                        AgentSkillDirectories.IsInsideUserSkillsDirectory(skillDirectory))
                    {
                        string normalizedSkillDirectory = Path.GetFullPath(skillDirectory)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string normalizedUserSkillsDirectory = Path.GetFullPath(AgentSkillDirectories.UserSkillsDirectory)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (normalizedSkillDirectory.Equals(normalizedUserSkillsDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(skillFilePath);
                        }
                        else
                        {
                            Directory.Delete(skillDirectory, recursive: true);
                        }
                    }
                }
                else if (File.Exists(skillFilePath))
                {
                    File.Delete(skillFilePath);
                }

                _selectedSkillNames.Remove(skill.Name);
                await LoadAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to delete agent skill '{skill.SkillFilePath}': {ex.Message}");
            }
        }

        public void SetActivePluginSkills(IEnumerable<AgentPluginSkill> pluginSkills)
        {
            var previousNames = new HashSet<string>(_activePluginSkills.Keys, StringComparer.OrdinalIgnoreCase);
            _activePluginSkills.Clear();
            foreach (AgentPluginSkill skill in pluginSkills)
            {
                if (string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.SkillFilePath))
                {
                    continue;
                }

                _activePluginSkills[skill.Name] = new AgentSkill
                {
                    Name = skill.Name,
                    Description = skill.Description,
                    SkillFilePath = skill.SkillFilePath
                };
            }

            previousNames.SymmetricExceptWith(_activePluginSkills.Keys);
            _removedPluginSkills.ExceptWith(previousNames);
            UpdateUI();
        }

        private List<AgentSkill> GetSelectedSkills()
        {
            return EnumerateInstalledSkills().Where(skill => IsSkillActive(skill.Name)).ToList();
        }

        private IEnumerable<string> EnumerateBuiltInRelatedSkillNames()
        {
            foreach (HashSet<string> names in _builtInRelatedSkills.Values)
            {
                foreach (string name in names)
                {
                    if (!_removedBuiltInRelatedSkills.Contains(name))
                    {
                        yield return name;
                    }
                }
            }
        }

        private bool IsBuiltInRelatedSkill(string skillName)
        {
            return _builtInRelatedSkills.Values.Any(names => names.Contains(skillName));
        }

        private bool IsSkillActive(string skillName)
        {
            return _selectedSkillNames.Contains(skillName) ||
                (_activePluginSkills.ContainsKey(skillName) && !_removedPluginSkills.Contains(skillName)) ||
                (IsBuiltInRelatedSkill(skillName) && !_removedBuiltInRelatedSkills.Contains(skillName));
        }

        private List<string> GetSelectedSkillNamesForUi()
        {
            return GetSelectedSkills().Select(skill => skill.Name).ToList();
        }

        private AgentSkill? FindSkill(string name)
        {
            return _activePluginSkills.TryGetValue(name, out AgentSkill? pluginSkill)
                ? pluginSkill
                : _installedPluginSkills.TryGetValue(name, out AgentSkill? installedSkill)
                    ? installedSkill
                    : _skills.FirstOrDefault(skill => skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private AgentSkill? FindSkillByPath(string path)
        {
            string normalizedPath = NormalizePathForCompare(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return null;
            }

            AgentSkill? pluginSkill = _installedPluginSkills.Values.FirstOrDefault(skill =>
                string.Equals(
                    NormalizePathForCompare(skill.SkillFilePath),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
            return pluginSkill ?? _skills.FirstOrDefault(skill =>
                string.Equals(
                    NormalizePathForCompare(skill.SkillFilePath),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateUI()
        {
            var skillItems = EnumerateInstalledSkills()
                .OrderBy(skill => skill.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(skill => new AgentSkillItem
                {
                    Name = skill.Name,
                    Description = skill.Description,
                    CanDelete = AgentSkillDirectories.IsInsideUserSkillsDirectory(skill.SkillFilePath)
                })
                .ToList();
            var selectedNames = GetSelectedSkillNamesForUi();

            void ApplyUI()
            {
                _agentPane.UpdateAgentSkillsMenu(skillItems, selectedNames, _getString);
                QueueContextChanged();
            }

            var dispatcher = _agentPane.DispatcherQueue;
            if (dispatcher?.HasThreadAccess == true)
            {
                ApplyUI();
                return;
            }

            if (dispatcher?.TryEnqueue(ApplyUI) == true)
            {
                return;
            }

            ApplyUI();
        }

        private void QueueContextChanged()
        {
            var dispatcher = _agentPane.DispatcherQueue;
            if (dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _contextChanged()) == true)
            {
                return;
            }

            _contextChanged();
        }

        private void UpdateSelectionUI()
        {
            var selectedNames = GetSelectedSkillNamesForUi();

            void ApplyUI()
            {
                _agentPane.UpdateAgentSkillSelection(selectedNames, _getString);
                QueueContextChanged();
            }

            var dispatcher = _agentPane.DispatcherQueue;
            if (dispatcher?.HasThreadAccess == true)
            {
                ApplyUI();
                return;
            }

            if (dispatcher?.TryEnqueue(ApplyUI) == true)
            {
                return;
            }

            ApplyUI();
        }

        private static IEnumerable<string> EnumerateSkillFiles(string skillsDirectory)
        {
            foreach (string directory in Directory.EnumerateDirectories(skillsDirectory))
            {
                string skillFile = Path.Combine(directory, "SKILL.md");
                if (File.Exists(skillFile))
                {
                    yield return skillFile;
                }
            }

            foreach (string markdownFile in Directory.EnumerateFiles(skillsDirectory, "*.md", SearchOption.TopDirectoryOnly))
            {
                yield return markdownFile;
            }
        }

        private static string GetSkillName(string skillFilePath)
        {
            string fileName = Path.GetFileName(skillFilePath);
            if (fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                string? directoryName = Path.GetFileName(Path.GetDirectoryName(skillFilePath));
                return directoryName ?? Path.GetFileNameWithoutExtension(skillFilePath);
            }

            return Path.GetFileNameWithoutExtension(skillFilePath);
        }

        internal static string ExtractDescription(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            string yamlDescription = ExtractYamlDescription(content);
            if (!string.IsNullOrWhiteSpace(yamlDescription))
            {
                return NormalizeDescription(yamlDescription);
            }

            string headingDescription = ExtractHeadingDescription(content);
            if (!string.IsNullOrWhiteSpace(headingDescription))
            {
                return NormalizeDescription(headingDescription);
            }

            return NormalizeDescription(ExtractFirstParagraph(content));
        }

        private static string ExtractYamlDescription(string content)
        {
            if (!content.StartsWith("---", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            int closingIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (closingIndex < 0)
            {
                return string.Empty;
            }

            string frontMatter = content.Substring(3, closingIndex - 3);
            var match = Regex.Match(
                frontMatter,
                @"(?im)^[ \t]*description[ \t]*:[ \t]*(?<value>[^\r\n]*)");
            if (!match.Success)
            {
                return string.Empty;
            }

            string value = match.Groups["value"].Value.Trim();
            if (value.Length == 0 || value is "|" or "|-" or "|+" or ">" or ">-" or ">+")
            {
                var builder = new StringBuilder();
                using var reader = new StringReader(frontMatter.Substring(match.Index + match.Length));
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    if (!char.IsWhiteSpace(line[0]))
                    {
                        break;
                    }
                    builder.AppendLine(line.Trim());
                }
                return builder.ToString();
            }
            return value.Trim('"', '\'');
        }

        private static string ExtractHeadingDescription(string content)
        {
            using var reader = new StringReader(content);
            bool inDescriptionSection = false;
            var builder = new StringBuilder();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    if (inDescriptionSection)
                    {
                        break;
                    }

                    string heading = trimmed.TrimStart('#').Trim();
                    if (heading.Equals("Description", StringComparison.OrdinalIgnoreCase) ||
                        heading.Equals("설명", StringComparison.OrdinalIgnoreCase))
                    {
                        inDescriptionSection = true;
                    }
                    continue;
                }

                if (!inDescriptionSection)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    if (builder.Length > 0)
                    {
                        break;
                    }
                    continue;
                }

                builder.AppendLine(trimmed);
            }

            return builder.ToString();
        }

        private static string ExtractFirstParagraph(string content)
        {
            using var reader = new StringReader(content);
            var builder = new StringBuilder();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) ||
                    trimmed.StartsWith("---", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    if (builder.Length > 0)
                    {
                        break;
                    }
                    continue;
                }

                builder.AppendLine(trimmed);
            }

            return builder.ToString();
        }

        private static string NormalizeDescription(string value)
        {
            return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        }

        private static string NormalizePathForCompare(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
            }
            catch
            {
                return path.Replace('/', Path.DirectorySeparatorChar).Trim();
            }
        }

        private static string GetDescriptionForPrompt(AgentSkill skill)
        {
            if (string.IsNullOrWhiteSpace(skill.Description))
            {
                return "(No description found.)";
            }
            const int maxDescriptionChars = 500;
            return skill.Description.Length <= maxDescriptionChars ? skill.Description :
                skill.Description.Substring(0, maxDescriptionChars).TrimEnd() + "...";
        }

        private static string GetSkillDirectory(AgentSkill skill)
        {
            return Path.GetDirectoryName(skill.SkillFilePath) ?? string.Empty;
        }
    }
}
