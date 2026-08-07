# Project Agent Instructions

## Communication

Communicate with the user in Japanese unless another language is requested.

Use English for persistent specifications, plans, agent briefs, and agent
configuration unless the repository has an established alternative convention.

## Superpowers

Before taking action, check whether an installed Superpowers skill applies.
Read and follow the current `SKILL.md`; treat it as the authoritative workflow.

User instructions and this file override a skill when they explicitly define a
lighter process for routine changes.

Always use:

- `superpowers:systematic-debugging` for bugs and unexpected behavior
- `superpowers:test-driven-development` for testable behavior changes
- `superpowers:receiving-code-review` when acting on review feedback
- `superpowers:verification-before-completion` before claiming success

Do not copy the full contents of Superpowers skills into plans or prompts.

## Task Classification

Classify a task before starting implementation.

A task is routine only when all of the following are true:

- The requested behavior and acceptance criteria are clear.
- The change is local, reversible, and expected to affect at most a few files.
- It does not introduce or materially change architecture, public APIs, schemas,
  dependencies, authentication, permissions, security boundaries, or data migration.
- It does not require coordination between independent implementation tasks.

Everything else is substantial.

When uncertain, classify the task as substantial.

## Routine Changes

For routine changes:

1. Inspect the relevant files and repository instructions.
2. State a concise implementation and verification approach.
3. Apply the relevant debugging or TDD workflow.
4. Implement directly in the primary thread unless delegation would clearly help.
5. Run fresh relevant verification before reporting completion.

Routine changes do not require:

- A committed design specification
- A separate implementation-plan document
- A new worktree
- A subagent
- A separate reviewer

Do not use the routine path merely to bypass ambiguity, testing, review, or
verification.

## Substantial Changes

For substantial changes:

1. Use `superpowers:brainstorming`.
2. Obtain approval for the design.
3. Save the approved design to
   `docs/superpowers/specs/YYYY-MM-DD-<topic>-design.md`.
4. Use `superpowers:writing-plans`.
5. Save the plan to
   `docs/superpowers/plans/YYYY-MM-DD-<feature-name>.md`.
6. Use `superpowers:using-git-worktrees` when isolation is appropriate.
7. Execute the plan with `superpowers:subagent-driven-development`.
8. Complete the required task reviews and final review.
9. Use `superpowers:finishing-a-development-branch`.

Keep specifications and plans proportional to the task. Prefer concise,
complete documents over process-heavy documents.

## Model and Role Assignment

The primary agent owns:

- Requirements and architecture
- Design and test planning
- Task decomposition
- Cross-task decisions
- Review adjudication
- Final verification and reporting

The primary agent is expected to use `gpt-5.6-sol` with `max` reasoning effort.

For bounded implementation tasks with an approved plan, explicitly dispatch
`luna_implementer`, configured to use `gpt-5.6-luna` with `max` reasoning
effort.

Do not delegate unresolved requirements or architecture decisions to Luna.

The primary agent may implement a routine change directly when delegation would
cost more coordination than it saves.

Never run concurrent implementation subagents in the same workspace.

If an implementation task requires substantial integration judgment, or the
Superpowers fix loop requires escalation, use a more capable model as prescribed
by `superpowers:subagent-driven-development`. Report the escalation rather than
silently substituting models.

If Luna is unavailable, report that limitation before beginning delegated
implementation.

## Implementation and Testing

For testable behavior changes, follow RED-GREEN-REFACTOR:

1. Write a focused failing test.
2. Run it and confirm the expected failure.
3. Write the minimum implementation needed to pass.
4. Run the test and confirm it passes.
5. Refactor while keeping the tests green.

Do not weaken, remove, or skip tests merely to obtain a passing result.

Configuration-only changes, documentation changes, generated code, and
throwaway prototypes may omit TDD when automated behavioral testing would not
provide meaningful evidence. Still perform the most relevant available
validation.

## Review

For routine changes, the primary agent may review the diff directly.

Require an independent reviewer when the change:

- Is substantial
- Affects security, concurrency, persistence, or public interfaces
- Spans multiple components
- Was implemented by a subagent
- Will be merged or submitted as a pull request

Subagent self-review does not replace independent review.

Return significant findings to the original implementer when practical.
Do not ignore technically valid feedback.

Use `gpt-5.6-sol` with `max` reasoning effort for the final review of substantial
changes.

## Verification

Before claiming completion:

1. Inspect the actual diff.
2. Verify the applicable acceptance criteria.
3. Run fresh relevant tests.
4. Run applicable build, type-check, lint, and formatting checks.
5. Confirm exit codes and failure counts.

A subagent success report is not verification evidence.

If a required check cannot be run, state the omitted check, the reason, and the
remaining risk.

## Scope and Safety

Preserve unrelated user changes and avoid unrelated refactoring.

Continue autonomously through safe, approved, in-scope work.

Stop and request direction when requirements conflict, a material product or
architecture decision is unresolved, permissions are missing, or the next
action would be destructive, externally visible, or materially expand scope.

## Subagent Model Fallback

For bounded implementation tasks, use the `implementer` custom agent.

Model-selection procedure:

1. First attempt to spawn `implementer` with `gpt-5.6-luna` and `max`
   reasoning effort.
2. If and only if the spawn fails because Luna is unavailable, unsupported,
   disabled, or not included in the current account or workspace entitlement,
   retry the same task once with `gpt-5.6-terra` and `max` reasoning effort.
3. Preserve the exact same task brief, constraints, acceptance criteria,
   interfaces, and report contract when retrying.
4. Record that the fallback occurred and include the original availability
   error in the final report.
5. Do not retry Luna repeatedly during the same task after an availability
   failure.

Do not treat the following as model-availability failures:

- Permission or sandbox denial
- Invalid agent configuration
- Missing files or dependencies
- Test or build failure
- Implementation difficulty
- Context or requirements ambiguity

Handle those failures according to their actual cause instead of switching
models.

If explicit model selection is unavailable in the current client, spawn
`implementer` without a model override. The configured default
`gpt-5.6-terra` will then be used.

