using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TxtAIEditor.Controls
{
    internal static class AgentWindowsApplicationResolver
    {
        public static string Resolve(string target)
        {
            string trimmed = target.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) ||
                File.Exists(trimmed) ||
                Path.IsPathRooted(trimmed) ||
                trimmed.Contains(Path.DirectorySeparatorChar) ||
                trimmed.Contains(Path.AltDirectorySeparatorChar) ||
                Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            {
                return trimmed;
            }

            string executableName = MapFriendlyName(trimmed);
            string? appPath = TryResolveRegisteredAppPath(executableName);
            if (!string.IsNullOrWhiteSpace(appPath))
            {
                return appPath;
            }

            string? officePath = TryResolveOfficeExecutable(executableName);
            return !string.IsNullOrWhiteSpace(officePath) ? officePath : trimmed;
        }

        private static string MapFriendlyName(string target)
        {
            string normalized = target.Trim().ToLowerInvariant();
            return normalized switch
            {
                "excel" or "excel.exe" or "엑셀" => "EXCEL.EXE",
                "word" or "winword" or "winword.exe" or "워드" => "WINWORD.EXE",
                "powerpoint" or "powerpnt" or "powerpnt.exe" or "ppt" or "파워포인트" => "POWERPNT.EXE",
                "outlook" or "outlook.exe" or "아웃룩" => "OUTLOOK.EXE",
                "onenote" or "onenote.exe" or "원노트" => "ONENOTE.EXE",
                "access" or "msaccess" or "msaccess.exe" or "액세스" => "MSACCESS.EXE",
                "publisher" or "mspub" or "mspub.exe" or "퍼블리셔" => "MSPUB.EXE",
                "calculator" or "calc" or "calc.exe" or "계산기" => "calc.exe",
                "notepad" or "notepad.exe" or "메모장" => "notepad.exe",
                "paint" or "mspaint" or "mspaint.exe" or "그림판" => "mspaint.exe",
                _ when Path.HasExtension(target) => target,
                _ => target + ".exe"
            };
        }

        private static string? TryResolveRegisteredAppPath(string executableName)
        {
            RegistryHive[] hives = { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
            RegistryView[] views = { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (RegistryHive hive in hives)
            {
                foreach (RegistryView view in views)
                {
                    try
                    {
                        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using RegistryKey? appPathKey = baseKey.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executableName);
                        string value = appPathKey?.GetValue(null) as string ?? string.Empty;
                        string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                        if (File.Exists(expanded))
                        {
                            return expanded;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        private static string? TryResolveOfficeExecutable(string executableName)
        {
            var officeExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "EXCEL.EXE", "WINWORD.EXE", "POWERPNT.EXE", "OUTLOOK.EXE",
                "ONENOTE.EXE", "MSACCESS.EXE", "MSPUB.EXE"
            };
            if (!officeExecutables.Contains(executableName))
            {
                return null;
            }

            string[] programFilesRoots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string programFiles in programFilesRoots
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                for (int version = 16; version >= 12; version--)
                {
                    string[] candidates =
                    {
                        Path.Combine(programFiles, "Microsoft Office", "root", $"Office{version}", executableName),
                        Path.Combine(programFiles, "Microsoft Office", $"Office{version}", executableName)
                    };
                    foreach (string candidate in candidates)
                    {
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }

            return null;
        }
    }
}
