using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TxtAIEditor.Core.Services
{
    public sealed class TerminalShellProfile
    {
        private TerminalShellProfile(string id, string displayName, string shortName, string executablePath, string arguments)
        {
            Id = id;
            DisplayName = displayName;
            ShortName = shortName;
            ExecutablePath = executablePath;
            Arguments = arguments;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string ShortName { get; }
        public string ExecutablePath { get; }
        public string Arguments { get; }
        public bool IsAvailable => IsExecutableAvailable(ExecutablePath);

        public string BuildCommandLine()
        {
            string command = QuoteArgument(ExecutablePath);
            if (!string.IsNullOrWhiteSpace(Arguments))
            {
                command += " " + Arguments;
            }

            return command;
        }

        public static IReadOnlyList<TerminalShellProfile> GetProfiles()
        {
            var powershellPath = ResolvePowerShellExecutable(out bool isPowerShell7);
            return new[]
            {
                new TerminalShellProfile(
                    "PowerShell",
                    isPowerShell7 ? "PowerShell 7" : "Windows PowerShell",
                    "P",
                    powershellPath,
                    "-NoLogo -NoProfile"),
                new TerminalShellProfile(
                    "GitBash",
                    "Git Bash",
                    "G",
                    ResolveGitBashExecutable(),
                    "--login -i"),
                new TerminalShellProfile(
                    "WSL",
                    "WSL",
                    "W",
                    "wsl.exe",
                    string.Empty),
                new TerminalShellProfile(
                    "Cmd",
                    "Command Prompt",
                    "C",
                    ResolveCmdExecutable(),
                    string.Empty)
            };
        }

        public static TerminalShellProfile Resolve(string? profileId)
        {
            var profiles = GetProfiles();
            return profiles.FirstOrDefault(profile => profile.Id.Equals(profileId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? profiles[0];
        }

        public static string NormalizeId(string? profileId)
        {
            return Resolve(profileId).Id;
        }

        private static string ResolvePowerShellExecutable(out bool isPowerShell7)
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PowerShell", "7", "pwsh.exe"),
                FindExecutableOnPath("pwsh.exe")
            };

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    isPowerShell7 = true;
                    return candidate;
                }
            }

            isPowerShell7 = false;
            return "powershell.exe";
        }

        private static string ResolveGitBashExecutable()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
                FindExecutableOnPath("bash.exe")
            };

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        }

        private static string ResolveCmdExecutable()
        {
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string systemCmd = Path.Combine(systemDirectory, "cmd.exe");
            if (File.Exists(systemCmd))
            {
                return systemCmd;
            }

            string pathCmd = FindExecutableOnPath("cmd.exe");
            return string.IsNullOrWhiteSpace(pathCmd) ? "cmd.exe" : pathCmd;
        }

        private static string FindExecutableOnPath(string fileName)
        {
            try
            {
                string? pathValue = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrWhiteSpace(pathValue))
                {
                    return string.Empty;
                }

                foreach (string directory in pathValue.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    string candidate = Path.Combine(directory.Trim(), fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static bool IsExecutableAvailable(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            return executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                (File.Exists(executablePath) || !executablePath.Contains(Path.DirectorySeparatorChar));
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }
    }
}
