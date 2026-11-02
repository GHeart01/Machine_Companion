namespace MakinoMCP.Core.Folders;

/// <summary>A refusal to read or list something. The message is written to be shown to Claude and never holds file contents.</summary>
public sealed class FolderAccessException(string message) : Exception(message);

public sealed record FolderStatus(string Name, string Path, string Description, bool Reachable, string? Problem);

/// <param name="Path">Relative to the shared folder, with backslashes.</param>
/// <param name="Size">Bytes; null for a subfolder.</param>
public sealed record FileEntry(string Path, long? Size, DateTimeOffset Modified, bool IsFolder);

/// <param name="Matched">How many entries matched before the limit was applied.</param>
public sealed record FileListing(string Folder, string Path, IReadOnlyList<FileEntry> Entries, int Matched, bool Capped, string? Note);

public sealed record FileRead(string Path, string Text, int FirstLine, int LastLine, int TotalLines, bool Truncated, string? Note);

public sealed record Hit(string Path, int Line, string Text);

public sealed record SearchResult(IReadOnlyList<Hit> Hits, bool Capped, int FilesSearched, string? Note);
