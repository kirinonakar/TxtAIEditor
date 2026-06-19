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
        private readonly string _skillsDirectory;
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

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _skillsDirectory = Path.Combine(userProfile, ".agents", "skills");
        }

        public async Task LoadAsync()
        {
            var loaded = new List<AgentSkill>();
            try
            {
                if (Directory.Exists(_skillsDirectory))
                {
                    foreach (string skillFilePath in EnumerateSkillFiles(_skillsDirectory))
                    {
                        string content = await File.ReadAllTextAsync(skillFilePath);
                        string name = GetSkillName(skillFilePath);
                        loaded.Add(new AgentSkill
                        {
                            Name = name,
                            Description = ExtractDescription(content),
                            SkillFilePath = skillFilePath
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load agent skills: {ex.Message}");
            }

            _skills.Clear();
            _skills.AddRange(loaded
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
                builder.AppendLine();
            }

            return builder.ToString().Trim();
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

        private static string GetDescriptionForPrompt(AgentSkill skill)
        {
            return string.IsNullOrWhiteSpace(skill.Description)
                ? "(No description found.)"
                : skill.Description;
        }
    }
}
