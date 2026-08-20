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
