namespace MakinoMCP.Core.Folders;

/// <summary>What the cell's Auto Data Entry (ADE) folders mean, as a short note on listings and reads inside them.
/// The folders are found by their stock names anywhere on the path (ADE_Error, ADE_Success and its FTN / ITN export
/// folders); nothing is parsed, only the path is looked at.</summary>
public static class AdeHints
{
    public const string ErrorFolder = "ADE_Error";

    public const string SuccessFolder = "ADE_Success";

    public const string Error =
        "ADE_Error: a file here is the original import file, moved here when its import failed and renamed with the " +
        "failure date and time in brackets. It holds no reason, and it may be partly applied: sections, and items within " +
        "a section, before the failing one can already be entered. The reason is only in the cell's AutoDataEntry.log " +
        "(or Exception.log) in C:\\Makino\\Log; share that folder to read it.";

    public const string Success =
        "ADE_Success: a .success file is an empty marker named after an imported file (without its extension); its " +
        "time is when the import finished. An ADD for an order that already exists is applied as an UPDATE.";

    public const string Export =
        "ADE_Success\\FTN and \\ITN hold tool data the cell exports itself, not imports, and they are never imported " +
        "again. Files named after the FTN are rewritten on each change (the current definition, not a history); files " +
        "named with a time stamp do form a history.";

    /// <summary>The note for a path (full, or relative to a shared folder whose own full path is included), or null
    /// outside the ADE folders.</summary>
    public static string? For(string path)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (Is(parts[i], ErrorFolder)) return Error;
            if (Is(parts[i], SuccessFolder))
            {
                return i + 1 < parts.Length && (Is(parts[i + 1], "FTN") || Is(parts[i + 1], "ITN")) ? Export : Success;
            }
        }
        return null;
    }

    private static bool Is(string name, string wanted) => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase);
}
