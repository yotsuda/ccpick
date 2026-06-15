# ccpick

A tiny, fast **session picker for [Claude Code](https://github.com/anthropics/claude-code)** built around [`fzf`](https://github.com/junegunn/fzf). Launch it, fuzzy-filter your recent sessions by their summaries, hit Enter — it runs `claude --resume <id>` for you.

```
ccpick
```

No more copying `claude --resume <guid>` out of a notes file, and no more scrolling the built-in picker: just type a few letters of what the session was about.

## Why

When Claude Code exits it prints something like:

```
claude --resume 863e7427-5223-455a-8b91-05fccb405d1f
```

Those GUIDs are already on disk as `~/.claude/projects/<slug>/<guid>.jsonl` transcripts — there is nothing to "save". `ccpick` indexes those transcripts, derives a human-readable title from each (the first user prompt, or a session summary if present), and hands the list to `fzf` for instant fuzzy filtering.

## How it works

```
ccpick list   →   fzf  (real-time fuzzy filter)   →   claude --resume <chosen id>
```

- **Reads** `~/.claude/projects/<slug>/<guid>.jsonl` (top-level files only; `subagents/` internal logs are skipped).
- **Titles** come from the first real user prompt or a `summary` line — heuristic, fully local, nothing is sent anywhere.
- **Caches** results in `~/.claude/ccpick-cache.json` keyed by file mtime, so only changed sessions are re-scanned. Cold scan of ~100 sessions ≈ a few seconds; warm launches are near-instant.

It is *not* an `fzf` plugin — `fzf` has no plugin system. `ccpick` is a small wrapper that uses `fzf` as the interactive filter, like `forgit` or `fzf-git.sh`.

## Requirements

- **PowerShell 7+** (`pwsh`)
- **fzf** — `winget install fzf` (or `scoop install fzf`)
- **claude** on `PATH`

## Install

```powershell
git clone https://github.com/<you>/ccpick.git C:\Tools\ccpick
winget install fzf
# add C:\Tools\ccpick to your PATH (so `ccpick` works from any cmd / pwsh window)
```

`ccpick.cmd` is a thin launcher so you can run `ccpick` straight from **cmd.exe**; it just invokes `ccpick.ps1`, which holds all the logic.

## Usage

| Command | What it does |
|---|---|
| `ccpick` | Open the fzf picker; type to filter, Enter to resume |
| `ccpick list` | Print one TAB-separated row per session (`id  date  cwd  title`) |
| `ccpick show <id>` | Print a one-session preview block |

## Notes & limitations

- **UTF-8 is forced** on the console + native pipe so non-ASCII titles (Japanese, etc.) survive the round trip through `fzf`.
- **No live preview pane (yet).** Spawning `pwsh` per keystroke for a preview costs ~1.4 s and makes `fzf` stutter; a rich preview needs a fast-start helper (a compiled binary or resident daemon). The row already shows date / cwd / title.
- Titles are heuristic; slash-command sessions (e.g. `/clear`) read a little oddly.

## Related tools

Several Claude Code session browsers exist. `ccpick`'s niche is **fzf-powered fuzzy search + minimal footprint + cmd.exe-friendly on Windows**:

- [sasazame/ccresume](https://github.com/sasazame/ccresume) — Node/Ink custom TUI, keyboard navigation (no fuzzy search), cross-platform.
- [josephyaduvanshi/claude-history-manager](https://github.com/JosephYaduvanshi/claude-history-manager) — native macOS app with pin/tag/search.
- [davidpp/claude-session-browser](https://github.com/davidpp/claude-session-browser) — TUI that copies the resume command to the clipboard.

## Disclaimer

Unofficial, community-built. Not affiliated with, endorsed by, or supported by Anthropic. Use at your own risk.

## License

MIT
