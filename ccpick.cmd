@echo off
rem ccpick - launch the Claude Code session picker from cmd.exe
rem Put this folder on PATH, then run `ccpick` (picker) / `ccpick list` / `ccpick show <id>`.
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0ccpick.ps1" %*
