using System.Text;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Core.Folders;

/// <summary>
/// The only file access Claude gets: read-only, inside the configured shared folders and their subfolders, never the
/// protected paths (the settings file with the password, the program folders), never the vendor files holding the cell's
/// e-mail password (AlarmNotification*.xml), never binary files, never unbounded. Password values in .config and .xml
/// files are shown as *** (<see cref="VendorSecrets"/>).
/// </summary>
public sealed class SharedFolders
{
    private const int BinarySniffBytes = 8192;
    private const int DefaultLogTail = 500;
    private const long SearchMaxFileBytes = 50L * 1024 * 1024;
    private const int SearchMaxFiles = 50_000;
    private const int HitTextChars = 300;
    private const string SettingsFileName = "MakinoMCP.settings.json";
    private const string AlarmNotificationPrefix = "AlarmNotification";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<SharedFolder> _folders;
    private readonly IReadOnlyList<string> _protected;
    private readonly int _maxRows;
    private readonly int _maxChars;
    private readonly int _maxFileKB;

    public SharedFolders(IReadOnlyList<SharedFolder> folders, Limits limits, IReadOnlyList<string> protectedPaths)
    {
        _folders = folders ?? [];
        var l = limits ?? new Limits();
        _maxRows = l.MaxRows < 1 ? 500 : l.MaxRows;
        _maxFileKB = l.MaxFileKB < 1 ? 256 : l.MaxFileKB;
        _maxChars = _maxFileKB * 1024;
        _protected = (protectedPaths ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => SafeFullPath(p.Trim()))
            .OfType<string>()
            .Select(TrimSep)
            .ToList();
    }

    // ---------------------------------------------------------------- list

    public IReadOnlyList<FolderStatus> List()
    {
        var probes = _folders.Select(f => Task.Run(() => Probe(f))).ToArray();
        var deadline = DateTime.UtcNow + ProbeTimeout;
        var results = new List<FolderStatus>();
        for (var i = 0; i < probes.Length; i++)
        {
            var f = _folders[i];
            var name = f.Name ?? "";
            var path = f.Path ?? "";
            var desc = f.Description ?? "";
            if (string.IsNullOrWhiteSpace(f.Path))
            {
                results.Add(new FolderStatus(name, path, desc, false, "no path set"));
                continue;
            }
            string? problem;
            try
            {
                var wait = deadline - DateTime.UtcNow;
                problem = probes[i].Wait(wait > TimeSpan.Zero ? wait : TimeSpan.Zero)
                    ? probes[i].Result
                    : $"did not answer within {(int)ProbeTimeout.TotalSeconds} seconds (offline or very slow)";
            }
            catch (Exception)
            {
                problem = "could not be checked";
            }
            results.Add(new FolderStatus(name, path, desc, problem is null, problem));
        }
        return results;
    }

    private static string? Probe(SharedFolder f)
    {
        if (string.IsNullOrWhiteSpace(f.Path)) return "no path set";
        try
        {
            return Directory.Exists(f.Path.Trim()) ? null : "folder not found or offline";
        }
        catch (Exception)
        {
            return "folder not found or offline";
        }
    }

    // ---------------------------------------------------------------- list files

    public FileListing ListFiles(string folder, string? subPath, string? pattern, int? limit)
    {
        var (root, dir) = Resolve(folder, subPath);
        if (!Directory.Exists(dir))
        {
            throw File.Exists(dir)
                ? new FolderAccessException($"'{Display(subPath)}' is a file, not a folder. Use read_file to read it.")
                : new FolderAccessException($"There is no folder '{Display(subPath)}' inside shared folder '{folder.Trim()}'.");
        }

        string? match = null;
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            if (pattern.IndexOfAny(['\\', '/', ':']) >= 0)
            {
                throw new FolderAccessException("The pattern matches names in the listed folder only. Use something like *.log, without folders in it.");
            }
            match = pattern.Trim();
        }
        var rootReal = RealPath(root);

        var take = Math.Min(limit is > 0 ? limit.Value : _maxRows, _maxRows);
        var entries = new List<FileSystemInfo>();
        try
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (match is not null && !NameMatches(match, info.Name)) continue;
                if (IsHidden(root, rootReal, info.FullName)) continue;
                if (IsLink(info) && !LinkStaysInside(info, root)) continue;
                entries.Add(info);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FolderAccessException($"Could not read the folder '{Display(subPath)}' (no permission, or it is offline).");
        }

        var sorted = entries.OrderByDescending(SafeWriteTime).ToList();
        var shown = sorted.Take(take).Select(e => new FileEntry(
            Relative(root, e.FullName),
            e is FileInfo fi ? SafeLength(fi) : null,
            SafeOffset(SafeWriteTime(e)),
            e is DirectoryInfo)).ToList();

        var capped = sorted.Count > shown.Count;
        string? note = capped
            ? $"Showing the newest {shown.Count} of {sorted.Count} matching entries (the limit is {take}). Use a pattern or a subfolder to narrow it."
            : null;
        if (AdeHints.For(dir) is { } ade) note = note is null ? ade : $"{note} {ade}";
        return new FileListing(folder.Trim(), Relative(root, dir), shown, sorted.Count, capped, note);
    }

    // ---------------------------------------------------------------- read file

    public FileRead ReadFile(string folder, string path, int? tailLines, int? fromLine, int? lineCount)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FolderAccessException("Say which file to read, as a path inside the shared folder.");
        }
        if (tailLines is not null && (fromLine is not null || lineCount is not null))
        {
            throw new FolderAccessException("Use either tailLines, or fromLine with lineCount, not both.");
        }
        if (tailLines is < 1)
        {
            throw new FolderAccessException("tailLines must be 1 or more.");
        }

        var (root, full) = Resolve(folder, path);
        if (Directory.Exists(full))
        {
            throw new FolderAccessException($"'{path.Trim()}' is a folder. Use list_files to see what is in it.");
        }
        if (!File.Exists(full))
        {
            throw new FolderAccessException($"There is no file '{path.Trim()}' inside shared folder '{folder.Trim()}'.");
        }
        RequireText(full, path.Trim());
        var mask = MasksSecrets(new FileInfo(full));

        var rel = Relative(root, full);
        var defaultedToLogTail = false;
        int? tail = tailLines;
        if (tail is null && fromLine is null && lineCount is null
            && full.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            tail = DefaultLogTail;
            defaultedToLogTail = true;
        }

        var from = Math.Max(fromLine ?? 1, 1);
        long upto = lineCount is > 0 ? (long)from + lineCount.Value : long.MaxValue;

        var queue = new Queue<(int No, string Text)>();
        var list = new List<(int No, string Text)>();
        long chars = 0;
        var rangeFull = false;
        var droppedForSize = false;
        var nextLine = 0;
        var total = 0;
        var cutLongLine = false;

        void Reset()
        {
            queue.Clear();
            list.Clear();
            chars = 0;
            droppedForSize = false;
            rangeFull = false;
            nextLine = 0;
            total = 0;
            cutLongLine = false;
        }

        ScanFile(full, _maxChars, (n, text, cut) =>
        {
            // Masked before it is counted, so the size limit applies to what is shown.
            if (mask) text = VendorSecrets.MaskLine(text);
            total = n;
            cutLongLine |= cut;
            if (tail is not null)
            {
                queue.Enqueue((n, text));
                chars += text.Length + 1;
                if (queue.Count > tail)
                {
                    chars -= queue.Dequeue().Text.Length + 1;
                }
                // Memory stays bounded by size as well as by line count.
                while (chars > _maxChars && queue.Count > 1)
                {
                    chars -= queue.Dequeue().Text.Length + 1;
                    droppedForSize = true;
                }
            }
            else if (n >= from && n < upto && !rangeFull)
            {
                if (list.Count > 0 && chars + text.Length + 1 > _maxChars)
                {
                    rangeFull = true;
                    nextLine = n;
                }
                else
                {
                    list.Add((n, text));
                    chars += text.Length + 1;
                }
            }
            return true;
        }, Reset);

        var truncated = false;
        var notes = new List<string>();
        List<(int No, string Text)> lines;
        if (tail is not null)
        {
            var kept = queue.ToList();
            if (droppedForSize)
            {
                truncated = true;
                notes.Add($"Only the last {kept.Count} lines fit in the {_maxFileKB} KB limit. Read earlier lines with fromLine and lineCount.");
            }
            lines = kept;
            if (defaultedToLogTail && total > DefaultLogTail)
            {
                notes.Add($"This is a log file, so it shows the last {DefaultLogTail} of {total} lines. Use fromLine and lineCount, or tailLines, to see other parts.");
            }
        }
        else
        {
            lines = list;
            if (rangeFull)
            {
                truncated = true;
                notes.Add($"Stopped at the {_maxFileKB} KB limit after line {nextLine - 1}. Read the rest with fromLine={nextLine}.");
            }
            if (lines.Count == 0 && total < from)
            {
                notes.Add(total == 0 ? "The file is empty." : $"The file has only {total} lines.");
            }
        }
        if (cutLongLine)
        {
            truncated = true;
            notes.Add($"One or more very long lines were cut at {_maxFileKB} KB.");
        }
        if (AdeHints.For(full) is { } ade) notes.Add(ade);

        var text = string.Join('\n', lines.Select(l => l.Text));
        var first = lines.Count > 0 ? lines[0].No : 0;
        var last = lines.Count > 0 ? lines[^1].No : 0;
        return new FileRead(rel, text, first, last, total, truncated, notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    // ---------------------------------------------------------------- search

    public SearchResult Search(string folder, string text, string? pattern, int? sinceHours)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new FolderAccessException("Say what text to search for.");
        }
        if (pattern is not null && pattern.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            throw new FolderAccessException("The pattern matches file names only. Use something like *.log, without folders in it.");
        }
        var (root, start) = Resolve(folder, null);
        if (!Directory.Exists(start))
        {
            throw new FolderAccessException($"Shared folder '{folder.Trim()}' is not reachable right now.");
        }

        var match = string.IsNullOrWhiteSpace(pattern) ? null : pattern.Trim();
        var rootReal = RealPath(root);
        var cutoff = sinceHours is > 0 ? DateTime.Now.AddHours(-sinceHours.Value) : (DateTime?)null;

        var files = new List<FileInfo>();
        var tooMany = false;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(start));
        while (pending.Count > 0 && !tooMany)
        {
            var dir = pending.Pop();
            List<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                if (IsHidden(root, rootReal, child.FullName)) continue;
                if (child is DirectoryInfo d)
                {
                    // Links to folders are not followed while searching (avoids loops and escapes).
                    if (!IsLink(d)) pending.Push(d);
                    continue;
                }
                var f = (FileInfo)child;
                if (IsLink(f) && !LinkStaysInside(f, root)) continue;
                if (match is not null && !NameMatches(match, f.Name)) continue;
                if (cutoff is not null && SafeWriteTime(f) < cutoff) continue;
                if (files.Count >= SearchMaxFiles) { tooMany = true; break; }
                files.Add(f);
            }
        }
        files.Sort((a, b) => SafeWriteTime(b).CompareTo(SafeWriteTime(a)));

        var hits = new List<Hit>();
        var skippedBig = new List<string>();
        var searched = 0;
        var capped = false;
        foreach (var f in files)
        {
            if (capped) break;
            var rel = Relative(root, f.FullName);
            if (SafeLength(f) > SearchMaxFileBytes)
            {
                skippedBig.Add(rel);
                continue;
            }
            var local = new List<Hit>();
            try
            {
                if (LooksBinary(f.FullName)) continue;
                searched++;
                var mask = MasksSecrets(f);
                ScanFile(f.FullName, 2000, (n, line, _) =>
                {
                    // Masked before matching, so searching for a password finds nothing.
                    if (mask) line = VendorSecrets.MaskLine(line);
                    if (line.Contains(text, StringComparison.OrdinalIgnoreCase))
                    {
                        local.Add(new Hit(rel, n, line.Length > HitTextChars ? line[..HitTextChars] + "..." : line));
                        if (hits.Count + local.Count >= _maxRows) return false;
                    }
                    return true;
                }, local.Clear);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FolderAccessException)
            {
                continue; // unreadable right now: skip quietly
            }
            hits.AddRange(local);
            if (hits.Count >= _maxRows) capped = true;
        }

        var notes = new List<string>();
        if (capped) notes.Add($"Stopped at {_maxRows} matches; narrow the search with a pattern, sinceHours or more specific text.");
        if (skippedBig.Count > 0)
        {
            var shown = string.Join(", ", skippedBig.Take(10));
            var more = skippedBig.Count > 10 ? $" and {skippedBig.Count - 10} more" : "";
            notes.Add($"Skipped files over 50 MB: {shown}{more}.");
        }
        if (tooMany) notes.Add($"Only the first {SearchMaxFiles} files found were considered.");
        return new SearchResult(hits, capped, searched, notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    // ---------------------------------------------------------------- path safety

    /// <summary>Finds the shared folder and turns the relative path into a full path that is provably inside it.</summary>
    private (string Root, string Full) Resolve(string? folderName, string? subPath)
    {
        var shared = FindFolder(folderName);
        var name = (shared.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(shared.Path))
        {
            throw new FolderAccessException($"Shared folder '{name}' has no path set in the MakinoMCP settings.");
        }
        var rootFull = SafeFullPath(shared.Path.Trim())
            ?? throw new FolderAccessException($"Shared folder '{name}' has a path that is not valid.");
        var root = TrimSep(rootFull);

        var sub = (subPath ?? "").Trim();
        if (sub.Length > 0 && (Path.IsPathRooted(sub) || sub[0] is '\\' or '/' || sub.Contains(':')))
        {
            throw new FolderAccessException(
                $"Give a path relative to shared folder '{name}' (for example 'logs\\today.log'), not a full path or drive letter.");
        }

        var full = sub.Length == 0 ? root : SafeFullPath(Path.Combine(root, sub));
        if (full is null)
        {
            throw new FolderAccessException("That is not a valid file path.");
        }
        full = TrimSep(full);
        if (!IsAtOrInside(full, root))
        {
            throw Outside(name);
        }

        // Walk each segment: a junction or symlink that leads out of the shared folder is refused.
        var rootReal = RealPath(root);
        var current = root;
        var real = rootReal;
        var rest = Relative(root, full);
        foreach (var segment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
            {
                real = Path.Combine(real, segment);
                continue;
            }
            if (IsLink(info))
            {
                var target = ResolvedTarget(info);
                if (target is null || !(IsAtOrInside(target, root) || IsAtOrInside(target, rootReal)))
                {
                    throw Outside(name);
                }
                real = target;
            }
            else
            {
                real = Path.Combine(real, segment);
            }
        }

        if (IsVendorSecretName(full) || IsVendorSecretName(real))
        {
            throw new FolderAccessException(
                "AlarmNotification*.xml files hold the cell's e-mail password and are never shared. Ask for something else in the shared folder.");
        }
        if (IsSettingsName(full) || IsSettingsName(real) || IsProtected(full) || IsProtected(real))
        {
            throw new FolderAccessException(
                "That file belongs to MakinoMCP itself (its settings or program files) and is never shared. Ask for something else in the shared folder.");
        }
        return (root, full);
    }

    private SharedFolder FindFolder(string? name)
    {
        var wanted = (name ?? "").Trim();
        var found = _folders.FirstOrDefault(f => string.Equals((f.Name ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        if (found is not null && wanted.Length > 0) return found;

        var names = _folders.Select(f => (f.Name ?? "").Trim()).Where(n => n.Length > 0).ToList();
        throw new FolderAccessException(names.Count == 0
            ? "No folders are shared. The MakinoMCP settings have no shared folders set up."
            : $"There is no shared folder named '{wanted}'. The shared folders are: {string.Join(", ", names)}.");
    }

    private static FolderAccessException Outside(string folderName) => new(
        $"That path is outside shared folder '{folderName}'. Only files inside a shared folder and its subfolders can be read.");

    private bool IsProtected(string fullPath)
    {
        var p = TrimSep(fullPath);
        foreach (var prot in _protected)
        {
            if (IsAtOrInside(p, prot)) return true;
        }
        return false;
    }

    private static bool IsLink(FileSystemInfo info)
    {
        try { return info.Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception) { return false; }
    }

    private static string? ResolvedTarget(FileSystemInfo info)
    {
        try
        {
            var t = info.ResolveLinkTarget(returnFinalTarget: true);
            return t is null ? null : TrimSep(t.FullName);
        }
        catch (Exception) { return null; }
    }

    private static string RealPath(string path)
    {
        var info = new DirectoryInfo(path);
        return IsLink(info) ? ResolvedTarget(info) ?? path : path;
    }

    private bool LinkStaysInside(FileSystemInfo info, string root)
    {
        var target = ResolvedTarget(info);
        if (target is null) return false;
        return (IsAtOrInside(target, root) || IsAtOrInside(target, RealPath(root)))
            && !IsProtected(target) && !IsSettingsName(target) && !IsVendorSecretName(target);
    }

    private static bool IsAtOrInside(string candidate, string container)
    {
        if (string.Equals(candidate, container, StringComparison.OrdinalIgnoreCase)) return true;
        // A drive root such as C:\ already ends with a separator.
        var prefix = container.EndsWith('\\') ? container : container + '\\';
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>MakinoMCP.settings.json (holds the password) is never shared, wherever it is and however it is spelled,
    /// nor any file whose name starts with it (the .tmp written while saving, backups).</summary>
    private static bool IsSettingsName(string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd('\\', '/')).TrimEnd(' ', '.');
        return name.StartsWith(SettingsFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>AlarmNotification*.xml (the cell's alarm e-mail settings) holds the SMTP password encrypted with a key that
    /// ships with the vendor software, so it is as good as plain text: never shared, however it is spelled.</summary>
    private static bool IsVendorSecretName(string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd('\\', '/')).TrimEnd(' ', '.');
        return name.StartsWith(AlarmNotificationPrefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether lines read from this file get their password values masked: a .config or .xml file, by its
    /// own name or, for a link, by the name of the file it points at.</summary>
    private static bool MasksSecrets(FileInfo file) =>
        VendorSecrets.Applies(file.FullName)
        || (IsLink(file) && ResolvedTarget(file) is { } target && VendorSecrets.Applies(target));

    /// <summary>True when the entry, by its path here or by its real path (root reached through a link), must stay hidden.</summary>
    private bool IsHidden(string root, string rootReal, string fullPath)
    {
        if (IsSettingsName(fullPath) || IsVendorSecretName(fullPath) || IsProtected(fullPath)) return true;
        if (!string.Equals(root, rootReal, StringComparison.OrdinalIgnoreCase))
        {
            var rel = Relative(root, fullPath);
            if (rel.Length > 0 && IsProtected(Path.Combine(rootReal, rel))) return true;
        }
        return false;
    }

    private static string TrimSep(string p)
    {
        var trimmed = p.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return p;
        // Keep a drive root such as C:\ intact.
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + '\\' : trimmed;
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string Relative(string root, string full)
    {
        if (full.Length <= root.Length) return "";
        return full[(root.EndsWith('\\') ? root.Length : root.Length + 1)..];
    }

    private static string Display(string? p) => string.IsNullOrWhiteSpace(p) ? "(the folder itself)" : p.Trim();

    private static DateTime SafeWriteTime(FileSystemInfo i)
    {
        try { return i.LastWriteTime; } catch (Exception) { return DateTime.MinValue; }
    }

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; } catch (Exception) { return 0; }
    }

    // Linear-time matcher (no regex, so no timeouts): * and ? only, case-insensitive.
    private static bool NameMatches(string pattern, string name) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    private static DateTimeOffset SafeOffset(DateTime t)
    {
        try { return new DateTimeOffset(t); }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MinValue; }
    }

    // ---------------------------------------------------------------- reading text

    private static FileStream OpenShared(string full)
    {
        try
        {
            return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FolderAccessException("That file could not be opened (no permission, or it is locked).");
        }
    }

    private static void RequireText(string full, string display)
    {
        if (LooksBinary(full))
        {
            throw new FolderAccessException($"'{display}' looks like a binary file, not text, so it can't be read. Only text files (logs, XML, CSV, NC programs) can be.");
        }
    }

    /// <summary>A NUL byte in the first 8 KB, unless the file starts with a byte-order mark (UTF-16/32 text has NULs).</summary>
    private static bool LooksBinary(string full)
    {
        using var fs = OpenShared(full);
        var buf = new byte[BinarySniffBytes];
        int n;
        try { n = fs.Read(buf, 0, buf.Length); }
        catch (IOException) { throw new FolderAccessException("That file could not be read (it may have been moved, locked or gone offline)."); }
        if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return false;
        if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return false;
        return Array.IndexOf(buf, (byte)0, 0, n) >= 0;
    }

    /// <summary>
    /// Streams the file line by line: encoding from a BOM, else UTF-8, else Latin-1 when the bytes are not valid UTF-8.
    /// <paramref name="onLine"/> returns false to stop; <paramref name="reset"/> runs before the Latin-1 retry.
    /// </summary>
    private static void ScanFile(string full, int lineCap, Func<int, string, bool, bool> onLine, Action reset)
    {
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), Encoding.Latin1 })
        {
            try
            {
                using var fs = OpenShared(full);
                using var sr = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 65536);
                var lines = new LineReader(sr);
                var n = 0;
                while (lines.ReadLine(lineCap, out var cut) is { } line)
                {
                    n++;
                    if (!onLine(n, line, cut)) break;
                }
                return;
            }
            catch (DecoderFallbackException)
            {
                reset();
            }
            catch (IOException)
            {
                throw new FolderAccessException("That file could not be read to the end (it may have been moved, locked or gone offline).");
            }
        }
    }

    /// <summary>Reads \n, \r\n or \r terminated lines, keeping at most <c>cap</c> characters of each.</summary>
    private sealed class LineReader(TextReader reader)
    {
        private readonly char[] _buf = new char[8192];
        private int _pos, _len;
        private bool _skipLf;

        public string? ReadLine(int cap, out bool cut)
        {
            cut = false;
            StringBuilder? sb = null;
            var started = false;
            while (true)
            {
                if (_pos >= _len)
                {
                    _len = reader.Read(_buf, 0, _buf.Length);
                    _pos = 0;
                    if (_len == 0)
                    {
                        return started ? sb?.ToString() ?? "" : null;
                    }
                }
                var c = _buf[_pos++];
                if (_skipLf)
                {
                    _skipLf = false;
                    if (c == '\n') continue;
                }
                started = true;
                if (c == '\n') return sb?.ToString() ?? "";
                if (c == '\r')
                {
                    _skipLf = true;
                    return sb?.ToString() ?? "";
                }
                sb ??= new StringBuilder();
                if (sb.Length < cap) sb.Append(c); else cut = true;
            }
        }
    }
}
