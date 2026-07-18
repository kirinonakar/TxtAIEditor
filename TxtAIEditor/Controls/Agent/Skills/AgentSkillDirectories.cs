using System;
using System.Collections.Generic;
using System.IO;

namespace TxtAIEditor.Controls
{
    internal static class AgentSkillDirectories
    {
        public static string UserSettingsDirectory
        {
            get
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(userProfile, ".TxtAIEditor");
            }
        }

        public static string UserSkillsDirectory => Path.Combine(UserSettingsDirectory, "skills");

        public static string LegacyAgentSkillsDirectory
        {
            get
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(userProfile, ".agents", "skills");
            }
        }

        public static string BuiltInSkillsDirectory => Path.Combine(AppContext.BaseDirectory, "md", "skills");

        public static IReadOnlyList<string> GetSkillSearchDirectories()
        {
            var directories = new List<string>
            {
                UserSkillsDirectory,
                LegacyAgentSkillsDirectory,
                BuiltInSkillsDirectory
            };

            var uniqueDirectories = new List<string>();
            foreach (string directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                bool alreadyAdded = false;
                foreach (string existingDirectory in uniqueDirectories)
                {
                    if (string.Equals(existingDirectory, directory, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyAdded = true;
                        break;
                    }
                }

                if (alreadyAdded)
                {
                    continue;
                }

                uniqueDirectories.Add(directory);
            }

            return uniqueDirectories;
        }

        public static bool IsInsideUserSkillsDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return AgentWorkspaceFileResolver.IsInsideRoot(UserSkillsDirectory, path);
        }
    }
}
