namespace MakinoMCP.Core.Cell;

/// <summary>A device's PMC alarm help files as the cell names them (full paths on the cell PC): a machine's .ini
/// files (PMCAlarmMessageFilename, then PMC2AlarmMessageFilename for alarms 990000 and up), or the vehicle's, a
/// station's or an aux device's .mdb database (AlarmDBFilename).</summary>
public sealed record AlarmHelpFiles(string Device, IReadOnlyList<string> Files);
