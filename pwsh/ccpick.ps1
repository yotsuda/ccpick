#requires -version 7
<#
.SYNOPSIS
  ccpick - Claude Code session picker. Lists recent sessions with summaries and
  resumes the chosen one via `claude --resume <id>`, using fzf for the
  interactive fuzzy-filter UI.

.DESCRIPTION
  Subcommands:
    list            Emit one TAB-separated row per session: id<TAB>date<TAB>cwdLeaf<TAB>title
    show <id>       Print a preview block for a session
    pick            (default) Run the fzf picker and resume the chosen session

  Sessions are read from ~/.claude/projects/<slug>/<guid>.jsonl. Files under a
  subagents/ subfolder are NOT sessions and are skipped.

  A cache (~/.claude/ccpick-cache.json) keyed by file mtime makes repeat launches
  near-instant: only sessions whose .jsonl changed are re-scanned.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('list', 'show', 'pick')]
    [string]$Command = 'pick',

    [Parameter(Position = 1)]
    [string]$Id
)

$ErrorActionPreference = 'Stop'
# fzf is a native exe; without this the PS<->fzf pipe mangles non-ASCII (e.g.
# Japanese) titles. Force UTF-8 on the console + native-command pipe.
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

$ProjectsRoot = Join-Path $HOME '.claude\projects'
$CachePath = Join-Path $HOME '.claude\ccpick-cache.json'

# Extraction of {Title; Cwd} from a single jsonl. Defined as a string so it can
# be reused verbatim inside ForEach-Object -Parallel (which can't see functions
# from this scope, and rejects scriptblock -using variables).
$ExtractBody = {
    param([string]$Path)
    $title = $null; $cwd = $null; $count = 0
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if (++$count -gt 60) { break }
        if ($line.Length -lt 2) { continue }
        try { $o = $line | ConvertFrom-Json } catch { continue }
        if (-not $cwd -and $o.cwd) { $cwd = [string]$o.cwd }
        if (-not $title) {
            if ($o.type -eq 'summary' -and $o.summary) { $title = [string]$o.summary }
            elseif ($o.type -eq 'user' -and -not $o.isMeta) {
                $c = $o.message.content; $text = $null
                if ($c -is [string]) { $text = $c }
                elseif ($c) { $text = ($c | Where-Object { $_.type -eq 'text' } | Select-Object -First 1).text }
                if ($text) {
                    $text = ($text -replace '<[^>]+>', ' ' -replace '\s+', ' ').Trim()
                    if ($text.Length -gt 3 -and $text -notmatch '^Caveat:') { $title = $text }
                }
            }
        }
        if ($title -and $cwd) { break }
    }
    if (-not $title) { $title = '(no prompt)' }
    if ($title.Length -gt 300) { $title = $title.Substring(0, 300) }
    [pscustomobject]@{ Title = $title; Cwd = $cwd }
}
$ExtractText = $ExtractBody.ToString()
$Extract = [scriptblock]::Create($ExtractText)

function Read-Cache {
    $c = @{}
    if (Test-Path $CachePath) {
        try {
            foreach ($p in (Get-Content $CachePath -Raw | ConvertFrom-Json).PSObject.Properties) {
                $c[$p.Name] = $p.Value
            }
        } catch { }
    }
    $c
}

function Get-Sessions {
    $files = @()
    if (Test-Path $ProjectsRoot) {
        foreach ($d in Get-ChildItem $ProjectsRoot -Directory) {
            $files += Get-ChildItem $d.FullName -Filter '*.jsonl' -File
        }
    }
    $cache = Read-Cache
    $rows = [System.Collections.Generic.List[object]]::new()
    $stale = [System.Collections.Generic.List[object]]::new()

    foreach ($f in $files) {
        $ticks = $f.LastWriteTime.Ticks
        $hit = $cache[$f.BaseName]
        if ($hit -and [long]$hit.Mtime -eq $ticks) {
            $rows.Add([pscustomobject]@{ Id = $f.BaseName; Mtime = $f.LastWriteTime; Cwd = $hit.Cwd; Title = $hit.Title })
        } else {
            $stale.Add([pscustomobject]@{ Id = $f.BaseName; Path = $f.FullName; Mtime = $f.LastWriteTime; Ticks = $ticks })
        }
    }

    if ($stale.Count -gt 0) {
        $scanned = $stale | ForEach-Object -ThrottleLimit 8 -Parallel {
            $ex = [scriptblock]::Create($using:ExtractText)
            $m = & $ex $_.Path
            [pscustomobject]@{ Id = $_.Id; Mtime = $_.Mtime; Ticks = $_.Ticks; Cwd = $m.Cwd; Title = $m.Title }
        }
        foreach ($s in $scanned) {
            $cache[$s.Id] = [pscustomobject]@{ Mtime = $s.Ticks; Cwd = $s.Cwd; Title = $s.Title }
            $rows.Add([pscustomobject]@{ Id = $s.Id; Mtime = $s.Mtime; Cwd = $s.Cwd; Title = $s.Title })
        }
        # prune cache entries whose files no longer exist, then persist
        $live = @{}; foreach ($f in $files) { $live[$f.BaseName] = $true }
        ($cache.Keys | Where-Object { -not $live[$_] }) | ForEach-Object { $cache.Remove($_) }
        ($cache | ConvertTo-Json -Depth 4) | Set-Content $CachePath -Encoding utf8
    }

    $rows | Sort-Object Mtime -Descending | ForEach-Object {
        $leaf = if ($_.Cwd) { Split-Path $_.Cwd -Leaf } else { '?' }
        [pscustomobject]@{
            Id = $_.Id; Date = $_.Mtime.ToString('yyyy-MM-dd HH:mm'); Leaf = $leaf; Cwd = $_.Cwd; Title = $_.Title
        }
    }
}

function Show-One {
    param([string]$Id)
    $cache = Read-Cache
    $hit = $cache[$Id]
    if ($hit) {
        $cwd = $hit.Cwd; $title = $hit.Title
    } else {
        $path = Get-ChildItem $ProjectsRoot -Directory | ForEach-Object { Join-Path $_.FullName "$Id.jsonl" } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $path) { return "session not found: $Id" }
        $m = & $Extract $path; $cwd = $m.Cwd; $title = $m.Title
    }
    "id   : $Id`ncwd  : $cwd`n`n$title"
}

switch ($Command) {
    'list' {
        Get-Sessions | ForEach-Object {
            $t = if ($_.Title.Length -gt 90) { $_.Title.Substring(0, 90) } else { $_.Title }
            "{0}`t{1}`t{2}`t{3}" -f $_.Id, $_.Date, $_.Leaf, $t
        }
    }
    'show' { Show-One -Id $Id }
    'pick' {
        if (-not (Get-Command fzf -ErrorAction SilentlyContinue)) {
            Write-Error 'fzf not found. Install it: winget install fzf'; break
        }
        $rows = Get-Sessions | ForEach-Object {
            $t = if ($_.Title.Length -gt 90) { $_.Title.Substring(0, 90) } else { $_.Title }
            "{0}`t{1}`t{2}`t{3}" -f $_.Id, $_.Date, $_.Leaf, $t
        }
        if (-not $rows) { Write-Host 'No sessions found.'; break }
        # No live --preview: spawning pwsh per keystroke costs ~1.4s and makes
        # fzf stutter. The row already carries date/cwd/title. A rich preview
        # needs a fast-start helper (compiled exe / resident daemon) - deferred.
        $choice = $rows | fzf --ansi --delimiter "`t" --with-nth='2,3,4' `
            --header 'Enter: resume   Esc: cancel   (type to fuzzy-filter)'
        if (-not $choice) { break }
        $id = ($choice -split "`t")[0]
        Write-Host "claude --resume $id"
        claude --resume $id
    }
}
