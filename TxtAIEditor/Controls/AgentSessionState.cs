using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentOpenSessionState
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public string PromptText { get; set; } = string.Empty;
        public string OutputText { get; set; } = string.Empty;
        public string ActivityText { get; set; } = string.Empty;
        public string SessionHistoryText { get; set; } = string.Empty;
        public string LastAnswerText { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
        public double SessionHistoryTokenCount { get; set; }
        public double CurrentRunTranscriptTokens { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<AgentAttachmentState> Attachments { get; set; } = new();
        public List<AgentFileEditPreview> SessionEdits { get; set; } = new();
        public List<AgentSessionRewindSnapshot> RewindSnapshots { get; set; } = new();
        public bool IsRunning { get; set; }
        public int CompletedNotificationCount { get; set; }
        public bool ThinkingLineActive { get; set; }
        public int ThinkingLineStart { get; set; }
        public string ThinkingLineTimestamp { get; set; } = string.Empty;
        public string ThinkingLinePrefix { get; set; } = string.Empty;
        public EditorSettings? LlmSettings { get; set; }
    }

    internal sealed class AgentRunContext
    {
        public string SessionId { get; set; } = string.Empty;
        public CancellationTokenSource? Cancellation { get; set; }
        public StringBuilder SessionHistory { get; } = new();
        public StringBuilder RetryDebugHistory { get; } = new();
        public double SessionHistoryTokenCount { get; set; }
        public double CurrentRunTranscriptTokens { get; set; }
        public List<AgentAttachmentState> Attachments { get; set; } = new();
        public List<LlmMessageAttachment> ImageToolAttachments { get; } = new();
        public List<AgentFileEditPreview> SessionEdits { get; set; } = new();
        public bool StreamToTabActive { get; set; }
        public bool StreamToTab { get; set; }
        public string? StreamToTabTargetTabId { get; set; }
        public string LastAnswerText { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
        public EditorSettings LlmSettings { get; set; } = new();
        public bool IsPlanningMode { get; set; }
        public bool HasEnabledSkills { get; set; }
        public bool HasEnabledMcp { get; set; }
        public string OriginalUserInstruction { get; set; } = string.Empty;
        public string PlanWorkspaceContext { get; set; } = string.Empty;
        public string PlanSelectionContext { get; set; } = string.Empty;
        public string GeneratedPlanPath { get; set; } = string.Empty;
    }

    internal sealed class AgentSessionRewindSnapshot
    {
        public string Title { get; set; } = string.Empty;
        public string PromptText { get; set; } = string.Empty;
        public string OutputText { get; set; } = string.Empty;
        public string ActivityText { get; set; } = string.Empty;
        public string SessionHistoryText { get; set; } = string.Empty;
        public string LastAnswerText { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
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
                LastAnswerText = session.LastAnswerText,
                WorkspaceRoot = session.WorkspaceRoot,
                SessionHistoryTokenCount = session.SessionHistoryTokenCount,
                CurrentRunTranscriptTokens = session.CurrentRunTranscriptTokens,
                Attachments = CloneAttachments(session.Attachments),
                SessionEdits = CloneEdits(session.SessionEdits)
            };
        }

        public List<AgentAttachmentState> CloneAttachments()
        {
            return CloneAttachments(Attachments);
        }

        public List<AgentFileEditPreview> CloneSessionEdits()
        {
            return CloneEdits(SessionEdits);
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
                OldContent = preview.OldContent,
                NewContent = preview.NewContent,
                IsNewFile = preview.IsNewFile,
                ModificationNumber = preview.ModificationNumber,
                TotalModifications = preview.TotalModifications
            };
        }
    }
}
