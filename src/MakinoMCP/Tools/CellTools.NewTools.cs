using System.ComponentModel;
using MakinoMCP.Core.Folders;
using ModelContextProtocol.Server;

namespace MakinoMCP.Tools;

// The newer tools: get_macro_values, get_work_set, find_serial (cell database) and explain_ade_file (a shared folder).
public static partial class CellTools
{
    [McpServerTool(Name = "get_macro_values", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What values did the machines' programs upload? Macro variables the cell read from a machine when its " +
                 "NC program asked for them (M480), newest first: time, machine, variable (the macro variable number), " +
                 "value, the pallet, main and executing program and sequence number the machine reported, and the " +
                 "machining result running on that machine at that time (part, revision, process, program, fixture; " +
                 "matched by time as the vendor's report does, so it can be missing between cycles). Typically probe " +
                 "or measurement results written by the program. note says when the cell has no uploaded values at all " +
                 "(the feature isn't used) or nothing matched. Default: the last 7 days, every machine and variable. The " +
                 "cell keeps them as long as its result history (historyDays).")]
    public static Task<string> GetMacroValues(
        ToolContext context,
        [Description("How many days back to look, from serverTime. Default 7.")] int days = 7,
        [Description(DeviceParam)] string? device = null,
        [Description("Only this macro variable number, e.g. 500. Leave empty for every variable.")] int? variable = null,
        [Description("Only values uploaded with this pallet number. Leave empty for every pallet.")] int? pallet = null,
        CancellationToken ct = default) =>
        context.Database("get_macro_values", (r, t) => r.GetMacroValuesAsync(days, device, variable, pallet, t), ct);

    [McpServerTool(Name = "get_work_set", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What is each work-set station asking the operator to do? The station monitor as text. Per station: " +
                 "status, mode, open (only when closed), the instruction while busy with since and plannedFinish, and " +
                 "the pallets on it. Per fixture with an instruction: pallet, fixture, instruction in the monitor's " +
                 "words with quantities (clamp, unclamp, unclamp (scrap), unclamp and clamp, the cancels, re-machining " +
                 "with its program, other, inspection, …), leavesAs (what the fixture becomes once the operator " +
                 "presses Finish: machining, machining suspended, work-set hold, …), the fixture's name, face comment " +
                 "and status reason, and two sides: unclamp (the part coming off: order, part, revision, process, job, " +
                 "their comments, information file, last program, the machine and how its machining finished, work " +
                 "status) and clamp (the part going on, with its quantity and information file). " +
                 "waitingForInspectionConfirmation: the cell wants a scrapped part's inspection confirmed at the station " +
                 "first. detail shows the raw instruction progress when the monitor has no words for it. A " +
                 "long-waiting instruction usually means the station waits for the operator's Finish; MakinoMCP never " +
                 "presses anything. notAvailable names what this cell or login can't show.")]
    public static Task<string> GetWorkSet(
        ToolContext context,
        [Description("Only this work-set station, by its name as get_cell_overview shows it. Leave empty for every station.")] string? device = null,
        CancellationToken ct = default) =>
        context.Database("get_work_set", (r, t) => r.GetWorkSetAsync(device, t), ct);

    [McpServerTool(Name = "find_serial", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Where was this part serial number recorded? Searches the serial numbers the cell recorded at " +
                 "machining, clamp and unclamp (all ten serial slots, text contained, case-insensitive), newest first: " +
                 "time the job finished, device, kind (machining, work-set clamp, work-set unclamp), fixture, operation " +
                 "sequence, quantity, the matching serials (slot, serial, status as \"code N\": its meanings aren't " +
                 "known) and the result each belongs to (pallet, order, part, revision, process, program). note says " +
                 "when the cell records no serials at all (serial tracking isn't used) or nothing matched. Default: the " +
                 "last 90 days, every device.")]
    public static Task<string> FindSerial(
        ToolContext context,
        [Description("The serial number, or part of it.")] string serial,
        [Description("How many days back to look, from serverTime. Default 90.")] int days = 90,
        [Description(DeviceParam)] string? device = null,
        CancellationToken ct = default) =>
        context.Database("find_serial", (r, t) => r.FindSerialAsync(serial, days, device, t), ct);

    [McpServerTool(Name = "explain_ade_file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Why was this ADE file rejected, or what will it do? Reads an automatic data entry file (MASData XML) " +
                 "in a shared folder, such as the ADE drop folder or its error folder, and explains it by the cell's " +
                 "data entry rules: verdict, sections (with item counts; ignored when the cell doesn't read that " +
                 "section), and per item its section, element, action (add, update, delete), key (name, revision, " +
                 "number, orderName), effect (an add of something that exists becomes an update) and problems. " +
                 "Checked: well-formed XML and a <MASData> root; only the expected item element in each section (an " +
                 "XML comment inside a section counts as a failed item); a valid action (orders, parts and common " +
                 "fixtures can't be deleted); the key attribute; orders: Priority 0-999, Status -1/0/4 (6 only for an " +
                 "existing order), PartName for a new order; NC programs: Type 1, 4 or 8 and a Filename for an add. " +
                 "One failing item makes the cell move the whole file to its error folder, though the other items are " +
                 "still carried out. A file named [date_time]… is one the cell rejected; it doesn't record why. " +
                 "notChecked names what depends on the cell's own data. Never moves, changes or deletes the file.")]
    public static Task<string> ExplainAdeFile(
        ToolContext context,
        [Description("The shared folder's name, exactly as list_shared_folders shows it (never a path).")] string folder,
        [Description("The file's path inside the shared folder, as list_files shows it.")] string path,
        CancellationToken ct = default) =>
        context.Folder("explain_ade_file", folder, f => Explain(f, folder, path, context.Settings.Settings?.Limits.MaxRows ?? 0), ct);

    private static AdeExplanation Explain(SharedFolders folders, string folder, string path, int maxRows)
    {
        // Through SharedFolders, so the path rules, refusals and password masking apply; the read is size-capped.
        var read = folders.ReadFile(folder, path, tailLines: null, fromLine: 1, lineCount: null);
        return AdeFile.Explain(read.Path, read.Text, read.Truncated, maxRows < 1 ? 500 : maxRows);
    }
}
