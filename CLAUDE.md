# pipe-dream

## Worktrees live outside the repo

Create git worktrees as **siblings** of the repo, never inside it:

```sh
git worktree add ../pipe-dream-<branch> -b <branch>
```

Claude Code's built-in `--worktree` flag, the `EnterWorktree` tool, and agent
`isolation: "worktree"` all place them under `.claude/worktrees/` — inside the working
tree. There is no setting that changes that path, so prefer the command above; if one
does land inside, move it out with `git worktree move <path> ../pipe-dream-<branch>`.

A nested worktree is a second full checkout of this repo inside itself. `git add -A`
will try to swallow it, it doubles every hit from a recursive search, and it shows up
as untracked noise in `git status` forever. `.claude/worktrees/` is gitignored as a
backstop — the backstop is not the rule.

`git worktree list` shows where they actually are.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).

## Code reads top-down

A reader should get from the entry point to the code they need by reading, not searching.
`Program.Main` → `App` → the `MainWindow` constructor is the model: each is a short table of
contents that names the steps, and each step is a method named for what it does.

- **A long method is a driver plus named steps.** Past roughly 80 lines, or whenever a reader
  has to scroll to follow it, cut it into a short method that calls the steps in order. Cut,
  do not rewrite: step bodies keep their lines and comments. Name steps for what they do
  (`WireMap16`, `LoadLevel`), not how.
- **A long file splits along its seams.** When a class mixes separable concerns, use
  `partial class` files named `Type.Concern.cs`, each with a header comment saying what
  lives there and where the rest is. Existing `// ---- X ----` markers and method-name
  prefixes are the seams. Never invent a seam to hit a line count.
- **Members read in call order.** Entry points first, helpers after, in the order they are
  reached. Fields a section resolves sit with that section.
- **Comments travel with their code and carry the why.** Keep them when moving code. Add
  one only where a reader would otherwise have to ask "why is this here". Doc comments sit
  on the member they describe.
- **Refactor for reading is behaviour-preserving.** No new abstractions, dependencies or
  renames of anything used outside the folder. The suite must stay green without test edits.
- **Delete what nothing reads.** An argument parsed and never used, a field never read.
- **Don't pad.** A 30-line method that reads fine stays as it is. Small files stay whole.
