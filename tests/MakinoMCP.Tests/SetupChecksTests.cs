using MakinoMCP.Core.Settings;
using MakinoMCP.Setup;
using Xunit;

namespace MakinoMCP.Tests;

public class SetupChecksTests
{
    private static readonly MakinoLogin Login = new("CELL-PC", "Makino", false, "reader", "s3cret-pw");

    [Fact]
    public void Validate_accepts_a_complete_sql_login()
    {
        Assert.Null(SetupChecks.ValidateLogin("CELL-PC", "Makino", false, "reader"));
    }

    [Fact]
    public void Validate_windows_login_needs_no_user()
    {
        Assert.Null(SetupChecks.ValidateLogin("CELL-PC", "Makino", true, ""));
    }

    [Fact]
    public void Validate_names_every_missing_field()
    {
        Assert.Equal("Enter the server, the database and the user name.", SetupChecks.ValidateLogin(" ", "", false, ""));
        Assert.Equal("Enter the database.", SetupChecks.ValidateLogin("CELL-PC", "", true, ""));
    }

    [Fact]
    public void Folders_need_a_name_unique_ignoring_case()
    {
        Assert.Null(SetupChecks.ValidateFolders([new("logs", @"\\srv\logs", ""), new("ade", @"\\srv\ade", "")]));
        Assert.Equal("Every shared folder needs a name.",
            SetupChecks.ValidateFolders([new(" ", @"\\srv\logs", "")]));
        Assert.Equal("Two shared folders are both called 'logs': give each its own name.",
            SetupChecks.ValidateFolders([new("logs", @"\\srv\a", ""), new("LOGS", @"\\srv\b", "")]));
    }

    [Fact]
    public void Folder_name_defaults_to_leaf_made_unique()
    {
        Assert.Equal("MakinoLog", SetupChecks.UniqueName(@"\\srv\share\MakinoLog\", []));
        Assert.Equal("MakinoLog-2", SetupChecks.UniqueName(@"\\srv\share\MakinoLog", ["makinolog"]));
        Assert.Equal("MakinoLog-3", SetupChecks.UniqueName(@"\\srv\share\MakinoLog", ["MakinoLog", "MakinoLog-2"]));
    }

    [Fact]
    public void Folder_name_for_a_drive_or_share_root()
    {
        Assert.Equal("share", SetupChecks.UniqueName(@"\\srv\share\", []));
        Assert.Equal("D-drive", SetupChecks.UniqueName(@"D:\", []));
    }

    [Fact]
    public async Task Test_connection_read_only_login()
    {
        var line = await SetupChecks.TestConnectionAsync(new FakeMakinoReader(), Login, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(StatusKind.Success, line.Kind);
        Assert.Equal("Connected: found 4 devices. This login is read only.", line.Text);
    }

    [Fact]
    public async Task Test_connection_login_that_can_write_is_a_warning()
    {
        var reader = new FakeMakinoReader { CanWrite = true };
        var line = await SetupChecks.TestConnectionAsync(reader, Login, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(StatusKind.Warning, line.Kind);
        Assert.Equal("Connected: found 4 devices. This login can also change data — MakinoMCP only ever reads.", line.Text);
    }

    [Fact]
    public async Task Test_connection_failure_uses_error_text_without_password()
    {
        var reader = new FakeMakinoReader { Fail = () => new InvalidOperationException("Login failed; Password=s3cret-pw") };
        var line = await SetupChecks.TestConnectionAsync(reader, Login, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(StatusKind.Error, line.Kind);
        Assert.StartsWith("Could not read the Makino cell database:", line.Text);
        Assert.DoesNotContain("s3cret-pw", line.Text);
    }

    [Fact]
    public async Task Test_connection_gives_up_at_the_cap()
    {
        var reader = new FakeMakinoReader { Hang = true };
        var line = await SetupChecks.TestConnectionAsync(reader, Login, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal(StatusKind.Error, line.Kind);
        Assert.Equal("No answer from CELL-PC within 0.1 seconds: check the server name and that the cell PC is on.", line.Text);
    }

    [Fact]
    public void Unreachable_lists_only_missing_folders()
    {
        var here = Path.GetTempPath();
        var missing = Path.Combine(here, "makinomcp-missing-" + Guid.NewGuid().ToString("N"));
        var names = SetupChecks.Unreachable([new("tmp", here, ""), new("gone", missing, "")], TimeSpan.FromSeconds(5));
        Assert.Equal(["gone"], names);
    }

    [Fact]
    public void Write_error_is_plain_and_names_the_path()
    {
        Assert.Equal(@"Couldn't save to F:\x\MakinoMCP.settings.json: Access is denied.",
            SetupChecks.SaveError(@"F:\x\MakinoMCP.settings.json", new UnauthorizedAccessException("Access is denied.")));
    }
}

public class ContrastTests
{
    [Fact]
    public void Ratio_matches_wcag_for_black_on_white()
    {
        Assert.Equal(21.0, SetupChecks.ContrastRatio(System.Drawing.Color.Black, System.Drawing.Color.White), 2);
    }

    [Fact]
    public void Best_keeps_the_first_on_a_tie()
    {
        var white = System.Drawing.Color.White;
        Assert.Equal(System.Drawing.Color.Black,
            SetupChecks.MostContrasting(white, System.Drawing.Color.Black, System.Drawing.Color.FromArgb(0, 0, 0)));
    }
}
