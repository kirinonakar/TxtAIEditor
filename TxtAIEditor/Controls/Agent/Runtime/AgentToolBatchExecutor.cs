using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed record AgentToolCallResult(
        string Name,
        JsonElement Arguments,
        string Result,
        string ResultForTranscript,
        bool InterruptedWait,
        string NormalizedName);

    internal sealed record AgentToolBatchResult(IReadOnlyList<AgentToolCallResult> Results, bool StopAfterLoopGuard);

    internal sealed class AgentToolBatchExecutor
    {
        private const string SupersededToolResult = "Tool failed: this call was skipped because a new orchestrator instruction arrived.";

        private readonly AgentFileToolService _fileTools;
        private readonly AgentFileToolController _fileToolController;
        private readonly AgentToolExecutionController _toolExecutionController;
        private readonly AgentToolExecutionGate _toolExecutionSessionGate;
        private readonly AgentSessionEditController _sessionEditController;
        private readonly AgentRunOutputController _runOutputController;
        private readonly AgentRunTranscriptService _runTranscriptService;
        private readonly AgentUiDispatcher _uiDispatcher;
        private readonly AsyncLocal<AgentRunContext?> _activeToolRunContext = new();

        public AgentToolBatchExecutor(
            AgentFileToolService fileTools,
            AgentFileToolController fileToolController,
            AgentToolExecutionController toolExecutionController,
            AgentToolExecutionGate toolExecutionSessionGate,
            AgentSessionEditController sessionEditController,
            AgentRunOutputController runOutputController,
            AgentRunTranscriptService runTranscriptService,
            AgentUiDispatcher uiDispatcher)
        {
            _fileTools = fileTools;
            _fileToolController = fileToolController;
            _toolExecutionController = toolExecutionController;
            _toolExecutionSessionGate = toolExecutionSessionGate;
            _sessionEditController = sessionEditController;
            _runOutputController = runOutputController;
            _runTranscriptService = runTranscriptService;
            _uiDispatcher = uiDispatcher;
        }

        public AgentRunContext? ActiveRunContext => _activeToolRunContext.Value;

        public async Task<AgentToolBatchResult> ExecuteAsync(
            AgentOpenSessionState activeOpenSession,
            AgentRunContext runContext,
            IReadOnlyList<AgentToolCallParser.ToolCallInfo> calls,
            AgentStepRetryPolicy retries,
            Dictionary<string, int> toolInvocationCounts,
            AgentSubagentCoordinator? orchestration,
            Task followUpRequested)
        {
            bool isSubagent = activeOpenSession.Role == AgentSessionRole.Subagent;
            bool planningMode = runContext.IsPlanningMode;
            CancellationToken cancellationToken = runContext.CancellationToken;
            bool stopAfterLoopGuard = false;
            var toolCalls = OrderToolCallsForExecution(calls, runContext.WorkspaceRoot);
            if (planningMode && orchestration != null)
            {
                toolCalls = toolCalls.OrderBy(call => NormalizeToolName(call.ToolName) == "make_plan").ToList();
            }
            var results = new List<AgentToolCallResult>();
            bool hasWebSearchCallInBatch = false;
            bool waitedForSubagentInBatch = false;

            int parallelStart = 0;
            string[] parallelResults = [];
            for (int toolIndex = 0; toolIndex < toolCalls.Count; toolIndex++)
            {
                runContext.ToolCancellationToken.ThrowIfCancellationRequested();

                if (toolIndex >= parallelStart + parallelResults.Length)
                {
                    parallelStart = toolIndex;
                    parallelResults = [];
                    if (!isSubagent || !followUpRequested.IsCompleted)
                    {
                        var reads = GetIndependentReads(toolCalls, toolIndex, toolInvocationCounts);
                        if (reads.Count > 1)
                        {
                            parallelResults = await Task.WhenAll(reads.Select(read => ExecuteReadToolAsync(
                                runContext, read.ToolName, NormalizeToolName(read.ToolName), read.Arguments,
                                isSubagent, followUpRequested, cancellationToken, trackFilePath: false)));
                        }
                    }
                }

                var tc = toolCalls[toolIndex];
                string currentToolName = tc.ToolName;
                JsonElement currentArguments = tc.Arguments;

                string normalizedToolName = NormalizeToolName(currentToolName);
                if (normalizedToolName == "apply_patch")
                {
                    currentArguments = SanitizeApplyPatchArguments(currentArguments);
                }

                if (toolCalls.Count > 1 && normalizedToolName == "web_search")
                {
                    if (hasWebSearchCallInBatch)
                    {
                        int delayMilliseconds = Random.Shared.Next(500, 2001);
                        await Task.Delay(delayMilliseconds, cancellationToken);
                    }

                    hasWebSearchCallInBatch = true;
                }

                string toolInvocationKey = BuildToolInvocationKey(normalizedToolName, currentArguments);
                toolInvocationCounts[toolInvocationKey] = toolInvocationCounts.TryGetValue(toolInvocationKey, out int prevCount) ? prevCount + 1 : 1;
                int currentInvocationCount = toolInvocationCounts[toolInvocationKey];
                string toolResult;
                bool interruptedSubagentWait = false;
                retries.Reset(AgentRetryReason.ToolCallFormat);

                if (toolIndex - parallelStart < parallelResults.Length)
                {
                    toolResult = parallelResults[toolIndex - parallelStart];
                    _toolExecutionController.TrackFileToolPath(normalizedToolName, currentArguments, toolResult);
                }
                else if (isSubagent && followUpRequested.IsCompleted)
                {
                    toolResult = SupersededToolResult;
                }
                else if (AgentSubagentCoordinator.IsCoordinationTool(normalizedToolName))
                {
                    bool isWait = normalizedToolName == AgentSubagentCoordinator.WaitTool;
                    if (isWait && waitedForSubagentInBatch)
                    {
                        toolResult = "Wait deferred. Review the subagent result already returned in this response before waiting again.";
                    }
                    else
                    {
                        toolResult = orchestration != null
                            ? await orchestration.ExecuteAsync(normalizedToolName, currentArguments, followUpRequested)
                            : await ExecuteSubagentProgressAsync(runContext, normalizedToolName, currentArguments);
                        waitedForSubagentInBatch |= isWait;
                        interruptedSubagentWait = isWait && toolResult == AgentSubagentCoordinator.WaitInterruptedResult;
                    }
                }
                else if (isSubagent && !AgentSubagentCoordinator.IsWorkerToolAvailable(normalizedToolName, runContext.IsPlanResearch))
                {
                    toolResult = $"Tool failed: {normalizedToolName} is unavailable for this subagent run.";
                }
                else if (planningMode && normalizedToolName == "make_plan" && orchestration != null &&
                    (waitedForSubagentInBatch || orchestration.NeedsModelReview))
                {
                    toolResult = "make_plan failed: review the subagent events before submitting the revised plan.";
                    if (!waitedForSubagentInBatch)
                    {
                        await orchestration.WaitForEventAsync(followUpRequested);
                        interruptedSubagentWait = followUpRequested.IsCompleted;
                    }
                }
                else if (!planningMode && normalizedToolName == "make_plan")
                {
                    toolResult = "make_plan failed: this tool is only available when planning mode is enabled.";
                }
                else if (IsPlanningMutationBlocked(runContext, normalizedToolName, currentArguments))
                {
                    toolResult = BuildPlanningMutationBlockedMessage(runContext);
                }
                else if (_toolExecutionController.IsFileRead(normalizedToolName) || _toolExecutionController.IsWebRead(normalizedToolName))
                {
                    toolResult = await ExecuteReadToolAsync(runContext, currentToolName, normalizedToolName,
                        currentArguments, isSubagent, followUpRequested, cancellationToken);
                }
                else
                {
                    string[] lockPaths = _toolExecutionController.GetFileMutationLockPaths(normalizedToolName, ref currentArguments);
                    toolResult = await ExecuteWithEditRecordingAsync(
                        activeOpenSession, runContext, currentToolName, normalizedToolName,
                        currentArguments, lockPaths, isSubagent, followUpRequested);

                    if (IsSuccessfulToolResult(toolResult) &&
                        (IsMutatingTool(normalizedToolName) || normalizedToolName == "run_powershell"))
                    {
                        toolInvocationCounts.Clear();
                    }
                }

                string toolResultForTranscript = toolResult;
                if (!interruptedSubagentWait && IsSuccessfulToolResult(toolResult))
                {
                    toolResultForTranscript = $"{toolResult}\n\n[Tool execution status: success.]";
                    if (normalizedToolName == "read_file" && currentInvocationCount >= 4)
                    {
                        toolResultForTranscript += $"\n\n[Loop guard] You have called read_file with the exact same parameters {currentInvocationCount} times in this request. Do not repeat this tool call meaninglessly. Stop and think carefully about the information already retrieved, choose a different next action, or write your final answer.";
                        if (currentInvocationCount >= 6)
                        {
                            stopAfterLoopGuard = true;
                        }
                    }
                }

                toolResultForTranscript = _runTranscriptService.AddToolTimingNote(
                    normalizedToolName,
                    toolResultForTranscript,
                    toolResult);

                results.Add(new AgentToolCallResult(
                    currentToolName, currentArguments, toolResult, toolResultForTranscript, interruptedSubagentWait, normalizedToolName));

                if (interruptedSubagentWait || stopAfterLoopGuard ||
                    (!IsSuccessfulToolResult(toolResult) && toolCalls.Count == 1))
                {
                    break;
                }
            }

            return new AgentToolBatchResult(results, stopAfterLoopGuard);
        }

        private async Task<string> ExecuteWithEditRecordingAsync(
            AgentOpenSessionState activeOpenSession,
            AgentRunContext runContext,
            string toolName,
            string normalizedToolName,
            JsonElement arguments,
            string[] lockPaths,
            bool isSubagent,
            Task followUpRequested)
        {
            using IDisposable lease = lockPaths.Length > 0
                ? await _toolExecutionSessionGate.EnterFilesAsync(lockPaths, runContext.CancellationToken)
                : await _toolExecutionSessionGate.EnterExclusiveAsync(runContext.CancellationToken);
            runContext.ToolCancellationToken.ThrowIfCancellationRequested();
            AgentRunWorkspaceResolver.VerifyPinnedWorkspace(runContext);
            _activeToolRunContext.Value = runContext;
            try
            {
                await _uiDispatcher.RunAsync(() => _sessionEditController.BeginToolRecording(runContext.SessionId));
                try
                {
                    if (isSubagent && followUpRequested.IsCompleted)
                    {
                        return SupersededToolResult;
                    }

                    if (IsPlanningMutationBlocked(runContext, normalizedToolName, arguments))
                    {
                        return BuildPlanningMutationBlockedMessage(runContext);
                    }

                    return await _toolExecutionController.ExecuteAsync(
                        toolName, arguments, runContext.LlmSettings.LlmAgentVerbose, runContext.ToolCancellationToken);
                }
                finally
                {
                    await _uiDispatcher.RunAsync(() =>
                    {
                        runContext.SessionEdits = _sessionEditController.EndToolRecording(runContext.SessionId);
                        activeOpenSession.SessionEdits = runContext.SessionEdits.ToList();
                    });
                }
            }
            finally
            {
                _activeToolRunContext.Value = null;
            }
        }

        private bool IsPlanningMutationBlocked(AgentRunContext runContext, string toolName, JsonElement arguments)
        {
            return runContext.IsPlanningMode && IsMutatingTool(toolName) && toolName != "make_plan" &&
                !IsAllowedPlanRevisionEdit(runContext, toolName, arguments);
        }

        private List<AgentToolCallParser.ToolCallInfo> GetIndependentReads(
            IReadOnlyList<AgentToolCallParser.ToolCallInfo> calls, int start,
            Dictionary<string, int> invocationCounts)
        {
            var reads = new List<AgentToolCallParser.ToolCallInfo>();
            var seenFileReads = new HashSet<string>(StringComparer.Ordinal);
            for (int index = start; index < calls.Count && reads.Count < AgentToolExecutionGate.ParallelLimit; index++)
            {
                var call = calls[index];
                string name = NormalizeToolName(call.ToolName);
                if (!_toolExecutionController.CanRunInReadBatch(name))
                {
                    break;
                }
                if (name == "read_file")
                {
                    string key = BuildToolInvocationKey(name, call.Arguments);
                    // The normal result loop must handle repeated calls and stop at its loop guard.
                    if (!seenFileReads.Add(key) || invocationCounts.GetValueOrDefault(key) >= 5)
                    {
                        break;
                    }
                }
                reads.Add(call);
            }
            return reads;
        }

        private async Task<string> ExecuteReadToolAsync(
            AgentRunContext context, string toolName, string normalizedName, JsonElement arguments,
            bool isSubagent, Task followUpRequested, CancellationToken token, bool trackFilePath = true)
        {
            using IDisposable lease = _toolExecutionController.IsWebRead(normalizedName)
                ? await _toolExecutionSessionGate.EnterWebAsync(token)
                : await _toolExecutionSessionGate.EnterFileReadAsync(
                    _toolExecutionController.GetFileReadLockPath(normalizedName, arguments), token);
            context.ToolCancellationToken.ThrowIfCancellationRequested();
            AgentRunWorkspaceResolver.VerifyPinnedWorkspace(context);
            if (isSubagent && followUpRequested.IsCompleted)
            {
                return SupersededToolResult;
            }

            _activeToolRunContext.Value = context;
            try
            {
                return await _toolExecutionController.ExecuteAsync(
                    toolName, arguments, context.LlmSettings.LlmAgentVerbose, context.ToolCancellationToken, trackFilePath);
            }
            finally
            {
                _activeToolRunContext.Value = null;
            }
        }

        private List<AgentToolCallParser.ToolCallInfo> OrderToolCallsForExecution(
            IReadOnlyList<AgentToolCallParser.ToolCallInfo> toolCalls,
            string workspaceRoot)
        {
            var orderedToolCalls = toolCalls.ToList();
            var lineByIndex = new Dictionary<int, int>();
            var indexesByPath = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < orderedToolCalls.Count; index++)
            {
                if (!TryGetFileEditOrderingKey(
                        orderedToolCalls[index],
                        workspaceRoot,
                        out string canonicalPath,
                        out int startLine))
                {
                    continue;
                }

                lineByIndex[index] = startLine;
                if (!indexesByPath.TryGetValue(canonicalPath, out List<int>? indexes))
                {
                    indexes = new List<int>();
                    indexesByPath[canonicalPath] = indexes;
                }

                indexes.Add(index);
            }

            foreach (List<int> indexes in indexesByPath.Values)
            {
                if (indexes.Count < 2)
                {
                    continue;
                }

                List<AgentToolCallParser.ToolCallInfo> sortedCalls = indexes
                    .OrderByDescending(index => lineByIndex[index])
                    .ThenBy(index => index)
                    .Select(index => orderedToolCalls[index])
                    .ToList();

                for (int slot = 0; slot < indexes.Count; slot++)
                {
                    orderedToolCalls[indexes[slot]] = sortedCalls[slot];
                }
            }

            return orderedToolCalls;
        }

        private bool TryGetFileEditOrderingKey(
            AgentToolCallParser.ToolCallInfo toolCall,
            string workspaceRoot,
            out string canonicalPath,
            out int startLine)
        {
            canonicalPath = string.Empty;
            startLine = 0;

            string normalizedToolName = NormalizeToolName(toolCall.ToolName);
            if (!IsFileEditingTool(normalizedToolName))
            {
                return false;
            }

            int? candidateStartLine = normalizedToolName switch
            {
                "replace_range" => TryGetPositiveIntArgument(
                    toolCall.Arguments,
                    "startLine",
                    "start_line"),
                "search_replace" => TryGetPositiveIntArgument(
                    toolCall.Arguments,
                    "startLine",
                    "start_line") ?? TryGetPositiveIntArgument(
                    toolCall.Arguments,
                    "endLine",
                    "end_line"),
                "apply_patch" => GetHighestPatchStartLine(toolCall.Arguments),
                _ => null
            };

            if (!candidateStartLine.HasValue)
            {
                return false;
            }

            string path;
            try
            {
                path = _fileToolController.GetEditPathArgument(toolCall.Arguments);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                string root = string.IsNullOrWhiteSpace(workspaceRoot)
                    ? _fileTools.WorkspaceRoot
                    : workspaceRoot;
                string fullPath = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(root, path);
                canonicalPath = Path.GetFullPath(fullPath);
                startLine = candidateStartLine.Value;
                return startLine >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static int? TryGetPositiveIntArgument(JsonElement arguments, params string[] names)
        {
            foreach (string name in names)
            {
                if (TryGetIntArgument(arguments, name, out int value) && value > 0)
                {
                    return value;
                }
            }

            return null;
        }

        private static int? GetHighestPatchStartLine(JsonElement arguments)
        {
            string patch = GetFirstStringArgument(arguments, "patch", "patchText", "diff", "content");
            if (string.IsNullOrWhiteSpace(patch))
            {
                return null;
            }

            int highestStartLine = 0;
            bool foundHunk = false;
            foreach (Match match in Regex.Matches(patch, @"@@\s*-(\d+)", RegexOptions.CultureInvariant))
            {
                if (match.Groups.Count <= 1 ||
                    !int.TryParse(match.Groups[1].Value, out int startLine))
                {
                    continue;
                }

                foundHunk = true;
                highestStartLine = Math.Max(highestStartLine, startLine);
            }

            return foundHunk ? highestStartLine : null;
        }

        private bool IsAllowedPlanRevisionEdit(
            AgentRunContext runContext,
            string normalizedToolName,
            JsonElement arguments)
        {
            if (string.IsNullOrWhiteSpace(runContext.PendingPlanPath) ||
                !IsPlanRevisionEditTool(normalizedToolName))
            {
                return false;
            }

            string targetPath = GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path");
            try
            {
                _fileTools.VerifyWorkspaceWritePath(runContext.PendingPlanPath);
                string[] actualTargets = normalizedToolName == "apply_patch"
                    ? AgentFileEditToolService.GetPatchPaths(targetPath, GetFirstStringArgument(arguments, "patch", "patchText", "diff", "content"))
                    : [targetPath];
                return actualTargets.Length > 0 && actualTargets.All(path =>
                    AgentWorkspaceFileResolver.SamePhysicalPath(_fileTools.ResolveToolPath(path), runContext.PendingPlanPath));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string BuildPlanningMutationBlockedMessage(AgentRunContext runContext)
        {
            if (!string.IsNullOrWhiteSpace(runContext.PendingPlanPath))
            {
                return
                    "blocked: in this plan-revision run you may edit only the pending plan file with file edit tools: " +
                    runContext.PendingPlanPath +
                    ". Use apply_patch, search_replace, replace_range, overwrite_file, append_to_file, or insert_to_file with that path, then call make_plan with the path to request approval again.";
            }

            return
                "blocked: planning mode is plan-only and cannot run file/editor mutation tools. " +
                "Continue with safe inspection if needed, or write the detailed Markdown plan as the final answer.";
        }

        private async Task<string> ExecuteSubagentProgressAsync(AgentRunContext context, string name, JsonElement arguments)
        {
            if (name != AgentSubagentCoordinator.ReportProgressTool || context.ReportToOrchestrator == null)
            {
                return $"Unknown tool: {name}";
            }

            string message = GetStringArgument(arguments, "message").Trim();
            if (message.Length == 0)
            {
                return "Tool failed: message is required.";
            }

            string kind = GetStringArgument(arguments, "kind").Trim().ToLowerInvariant();
            if (kind.Length == 0)
            {
                kind = "progress";
            }
            if (kind is not ("progress" or "finding" or "blocker"))
            {
                return "Tool failed: kind must be progress, finding, or blocker.";
            }

            string outcome = await context.ReportToOrchestrator(message, kind);
            _ = _runOutputController.AppendSubagentProgressAsync(context, message);
            return outcome;
        }
    }
}
