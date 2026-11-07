using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using MakinoMCP.Launcher;
using Xunit;

namespace MakinoMCP.Tests;

public class UpdateApplierTests : IDisposable
{
    private const string OldBuild = "0.6.0-20261101-1000";
    private const string NewBuild = "0.7.0-20261107-1200";
    private static readonly Version NewVersion = new(0, 7, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mmcp-ua-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly List<string> _log = [];

    public UpdateApplierTests()
    {
        _install = Path.Combine(_root, "MakinoMCP");
        Directory.CreateDirectory(Path.Combine(_install, "versions", OldBuild));
        File.WriteAllText(Path.Combine(_install, "versions", OldBuild, "MakinoMCP.exe"), "old app");
        File.WriteAllText(Path.Combine(_install, "MakinoMCP.exe"), "old launcher");
        File.WriteAllText(Path.Combine(_install, "MakinoMCP.exe.config"), "old config");
        File.WriteAllText(Path.Combine(_install, "current.txt"), OldBuild);
        File.WriteAllText(Path.Combine(_install, "MakinoMCP.settings.json"), "{ \"makino\": { \"password\": \"keep-me\" } }");
        File.WriteAllText(Path.Combine(_install, UpdateSource.MarkerFileName), "original marker");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Zip(string build = NewBuild, Action<ZipArchive>? extra = null, bool withExe = true, string launcher = "new launcher")
    {
        var path = Path.Combine(_root, "release-" + Guid.NewGuid().ToString("N") + ".zip");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var w = new StreamWriter(z.CreateEntry(name).Open());
            w.Write(text);
        }
        Add("current.txt", build);
        if (withExe) Add($"versions/{build}/MakinoMCP.exe", "new app");
        Add($"versions/{build}/build.txt", "build=" + build);
        Add("MakinoMCP.exe", launcher);
        Add("MakinoMCP.exe.config", "old config");
        Add("MakinoMCP.settings.example.json", "{ \"example\": true }");
        Add("README.md", "new readme");
        Add(UpdateSource.MarkerFileName, "zip marker");
        Add("MakinoMCP.settings.json", "{ \"evil\": true }");
        extra?.Invoke(z);
        return path;
    }

    private ApplyResult Apply(string zip, Func<string, bool>? isSigned = null, Func<string, bool>? inUse = null) =>
        UpdateApplier.Apply(_install, zip, NewVersion, isSigned ?? (_ => false), inUse ?? (_ => false), _log.Add);

    private string Current() => File.ReadAllText(Path.Combine(_install, "current.txt")).Trim();
    private string Text(params string[] parts) => File.ReadAllText(Path.Combine([_install, .. parts]));

    [Fact]
    public void Apply_NewBuild_InstallsAndSwitchesCurrent()
    {
        var r = Apply(Zip());
        Assert.True(r.Applied, r.Reason);
        Assert.Equal(NewBuild, r.Build);
        Assert.Equal(NewBuild, Current());
        Assert.Equal("new app", Text("versions", NewBuild, "MakinoMCP.exe"));
        Assert.Equal("new launcher", Text("MakinoMCP.exe"));
        Assert.Equal("new readme", Text("README.md"));
        Assert.True(Directory.Exists(Path.Combine(_install, "versions", OldBuild)), "the previous build is kept for a rollback");
        Assert.False(File.Exists(Path.Combine(_install, "current.txt.tmp")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_install, "versions"), "*.tmp-*"));
    }

    [Fact]
    public void Apply_KeepsSettingsAndMarker()
    {
        Assert.True(Apply(Zip()).Applied);
        Assert.Contains("keep-me", Text("MakinoMCP.settings.json"));
        Assert.Equal("original marker", Text(UpdateSource.MarkerFileName));
    }

    [Fact]
    public void Apply_UnchangedLauncher_LeftInPlace()
    {
        var before = File.GetLastWriteTimeUtc(Path.Combine(_install, "MakinoMCP.exe"));
        Assert.True(Apply(Zip(launcher: "old launcher")).Applied);
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(_install, "MakinoMCP.exe")));
        Assert.False(Directory.Exists(Path.Combine(_install, "versions", "old-launchers")) &&
                     Directory.GetFiles(Path.Combine(_install, "versions", "old-launchers")).Length > 0);
    }

    [Fact]
    public void Apply_RejectsTraversalEntry()
    {
        var r = Apply(Zip(extra: z => z.CreateEntry("../evil.txt")));
        Assert.False(r.Applied);
        Assert.Equal(OldBuild, Current());
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void Apply_RejectsRootedEntry()
    {
        var r = Apply(Zip(extra: z => z.CreateEntry("C:/evil.txt")));
        Assert.False(r.Applied);
        Assert.Equal(OldBuild, Current());
    }

    [Fact]
    public void Apply_RejectsWrongVersionInCurrentTxt()
    {
        var r = Apply(Zip(build: "0.8.0-20261107-1200"));
        Assert.False(r.Applied);
        Assert.Equal(OldBuild, Current());
        Assert.False(Directory.Exists(Path.Combine(_install, "versions", "0.8.0-20261107-1200")));
    }

    [Fact]
    public void Apply_RejectsMissingExe()
    {
        var r = Apply(Zip(withExe: false));
        Assert.False(r.Applied);
        Assert.Equal(OldBuild, Current());
    }

    [Fact]
    public void Apply_SwapsOpenLauncher()
    {
        var launcher = Path.Combine(_install, "MakinoMCP.exe");
        // A running exe can be renamed but not overwritten or deleted: share read and delete only.
        using (new FileStream(launcher, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var r = Apply(Zip());
            Assert.True(r.Applied, r.Reason);
        }
        Assert.Equal("new launcher", Text("MakinoMCP.exe"));
        var aside = Directory.GetFiles(Path.Combine(_install, "versions", "old-launchers"), "MakinoMCP.exe.*");
        Assert.Single(aside);
        Assert.Equal("old launcher", File.ReadAllText(aside[0]));
    }

    [Fact]
    public void Apply_LauncherSwapFails_RestoresAndKeepsCurrent()
    {
        // old-launchers is a file, so the running launcher can't be moved aside.
        File.WriteAllText(Path.Combine(_install, "versions", "old-launchers"), "in the way");
        var r = Apply(Zip());
        Assert.False(r.Applied);
        Assert.Equal(OldBuild, Current());
        Assert.Equal("old launcher", Text("MakinoMCP.exe"));
        Assert.Equal("old config", Text("MakinoMCP.exe.config"));
    }

    [SkippableFact]
    public void Apply_ReadOnlyInstall_LeavesCurrentAlone()
    {
        var versions = new DirectoryInfo(Path.Combine(_install, "versions"));
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var acl = versions.GetAccessControl();
        acl.AddAccessRule(deny);
        try
        {
            versions.SetAccessControl(acl);
        }
        catch (Exception ex)
        {
            Skip.If(true, "can't set an ACL here: " + ex.Message);
        }
        try
        {
            var r = Apply(Zip());
            Assert.False(r.Applied);
            Assert.Contains("written", r.Reason);
            Assert.Equal(OldBuild, Current());
        }
        finally
        {
            acl = versions.GetAccessControl();
            acl.RemoveAccessRule(deny);
            versions.SetAccessControl(acl);
        }
    }

    [Fact]
    public void Apply_CleansLeftoversFromInterruptedRun()
    {
        var versions = Path.Combine(_install, "versions");
        var staleDir = Path.Combine(versions, NewBuild + ".tmp-abc");
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(Path.Combine(staleDir, "half.bin"), "x");
        Directory.SetLastWriteTimeUtc(staleDir, DateTime.UtcNow.AddHours(-2));
        var staleZip = Path.Combine(versions, ".download-abc.zip");
        File.WriteAllText(staleZip, "x");
        File.SetLastWriteTimeUtc(staleZip, DateTime.UtcNow.AddHours(-2));

        Assert.True(Apply(Zip()).Applied);
        Assert.False(Directory.Exists(staleDir));
        Assert.False(File.Exists(staleZip));
    }

    [Fact]
    public void Apply_TidyKeepsThreeNewestPlusPrevious()
    {
        var versions = Path.Combine(_install, "versions");
        // The live build is the oldest one, so it is kept only as the previous (rollback) build.
        Directory.SetCreationTimeUtc(Path.Combine(versions, OldBuild), DateTime.UtcNow.AddDays(-30));
        string[] others = ["0.5.0-20261020-1000", "0.5.1-20261021-1000", "0.5.2-20261022-1000", "0.5.3-20261023-1000"];
        for (var i = 0; i < others.Length; i++)
        {
            var d = Path.Combine(versions, others[i]);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "MakinoMCP.exe"), "x");
            Directory.SetCreationTimeUtc(d, DateTime.UtcNow.AddDays(-10 + i));
        }

        Assert.True(Apply(Zip()).Applied);
        var left = Directory.GetDirectories(versions).Select(Path.GetFileName).Where(n => n != "old-launchers").OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "0.5.2-20261022-1000", "0.5.3-20261023-1000", OldBuild, NewBuild }.OrderBy(n => n), left);
    }

    [Fact]
    public void Apply_TidySkipsInUse()
    {
        var versions = Path.Combine(_install, "versions");
        string[] others = ["0.5.0-20261020-1000", "0.5.1-20261021-1000", "0.5.2-20261022-1000", "0.5.3-20261023-1000"];
        for (var i = 0; i < others.Length; i++)
        {
            var d = Path.Combine(versions, others[i]);
            Directory.CreateDirectory(d);
            Directory.SetCreationTimeUtc(d, DateTime.UtcNow.AddDays(-10 + i));
        }
        Assert.True(Apply(Zip(), inUse: d => Path.GetFileName(d) == "0.5.0-20261020-1000").Applied);
        Assert.True(Directory.Exists(Path.Combine(versions, "0.5.0-20261020-1000")));
        Assert.False(Directory.Exists(Path.Combine(versions, "0.5.1-20261021-1000")));
    }

    [Fact]
    public void Apply_SignedInstall_RejectsUnsignedUpdate()
    {
        var installedApp = Path.Combine(_install, "versions", OldBuild, "MakinoMCP.exe");
        var r = Apply(Zip(), isSigned: p => string.Equals(p, installedApp, StringComparison.OrdinalIgnoreCase));
        Assert.False(r.Applied);
        Assert.Contains("sign", r.Reason);
        Assert.Equal(OldBuild, Current());
        Assert.False(Directory.Exists(Path.Combine(_install, "versions", NewBuild)));
        Assert.Equal("old launcher", Text("MakinoMCP.exe"));
    }

    [Fact]
    public void Apply_SignedInstall_AcceptsSignedUpdate()
    {
        Assert.True(Apply(Zip(), isSigned: _ => true).Applied);
        Assert.Equal(NewBuild, Current());
    }

    [Fact]
    public void Apply_SignedLauncher_RejectsUnsignedLauncher()
    {
        var installedLauncher = Path.Combine(_install, "MakinoMCP.exe");
        var r = Apply(Zip(), isSigned: p => string.Equals(p, installedLauncher, StringComparison.OrdinalIgnoreCase));
        Assert.False(r.Applied);
        Assert.Equal(OldBuild, Current());
        Assert.Equal("old launcher", Text("MakinoMCP.exe"));
    }

    [Fact]
    public void Apply_UnsignedInstall_AcceptsUnsigned()
    {
        Assert.True(Apply(Zip(), isSigned: _ => false).Applied);
    }

    [SkippableFact]
    public void HasValidSignature_true_for_a_microsoft_signed_exe_but_not_signpath()
    {
        // dotnet.exe carries an embedded Microsoft signature: proves the WinVerifyTrust call works at all.
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Skip.If(string.IsNullOrEmpty(dotnet) || !File.Exists(dotnet), "dotnet.exe not found");
        Assert.True(Signatures.HasValidSignature(dotnet!));
        Assert.False(Signatures.IsSignPathSigned(dotnet!));
        var copy = Path.Combine(_root, "tampered.exe");
        var bytes = File.ReadAllBytes(dotnet!);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(copy, bytes);
        Assert.False(Signatures.HasValidSignature(copy));
    }

    [Fact]
    public void IsSignPathSigned_UnsignedFile_False()
    {
        var copy = Path.Combine(_root, "unsigned.exe");
        File.WriteAllText(copy, "MZ not really an exe");
        Assert.False(Signatures.IsSignPathSigned(copy));
        Assert.False(Signatures.IsSignPathSigned(Path.Combine(_root, "missing.exe")));
    }
}
