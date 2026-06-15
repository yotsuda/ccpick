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
ccpick list   →   fzf  (real-time fuzzy filter + preview)   →   claude --resume <chosen id>
```

- **Reads** `~/.claude/projects/<slug>/<guid>.jsonl` (top-level files only; `subagents/` internal logs are skipped).
- **Titles** come from the first real user prompt or a `summary` line — heuristic, fully local, nothing is sent anywhere.
- **Caches** results in `~/.claude/ccpick-cache.json` keyed by file mtime, so only changed sessions are re-scanned. Cold scan of ~100 sessions ≈ 2 s; warm launches ≈ 0.2 s.
- **Preview** of the focused session is shown live by re-invoking `ccpick show <id>` (a ~0.2 s spawn — cheap because the tool starts fast).

It is *not* an `fzf` plugin — `fzf` has no plugin system. `ccpick` is a small wrapper that uses `fzf` as the interactive filter, like `forgit` or `fzf-git.sh`.

## Requirements

- **.NET 8+ runtime** (the tool is a [.NET global tool](https://learn.microsoft.com/dotnet/core/tools/global-tools))
- **fzf** — `winget install fzf` / `brew install fzf` / `apt install fzf`
- **claude** on `PATH`

## Install

```sh
winget install fzf                  # or: brew install fzf / apt install fzf
dotnet tool install --global ccpick
```

The same one `dotnet tool` package runs on **Windows, macOS, and Linux**, and `ccpick` lands on your `PATH` automatically.

> Not yet published to NuGet. Until then, install from a local build:
> ```sh
> git clone https://github.com/yotsuda/ccpick && cd ccpick
> dotnet pack -c Release
> dotnet tool install --global --add-source ./bin/Release ccpick
> ```

## Usage

| Command | What it does |
|---|---|
| `ccpick` | Open the fzf picker; type to filter, Enter to resume |
| `ccpick list` | Print one TAB-separated row per session (`id  date  cwd  title`) |
| `ccpick show <id>` | Print a one-session preview block |

## Notes

- **UTF-8 everywhere.** The pipe to/from `fzf` is forced to UTF-8 so non-ASCII titles (Japanese, etc.) survive the round trip.
- Titles are heuristic; slash-command sessions (e.g. `/clear`) read a little oddly.
- A **legacy PowerShell version** (no .NET build needed, but pulls in `pwsh` and has no live preview) lives in [`pwsh/`](pwsh/).

## Related tools

Several Claude Code session browsers exist. `ccpick`'s niche is **fzf-powered fuzzy search + tiny footprint + one cross-platform `dotnet tool`**:

- [sasazame/ccresume](https://github.com/sasazame/ccresume) — Node/Ink custom TUI, keyboard navigation (no fuzzy search), cross-platform.
- [josephyaduvanshi/claude-history-manager](https://github.com/JosephYaduvanshi/claude-history-manager) — native macOS app with pin/tag/search.
- [davidpp/claude-session-browser](https://github.com/davidpp/claude-session-browser) — TUI that copies the resume command to the clipboard.

## Disclaimer

Unofficial, community-built. Not affiliated with, endorsed by, or supported by Anthropic. Use at your own risk.

## License

MIT
