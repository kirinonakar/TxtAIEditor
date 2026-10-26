using System.Collections.Generic;

namespace TxtAIEditor.Controls
{
    internal enum AgentRetryReason
    {
        EmptyResponse, Truncation, ToolCallFormat, MakePlan
    }

    internal sealed class AgentStepRetryPolicy
    {
        private readonly Dictionary<AgentRetryReason, int> _counts = new();
        internal bool TryRetry(AgentRetryReason reason)
        {
            int limit = reason switch
            {
                AgentRetryReason.EmptyResponse => 1,
                AgentRetryReason.Truncation => 3,
                _ => 2
            };
            _counts.TryGetValue(reason, out int count);
            if (count >= limit) return false;
            _counts[reason] = count + 1;
            return true;
        }

        internal void Reset(AgentRetryReason reason) => _counts.Remove(reason);
    }
}
