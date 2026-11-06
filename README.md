# MakinoMCP

MakinoMCP lets Claude look at what a Makino MAS-A5 machining cell is doing. Ask in plain language, for example
"is anything alarmed right now?", and Claude answers from the cell's own database and, if you share them, its log files.

It is a small [Model Context Protocol](https://modelcontextprotocol.io) server for Windows. It only reads.

> Not made or endorsed by Makino. "Makino" and "MAS-A5" are names of products of their respective owner and are used here
> only to say what this tool reads.

## What it reads, and the read-only promise

- The cell's **Makino database** (SQL Server): device status, alarms, utilisation, recent machining and transport activity,
  orders, pallets, tools, part routings and production counts.
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

`MakinoMCP.exe` is a small launcher. `current.txt` names the current build in the `versions` folder; the launcher copies
that build once to `%LOCALAPPDATA%\MakinoMCP\app\<build>` and from then on runs the PC's own copy. While a new build
is being copied in the background, Claude is served by the PC's previous copy; on a PC that has none yet, it runs
straight from the share, which can be too slow for Claude's first connection (open `MakinoMCP.exe` once first). Because PCs run their own copies, a new
build can be put on the share while people are connected (see [Updating](#updating)). If the share is unreachable it
runs the newest copy already on the PC. Settings are saved in `MakinoMCP.settings.json` beside the launcher.

The share holds:

| | |
|---|---|
| `MakinoMCP.exe`, `MakinoMCP.exe.config` | the launcher: Claude starts it; open it yourself for setup |
| `MakinoMCP.settings.json` | your cell login and shared folders, written by the setup window |
| `MakinoMCP.settings.example.json` | an example settings file |
| `current.txt` | the build the launcher starts |
| `versions\<build>\` | the app (`MakinoMCP.exe`), `build.txt` (where and when it was built), `version.txt`, `LICENSE` |

## Connect Claude

**The easy way, on each PC:** open `MakinoMCP.exe` and use the **Use MakinoMCP in Claude on this PC** switch. On adds
MakinoMCP to Claude Code and to Claude desktop (Cowork, not yet tested) for your Windows account; restart Claude
to see the tools. Off removes it from Claude on this PC and stops it. Other PCs aren't affected.

To connect by hand instead, the setup window shows both snippets with your path filled in.

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
   - From a release zip: copy its `versions\<build>` folder into the share's `versions` folder, then `MakinoMCP.exe`
     and `MakinoMCP.exe.config` if they changed, and `current.txt` last. Keep the share's `MakinoMCP.settings.json`.
     Windows won't overwrite a launcher that is running but does let you move it, so move the old `MakinoMCP.exe`
     aside first (for example to `versions\old-launchers\MakinoMCP.exe.<date>`).
2. Nothing to do on the PCs. The next Claude session on each PC still uses its previous copy and copies the new build
   in the background; the session after that runs the new build and removes the PC's older copies. To switch a PC
   over at once, open `MakinoMCP.exe` there (it copies the new build before the setup window opens).

Claude sessions already running keep using their build until they are restarted.

Every publish is a new build, named `<version>-<date>-<time>` (for example `0.2.0-20261106-0930`), and each PC copies
each build once. `publish.ps1` replaces the launcher only when it changed: one that running sessions still hold open is
moved into `versions\old-launchers` and deleted by a later publish once nothing uses it. It keeps the newest three
builds on the share. To roll back, write an older build's folder name (see `versions`) into `current.txt`.

## What to ask Claude

- "What is every machine in the cell doing right now?"
- "Which alarms keep coming back this week, and on which machine?"
- "Did the cell sit idle overnight? Show me the gaps."
- "Which orders are still open, and where are their pallets?"
- "What needs a person right now?"
- "Which tools are near the end of their life, and which alarmed this week?"
- "How many parts did we make this week, and how far along is each order?"
- "How is this part made, and are its tools in the machines?"

## Tools

| Tool | What Claude gets |
| --- | --- |
| `get_cell_overview` | Every device: connected, emergency stop, status (down, idle, busy, manual, error), recovery request, mode, current action, pallets, open alarms, last activity. Machines add tool status, CNC run state, operator call, current job and program; the cart adds its transport from and to. `historyDays`: how long the cell keeps PMC alarms and results. |
| `get_alarms` | Open and recent PMC and CNC alarms, newest first, with how long each was open. |
| `get_alarm_summary` | Alarms grouped by device and number, most frequent first. |
| `get_utilization` | Hours busy, idle, down, manual and in error per device and day; OEE where the cell keeps it. |
| `get_recent_activity` | Machining, work-set and vehicle transport results, to spot gaps. |
| `get_orders` | Queued and finished orders with part, status, quantity, completed, scrap and remaining quantities, machining suspend, and due time. |
| `get_pallets` | Each pallet's status, location and fixtures. |
| `get_tools` | Which tools need attention and where a tool is: status per device, life used and remaining, measured length and diameter. |
| `get_tool_changes` | What changed in the tool data (measurements, imports, edits, alarms), newest first; routine life-count updates and tools that only reached their life warning are counted, and listed only on request. Default: the last 3 days. |
| `get_tool_alarms` | Which tools alarmed lately (worn out, broken, ...), with the tool's life and size then. |
| `get_tool_movements` | Tools put into or taken out of the machines, with who and why. |
| `get_part_routing` | How a part is made: processes, jobs, programs and fixtures, and whether the tools each program needs are on the machines. The tool check is approximate: it uses the tool set the cell registered for each program (subprograms included) and doesn't check remaining life. |
| `get_production` | Parts completed, scrapped and in process per order, plus per-day totals, counted the way the cell's own order screen counts. |
| `get_attention` | One list of what waits on a person: recovery requests, errors, operator calls, damaged fixtures, tools in alarm; parked fixtures are counted. |
| `list_shared_folders` | The folders you shared and whether each is reachable. |
| `list_files` | Files and subfolders in a shared folder, newest first. |
| `read_file` | A text file, or its last lines or a line range. |
| `search_files` | Plain-text search across a shared folder. |

## Tool data

- **Units.** Tool lengths and diameters are in the cell's own unit (its tool offset unit, so inch on some cells), not
  always mm. Tool results say which in `lengthUnit`.
- **Tool life** is shown as used, remaining and percent, already corrected for whether the cell counts life up or down;
  if the cell doesn't say, up is assumed and the result says so (`lifeDirectionAssumed`). Time-based life is stored in
  seconds and shown in minutes.
- **Tool status:** an alarm flag beats a stored "ready" (and a life warning flag shows as life warning); otherwise it is
  the stored status.
- **Compact by default.** Big lists (routine life updates and steps to life warning, parked fixtures, idle orders) are
  counted, not listed; `includeLifeUpdates`, `includeParked` or `includeIdle` lists them.
- **Tables a login can't read.** Where the database login cannot read a table, MakinoMCP uses the vendor's view of it when
  there is one; otherwise the result names what is `notAvailable`.

## Make more of it

- Share the cell PC's `C:\Makino\Log` folder read only and add it in the setup window. Claude can then read logs such
  as `Exception.log` and `System.log` next to the alarms the database shows (other logs may live elsewhere on the PC).
- Also share the Makino install's `Message\<language>` folder. Its `.ini` files hold the cause and recovery text of CNC
  alarms, so Claude can explain a CNC alarm number in words. The alarm text of the other devices is in Access `.mdb`
  files, which the text tools can't read.

## Limits

- **History is short.** `historyDays` in the overview says how many days the cell keeps PMC alarms and result history
  (90 days unless the cell is set otherwise). The cell keeps daily production counts for about three months. CNC alarms
  are limited to about the last 200 regardless. Older events are not there to read.
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
touching the settings file already there, as a new build (see [Updating](#updating)).

## Licence

MIT, see [LICENSE](LICENSE).
