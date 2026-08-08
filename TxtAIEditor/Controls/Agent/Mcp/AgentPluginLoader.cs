using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Controls
{
    internal sealed record AgentPluginLoadResult(
        string PluginName,
        string PluginRoot,
        IReadOnlyList<AgentMcpServer> Servers,
        IReadOnlyList<string> Warnings);

    internal static partial class AgentPluginLoader
    {
        internal const string PluginSchema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";
        internal const string McpSchema = "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json";

        private static readonly HashSet<string> ManifestFields = new(StringComparer.Ordinal)
        {
            "$schema", "name", "version", "description", "author", "homepage", "repository", "license", "keywords", "extensions"
        };

        private static readonly HashSet<string> McpTopLevelFields = new(StringComparer.Ordinal)
        {
            "$schema", "mcpServers"
        };

        private static readonly HashSet<string> StdioFields = new(StringComparer.Ordinal)
        {
            "type", "command", "args", "env", "cwd"
        };

        private static readonly HashSet<string> HttpFields = new(StringComparer.Ordinal)
        {
            "type", "url", "headers"
        };

        public static AgentPluginLoadResult Load(string selectedDirectory, string pluginDataBaseDirectory)
        {
            string pluginRoot = ResolveDirectory(selectedDirectory);
            string manifestPath = ResolveRequiredFile(pluginRoot, "plugin.json");
            var warnings = new List<string>();

            using JsonDocument manifestDocument = ParseJsonFile(manifestPath, "plugin.json");
            string pluginName = ValidateManifest(manifestDocument.RootElement, warnings);

            string mcpPath = Path.Combine(pluginRoot, "mcp.json");
            if (!File.Exists(mcpPath))
            {
                throw new InvalidDataException("mcp.json was not found in the Agent Plugin root.");
            }

            mcpPath = ResolveContainedFile(pluginRoot, mcpPath, "mcp.json");
            string pluginDataDirectory = Path.GetFullPath(Path.Combine(pluginDataBaseDirectory, pluginName));
            Directory.CreateDirectory(pluginDataDirectory);

            using JsonDocument mcpDocument = ParseJsonFile(mcpPath, "mcp.json");
            IReadOnlyList<AgentMcpServer> servers = ValidateAndReadMcp(
                mcpDocument.RootElement,
                pluginName,
                pluginRoot,
                pluginDataDirectory,
                warnings);

            if (servers.Count == 0)
            {
                throw new InvalidDataException("The Agent Plugin has no supported valid MCP server entries.");
            }

            return new AgentPluginLoadResult(pluginName, pluginRoot, servers, warnings);
        }

        internal static string ExpandRuntimeVariables(AgentMcpServer server, string value)
        {
            if (!server.IsAgentPlugin)
            {
                return value;
            }

            return PluginVariableRegex().Replace(value, match =>
                match.Groups[1].Value.Equals("ROOT", StringComparison.Ordinal)
                    ? server.AgentPluginRoot
                    : server.AgentPluginDataDirectory);
        }

        internal static string ResolveRuntimeCommand(AgentMcpServer server)
        {
            if (!server.IsAgentPlugin || !server.Command.StartsWith("./", StringComparison.Ordinal))
            {
                return server.Command;
            }

            string relative = server.Command.Substring(2).Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(server.AgentPluginRoot, relative));
            EnsureContained(server.AgentPluginRoot, candidate, "stdio command");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            return ResolveContainedFile(server.AgentPluginRoot, candidate, "stdio command");
        }

        internal static string ResolveRuntimeWorkingDirectory(AgentMcpServer server)
        {
            if (!server.IsAgentPlugin)
            {
                return server.WorkingDirectory;
            }

            string configured = string.IsNullOrWhiteSpace(server.WorkingDirectory)
                ? "${PLUGIN_ROOT}"
                : server.WorkingDirectory;
            string permittedRoot = configured.StartsWith("${PLUGIN_DATA}", StringComparison.Ordinal)
                ? server.AgentPluginDataDirectory
                : server.AgentPluginRoot;
            string expanded = configured.StartsWith("./", StringComparison.Ordinal)
                ? Path.Combine(server.AgentPluginRoot, configured.Substring(2).Replace('/', Path.DirectorySeparatorChar))
                : ExpandRuntimeVariables(server, configured).Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(expanded);
            EnsureContained(permittedRoot, candidate, "cwd");

            var directory = new DirectoryInfo(candidate);
            if (directory.Exists)
            {
                candidate = ResolvePathFollowingLinks(permittedRoot, candidate);
                EnsureContained(permittedRoot, candidate, "cwd");
            }

            return candidate;
        }

        private static string ValidateManifest(JsonElement manifest, List<string> warnings)
        {
            if (manifest.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("plugin.json must contain a JSON object.");
            }

            string schema = ReadRequiredString(manifest, "$schema", "plugin.json");
            if (!schema.Equals(PluginSchema, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsupported Agent Plugins schema: {schema}");
            }

            string name = ReadRequiredString(manifest, "name", "plugin.json");
            if (name.Length > 64 || !PluginNameRegex().IsMatch(name))
            {
                throw new InvalidDataException("plugin.json contains an invalid plugin name.");
            }

            foreach (JsonProperty property in manifest.EnumerateObject())
            {
                if (!ManifestFields.Contains(property.Name))
                {
                    warnings.Add($"plugin.json: ignored unknown field '{property.Name}'.");
                }
            }

            ValidateOptionalString(manifest, "version", "plugin.json");
            ValidateOptionalString(manifest, "description", "plugin.json");
            ValidateOptionalString(manifest, "homepage", "plugin.json");
            ValidateOptionalString(manifest, "repository", "plugin.json");
            ValidateOptionalString(manifest, "license", "plugin.json");
            ValidateAuthor(manifest);
            ValidateKeywords(manifest);

            if (manifest.TryGetProperty("extensions", out JsonElement extensions) &&
                extensions.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("plugin.json: ignored non-object 'extensions' field.");
            }

            return name;
        }

        private static IReadOnlyList<AgentMcpServer> ValidateAndReadMcp(
            JsonElement root,
            string pluginName,
            string pluginRoot,
            string pluginDataDirectory,
            List<string> warnings)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("mcp.json must contain a JSON object.");
            }

            string schema = ReadRequiredString(root, "$schema", "mcp.json");
            if (!schema.Equals(McpSchema, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsupported Agent Plugins MCP schema: {schema}");
            }

            string? unknownTopLevel = root.EnumerateObject()
                .Select(property => property.Name)
                .FirstOrDefault(name => !McpTopLevelFields.Contains(name));
            if (unknownTopLevel != null)
            {
                throw new InvalidDataException($"mcp.json contains unknown top-level field '{unknownTopLevel}'.");
            }

            if (!root.TryGetProperty("mcpServers", out JsonElement mcpServers) ||
                mcpServers.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("mcp.json must contain an 'mcpServers' object.");
            }

            var servers = new List<AgentMcpServer>();
            foreach (JsonProperty property in mcpServers.EnumerateObject())
            {
                try
                {
                    servers.Add(ReadServer(property.Name, property.Value, pluginName, pluginRoot, pluginDataDirectory));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"mcp.json: skipped server '{property.Name}': {ex.Message}");
                }
            }

            return servers;
        }

        private static AgentMcpServer ReadServer(
            string serverName,
            JsonElement element,
            string pluginName,
            string pluginRoot,
            string pluginDataDirectory)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("server entry must be an object.");
            }

            string type = ReadRequiredString(element, "type", $"server '{serverName}'");
            string displayName = $"{pluginName}: {serverName}";
            if (type.Equals("stdio", StringComparison.Ordinal))
            {
                EnsureOnlyFields(element, StdioFields);
                string command = ReadRequiredString(element, "command", $"server '{serverName}'");
                command = ValidateCommand(command, pluginRoot);
                List<string> arguments = ReadOptionalStringArray(element, "args");
                Dictionary<string, string> environment = ReadOptionalStringObject(element, "env");
                if (environment.ContainsKey("PLUGIN_ROOT") || environment.ContainsKey("PLUGIN_DATA"))
                {
                    throw new InvalidDataException("PLUGIN_ROOT and PLUGIN_DATA are reserved environment variables.");
                }

                string workingDirectory = element.TryGetProperty("cwd", out JsonElement cwdElement)
                    ? ReadString(cwdElement, "cwd")
                    : "${PLUGIN_ROOT}";
                ValidateWorkingDirectory(workingDirectory, pluginRoot, pluginDataDirectory);

                return new AgentMcpServer
                {
                    Name = displayName,
                    Transport = AgentMcpTransportTypes.Stdio,
                    Command = command,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    Environment = environment,
                    AgentPluginName = pluginName,
                    AgentPluginServerName = serverName,
                    AgentPluginRoot = pluginRoot,
                    AgentPluginDataDirectory = pluginDataDirectory
                };
            }

            if (type.Equals("streamable-http", StringComparison.Ordinal))
            {
                EnsureOnlyFields(element, HttpFields);
                string endpoint = ReadRequiredString(element, "url", $"server '{serverName}'");
                ValidateRemoteUrl(endpoint);
                return new AgentMcpServer
                {
                    Name = displayName,
                    Transport = AgentMcpTransportTypes.Http,
                    Endpoint = endpoint,
                    Headers = ReadOptionalStringObject(element, "headers"),
                    AgentPluginName = pluginName,
                    AgentPluginServerName = serverName,
                    AgentPluginRoot = pluginRoot,
                    AgentPluginDataDirectory = pluginDataDirectory
                };
            }

            if (type.Equals("sse", StringComparison.Ordinal))
            {
                throw new InvalidDataException("legacy HTTP+SSE transport is not supported.");
            }

            throw new InvalidDataException($"unknown transport '{type}'.");
        }

        private static string ValidateCommand(string command, string pluginRoot)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new InvalidDataException("stdio command cannot be blank.");
            }

            if (command.StartsWith("./", StringComparison.Ordinal))
            {
                string relative = command.Substring(2).Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(relative))
                {
                    throw new InvalidDataException("stdio command path is empty.");
                }

                string fullPath = Path.GetFullPath(Path.Combine(pluginRoot, relative));
                EnsureContained(pluginRoot, fullPath, "stdio command");
                return command;
            }

            if (Path.IsPathRooted(command) ||
                command.Contains('/') ||
                command.Contains('\\'))
            {
                throw new InvalidDataException("stdio command must be a bare executable name or a './' plugin-relative path.");
            }

            return command;
        }

        private static void ValidateWorkingDirectory(string cwd, string pluginRoot, string pluginDataDirectory)
        {
            string expanded;
            string permittedRoot;
            if (cwd.StartsWith("./", StringComparison.Ordinal))
            {
                permittedRoot = pluginRoot;
                expanded = Path.Combine(pluginRoot, cwd.Substring(2).Replace('/', Path.DirectorySeparatorChar));
            }
            else if (cwd.Equals("${PLUGIN_ROOT}", StringComparison.Ordinal) ||
                cwd.StartsWith("${PLUGIN_ROOT}/", StringComparison.Ordinal))
            {
                permittedRoot = pluginRoot;
                expanded = pluginRoot + cwd.Substring("${PLUGIN_ROOT}".Length).Replace('/', Path.DirectorySeparatorChar);
            }
            else if (cwd.Equals("${PLUGIN_DATA}", StringComparison.Ordinal) ||
                cwd.StartsWith("${PLUGIN_DATA}/", StringComparison.Ordinal))
            {
                permittedRoot = pluginDataDirectory;
                expanded = pluginDataDirectory + cwd.Substring("${PLUGIN_DATA}".Length).Replace('/', Path.DirectorySeparatorChar);
            }
            else
            {
                throw new InvalidDataException("cwd must start with './', '${PLUGIN_ROOT}', or '${PLUGIN_DATA}'.");
            }

            EnsureContained(permittedRoot, Path.GetFullPath(expanded), "cwd");
        }

        private static void ValidateRemoteUrl(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new InvalidDataException("remote URL must be an absolute HTTP(S) URL without user information or a fragment.");
            }

            bool isLoopback = uri.IsLoopback ||
                uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            if (!isLoopback && uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("non-loopback remote MCP URLs must use HTTPS.");
            }
        }

        private static string ResolveDirectory(string path)
        {
            string fullPath = Path.GetFullPath(path);
            var directory = new DirectoryInfo(fullPath);
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException(fullPath);
            }

            FileSystemInfo? resolved = directory.ResolveLinkTarget(returnFinalTarget: true);
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved?.FullName ?? directory.FullName));
        }

        private static string ResolveRequiredFile(string pluginRoot, string fileName)
        {
            string path = Path.Combine(pluginRoot, fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"{fileName} was not found in the Agent Plugin root.", path);
            }

            return ResolveContainedFile(pluginRoot, path, fileName);
        }

        private static string ResolveContainedFile(string pluginRoot, string path, string label)
        {
            string resolvedPath = ResolvePathFollowingLinks(pluginRoot, Path.GetFullPath(path));
            EnsureContained(pluginRoot, resolvedPath, label);
            return resolvedPath;
        }

        private static string ResolvePathFollowingLinks(string root, string candidate)
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string normalizedCandidate = Path.GetFullPath(candidate);
            EnsureContained(normalizedRoot, normalizedCandidate, "path");

            string relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
            string current = normalizedRoot;
            foreach (string segment in relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo? info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current)
                        ? new FileInfo(current)
                        : null;
                if (info == null)
                {
                    continue;
                }

                FileSystemInfo? resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                current = Path.GetFullPath(resolved?.FullName ?? info.FullName);
            }

            return Path.GetFullPath(current);
        }

        private static void EnsureContained(string permittedRoot, string candidate, string label)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(permittedRoot));
            string fullCandidate = Path.GetFullPath(candidate);
            if (!fullCandidate.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !fullCandidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{label} resolves outside its permitted directory.");
            }
        }

        private static JsonDocument ParseJsonFile(string path, string label)
        {
            try
            {
                return JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{label} is not valid JSON: {ex.Message}", ex);
            }
        }

        private static string ReadRequiredString(JsonElement element, string name, string context)
        {
            if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"{context} requires string field '{name}'.");
            }

            string result = value.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(result))
            {
                throw new InvalidDataException($"{context} field '{name}' cannot be empty.");
            }

            return result;
        }

        private static string ReadString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"'{name}' must be a string.");
            }

            return element.GetString() ?? string.Empty;
        }

        private static void ValidateOptionalString(JsonElement element, string name, string context)
        {
            if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"{context} field '{name}' must be a string.");
            }
        }

        private static void ValidateAuthor(JsonElement manifest)
        {
            if (!manifest.TryGetProperty("author", out JsonElement author))
            {
                return;
            }

            if (author.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("plugin.json field 'author' must be an object.");
            }

            var allowed = new HashSet<string>(StringComparer.Ordinal) { "name", "email", "url" };
            EnsureOnlyFields(author, allowed);
            foreach (JsonProperty property in author.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"plugin.json author field '{property.Name}' must be a string.");
                }
            }
        }

        private static void ValidateKeywords(JsonElement manifest)
        {
            if (!manifest.TryGetProperty("keywords", out JsonElement keywords))
            {
                return;
            }

            if (keywords.ValueKind != JsonValueKind.Array ||
                keywords.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                throw new InvalidDataException("plugin.json field 'keywords' must be an array of strings.");
            }
        }

        private static void EnsureOnlyFields(JsonElement element, HashSet<string> allowed)
        {
            string? unknown = element.EnumerateObject()
                .Select(property => property.Name)
                .FirstOrDefault(name => !allowed.Contains(name));
            if (unknown != null)
            {
                throw new InvalidDataException($"unknown field '{unknown}'.");
            }
        }

        private static List<string> ReadOptionalStringArray(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                return new List<string>();
            }

            if (value.ValueKind != JsonValueKind.Array ||
                value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                throw new InvalidDataException($"'{name}' must be an array of strings.");
            }

            return value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();
        }

        private static Dictionary<string, string> ReadOptionalStringObject(JsonElement element, string name)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                return result;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"'{name}' must be an object with string values.");
            }

            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"'{name}.{property.Name}' must be a string.");
                }

                result[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            return result;
        }

        [GeneratedRegex("^(?!.*(?:--|\\.\\.))[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$")]
        private static partial Regex PluginNameRegex();

        [GeneratedRegex("\\$\\{PLUGIN_(ROOT|DATA)\\}")]
        private static partial Regex PluginVariableRegex();
    }
}
