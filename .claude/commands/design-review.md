---
description: Adversarially review the current change for design shape, not bugs — does it look like what we'd build from scratch?
argument-hint: "[optional: path, subsystem, or 'staged' / 'branch']"
allowed-tools: Bash(git diff:*), Bash(git status:*), Bash(git log:*), Bash(git stash list), Read, Grep, Glob
---

Review the change described by `$1` (default: the uncommitted working-tree diff) for
**design shape**. This is not a bug hunt — `/code-review` does that. It is not a
cleanup pass — `/simplify` does that, and it optimizes for less code, which is the
opposite of what this command is for.

Read @CLAUDE.md "Priorities" first. The whole point of this review is to catch the
failure mode that section describes: a change that is correct and small but is not
the design we would choose if we were writing it today.

## Gather

Get the diff, then read enough surrounding code to know what the subsystem looked
like before. A diff alone cannot tell you whether a new field belongs where it was
put — you need the type that now owns it and the types that could have.

## The rubric

Answer all six, in order, in writing. Do not skip one because the answer seems
obvious; the obvious answers are where this fails.

1. **From scratch.** Ignoring every line of existing code, what is the right design
   for what this change is trying to do? Say it in three or four sentences, naming
   the types and where each piece of state lives. Write this before looking at what
   the diff actually did, so the diff cannot anchor you.

2. **The delta.** Where does the change differ from that? List each difference.

3. **The reason for each delta.** For every difference, name the reason. A real
   reason is a constraint: an ordering or lifetime requirement, an engine
   limitation, a wire-format cost that is genuinely paid by someone outside this
   repo, an authoring cost to a human. These are **not** reasons, and finding one
   is itself the finding:
   - it was less churn / a smaller diff / touched fewer files
   - it avoided a `.hike`, save, or `.tres` format bump
   - an existing generic mechanism could be stretched to cover it
   - the existing code was already shaped that way

4. **Sources of truth.** What does this change now let you state in two places?
   Every duplicated rule, constant, derivation, or table — including one derived in
   C# and again in a shader, one computed by `WorldGen` and again by the map
   painter, or a value authored in a `.tres` and defaulted in code. For each, say
   which one wins when they disagree and how you would find out that they had.

5. **Enforcement.** What invariant does this change introduce or rely on, and what
   would catch a violation? Name the specific checker — an HK analyzer rule,
   `resource_check`, `spawn_check`, `block_check`, `shader_check`, `validate_uids`,
   a hard error at load. If the answer is "a human remembering CLAUDE.md", say so
   plainly: that is a finding, and the fix is usually a few lines in
   `tools/hike_analyzers` or an existing `*_check`.

6. **Authoring cost.** What does this change cost the person authoring `.tres` /
   `.tscn` content — more files to keep in sync, a value that must be repeated, a
   field whose effect is invisible until the world is rebaked, a field that cannot
   affect its container? Per CLAUDE.md, authored content is the long-tail
   bottleneck: a harder implementation that makes authoring safer is the right
   trade, so an answer of "it made authoring worse to make the code easier" is a
   finding.

## Report

Lead with a one-line verdict: **the design is right**, **the design is right but
under-enforced**, or **the design is wrong, and here is the one we should build**.

Then the findings, most consequential first. For each: what it is, which rubric
question it came from, and the concrete change that fixes it. Rank by consequence
to the data model and to authoring, never by how easy the fix is.

If the honest verdict is that the change is fine, say so in a sentence and stop.
Do not pad the report to look thorough — but do not soften a real finding because
the fix would be a large diff. Cost is not a reason here.
