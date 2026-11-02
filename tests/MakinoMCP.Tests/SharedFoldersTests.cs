using Xunit;
using System.Diagnostics;
using System.Text;
using MakinoMCP.Core.Folders;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Tests;

public sealed class SharedFoldersTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "makinomcp-folders-" + Guid.NewGuid().ToString("N"));
    private readonly string _shared;
    private readonly string _outside;
    private readonly string _settingsFile;

    public SharedFoldersTests()
    {
        _shared = Path.Combine(_base, "shared");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_outside);
        _settingsFile = Path.Combine(_shared, "MakinoMCP.settings.json");
        File.WriteAllText(_settingsFile, "{\"password\":\"hunter2-secret\"}");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "TOP-SECRET-OUTSIDE");
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first so cleanup never walks into (or fails on) their targets.
            foreach (var d in Directory.EnumerateDirectories(_shared))
            {
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d, false);
            }
            Directory.Delete(_base, true);
        }
        catch (Exception) { /* temp folder; leaving it behind is harmless */ }
    }

    private SharedFolders Make(Limits? limits = null, params string[] protectedPaths) =>
        new([new SharedFolder("Logs", _shared, "test folder")], limits ?? new Limits(), protectedPaths);

    private string Write(string rel, string content)
    {
        var p = Path.Combine(_shared, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Rejects_dotdot_escape()
    {
        var f = Make();
        var e = Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", @"..\outside\secret.txt", null, null, null));
        Assert.Contains("outside", e.Message);
        Assert.DoesNotContain("TOP-SECRET", e.Message);
        Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", @"sub\..\..\outside\secret.txt", null, null, null));
        Assert.Throws<FolderAccessException>(() => f.ListFiles("Logs", "..", null, null));
        // A sibling whose name starts with the shared folder's name must not pass a prefix test.
        Directory.CreateDirectory(_shared + "-evil");
        File.WriteAllText(Path.Combine(_shared + "-evil", "x.txt"), "x");
        Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", @"..\shared-evil\x.txt", null, null, null));
    }

    [Fact]
    public void Rejects_absolute_path()
    {
        var f = Make();
        var abs = Path.Combine(_outside, "secret.txt");
        var e = Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", abs, null, null, null));
        Assert.Contains("relative", e.Message);
        Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", @"\outside\secret.txt", null, null, null));
        Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", @"\\localhost\c$\Windows\win.ini", null, null, null));
        Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", "a.txt:stream", null, null, null));
    }

    [Fact]
    public void Rejects_unknown_folder_name()
    {
        var f = new SharedFolders(
            [new SharedFolder("Logs", _shared, ""), new SharedFolder("Programs", _outside, "")], new Limits(), []);
        var e = Assert.Throws<FolderAccessException>(() => f.ListFiles("Nope", null, null, null));
        Assert.Contains("Logs", e.Message);
        Assert.Contains("Programs", e.Message);
        // Name matching ignores case and surrounding spaces.
        Assert.NotNull(f.ListFiles("  logs ", null, null, null));
    }

    [Fact]
    public void Rejects_junction_leading_out()
    {
        var link = Path.Combine(_shared, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_outside}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var p = Process.Start(psi)!)
        {
            p.WaitForExit();
            if (p.ExitCode != 0 || !Directory.Exists(link))
            {
                // Environment cannot make junctions; nothing to verify here.
                return;
            }
        }
        var f = Make();
        var e = Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", @"link\secret.txt", null, null, null));
        Assert.DoesNotContain("TOP-SECRET", e.Message);
        Assert.Throws<FolderAccessException>(() => f.ListFiles("Logs", "link", null, null));
        // The junction is not offered in listings, and a search does not follow it.
        Assert.DoesNotContain(f.ListFiles("Logs", null, null, null).Entries, x => x.Path == "link");
        Assert.Empty(f.Search("Logs", "TOP-SECRET", null, null).Hits);
    }

    [Fact]
    public void Allows_junction_staying_inside()
    {
        Write(@"real\a.txt", "inside text");
        var link = Path.Combine(_shared, "alias");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(_shared, "real")}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using (var p = Process.Start(psi)!)
        {
            p.WaitForExit();
            if (p.ExitCode != 0 || !Directory.Exists(link)) return;
        }
        Assert.Equal("inside text", Make().ReadFile("Logs", @"alias\a.txt", null, null, null).Text);
    }

    [Fact]
    public void Refuses_settings_file_inside_shared_folder()
    {
        var f = Make(null, _settingsFile);
        var e = Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", "MakinoMCP.settings.json", null, null, null));
        Assert.DoesNotContain("hunter2", e.Message);
        // Different spelling of the same file is refused too.
        Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", @"sub\..\makinomcp.SETTINGS.json", null, null, null));
        // Hidden from listings and searches.
        Write("other.txt", "hunter2 mentioned here");
        Assert.DoesNotContain(f.ListFiles("Logs", null, null, null).Entries, x => x.Path.Contains("settings"));
        var hits = f.Search("Logs", "hunter2", null, null).Hits;
        Assert.All(hits, h => Assert.Equal("other.txt", h.Path));
        Assert.Single(hits);
    }

    [Fact]
    public void Refuses_protected_folder_contents()
    {
        var prog = Path.Combine(_shared, "program");
        Write(@"program\app.xml", "<x/>");
        var f = Make(null, prog);
        Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", @"program\app.xml", null, null, null));
        Assert.Throws<FolderAccessException>(() => f.ListFiles("Logs", "program", null, null));
        Assert.Empty(f.Search("Logs", "<x/>", null, null).Hits);
    }

    [Fact]
    public void Refuses_binary_file()
    {
        var p = Path.Combine(_shared, "data.bin");
        var bytes = new byte[100];
        Encoding.ASCII.GetBytes("MZ header text").CopyTo(bytes, 0);
        File.WriteAllBytes(p, bytes);
        var f = Make();
        var e = Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", "data.bin", null, null, null));
        Assert.Contains("binary", e.Message);
        Assert.Empty(f.Search("Logs", "header", null, null).Hits);
    }

    [Fact]
    public void Reads_utf16_with_bom_and_latin1_fallback()
    {
        File.WriteAllText(Path.Combine(_shared, "u16.txt"), "line one\nline two", Encoding.Unicode);
        File.WriteAllBytes(Path.Combine(_shared, "latin.txt"), [0x63, 0x61, 0x66, 0xE9, 0x0D, 0x0A, 0x32]);
        var f = Make();
        Assert.Equal("line one\nline two", f.ReadFile("Logs", "u16.txt", null, null, null).Text);
        var latin = f.ReadFile("Logs", "latin.txt", null, null, null);
        Assert.Equal("caf\u00E9\n2", latin.Text);
        Assert.Equal(2, latin.TotalLines);
    }

    [Fact]
    public void Log_defaults_to_last_500_lines()
    {
        Write("run.log", string.Join("\n", Enumerable.Range(1, 1200).Select(i => $"entry {i}")) + "\n");
        var f = Make();
        var r = f.ReadFile("Logs", "run.log", null, null, null);
        Assert.Equal(1200, r.TotalLines);
        Assert.Equal(701, r.FirstLine);
        Assert.Equal(1200, r.LastLine);
        Assert.Equal(500, r.Text.Split('\n').Length);
        Assert.StartsWith("entry 701", r.Text);
        Assert.False(r.Truncated);
        Assert.NotNull(r.Note);

        var tail = f.ReadFile("Logs", "run.log", 3, null, null);
        Assert.Equal("entry 1198\nentry 1199\nentry 1200", tail.Text);

        // A non-log file starts at line 1.
        Write("run.txt", string.Join("\n", Enumerable.Range(1, 1200).Select(i => $"entry {i}")));
        var txt = f.ReadFile("Logs", "run.txt", null, null, null);
        Assert.Equal(1, txt.FirstLine);
        Assert.Equal(1200, txt.LastLine);
    }

    [Fact]
    public void Reads_from_line_and_count()
    {
        Write("n.txt", string.Join("\r\n", Enumerable.Range(1, 20).Select(i => $"L{i}")));
        var r = Make().ReadFile("Logs", "n.txt", null, 5, 3);
        Assert.Equal("L5\nL6\nL7", r.Text);
        Assert.Equal(5, r.FirstLine);
        Assert.Equal(7, r.LastLine);
        Assert.Equal(20, r.TotalLines);
        Assert.False(r.Truncated);

        var past = Make().ReadFile("Logs", "n.txt", null, 99, 3);
        Assert.Equal("", past.Text);
        Assert.NotNull(past.Note);
    }

    [Fact]
    public void Caps_at_maxFileKB_and_says_truncated()
    {
        var line = new string('x', 99);
        Write("big.txt", string.Join("\n", Enumerable.Range(0, 1000).Select(_ => line)));
        var f = Make(new Limits(500, 1));
        var r = f.ReadFile("Logs", "big.txt", null, null, null);
        Assert.True(r.Truncated);
        Assert.True(r.Text.Length <= 1024);
        Assert.Equal(1000, r.TotalLines);
        Assert.Contains($"fromLine={r.LastLine + 1}", r.Note);

        var tail = f.ReadFile("Logs", "big.txt", 900, null, null);
        Assert.True(tail.Truncated);
        Assert.True(tail.Text.Length <= 1024);
        Assert.Equal(1000, tail.LastLine);
        Assert.Contains("fromLine", tail.Note);
    }

    [Fact]
    public void Reads_file_held_open_for_writing()
    {
        var p = Path.Combine(_shared, "live.log");
        using var writer = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes("first\nsecond\n");
        writer.Write(bytes);
        writer.Flush();
        var r = Make().ReadFile("Logs", "live.log", null, null, null);
        Assert.Equal("first\nsecond", r.Text);
        Assert.Equal(2, r.TotalLines);
    }

    [Fact]
    public void Offline_folder_marked_unreachable()
    {
        var f = new SharedFolders(
            [
                new SharedFolder("Offline", @"\\nonexistent-host-xyz\share", "gone"),
                new SharedFolder("Blank", "  ", ""),
                new SharedFolder("Missing", Path.Combine(_base, "nope"), ""),
                new SharedFolder("Fine", _shared, "ok"),
            ],
            new Limits(), []);
        var sw = Stopwatch.StartNew();
        var list = f.List();
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"List took {sw.Elapsed}");
        Assert.False(list[0].Reachable);
        Assert.NotNull(list[0].Problem);
        Assert.False(list[1].Reachable);
        Assert.Equal("no path set", list[1].Problem);
        Assert.False(list[2].Reachable);
        Assert.True(list[3].Reachable);
        Assert.Null(list[3].Problem);
        Assert.Equal("ok", list[3].Description);
    }

    [Fact]
    public void Bad_settings_values_never_throw()
    {
        var f = new SharedFolders(
            [new SharedFolder("Logs", _shared, null!), new SharedFolder("NoPath", null!, "")],
            new Limits(0, -5), []);
        Write("a.txt", "hello");
        Assert.Equal("hello", f.ReadFile("Logs", "a.txt", null, null, null).Text);
        Assert.Equal("no path set", f.List()[1].Problem);
        var e = Assert.Throws<FolderAccessException>(() => f.ListFiles("NoPath", null, null, null));
        Assert.Contains("no path", e.Message);
        // MaxRows 0 means the default 500.
        for (var i = 0; i < 3; i++) Write($"f{i}.txt", "x");
        Assert.Equal(5, f.ListFiles("Logs", null, null, 1000).Entries.Count); // settings file, a.txt, f0..f2
    }

    [Fact]
    public void Search_finds_line_numbers()
    {
        Write(@"a.log", "start\nERROR disk full\nok\nerror again\n");
        Write(@"sub\b.txt", "nothing\nAn Error here\n");
        Write(@"sub\c.dat", "error in dat");
        var f = Make();
        var all = f.Search("Logs", "error", null, null);
        Assert.Equal(4, all.Hits.Count);
        Assert.Contains(all.Hits, h => h.Path == "a.log" && h.Line == 2 && h.Text == "ERROR disk full");
        Assert.Contains(all.Hits, h => h.Path == "a.log" && h.Line == 4);
        Assert.Contains(all.Hits, h => h.Path == @"sub\b.txt" && h.Line == 2);

        var logs = f.Search("Logs", "error", "*.log", null);
        Assert.Equal(2, logs.Hits.Count);

        var recent = f.Search("Logs", "error", null, 1);
        Assert.Equal(4, recent.Hits.Count);
        File.SetLastWriteTime(Path.Combine(_shared, "a.log"), DateTime.Now.AddHours(-5));
        Assert.DoesNotContain(f.Search("Logs", "error", null, 1).Hits, h => h.Path == "a.log");
    }

    [Fact]
    public void Search_stops_at_maxRows_and_says_so()
    {
        Write("many.txt", string.Join("\n", Enumerable.Range(0, 50).Select(i => $"hit {i}")));
        var r = Make(new Limits(10, 256)).Search("Logs", "hit", null, null);
        Assert.Equal(10, r.Hits.Count);
        Assert.True(r.Capped);
        Assert.Contains("10", r.Note);
    }

    [Fact]
    public void ListFiles_newest_first_and_capped()
    {
        var t = DateTime.Now.AddDays(-1);
        File.SetLastWriteTime(_settingsFile, t.AddDays(-30));
        for (var i = 0; i < 5; i++)
        {
            var p = Write($"f{i}.log", new string('a', i + 1));
            File.SetLastWriteTime(p, t.AddMinutes(i));
        }
        var dir = Path.Combine(_shared, "newest-dir");
        Directory.CreateDirectory(dir);
        Directory.SetLastWriteTime(dir, t.AddHours(1));
        Write("skip.txt", "x");
        File.SetLastWriteTime(Path.Combine(_shared, "skip.txt"), t.AddHours(-1));

        var f = Make();
        var all = f.ListFiles("Logs", null, "*.log", null);
        Assert.Equal(["f4.log", "f3.log", "f2.log", "f1.log", "f0.log"], all.Entries.Select(e => e.Path));
        Assert.Equal(5L, all.Entries[0].Size);

        var three = f.ListFiles("Logs", null, null, 3);
        Assert.True(three.Capped);
        Assert.Equal(3, three.Entries.Count);
        Assert.True(three.Entries[0].IsFolder);
        Assert.Equal("newest-dir", three.Entries[0].Path);
        Assert.Null(three.Entries[0].Size);
        Assert.Contains("3", three.Note);

        var limited = Make(new Limits(2, 256)).ListFiles("Logs", null, null, 100);
        Assert.Equal(2, limited.Entries.Count);
        Assert.True(limited.Capped);
    }

    [Fact]
    public void ListFiles_in_subfolder_gives_paths_relative_to_shared_folder()
    {
        Write(@"deep\x\one.txt", "1");
        var r = Make().ListFiles("Logs", @"deep\x", null, null);
        Assert.Equal(@"deep\x\one.txt", Assert.Single(r.Entries).Path);
        Assert.Throws<FolderAccessException>(() => Make().ListFiles("Logs", @"deep\x\one.txt", null, null));
        Assert.Throws<FolderAccessException>(() => Make().ListFiles("Logs", null, @"..\*", null));
    }

    [Fact]
    public void Reading_a_missing_file_or_a_folder_gives_a_plain_refusal()
    {
        Directory.CreateDirectory(Path.Combine(_shared, "d"));
        var f = Make();
        Assert.Contains("no file", Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", "nope.txt", null, null, null)).Message);
        Assert.Contains("folder", Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", "d", null, null, null)).Message);
        Assert.Throws<FolderAccessException>(() => f.ReadFile("Logs", "a.txt", 5, 2, null));
    }
}
