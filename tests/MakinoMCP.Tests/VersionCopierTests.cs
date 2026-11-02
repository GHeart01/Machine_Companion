using MakinoMCP.Launcher;
using Xunit;

namespace MakinoMCP.Tests;

public class VersionCopierTests
{
    [Fact]
    public void Copy_with_progress_ends_at_total_and_renames_into_place()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "share", "versions", "1.2.3");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllBytes(Path.Combine(source, "MakinoMCP.exe"), new byte[3_500_000]);
            File.WriteAllText(Path.Combine(source, "sub", "a.txt"), "hello");
            File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
            var local = Path.Combine(root, "local");

            var reports = new List<(long done, long total)>();
            VersionCopier.CopyToLocal(source, local, "1.2.3", (done, total) => reports.Add((done, total)));

            Assert.NotEmpty(reports);
            Assert.Equal(3_500_005, reports[^1].total);
            Assert.Equal(reports[^1].total, reports[^1].done);
            Assert.All(reports, r => Assert.True(r.done <= r.total));
            Assert.Equal(reports.Select(r => r.done).Order(), reports.Select(r => r.done)); // never goes backwards
            var target = Path.Combine(local, "1.2.3");
            Assert.Equal(3_500_000, new FileInfo(Path.Combine(target, "MakinoMCP.exe")).Length);
            Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "sub", "a.txt")));
            Assert.True(File.Exists(Path.Combine(target, "empty.bin")));
            Assert.Empty(Directory.GetDirectories(local, "*.tmp*"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Copy_without_progress_and_existing_target_is_a_no_op()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "share");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "x.txt"), "x");
            var local = Path.Combine(root, "local");

            VersionCopier.CopyToLocal(source, local, "v", null);
            Assert.Equal("x", File.ReadAllText(Path.Combine(local, "v", "x.txt")));

            File.WriteAllText(Path.Combine(source, "x.txt"), "changed");
            VersionCopier.CopyToLocal(source, local, "v", null);
            Assert.Equal("x", File.ReadAllText(Path.Combine(local, "v", "x.txt")));
            Assert.Empty(Directory.GetDirectories(local, "*.tmp*"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_file_that_cannot_be_read_fails_and_leaves_no_temp_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "share");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.txt"), "a");
            var locked = Path.Combine(source, "z.bin");
            File.WriteAllText(locked, "z");
            var local = Path.Combine(root, "local");

            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => VersionCopier.CopyToLocal(source, local, "v", null));
            }
            Assert.False(Directory.Exists(Path.Combine(local, "v")));
            Assert.Empty(Directory.GetDirectories(local, "*.tmp*"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
