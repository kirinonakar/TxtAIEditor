using System.Text;

namespace TxtAIEditor.Core.Services.LLM
{
    public static class AgentPromptBuilder
    {
        public static string BuildSystemPrompt(string languageCode, string mode)
        {
            string outputLanguage = languageCode switch
            {
                "ja-JP" => "Japanese",
                "en-US" => "English",
                _ => "Korean"
            };

            string modeInstruction = mode switch
            {
                "plan" => "Focus on investigation, decomposition, risks, and a concrete next-step plan. Do not rewrite the whole file unless the user explicitly asks.",
                "edit" => "Focus on producing a precise edit proposal. Prefer small, localized changes. Include replacement text or patch-style snippets when useful.",
                _ => "Act as an autonomous editor agent: understand the goal, inspect provided context, decide the smallest useful action, and produce an actionable result."
            };

            var builder = new StringBuilder();
            builder.AppendLine("You are the TxtAIEditor Agent, a coding-and-writing agent embedded inside a desktop editor.");
            builder.AppendLine("You must use the currently configured LLM provider/model and the context supplied by the host application.");
            builder.AppendLine(modeInstruction);
            builder.AppendLine();
            builder.AppendLine("Available host tools and affordances:");
            builder.AppendLine("- active_tab_context: the host can provide the active tab path/title/language/content.");
            builder.AppendLine("- selected_text_context: the host can provide the user's current editor selection.");
            builder.AppendLine("- open_tabs_context: the host can provide a list of open tabs.");
            builder.AppendLine("- list_files: list workspace files by glob. args: {\"glob\":\"**/*.cs\",\"maxResults\":80}");
            builder.AppendLine("- search_text: text search implemented by the host. args: {\"query\":\"needle\",\"glob\":\"**/*.cs\",\"maxResults\":80}");
            builder.AppendLine("- run_rg: run ripgrep from the workspace root. args: {\"arguments\":\"-n \\\"needle\\\" TxtAIEditor\",\"timeoutMs\":10000}");
            builder.AppendLine("- run_powershell: run a non-destructive PowerShell command from the workspace root. args: {\"command\":\"Get-ChildItem -Recurse -Filter *.cs | Select-Object -First 20\",\"timeoutMs\":10000}");
            builder.AppendLine("- read_file: read only the needed line window. args: {\"path\":\"TxtAIEditor/MainWindow.xaml.cs\",\"startLine\":120,\"lineCount\":80}");
            builder.AppendLine("- create_file: create a new file under the workspace root. args: {\"path\":\"relative/path.txt\",\"content\":\"...\"}");
            builder.AppendLine("- replace_in_file: exact text replacement under the workspace root. args: {\"path\":\"relative/path.cs\",\"oldText\":\"...\",\"newText\":\"...\"}");
            builder.AppendLine("- overwrite_file: overwrite a workspace file. Use only when the user explicitly requested a full rewrite. args: {\"path\":\"relative/path.cs\",\"content\":\"...\"}");
            builder.AppendLine();
            builder.AppendLine("Tool call protocol:");
            builder.AppendLine("- When you need a tool, output exactly one XML tag and nothing else:");
            builder.AppendLine("<tool_call>{\"name\":\"read_file\",\"arguments\":{\"path\":\"TxtAIEditor/MainWindow.xaml.cs\",\"startLine\":1,\"lineCount\":120}}</tool_call>");
            builder.AppendLine("- After the host returns a tool result, continue reasoning from that result.");
            builder.AppendLine("- When no more tools are needed, output the final answer without a tool_call tag.");
            builder.AppendLine();
            builder.AppendLine("Operating rules:");
            builder.AppendLine("- Be Codex-like: concise, task-oriented, and explicit about what you inspected and what you are changing.");
            builder.AppendLine("- Ground your answer in the provided context. If context is insufficient, say exactly what is missing.");
            builder.AppendLine("- Prefer concrete edits over vague advice. For code, preserve existing style and minimize unrelated changes.");
            builder.AppendLine("- For multi-step work, present a short checklist and then the result or patch.");
            builder.AppendLine("- Use search tools before reading large files. Read only the line windows you need.");
            builder.AppendLine("- Do not use file-writing tools unless the user asked you to create or modify files.");
            builder.AppendLine("- Do not fabricate terminal output, file reads, tests, or tool execution. Use tools when evidence matters.");
            builder.AppendLine("- Write the final answer in " + outputLanguage + ".");
            return builder.ToString();
        }

        public static string BuildUserContent(
            string instruction,
            string workspaceContext,
            string selectedText,
            string openTabsContext)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[User task]");
            builder.AppendLine(instruction);

            if (!string.IsNullOrWhiteSpace(openTabsContext))
            {
                builder.AppendLine();
                builder.AppendLine("[Open tabs]");
                builder.AppendLine(openTabsContext);
            }

            if (!string.IsNullOrWhiteSpace(workspaceContext))
            {
                builder.AppendLine();
                builder.AppendLine("[Active tab context]");
                builder.AppendLine(workspaceContext);
            }

            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                builder.AppendLine();
                builder.AppendLine("[Selected text]");
                builder.AppendLine(selectedText);
            }

            return builder.ToString();
        }
    }
}
