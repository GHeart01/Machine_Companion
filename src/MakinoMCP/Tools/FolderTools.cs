using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MakinoMCP.Tools;

/// <summary>The shared-folder tools: thin, SharedFolders holds every path rule and cap, ToolContext the rest.</summary>
[McpServerToolType]
public static class FolderTools
{
    private const string FolderParam = "The shared folder's name, exactly as list_shared_folders shows it (never a path).";

    [McpServerTool(Name = "list_shared_folders", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Which folders can you read? The shared folders set up for MakinoMCP (for example the cell PC's log " +
                 "folder or the ADE drop folder): name, path, description, and whether each is reachable right now. " +
                 "Call it before the other file tools to learn the folder names they take.")]
    public static Task<string> ListSharedFolders(ToolContext context, CancellationToken ct) =>
        context.Folder("list_shared_folders", null, f =>
        {
            var folders = f.List();
            return new
            {
                folders,
                note = folders.Count == 0
                    ? "No folders are shared. They are added in the MakinoMCP setup window (run MakinoMCP.exe with no arguments)."
                    : null,
            };
        }, ct);

    [McpServerTool(Name = "list_files", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What is in a shared folder, newest first? Files and subfolders with size and last-modified time. " +
                 "Defaults: the folder's top level, every name, up to the row limit (500 unless the settings say " +
                 "otherwise). Use pattern (e.g. *.log) or subpath to narrow a large folder. AlarmNotification*.xml " +
                 "files (they hold the cell's e-mail password) are never listed. In the cell's Auto Data Entry folders " +
                 "the note says what the files mean: ADE_Error holds the original files whose import failed (moved there " +
                 "and renamed with the failure time; no reason inside, and they may be partly applied: the reason is in " +
                 "the cell's AutoDataEntry.log or Exception.log), ADE_Success empty .success markers per imported file, " +
                 "and ADE_Success\\FTN and \\ITN tool data the cell exports, not imports.")]
    public static Task<string> ListFiles(
        ToolContext context,
        [Description(FolderParam)] string folder,
        [Description("A subfolder inside the shared folder, e.g. 2026\\11. Default: the top level.")] string? subpath = null,
        [Description("A file name pattern with * and ?, e.g. *.log or ADE_*.xml. Default: every name.")] string? pattern = null,
        [Description("How many entries at most. Default and maximum: the row limit (500 unless set otherwise).")] int? limit = null,
        CancellationToken ct = default) =>
        context.Folder("list_files", folder, f => f.ListFiles(folder, subpath, pattern, limit), ct);

    [McpServerTool(Name = "read_file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What does this file say? Reads a text file inside a shared folder (logs, XML, CSV, NC programs; " +
                 "binary files are refused). Default: .log files show their last 500 lines, other files from the top. " +
                 "Reads stop at the size limit (256 KB unless set otherwise) and say they were cut; then use tailLines " +
                 "for the end, or fromLine with lineCount for a range. The result gives the line numbers shown and the " +
                 "file's total lines. Password values in .config and .xml files show as ***; AlarmNotification*.xml " +
                 "files are refused. A file inside the ADE folders comes with a note on what it is (see list_files).")]
    public static Task<string> ReadFile(
        ToolContext context,
        [Description(FolderParam)] string folder,
        [Description("The file's path inside the shared folder, as list_files shows it, e.g. 2026\\11\\cell.log.")] string path,
        [Description("Only the last N lines. Don't combine with fromLine/lineCount.")] int? tailLines = null,
        [Description("First line to show (1 = the first line), with lineCount.")] int? fromLine = null,
        [Description("How many lines to show from fromLine.")] int? lineCount = null,
        CancellationToken ct = default) =>
        context.Folder("read_file", folder, f => f.ReadFile(folder, path, tailLines, fromLine, lineCount), ct);

    [McpServerTool(Name = "search_files", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Where does this text appear? Searches the text files in a shared folder and its subfolders " +
                 "(case-insensitive, plain text, not a regex) and returns matching lines with file and line number, " +
                 "capped at the row limit. Use it to find an alarm number, program name, pallet or error message in " +
                 "the cell's logs. Defaults: every file, any age; narrow with pattern (e.g. *.log) and sinceHours on " +
                 "large folders. Password values in .config and .xml files show as *** (and never match); " +
                 "AlarmNotification*.xml files are skipped.")]
    public static Task<string> SearchFiles(
        ToolContext context,
        [Description(FolderParam)] string folder,
        [Description("The text to look for.")] string text,
        [Description("Only files whose name matches, e.g. *.log. Default: every file.")] string? pattern = null,
        [Description("Only files changed in the last N hours. Default: any age.")] int? sinceHours = null,
        CancellationToken ct = default) =>
        context.Folder("search_files", folder, f => f.Search(folder, text, pattern, sinceHours), ct);
}
