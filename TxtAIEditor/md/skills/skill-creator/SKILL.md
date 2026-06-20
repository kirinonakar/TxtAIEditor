---
name: skill-creator
description: Create, improve, audit, and simplify reusable agent skills in a vendor-neutral SKILL.md format. Use when the user wants to turn a workflow, coding habit, project rule, tool usage pattern, checklist, or prompt into a portable skill for an AI coding or productivity agent.
---

# Skill Creator

Help the user create practical, portable `SKILL.md` files for AI agents.

A skill should be small, specific, reusable, and easy for an agent to apply at the right time. Prefer clear triggers, concrete workflow steps, examples, and safety checks over long generic instructions.

## When to use this skill

Use this skill when the user asks to:

* create a new skill
* improve an existing `SKILL.md`
* convert a prompt, checklist, workflow, or project rule into a skill
* make a skill more agent-neutral
* simplify an overly long skill
* write a skill for coding, browser automation, testing, document work, design review, debugging, refactoring, research, or project-specific workflows
* review whether a skill will trigger correctly

Do not use this skill for ordinary coding or writing tasks unless the user specifically wants a reusable skill.

## TxtAIEditor save location

When creating a new skill for TxtAIEditor and the user does not request a different location, create it directly under the user settings skill directory:

`%USERPROFILE%\.TxtAIEditor\skills\<skill-name>\SKILL.md`

Resolve `%USERPROFILE%` to the actual absolute path before using file tools. Create the `<skill-name>` directory if needed. Do not write new user-created skills into the app's built-in `md\skills` directory or the legacy `%USERPROFILE%\.agents\skills` directory unless the user explicitly asks for that location.

## Core principles

Good skills should be:

1. **Specific**
   The skill should solve one clear class of tasks.

2. **Triggerable**
   The `description` should say when the agent should use the skill.

3. **Portable**
   Avoid vendor-specific names, commands, internal tools, or product-only assumptions unless the user explicitly wants that.

4. **Short enough to read**
   Keep the main instructions compact. Put long references, examples, or scripts in separate files when possible.

5. **Actionable**
   Tell the agent what to do, what to avoid, and how to verify the result.

6. **Safe by default**
   Include guardrails for destructive edits, secrets, credentials, external commands, and user confirmation when needed.

## Recommended SKILL.md structure

Use this structure unless the user requests something different:

```markdown
---
name: concise-skill-name
description: Clear trigger description. Use when the user asks for X, Y, or Z. Do not use for unrelated tasks.
---

# Skill Title

Briefly explain the purpose of the skill.

## When to use

List the situations where this skill should be used.

## When not to use

List common false positives.

## Workflow

1. Understand the user's goal.
2. Inspect the relevant files, inputs, or context.
3. Make the smallest useful change.
4. Validate the result.
5. Report what changed and what was verified.

## Rules

- Important rule 1.
- Important rule 2.
- Important rule 3.

## Output

Describe what the agent should return to the user.

## Examples

User: "Example request"
Assistant should: "Expected behavior"
```

## Writing the frontmatter

The frontmatter should be minimal.

Required fields:

```yaml
---
name: skill-name
description: Use when the user asks for a specific type of task. Include clear trigger words and boundaries.
---
```

### Name rules

Use:

* lowercase words
* hyphens instead of spaces
* short names
* task-oriented names

Good:

```yaml
name: react-performance-review
name: pdf-extraction-workflow
name: winui-debugging-guide
name: browser-automation-helper
```

Avoid:

```yaml
name: amazing-super-agent
name: general-helper
name: my-rules
name: claude-only-skill
```

### Description rules

The description is the most important trigger text. It should tell the agent when to open the skill.

Good description pattern:

```yaml
description: Review React or Next.js code for performance, rendering, bundle size, and data-fetching issues. Use when the user asks to optimize React components, reduce unnecessary renders, improve frontend performance, or review a React app.
```

Avoid vague descriptions:

```yaml
description: Helps with React.
description: A useful skill for coding.
description: Makes things better.
```

## Workflow for creating a new skill

When creating a new skill:

1. Identify the task category.
2. Define when the skill should trigger.
3. Define when it should not trigger.
4. Write a short workflow.
5. Add strict rules only where they prevent common mistakes.
6. Add examples that match real user requests.
7. Remove vendor-specific assumptions unless required.
8. Check that the skill is not too broad.
9. For TxtAIEditor, save the final `SKILL.md` to `%USERPROFILE%\.TxtAIEditor\skills\<skill-name>\SKILL.md` unless the user asked for text-only output or a different path.

## Workflow for improving an existing skill

When improving a skill:

1. Preserve the user's intent.
2. Keep useful domain-specific details.
3. Remove duplication and generic advice.
4. Strengthen the description trigger.
5. Add missing "when not to use" boundaries.
6. Make steps concrete and testable.
7. Remove product-specific references unless needed.
8. Return the improved `SKILL.md`.

## Agent-neutral language

Prefer neutral terms:

* "agent"
* "tool"
* "runtime"
* "workspace"
* "project"
* "files"
* "user"

Avoid product-specific terms unless the user requests them:

* product-specific artifact systems
* product-specific tool names
* product-specific command names
* product-specific hidden evaluation flows
* assumptions about a single IDE or assistant

## Safety rules

When a skill involves file edits, commands, automation, credentials, or external systems, include relevant guardrails:

* Do not delete or rewrite large parts of a project without a clear reason.
* Do not expose secrets, API keys, tokens, cookies, or private credentials.
* Prefer minimal targeted changes over broad refactors.
* Validate changes with available tests, builds, linters, or manual checks.
* Clearly report files changed and verification performed.
* Ask for confirmation before destructive, irreversible, or high-risk actions when the user has not already authorized them.

## Quality checklist

Before finalizing a skill, check:

* The `name` is short and clear.
* The `description` has strong trigger phrases.
* The skill is not too broad.
* The workflow is concrete.
* The rules are not generic filler.
* The skill avoids unnecessary vendor lock-in.
* The skill tells the agent what to output.
* The skill includes examples when useful.
* The skill can be understood without external context.
* The skill does not encourage unsafe behavior.

## Output format

When the user asks for a complete skill, return a full `SKILL.md`.

When the user asks for edits, return either:

1. the full revised `SKILL.md`, or
2. a concise patch-style summary plus the revised sections

Prefer the full revised file when the skill is short.

## Example user requests

User: "Convert this prompt into a skill."
Assistant should create a complete `SKILL.md` with neutral frontmatter and a clear workflow.

User: "Will this skill description trigger reliably?"
Assistant should review the description, identify weak trigger wording, and propose a better version.
