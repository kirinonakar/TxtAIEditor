using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal enum AgentSessionRole { Default, Orchestrator, Subagent }

    internal static class AgentSessionLabels
    {
        public const string OrchestratorPrompt = "[Orchestrator]:";

        public static string Prefix(AgentSessionRole role) => role switch
        {
            AgentSessionRole.Orchestrator => "[O] ",
            AgentSessionRole.Subagent => "[S] ",
            _ => string.Empty
        };
    }

    internal sealed class AgentOpenSessionState
    {
        private sealed record SessionBody(string PromptText, string OutputText, string ActivityText,
            string SessionHistoryText, string ModelSessionHistoryText, string LastAnswerText,
            List<AgentAttachmentState> Attachments);

        private AgentSessionSpill<SessionBody>? _storedBody;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public AgentSessionRole Role { get; set; }
        public AgentSessionSelections Selections { get; set; } = new();
        public string? OrchestratorSessionId { get; set; }
        public bool UseSubagents { get; set; }
        public string PromptText { get; set; } = string.Empty;
        public string OutputText { get; set; } = string.Empty;
        public bool NeedsHistoryFormatting { get; set; }
        public string ActivityText { get; set; } = string.Empty;
        public string SessionHistoryText { get; set; } = string.Empty;
        public string ModelSessionHistoryText { get; set; } = string.Empty;
        public string LastAnswerText { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
        public string? ApprovedPlanWorkspacePin { get; set; }
        // Saved plan awaiting approval; cleared when the plan is approved.
        public string PendingPlanPath { get; set; } = string.Empty;
        public double SessionHistoryTokenCount { get; set; }
        public double CurrentRunTranscriptTokens { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<AgentAttachmentState> Attachments { get; set; } = new();
        public List<AgentFileEditPreview> SessionEdits { get; set; } = new();
        public AgentSessionRewindStack RewindSnapshots { get; } = new();
        public bool IsRunning { get; set; }
        public bool IsRewinding { get; set; }
        public int CompletedNotificationCount { get; set; }
        public bool ThinkingLineActive { get; set; }
        public int ThinkingLineStart { get; set; }
        public string ThinkingLineTimestamp { get; set; } = string.Empty;
        public string ThinkingLinePrefix { get; set; } = string.Empty;
        public Func<string>? LiveThinkingLabel { get; set; }
        public EditorSettings? LlmSettings { get; set; }
        public AgentHistoryStore.OpenHistorySession? UnloadedHistory { get; set; }

        public bool IsParked => _storedBody != null;

        public async Task ParkBodyAsync(Func<bool> canPark)
        {
            var body = new SessionBody(PromptText, OutputText, ActivityText,
                SessionHistoryText, ModelSessionHistoryText, LastAnswerText, Attachments.ToList());
            var stored = await Task.Run(() => AgentSessionSpill<SessionBody>.Write(body));
            if (!canPark())
            {
                stored.Dispose();
                return;
            }

            _storedBody = stored;
            PromptText = string.Empty;
            OutputText = string.Empty;
            ActivityText = string.Empty;
            SessionHistoryText = string.Empty;
            ModelSessionHistoryText = string.Empty;
            LastAnswerText = string.Empty;
            Attachments = new();
            SessionEdits = new();
        }

        public void RestoreBody()
        {
            if (_storedBody == null)
            {
                return;
            }

            var body = _storedBody.Read();
            PromptText = body.PromptText;
            OutputText = body.OutputText;
            ActivityText = body.ActivityText;
            SessionHistoryText = body.SessionHistoryText;
            ModelSessionHistoryText = body.ModelSessionHistoryText;
            LastAnswerText = body.LastAnswerText;
            Attachments = body.Attachments;
            DiscardStoredBody();
        }

        public void DiscardStoredBody()
        {
            _storedBody?.Dispose();
            _storedBody = null;
        }

        public void ReleaseUnloadedHistory()
        {
            UnloadedHistory?.Dispose();
            UnloadedHistory = null;
        }
    }

    internal enum AgentRunStatus { Running, Completed, Stopped, Failed }

    internal sealed class AgentRunContext
    {
        public AgentRunStatus Status { get; set; } = AgentRunStatus.Running;
        public AgentRequestTokenCalibration RequestTokenCalibration { get; } = new();
        public string SessionId { get; set; } = string.Empty;
        public AgentSessionSelections Selections { get; init; } = new();
        public CancellationTokenSource? Cancellation { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public CancellationToken ParentCancellationToken { get; set; }
        public CancellationToken ToolCancellationToken =>
            ParentCancellationToken.IsCancellationRequested ? ParentCancellationToken : CancellationToken;
        public StringBuilder SessionHistory { get; } = new();
        public StringBuilder ModelSessionHistory { get; } = new();
        public StringBuilder RetryDebugHistory { get; } = new();
        public double SessionHistoryTokenCount { get; set; }
        public double CurrentRunTranscriptTokens { get; set; }
        // Tokens of the captured request, measured before each model call (after any
        // context compression) with the same estimator as the compression threshold.
        // 0 means no measurement has been taken for this run yet.
        public double ActualRequestTokens { get; set; }
        // CurrentRunTranscriptTokens when ActualRequestTokens was measured. The difference
        // since then is added on top so the display keeps tracking the growing transcript.
        public double ActualRequestTokensBase { get; set; }
        public List<AgentAttachmentState> Attachments { get; set; } = new();
        public List<LlmMessageAttachment> ImageToolAttachments { get; } = new();
        public bool VisionFallbackPending { get; set; }
        public string PendingVisionFallbackContext { get; set; } = string.Empty;
        public List<AgentFileEditPreview> SessionEdits { get; set; } = new();
        public int InitialEditCount { get; init; }
        public AgentSessionRewindSnapshot? RewindSnapshot { get; set; }
        public bool StreamToTabActive { get; set; }
        public bool StreamToTab { get; set; }
        public string? StreamToTabTargetTabId { get; set; }
        public string LastAnswerText { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
        public string? PinnedWorkspaceRoot { get; set; }
        public EditorSettings LlmSettings { get; set; } = new();
        public string ApiType { get; set; } = string.Empty;
        public string StreamingResponseText { get; set; } = string.Empty;
        public string StreamingReasoningText { get; set; } = string.Empty;
        // Estimated tokens of the reasoning text that is still streaming and is not yet
        // committed to CurrentRunTranscriptTokens. Display-only overlay so the panel
        // token count keeps updating while the model is thinking.
        public double InFlightReasoningTokens { get; set; }
        // Includes answer text, inline thinking and tool-call text until the response is
        // committed to the transcript. Keep this separate from native reasoning tokens.
        public double InFlightResponseTokens { get; set; }
        public bool IsPlanningMode { get; set; }
        public bool IsPlanResearch { get; set; }
        public Func<string, string, Task<string>>? ReportToOrchestrator { get; set; }
        public string FailureReason { get; set; } = string.Empty;
        public bool HasEnabledSkills { get; set; }
        public bool HasEnabledMcp { get; set; }
        public string OriginalUserInstruction { get; set; } = string.Empty;
        public string PlanWorkspaceContext { get; set; } = string.Empty;
        public string PlanSelectionContext { get; set; } = string.Empty;
        public string GeneratedPlanPath { get; set; } = string.Empty;
        // Plan file that may be revised in this run (session pending plan at run start).
        public string PendingPlanPath { get; set; } = string.Empty;
        public bool PlanResubmittedForApproval { get; set; }
        public AgentFollowUpQueue FollowUps { get; set; } = new();
    }

    internal sealed class AgentFollowUpQueue
    {
        private readonly object _gate = new();
        private TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _prompt;
        private bool _closed;

        public bool TryQueue(string prompt)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return false;
                }
                _prompt = _prompt == null
                    ? prompt
                    : _prompt + Environment.NewLine + Environment.NewLine + prompt;
                _requested.TrySetResult();
                return true;
            }
        }

        public string? Take(out Task requested)
        {
            lock (_gate)
            {
                string? prompt = _prompt;
                if (prompt != null)
                {
                    _prompt = null;
                    _requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                requested = _requested.Task;
                return prompt;
            }
        }

        public bool TryCloseIfEmpty()
        {
            lock (_gate)
            {
                if (_prompt != null)
                {
                    return false;
                }
                _closed = true;
                return true;
            }
        }

        public string? Close()
        {
            lock (_gate)
            {
                _closed = true;
                return _prompt;
            }
        }
    }

    internal sealed class AgentSessionRewindSnapshot
    {
        public List<string> SubagentSessionIds { get; set; } = new();
        public Dictionary<string, HashSet<long>> SubagentInitialEditOrders { get; set; } = new(StringComparer.Ordinal);
        public string Title { get; set; } = string.Empty;
        public string PromptText { get; set; } = string.Empty;
        public string OutputText { get; set; } = string.Empty;
        public string ActivityText { get; set; } = string.Empty;
        public string SessionHistoryText { get; set; } = string.Empty;
        public string ModelSessionHistoryText { get; set; } = string.Empty;
        public string LastAnswerText { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
        public string PendingPlanPath { get; set; } = string.Empty;
        public double SessionHistoryTokenCount { get; set; }
        public double CurrentRunTranscriptTokens { get; set; }
        public List<AgentAttachmentState> Attachments { get; set; } = new();
        public List<AgentFileEditPreview> SessionEdits { get; set; } = new();

        public static AgentSessionRewindSnapshot Capture(AgentOpenSessionState session)
        {
            return new AgentSessionRewindSnapshot
            {
                Title = session.Title,
                PromptText = session.PromptText,
                OutputText = session.OutputText,
                ActivityText = session.ActivityText,
                SessionHistoryText = session.SessionHistoryText,
                ModelSessionHistoryText = session.ModelSessionHistoryText,
                LastAnswerText = session.LastAnswerText,
                WorkspaceRoot = session.WorkspaceRoot,
                PendingPlanPath = session.PendingPlanPath,
                SessionHistoryTokenCount = session.SessionHistoryTokenCount,
                CurrentRunTranscriptTokens = session.CurrentRunTranscriptTokens,
                Attachments = CloneAttachments(session.Attachments),
                SessionEdits = CloneEdits(session.SessionEdits)
            };
        }

        public void RecordSubagent(AgentOpenSessionState session, bool isNewSession)
        {
            if (isNewSession)
            {
                SubagentSessionIds.Add(session.Id);
            }
            else if (!SubagentSessionIds.Contains(session.Id) && !SubagentInitialEditOrders.ContainsKey(session.Id))
            {
                SubagentInitialEditOrders.Add(session.Id, session.SessionEdits.Select(edit => edit.CommitOrder).ToHashSet());
            }
        }

        public List<AgentAttachmentState> CloneAttachments()
        {
            return CloneAttachments(Attachments);
        }

        public static List<AgentAttachmentState> CloneAttachments(IEnumerable<AgentAttachmentState>? attachments)
        {
            return attachments?.Select(CloneAttachment).ToList() ?? new List<AgentAttachmentState>();
        }

        public static List<AgentFileEditPreview> CloneEdits(IEnumerable<AgentFileEditPreview>? edits)
        {
            return edits?.Select(CloneEdit).ToList() ?? new List<AgentFileEditPreview>();
        }

        private static AgentAttachmentState CloneAttachment(AgentAttachmentState attachment)
        {
            return new AgentAttachmentState
            {
                Id = attachment.Id,
                Path = attachment.Path,
                DisplayName = attachment.DisplayName,
                Detail = attachment.Detail,
                TextContent = attachment.TextContent,
                ImageContent = CloneImageAttachment(attachment.ImageContent),
                EstimatedTokens = attachment.EstimatedTokens,
                IsPathOnlyDocument = attachment.IsPathOnlyDocument
            };
        }

        private static LlmMessageAttachment? CloneImageAttachment(LlmMessageAttachment? attachment)
        {
            if (attachment == null)
            {
                return null;
            }

            return new LlmMessageAttachment
            {
                DisplayName = attachment.DisplayName,
                MimeType = attachment.MimeType,
                Base64Data = attachment.Base64Data,
                Width = attachment.Width,
                Height = attachment.Height,
                EstimatedTokens = attachment.EstimatedTokens
            };
        }

        private static AgentFileEditPreview CloneEdit(AgentFileEditPreview preview)
        {
            return new AgentFileEditPreview
            {
                ActionName = preview.ActionName,
                RelativePath = preview.RelativePath,
                FullPath = preview.FullPath,
                DisplayPath = preview.DisplayPath,
                OldContent = preview.OldContent,
                NewContent = preview.NewContent,
                IsNewFile = preview.IsNewFile,
                CommitOrder = preview.CommitOrder,
                ModificationNumber = preview.ModificationNumber,
                TotalModifications = preview.TotalModifications
            };
        }
    }
}
