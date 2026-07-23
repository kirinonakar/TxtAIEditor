using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class WslDistributionService
    {
        public async Task<IReadOnlyList<RemoteServerProfile>> GetInstalledProfilesAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                using Process process = new()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "wsl.exe",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.StartInfo.ArgumentList.Add("--list");
                process.StartInfo.ArgumentList.Add("--quiet");
                process.Start();

                await using MemoryStream output = new();
                Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                await copyTask;
                if (process.ExitCode != 0)
                {
                    return Array.Empty<RemoteServerProfile>();
                }

                string text = DecodeWslOutput(output.ToArray());
                return text
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Replace("\0", string.Empty).Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(name => CreateProfile(name))
                    .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<RemoteServerProfile>();
            }
        }

        public static RemoteServerProfile CreateProfile(
            string distributionName,
            string homePath = "/")
        {
            return new RemoteServerProfile
            {
                Id = CreateStableId(distributionName),
                Name = distributionName,
                ServerType = RemoteServerType.Wsl,
                Port = 0,
                UserName = NormalizeHomePath(homePath)
            };
        }

        public static async Task<string> GetHomePathAsync(
            string distributionName,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using Process process = new()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "wsl.exe",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.StartInfo.ArgumentList.Add("--distribution");
                process.StartInfo.ArgumentList.Add(distributionName);
                process.StartInfo.ArgumentList.Add("--exec");
                process.StartInfo.ArgumentList.Add("sh");
                process.StartInfo.ArgumentList.Add("-lc");
                process.StartInfo.ArgumentList.Add("printf %s \"$HOME\"");
                process.Start();

                await using MemoryStream output = new();
                Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                await copyTask;
                return process.ExitCode == 0
                    ? NormalizeHomePath(DecodeWslOutput(output.ToArray()))
                    : "/";
            }
            catch
            {
                return "/";
            }
        }

        private static string NormalizeHomePath(string homePath)
        {
            string normalized = (homePath ?? string.Empty)
                .Replace("\0", string.Empty)
                .Replace('\\', '/')
                .Trim();
            return string.IsNullOrWhiteSpace(normalized)
                ? "/"
                : "/" + normalized.Trim('/');
        }

        private static Guid CreateStableId(string distributionName)
        {
            byte[] hash = SHA256.HashData(
                Encoding.UTF8.GetBytes($"TxtAIEditor.WSL:{distributionName.ToUpperInvariant()}"));
            return new Guid(hash.AsSpan(0, 16));
        }

        private static string DecodeWslOutput(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            bool looksUtf16 = bytes.Length > 3 &&
                Enumerable.Range(1, Math.Min(bytes.Length, 64) / 2)
                    .Count(index => bytes[(index * 2) - 1] == 0) > 4;
            return looksUtf16
                ? Encoding.Unicode.GetString(bytes)
                : Encoding.UTF8.GetString(bytes);
        }
    }
}
