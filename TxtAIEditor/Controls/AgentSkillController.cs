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
        private readonly HashSet<string> _selectedSkillNames = new(StringComparer.OrdinalIgnoreCase);

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

        public async Task LoadAsync()
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

            _selectedSkillNames.RemoveWhere(name => _skills.All(skill => !skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
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
                builder.AppendLine($"SKILL.md: {skill.SkillFilePath}");
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

            if (_skills.Count == 0)
            {
                await LoadAsync();
            }

            AgentSkill? skill = FindSkill(nameOrPath) ?? FindSkillByPath(nameOrPath);
            if (skill == null)
            {
                string availableSkills = string.Join(", ", _skills.Select(item => item.Name));
                return string.IsNullOrWhiteSpace(availableSkills)
                    ? $"skill_use failed: skill not found: {nameOrPath}. No installed skills were found."
                    : $"skill_use failed: skill not found: {nameOrPath}. Available skills: {availableSkills}";
            }

            if (!File.Exists(skill.SkillFilePath))
            {
                return $"skill_use failed: SKILL.md not found for {skill.Name}: {skill.SkillFilePath}";
            }

            string content = await File.ReadAllTextAsync(skill.SkillFilePath);
            var builder = new StringBuilder();
            builder.AppendLine($"[Skill: {skill.Name}]");
            builder.AppendLine($"SKILL.md: {skill.SkillFilePath}");
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

        public void ToggleSkill(string skillName)
        {
            if (FindSkill(skillName) == null)
            {
                return;
            }

            if (!_selectedSkillNames.Add(skillName))
            {
                _selectedSkillNames.Remove(skillName);
            }

            UpdateUI();
        }

        public void RemoveSelectedSkill(string skillName)
        {
            _selectedSkillNames.Remove(skillName);
            UpdateUI();
        }

        private List<AgentSkill> GetSelectedSkills()
        {
            return _skills
                .Where(skill => _selectedSkillNames.Contains(skill.Name))
                .ToList();
        }

        private AgentSkill? FindSkill(string name)
        {
            return _skills.FirstOrDefault(skill => skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private AgentSkill? FindSkillByPath(string path)
        {
            string normalizedPath = NormalizePathForCompare(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return null;
            }

            return _skills.FirstOrDefault(skill =>
                string.Equals(
                    NormalizePathForCompare(skill.SkillFilePath),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateUI()
        {
            var skillItems = _skills
                .Select(skill => new AgentSkillItem
                {
                    Name = skill.Name,
                    Description = skill.Description
                })
                .ToList();
            var selectedNames = _selectedSkillNames.ToList();
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.UpdateAgentSkillsMenu(skillItems, selectedNames, _getString);
                _contextChanged();
            });
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

        private static string ExtractDescription(string content)
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
                @"(?im)^\s*description\s*:\s*(?<value>.+?)\s*$");
            if (!match.Success)
            {
                return string.Empty;
            }

            return match.Groups["value"].Value.Trim().Trim('"', '\'');
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
            string normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            const int maxDescriptionChars = 500;
            if (normalized.Length <= maxDescriptionChars)
            {
                return normalized;
            }

            return normalized.Substring(0, maxDescriptionChars).TrimEnd() + "...";
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
            return string.IsNullOrWhiteSpace(skill.Description)
                ? "(No description found.)"
                : skill.Description;
        }
    }
}
