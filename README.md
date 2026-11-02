# MakinoMCP

MakinoMCP lets Claude look at what a Makino MAS-A5 machining cell is doing. Ask in plain language, for example
"is anything alarmed right now?", and Claude answers from the cell's own database and, if you share them, its log files.

It is a small [Model Context Protocol](https://modelcontextprotocol.io) server for Windows. It only reads.

> Not made or endorsed by Makino. "Makino" and "MAS-A5" are names of products of their respective owner and are used here
> only to say what this tool reads.

## What it reads, and the read-only promise

- The cell's **Makino database** (SQL Server): device status, alarms, utilisation, recent machining and transport activity,
  orders and pallets.
- Optionally, **folders you choose to share** (for example the cell PC's `C:\Makino\Log`, shared read only): list, read and
  search text files.

MakinoMCP never writes. It does not change the cell, the database or any file.

- Database access is `SELECT` only, and every tool is marked read-only for the MCP client.
- Folder tools can only read text files inside the folders you list. The settings file itself is hidden from them.
- Nothing is sent anywhere except to the Claude app you connected it to.

A login that can only read is recommended: then the database enforces the promise as well as this program.

## Requirements

- Windows 10 or 11.
- Network access from the PC that runs it to the cell's SQL Server.
- A SQL login, or a Windows login, that can read the Makino database.
- Optional: a read-only share of the cell PC's log folder (`C:\Makino\Log`).

## Install

1. Download `MakinoMCP-<version>-win-x64.zip` from the [latest release](https://github.com/GHeart01/Machine_Companion/releases/latest)
   and check it against the `.sha256` file next to it.
2. Unzip it into a folder, ideally one on a share so every PC uses the same copy (for example `\\server\Tools\MakinoMCP`).
3. Run `MakinoMCP.exe`. The setup window opens: enter the server, database and login, press **Test connection**, add any
   folders to share, then **Save**.
4. Open `MakinoMCP.exe` once on each PC that will use Claude. The first time, it copies itself to the PC (about a minute on a
   slow network). After that Claude connects in a second.

Windows SmartScreen may warn that the program is from an unknown publisher: it is not code-signed. Choose **More info**,
then **Run anyway** only if you downloaded it from the release above.

`MakinoMCP.exe` is a small launcher. `current.txt` names the current version in the `versions` folder; the launcher copies
that version once to `%LOCALAPPDATA%\MakinoMCP\app\<version>` and from then on runs the PC's own copy (on the very first
start it runs straight from the share while the copy is made in the background). Because PCs run their own copies, a new
version can be put on the share while people are connected (see [Updating](#updating)). If the share is unreachable it
runs the newest copy already on the PC. Settings are saved in `MakinoMCP.settings.json` beside the launcher.

## Connect Claude

The setup window shows both snippets with your path filled in.

**Claude Code** (tested; once per PC, for your user):

```
claude mcp add --scope user makino -- "\\server\Tools\MakinoMCP\MakinoMCP.exe" serve
```

**Claude Cowork / Claude Desktop** (not yet tested): add this entry to `mcpServers` in the app's configuration, then
restart the app.

```json
"makino": { "command": "\\\\server\\Tools\\MakinoMCP\\MakinoMCP.exe", "args": ["serve"] }
```

The Cowork route is not yet tested. If Cowork doesn't list the MakinoMCP tools after a restart, use Claude Code or
[open an issue](https://github.com/GHeart01/Machine_Companion/issues).

## Updating

1. Put the new version on the share, either way:
   - From a source checkout: `.\publish.ps1 -Target \\server\Tools\MakinoMCP`.
   - From a release zip: copy its `versions\<version>` folder into the share's `versions` folder, then `MakinoMCP.exe`
     and `MakinoMCP.exe.config` if they changed, and `current.txt` last. Keep the share's `MakinoMCP.settings.json`.
     Windows won't overwrite a launcher that is running but does let you rename it, so rename the old `MakinoMCP.exe`
     aside first (for example to `MakinoMCP.exe.old`).
2. Open `MakinoMCP.exe` once on each PC. The first time, it copies the new version to the PC (about a minute on a slow
   network). After that Claude connects in a second.

Claude sessions already running keep using their version until they are restarted.

Every update needs a new version number: a PC that already has a version never copies it again. `publish.ps1` therefore
refuses a version that is already on the share. `-Overwrite` replaces it anyway, and is only for a version no PC has used
yet. `publish.ps1` replaces the launcher only when it changed, renaming the old one to `MakinoMCP.exe.old-<time>`. Old
`versions\*` folders and `.old-*` files can be deleted once no PC uses them.

## What to ask Claude

- "What is every machine in the cell doing right now?"
- "Which alarms keep coming back this week, and on which machine?"
- "Did the cell sit idle overnight? Show me the gaps."
- "Which orders are still open, and where are their pallets?"

## Tools

| Tool | What Claude gets |
| --- | --- |
| `get_cell_overview` | Every device: connected, emergency stop, mode, current action, pallets, open alarms, last activity. |
| `get_alarms` | Open and recent PMC and CNC alarms, newest first, with how long each was open. |
| `get_alarm_summary` | Alarms grouped by device and number, most frequent first. |
| `get_utilization` | Hours busy, idle, down, manual and in error per device and day; OEE where the cell keeps it. |
| `get_recent_activity` | Machining, work-set and vehicle transport results, to spot gaps. |
| `get_orders` | Queued and finished orders with part, status, quantity and due time. |
| `get_pallets` | Each pallet's status, location and fixtures. |
| `list_shared_folders` | The folders you shared and whether each is reachable. |
| `list_files` | Files and subfolders in a shared folder, newest first. |
| `read_file` | A text file, or its last lines or a line range. |
| `search_files` | Plain-text search across a shared folder. |

## Limits

- **History is short.** The cell keeps about the last 200 CNC alarms and, on a typical cell, about three months of PMC
  alarms. Older events are not there to read.
- Results are capped (500 rows and 256 KB per file read, unless you change `limits` in the settings file).
- **Some numbers have no known meaning.** The meanings of some status numbers were worked out from the installed MAS-A5
  software, not from published documentation. A value without a known meaning appears as `code N`.

## Security notes

- The database password is stored **in plain text** in `MakinoMCP.settings.json`. Keep that folder readable only by the
  people who should have the login, and use a read-only login. A Windows login avoids storing a password at all.
- MakinoMCP shows Claude what the database and shared folders contain, so anything in them can end up in your Claude
  conversation. Share only folders you are happy for it to see.
- `MakinoMCP.settings.json` is not part of the release and is ignored by git.

## Build from source

```
dotnet test MakinoMCP.slnx
.\publish.ps1 -Zip
```

The layout is written to `artifacts\layout`. `.\publish.ps1 -Target \\server\Tools\MakinoMCP` puts it on a share without
touching the settings file already there; raise `<Version>` in `src\MakinoMCP\MakinoMCP.csproj` first (see
[Updating](#updating)).

## Licence

MIT, see [LICENSE](LICENSE).
