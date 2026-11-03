# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

MakinoMCP is a public, read-only MCP server that lets Claude (Claude Code, Cowork) see what a Makino MAS-A5 cell is
doing: live machine and station state, alarms, utilization, recent activity, orders and pallets from the cell's SQL
Server database, plus read access to folders the user chooses to share. Each user enters their own database login in a
setup window; it is saved in plain text in `MakinoMCP.settings.json` next to the launcher. Public repo:
https://github.com/GHeart01/Machine_Companion (docs page on GitHub Pages, downloads under Releases).

Machine-specific notes (which share it is published to, the live-test settings, local design docs) belong in
`CLAUDE.local.md`, which is git-ignored. Read it if it exists.

## Rules every change keeps

- **Read only, always.** No tool writes, sends or deletes anything, in the database or in a folder. SQL is fixed,
  parameterized SELECTs only, and every batch starts `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;`
  (`SqlMakinoReader.Command`, `SchemaInfo.LoadAsync`). Never add a "run this SQL" tool.
- **The password never appears** in a tool result, error, log line, UI text or CI log. Errors go through
  `ErrorText.ForClaude`; `MakinoLogin`/`McpSettings` mask it in `ToString`; `SettingsStore.Load` reports JSON errors by
  position only.
- **Folder tools stay inside the shared folders.** Folders are chosen by configured name; paths are resolved and checked
  against the root including links (`SharedFolders.Resolve`, `LinkStaysInside`); any file whose name starts with
  `MakinoMCP.settings.json`, and MakinoMCP's own folders, are always refused.
- **Stdout belongs to MCP.** In `serve` mode nothing but protocol messages reaches stdout; logs go to stderr and
  `%LOCALAPPDATA%\MakinoMCP\logs\` (`FileLog`). The launcher writes nothing to stdout either.
- **Bounded output.** Every history tool has a default window and caps at `limits.maxRows`; file reads cap at
  `limits.maxFileKB`; `.log` files default to the last 500 lines. Results that read the database include `serverTime`.
- **Older cells degrade, they don't crash.** Optional columns go through `Opt()` (NULL when absent);
  `DeviceResultDaySummary` is the only optional table — any other missing table is an error, never an empty list.
- **Code meanings live only in `Codes.cs`.** Unknown values render as `code N`. Some status columns are deliberately
  unmapped (pallet status, table/front/back status, vehicle finish status) because their meanings aren't confirmed.
- **Public repo hygiene.** Nothing specific to any one company in tracked files: examples use `MAKINO-CELL-PC`,
  `your-login`, `\\server\Tools`; test data is sample data. `scripts/check-private.ps1` (and CI, via the
  `PRIVATE_PATTERNS` secret) fails on private names. `docs/superpowers/`, `.superpowers/` and real settings files are
  git-ignored.
- **Windows are light theme only** (no `SetColorMode`); colours come from `SystemColors` so High Contrast still works.

## Layout

| Folder | What it is |
|---|---|
| `src/MakinoMCP.Core` | net10.0, no UI or MCP: `Settings` (file model and store), `Cell` (`SqlMakinoReader` split by area, `SchemaInfo` probing, `Codes`, `ErrorText`), `Folders` (`SharedFolders`) |
| `src/MakinoMCP` | net10.0-windows WinExe `MakinoMCP.exe`: `serve` = MCP host (`Tools/CellTools`, `Tools/FolderTools`, `ToolContext` with time budgets 45 s DB / 20 s folders), no args or `setup` = WinForms setup window (`Setup/`), including the "Use MakinoMCP in Claude on this PC" switch (`ClaudeSwitch`: `claude mcp add/remove` via `ClaudeRegistration`, Cowork's config via `CoworkConfig`, stops this user's `serve` processes via `RunningServers`) |
| `src/MakinoMCP.Launcher` | net48 WinExe, also named `MakinoMCP.exe`, the file users run from a share: reads `current.txt`, runs the local copy in `%LOCALAPPDATA%\MakinoMCP\app\<v>\`; setup mode copies first behind `CopyProgressForm`; serve mode on a fresh PC runs the share copy and prefetches in a detached process (`CreateProcessW`, no inherited handles); `VersionCopier` is the one copy routine |
| `tests/MakinoMCP.Tests` | xUnit (SkippableFact for live, junction and symlink tests); `StdioSpikeTests` drive a real launcher layout through an MCP client |
| `scripts/check-private.ps1` | fails on private names (patterns from `-Patterns` or env `PRIVATE_PATTERNS`); never prints the patterns |
| `site/` | the GitHub Pages docs page (single self-contained HTML) |
| `.github/workflows` | `ci.yml` (build, test, private check), `release.yml` (on `v*` tags: zip + SHA-256 + provenance attestation), `pages.yml` |

Published layout (what a share or the release zip holds): `MakinoMCP.exe` (launcher) + `.config`, `current.txt`,
`versions\<v>\MakinoMCP.exe` (self-contained, single file, compressed), `MakinoMCP.settings.example.json`, README,
LICENSE, and the user's own `MakinoMCP.settings.json` (never shipped, never overwritten).

## Everyday commands

```powershell
dotnet build MakinoMCP.slnx                                   # 0 warnings: TreatWarningsAsErrors is on
dotnet test MakinoMCP.slnx                                    # live DB tests skip unless MAKINOMCP_LIVE_SETTINGS is set
dotnet test MakinoMCP.slnx --filter "FullyQualifiedName~SharedFoldersTests"
dotnet test MakinoMCP.slnx -c Release                         # run this too before publishing: launcher tests are timing-sensitive
$env:MAKINOMCP_LIVE_SETTINGS = '<path to a settings file>'; dotnet test MakinoMCP.slnx --filter "FullyQualifiedName~Live"
.\scripts\check-private.ps1 -Patterns "<one regex per line>"  # before every push
.\publish.ps1 -Zip                                            # build artifacts\layout and artifacts\MakinoMCP-<v>-win-x64.zip
.\publish.ps1 -Target \\server\Tools\MakinoMCP                # mirror to a share; refuses a version already there
```

## Releasing

1. Raise `<Version>` in `src/MakinoMCP/MakinoMCP.csproj`. `publish.ps1 -Target` refuses to overwrite a version folder
   that already exists (`-Overwrite` only for a version no PC has used yet), because PCs that already copied a version
   never re-copy it.
2. Commit, push, and wait for CI (`main` requires the `build` check; the owner can bypass).
3. `git tag v<version>` and `git push origin v<version>`: the Release workflow builds and attaches the zip.
4. Publish to a share with `publish.ps1 -Target`: it writes the new `versions\<v>`, replaces the launcher only if its
   hash changed (renaming the old one to `.old-<stamp>`, since running sessions hold it open), and writes
   `current.txt` last.

## Open items

- The Cowork route (Claude desktop `mcpServers`) is untested; README, site and setup window say so. If it doesn't work,
  ship a `.mcpb` Desktop Extension instead.
- Not code-signed yet (SmartScreen warns on first run); SignPath Foundation is the planned route.
- Later: SQL Server in CI against a freshly written sample schema, issue templates, `.mcpb` packaging.
