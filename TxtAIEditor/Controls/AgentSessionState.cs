using System;
using System.Collections.Generic;
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
        public string WorkspaceRoot { get; set; } = string.Empty;
        public double SessionHistoryTokenCount { get; set; }
        public double CurrentRunTranscriptTokens { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<AgentAttachmentState> Attachments { get; set; } = new();
        public List<AgentFileEditPreview> SessionEdits { get; set; } = new();
        public bool IsRunning { get; set; }
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
        public double SessionHistoryTokenCount { get; set; }
        public double CurrentRunTranscriptTokens { get; set; }
        public List<AgentAttachmentState> Attachments { get; set; } = new();
        public List<LlmMessageAttachment> ImageToolAttachments { get; } = new();
        public List<AgentFileEditPreview> SessionEdits { get; set; } = new();
        public bool StreamToTabActive { get; set; }
        public bool StreamToTab { get; set; }
        public string? StreamToTabTargetTabId { get; set; }
        public string WorkspaceRoot { get; set; } = string.Empty;
        public EditorSettings LlmSettings { get; set; } = new();
    }
}
