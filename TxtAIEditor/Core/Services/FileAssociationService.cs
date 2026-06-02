using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TxtAIEditor.Core.Services
{
    public static class FileAssociationService
    {
        private const string AppName = "TxtAIEditor";
        private const string ApplicationDescription = "TxtAIEditor text editor";
        private const string ApplicationRegistryPath = @"Software\Classes\Applications\TxtAIEditor.exe";
        private const string RegisteredApplicationsPath = @"Software\RegisteredApplications";

        public static void RegisterUnpackagedFileAssociations()
        {
            try
            {
                string? executablePath = ResolveExecutablePath();
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    return;
                }

                bool changed = false;
                changed |= RegisterApplication(executablePath);
                changed |= RegisterFileType(".txt", "TxtAIEditor.txt", "Text Document", executablePath);
                changed |= RegisterFileType(".md", "TxtAIEditor.md", "Markdown Document", executablePath);
                changed |= RegisterFileType(".csv", "TxtAIEditor.csv", "CSV Document", executablePath);
                changed |= RegisterClassicContextMenu(executablePath);

                if (changed)
                {
                    NotifyShellAssociationChanged();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to register unpackaged file associations: {ex.Message}");
            }
        }

        private static string? ResolveExecutablePath()
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) &&
                processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }

            string appBase = AppContext.BaseDirectory;
            string candidate = Path.Combine(appBase, "TxtAIEditor.exe");
            return File.Exists(candidate) ? candidate : processPath;
        }

        private static bool RegisterApplication(string executablePath)
        {
            bool changed = false;
            using RegistryKey appKey = Registry.CurrentUser.CreateSubKey(ApplicationRegistryPath);
            changed |= SetValueIfDifferent(appKey, "FriendlyAppName", AppName, RegistryValueKind.String);

            using RegistryKey commandKey = appKey.CreateSubKey(@"shell\open\command");
            changed |= SetValueIfDifferent(commandKey, string.Empty, BuildOpenCommand(executablePath), RegistryValueKind.String);

            using RegistryKey supportedTypesKey = appKey.CreateSubKey("SupportedTypes");
            changed |= SetValueIfDifferent(supportedTypesKey, ".txt", string.Empty, RegistryValueKind.String);
            changed |= SetValueIfDifferent(supportedTypesKey, ".md", string.Empty, RegistryValueKind.String);
            changed |= SetValueIfDifferent(supportedTypesKey, ".csv", string.Empty, RegistryValueKind.String);

            using RegistryKey capabilitiesKey = appKey.CreateSubKey("Capabilities");
            changed |= SetValueIfDifferent(capabilitiesKey, "ApplicationName", AppName, RegistryValueKind.String);
            changed |= SetValueIfDifferent(capabilitiesKey, "ApplicationDescription", ApplicationDescription, RegistryValueKind.String);

            using RegistryKey fileAssociationsKey = capabilitiesKey.CreateSubKey("FileAssociations");
            changed |= SetValueIfDifferent(fileAssociationsKey, ".txt", "TxtAIEditor.txt", RegistryValueKind.String);
            changed |= SetValueIfDifferent(fileAssociationsKey, ".md", "TxtAIEditor.md", RegistryValueKind.String);
            changed |= SetValueIfDifferent(fileAssociationsKey, ".csv", "TxtAIEditor.csv", RegistryValueKind.String);

            using RegistryKey registeredAppsKey = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsPath);
            changed |= SetValueIfDifferent(registeredAppsKey, AppName, ApplicationRegistryPath + @"\Capabilities", RegistryValueKind.String);
            return changed;
        }

        private static bool RegisterFileType(string extension, string progId, string description, string executablePath)
        {
            bool changed = false;
            using RegistryKey progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}");
            changed |= SetValueIfDifferent(progIdKey, string.Empty, description, RegistryValueKind.String);

            using RegistryKey iconKey = progIdKey.CreateSubKey("DefaultIcon");
            changed |= SetValueIfDifferent(iconKey, string.Empty, $"{Quote(executablePath)},0", RegistryValueKind.String);

            using RegistryKey commandKey = progIdKey.CreateSubKey(@"shell\open\command");
            changed |= SetValueIfDifferent(commandKey, string.Empty, BuildOpenCommand(executablePath), RegistryValueKind.String);

            using RegistryKey extensionKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}");
            string? userChoiceProgId = ReadUserChoiceProgId(extension);
            if (string.IsNullOrWhiteSpace(userChoiceProgId))
            {
                changed |= SetValueIfDifferent(extensionKey, string.Empty, progId, RegistryValueKind.String);
            }

            using RegistryKey openWithProgIdsKey = extensionKey.CreateSubKey("OpenWithProgids");
            changed |= SetValueIfDifferent(openWithProgIdsKey, progId, Array.Empty<byte>(), RegistryValueKind.Binary);
            return changed;
        }

        private static bool RegisterClassicContextMenu(string executablePath)
        {
            bool changed = false;
            changed |= RegisterClassicContextMenuForType(
                @"Software\Classes\*\shell\Open in TxtAIEditor",
                executablePath);
            changed |= RegisterClassicContextMenuForType(
                @"Software\Classes\Folder\shell\Open in TxtAIEditor",
                executablePath);

            if (IsRunningPackaged())
            {
                changed |= RegisterClassicContextMenuWithRegExe(executablePath);
            }

            return changed;
        }

        private static bool RegisterClassicContextMenuForType(string shellKeyPath, string executablePath)
        {
            bool changed = false;
            using RegistryKey shellKey = Registry.CurrentUser.CreateSubKey(shellKeyPath);
            changed |= SetValueIfDifferent(shellKey, string.Empty, "Open in TxtAIEditor", RegistryValueKind.String);
            changed |= SetValueIfDifferent(shellKey, "MUIVerb", "Open in TxtAIEditor", RegistryValueKind.String);
            changed |= SetValueIfDifferent(shellKey, "Icon", $"{Quote(executablePath)},0", RegistryValueKind.String);

            using RegistryKey commandKey = shellKey.CreateSubKey("command");
            changed |= SetValueIfDifferent(commandKey, string.Empty, BuildOpenCommand(executablePath), RegistryValueKind.String);
            return changed;
        }

        private static bool RegisterClassicContextMenuWithRegExe(string executablePath)
        {
            bool succeeded = true;
            succeeded &= RunRegAdd(@"HKCU\Software\Classes\*\shell\Open in TxtAIEditor", "/ve", "Open in TxtAIEditor");
            succeeded &= RunRegAdd(@"HKCU\Software\Classes\*\shell\Open in TxtAIEditor", "/v", "MUIVerb", "Open in TxtAIEditor");
            succeeded &= RunRegAdd(@"HKCU\Software\Classes\*\shell\Open in TxtAIEditor", "/v", "Icon", $"{Quote(executablePath)},0");
            succeeded &= RunRegAdd(@"HKCU\Software\Classes\*\shell\Open in TxtAIEditor\command", "/ve", BuildOpenCommand(executablePath));

            succeeded &= RunRegAdd(@"HKCU\Software\Classes\Folder\shell\Open in TxtAIEditor", "/ve", "Open in TxtAIEditor");
            succeeded &= RunRegAdd(@"HKCU\Software\Classes\Folder\shell\Open in TxtAIEditor", "/v", "MUIVerb", "Open in TxtAIEditor");
            succeeded &= RunRegAdd(@"HKCU\Software\Classes\Folder\shell\Open in TxtAIEditor", "/v", "Icon", $"{Quote(executablePath)},0");
            succeeded &= RunRegAdd(@"HKCU\Software\Classes\Folder\shell\Open in TxtAIEditor\command", "/ve", BuildOpenCommand(executablePath));
            return succeeded;
        }

        private static bool RunRegAdd(string keyPath, string valueSwitch, string valueData)
        {
            return RunRegAdd(keyPath, valueSwitch, null, valueData);
        }

        private static bool RunRegAdd(string keyPath, string valueSwitch, string? valueName, string valueData)
        {
            try
            {
                var startInfo = new ProcessStartInfo("reg.exe")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.ArgumentList.Add("add");
                startInfo.ArgumentList.Add(keyPath);
                startInfo.ArgumentList.Add(valueSwitch);
                if (!string.IsNullOrEmpty(valueName))
                {
                    startInfo.ArgumentList.Add(valueName);
                }
                startInfo.ArgumentList.Add("/t");
                startInfo.ArgumentList.Add("REG_SZ");
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add(valueData);
                startInfo.ArgumentList.Add("/f");

                using Process process = Process.Start(startInfo)!;
                return process.WaitForExit(2500) && process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to register classic context menu via reg.exe: {ex.Message}");
                return false;
            }
        }

        private static bool IsRunningPackaged()
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current.Id.FullName;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool SetValueIfDifferent(RegistryKey key, string valueName, object expectedValue, RegistryValueKind valueKind)
        {
            object? existingValue = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (existingValue != null)
            {
                try
                {
                    bool kindMatches = key.GetValueKind(valueName) == valueKind;
                    if (kindMatches && RegistryValuesEqual(existingValue, expectedValue))
                    {
                        return false;
                    }
                }
                catch
                {
                }
            }

            key.SetValue(valueName, expectedValue, valueKind);
            return true;
        }

        private static bool RegistryValuesEqual(object existingValue, object expectedValue)
        {
            if (existingValue is byte[] existingBytes && expectedValue is byte[] expectedBytes)
            {
                return existingBytes.SequenceEqual(expectedBytes);
            }

            return string.Equals(
                Convert.ToString(existingValue),
                Convert.ToString(expectedValue),
                StringComparison.Ordinal);
        }

        private static string? ReadUserChoiceProgId(string extension)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice");
                return key?.GetValue("ProgId") as string;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildOpenCommand(string executablePath)
        {
            return $"{Quote(executablePath)} \"%1\"";
        }

        private static string Quote(string value)
        {
            return $"\"{value}\"";
        }

        private static void NotifyShellAssociationChanged()
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;
    }
}
