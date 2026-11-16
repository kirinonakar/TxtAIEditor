using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services.LLM;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentSubagentCoordinator : IDisposable
    {
        public const string ProgressMarker = "[Orchestrator progress] ";
        public const string ReportProgressTool = "report_progress";

        public static IReadOnlyList<LlmTool> Tools { get; } = (LlmTool[])
        [
            CreateTool("spawn_subagent", "Start an independent task in the background and return its agent ID immediately.",
                new { title = Text("Short session title"), instruction = Text("Task, scope, and expected result") }, "title", "instruction"),
            CreateTool("send_subagent_message", "Give a finished subagent another instruction in the same conversation.",
                new { agentId = Text("ID returned by spawn_subagent"), instruction = Text("Question or follow-up task") }, "agentId", "instruction"),
            CreateTool("subagent_status", "Get the status and latest progress of every subagent without waiting.", new { }),
            CreateTool("wait_subagent", "Wait for and collect findings, errors, and changed files. Omit agentId to collect all outstanding results.",
                new { agentId = Text("Optional subagent ID") }),
            CreateTool(ReportProgressTool, "Report a concise milestone, finding, or blocker.",
                new { message = Text("Progress update in the user's language") }, "message")
        ];

        public static LlmTool ProgressTool => Tools[^1];

        private readonly int _limit;
        private readonly Func<string, string, string, Func<string, Task>, CancellationToken, Task<AgentRunContext>> _run;
        private readonly Func<string, Task> _publishProgress;
        private readonly Func<string, string, string> _getString;
        private readonly CancellationTokenSource _cancellation;
        private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);
        private readonly List<string> _progressLog = new();

        public AgentSubagentCoordinator(
            int limit,
            Func<string, string, string, Func<string, Task>, CancellationToken, Task<AgentRunContext>> run,
            Func<string, Task> publishProgress,
            Func<string, string, string> getString,
            CancellationToken cancellationToken)
        {
            _limit = limit;
            _run = run;
            _publishProgress = publishProgress;
            _getString = getString;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public bool HasUncollectedResults => _workers.Values.Any(worker => !worker.Collected);

        public static bool IsCoordinationTool(string name) => Tools.Any(tool => tool.Name == name);

        public string BuildInstructions(bool planningMode) => $$"""
            [Orchestrator]
            You own the user's task, integration, verification, and final answer. Delegate tasks that benefit from independent work.
            You may create up to {{_limit}} subagent sessions for this run. Launch independent assignments before waiting so they can run concurrently.
            Give each subagent the needed context, expected result, and a distinct scope. Coordinate changes to shared files and check their results.
            Use subagent_status to see progress and blockers. Use wait_subagent to collect results and send_subagent_message for questions or follow-up work in the same session.
            Use report_progress in the user's language when starting a stage, receiving important findings, or encountering a blocker.
            Collect and assess all delegated results before finalizing or submitting a plan. {{(planningMode ? "Delegate investigation and plan review only." : "")}}
            For text tool calls, use the normal tool_call syntax with these names and JSON keys:
            spawn_subagent(title, instruction), send_subagent_message(agentId, instruction), subagent_status(), wait_subagent(optional agentId), report_progress(message).
            """;

        public static string BuildWorkerInstructions(bool planningMode) => """
            [Subagent]
            Your counterpart is the orchestrator. Treat each [Orchestrator]: message as an assignment or follow-up from that orchestrator.
            Work within its assigned scope and use report_progress(message) at meaningful checkpoints or when blocked.
            Return your answer to the orchestrator, including findings, changes, verified results, and remaining questions.
            Avoid unrelated changes and coordinate overlapping work through the orchestrator.
            """ + (planningMode ? "\nThis is plan research. Investigate and report findings without changing files, tabs, or external state." : "");

        public async Task<string> ExecuteAsync(string name, JsonElement arguments)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            string id = GetStringArgument(arguments, "agentId").Trim();
            string instruction = GetStringArgument(arguments, "instruction").Trim();
            switch (name)
            {
                case "spawn_subagent":
                    string title = GetStringArgument(arguments, "title").Trim();
                    if (title.Length == 0 || instruction.Length == 0)
                    {
                        return "Tool failed: title and instruction are required.";
                    }
                    if (_workers.Count >= _limit)
                    {
                        return $"Tool failed: the {_limit}-subagent limit has been reached. Reuse an existing subagent for follow-up work.";
                    }

                    var worker = new Worker(Guid.NewGuid().ToString(), AgentRunTextFormatter.TruncateUserPrompt(title));
                    _workers.Add(worker.Id, worker);
                    Start(worker, instruction, "AgentSubagentStartedFormat", "서브에이전트 작업 시작 · {0}");
                    return $"Started subagent {worker.Id} ({worker.Title}).";

                case "send_subagent_message":
                    if (!_workers.TryGetValue(id, out Worker? recipient) || instruction.Length == 0)
                    {
                        return "Tool failed: a valid agentId and an instruction are required.";
                    }
                    if (!recipient.Collected)
                    {
                        return "Tool failed: collect this subagent's current result with wait_subagent before sending another instruction.";
                    }

                    Start(recipient, instruction, "AgentSubagentFollowUpFormat", "서브에이전트 후속 작업 시작 · {0}");
                    return $"Sent a follow-up to subagent {recipient.Id} ({recipient.Title}).";

                case "subagent_status":
                    return _workers.Count == 0
                        ? "No subagents have been started."
                        : string.Join("\n", _workers.Values.Select(item =>
                            $"{item.Id} ({item.Title}): {(item.Run.IsCompletedSuccessfully ? item.Run.Result.Status : AgentRunStatus.Running)}. {item.LatestProgress}"));

                case "wait_subagent":
                    if (id.Length == 0)
                    {
                        return await CollectOutstandingAsync();
                    }
                    return _workers.TryGetValue(id, out Worker? requested)
                        ? await CollectAsync([requested])
                        : "Tool failed: unknown subagent ID.";

                case ReportProgressTool:
                    string message = GetStringArgument(arguments, "message").Trim();
                    if (message.Length == 0)
                    {
                        return "Tool failed: message is required.";
                    }
                    await PublishAsync(message);
                    return "Progress shared with the user.";

                default:
                    return $"Unknown tool: {name}";
            }
        }

        public Task<string> CollectOutstandingAsync() =>
            CollectAsync(_workers.Values.Where(worker => !worker.Collected).ToArray());

        private async Task<string> CollectAsync(Worker[] workers)
        {
            if (workers.Length == 0)
            {
                return "No outstanding subagent results.";
            }

            if (workers.Any(worker => !worker.Run.IsCompleted))
            {
                await PublishAsync(_getString("AgentSubagentWaiting", "서브에이전트 결과를 기다리는 중"));
            }

            WorkerResult[] results = await Task.WhenAll(workers.Select(worker => worker.Run)).WaitAsync(_cancellation.Token);
            foreach (Worker worker in workers)
            {
                worker.Collected = true;
            }

            return string.Join("\n\n", workers.Zip(results, (worker, result) =>
                $"{worker.Id} ({worker.Title}) · {result.Status}\n{result.Text}"));
        }

        private void Start(Worker worker, string instruction, string resource, string fallback)
        {
            worker.Collected = false;
            worker.LatestProgress = string.Empty;
            worker.Run = RunWorkerAsync(worker, instruction, resource, fallback);
        }

        private async Task<WorkerResult> RunWorkerAsync(Worker worker, string instruction, string resource, string fallback)
        {
            await Task.Yield();
            WorkerResult result;
            try
            {
                await PublishAsync(string.Format(_getString(resource, fallback), worker.Title));
                AgentRunContext context = await _run(worker.Id, worker.Title, instruction, async message =>
                {
                    worker.LatestProgress = message;
                    await PublishAsync(string.Format(
                        _getString("AgentSubagentProgressFormat", "{0} · {1}"), worker.Title, message));
                }, _cancellation.Token);

                string[] editedPaths = context.SessionEdits.Select(edit => edit.RelativePath).Distinct().ToArray();
                string changes = editedPaths.Length > 0 ? "\nChanged files: " + string.Join(", ", editedPaths) : string.Empty;
                string text = context.Status == AgentRunStatus.Failed ? context.FailureReason : context.LastAnswerText;
                result = new WorkerResult(context.Status, text + changes);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                result = new WorkerResult(AgentRunStatus.Stopped, string.Empty);
            }
            catch (Exception ex)
            {
                result = new WorkerResult(AgentRunStatus.Failed, ex.Message);
            }

            string status = result.Status switch
            {
                AgentRunStatus.Completed => _getString("AgentSubagentCompletedFormat", "서브에이전트 작업 완료 · {0}"),
                AgentRunStatus.Stopped => _getString("AgentSubagentStoppedFormat", "서브에이전트 작업 중단 · {0}"),
                _ => _getString("AgentSubagentFailedFormat", "서브에이전트 작업 실패 · {0}")
            };
            await PublishAsync(string.Format(status, worker.Title));
            return result;
        }

        private async Task PublishAsync(string message)
        {
            string line = FormatProgressLine(message);
            _progressLog.Add(ProgressMarker + line);
            await _publishProgress(line);
        }

        public static string FormatProgressLine(string message) =>
            string.Join(" ", message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

        public string DrainProgressLog()
        {
            if (_progressLog.Count == 0)
            {
                return string.Empty;
            }

            string log = Environment.NewLine + string.Join(Environment.NewLine, _progressLog) + Environment.NewLine;
            _progressLog.Clear();
            return log;
        }

        public async Task StopAsync()
        {
            _cancellation.Cancel();
            await Task.WhenAll(_workers.Values.Select(worker => worker.Run));
        }

        public void Dispose() => _cancellation.Dispose();

        private static object Text(string description) => new { type = "string", description };

        private static LlmTool CreateTool(string name, string description, object properties, params string[] required) =>
            new() { Name = name, Description = description, Parameters = new { type = "object", properties, required } };

        private sealed record WorkerResult(AgentRunStatus Status, string Text);

        private sealed class Worker(string id, string title)
        {
            public string Id { get; } = id;
            public string Title { get; } = title;
            public string LatestProgress { get; set; } = string.Empty;
            public bool Collected { get; set; }
            public Task<WorkerResult> Run { get; set; } = null!;
        }
    }
}
