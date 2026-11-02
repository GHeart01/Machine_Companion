using System.Reflection;
using Microsoft.Data.SqlClient;
using Xunit;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Tests;

public class ErrorTextTests
{
    private static readonly MakinoLogin Login = new("CELL-PC", "Makino", false, "reader", "Hunter2-secret");

    [Fact]
    public void ErrorText_never_contains_password()
    {
        var cs = Login.ConnectionString();
        var cases = new Exception[]
        {
            new InvalidOperationException($"Bad thing with {cs} and the password Hunter2-secret inside"),
            new ArgumentException("Format of the initialization string: Server=x;User ID=reader;Pwd=Hunter2-secret;"),
            new Exception("other: password = Hunter2-secret; trailing"),
            new AggregateException(new TimeoutException($"timed out using {cs}")),
            MakeSqlException(18456, $"Login failed for user 'reader'. {cs}"),
        };

        foreach (var e in cases)
        {
            var text = ErrorText.ForClaude(e, Login);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("Hunter2-secret", text);
            Assert.DoesNotContain(cs, text);
            Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Pwd=", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ErrorText_login_failed_hint()
    {
        var text = ErrorText.ForClaude(MakeSqlException(18456, "Login failed for user 'reader'."), Login);
        Assert.Contains("login refused", text);
    }

    [Fact]
    public void ErrorText_timeout_and_unreachable_hints()
    {
        Assert.Contains("timed out", ErrorText.ForClaude(MakeSqlException(-2, "Execution Timeout Expired."), Login));
        Assert.Contains("timed out", ErrorText.ForClaude(new TimeoutException("slow"), Login));
        Assert.Contains("check the server name", ErrorText.ForClaude(MakeSqlException(53, "A network-related error occurred."), Login));
    }

    [Fact]
    public void ErrorText_permission_denied_hint()
    {
        var text = ErrorText.ForClaude(
            MakeSqlException(229, "The SELECT permission was denied on the object 'Orders', database 'Makino', schema 'dbo'."), Login);
        Assert.Contains("not allowed to read", text);
        Assert.Contains("db_datareader", text);
    }

    [Fact]
    public void SchemaInfo_missing_column_reported()
    {
        var schema = new SchemaInfo(
        [
            ("MachineStatus", "DeviceID"),
            ("MachineStatus", "Connected"),
            ("MachineStatus", "Emergency"),
            ("MachineStatus", "CNCMode"),
        ]);

        Assert.False(schema.Has("MachineStatus", "PowerStatus"));
        Assert.True(schema.Has("machinestatus", "cncmode"));
        Assert.True(schema.HasTable("MachineStatus"));
        Assert.False(schema.HasTable("DeviceResultDaySummary"));
        Assert.False(schema.Has("DeviceResultDaySummary", "OEE"));
    }

    [Theory]
    [InlineData("Normal", "", false, "normal")]
    [InlineData("BrokenToolShort", "", true, "broken tool (short)")]
    [InlineData("BrokenToolShort, ToolLife", "", true, "broken tool (short), tool life")]
    [InlineData("ToolLife", "", false, "tool life")]
    [InlineData("65536", "", true, "broken tool (short)")]
    [InlineData("SomethingNew", "", false, "SomethingNew")]
    [InlineData("BrokenToolLong", "1234", true, "broken tool (long), alarm 1234")]
    [InlineData("Normal", "1234", false, "normal")]
    [InlineData(null, null, false, "")]
    public void MachiningResult_reads_enum_names(string? finish, string? alarm, bool abnormal, string expected)
    {
        Assert.Equal(expected, SqlMakinoReader.MachiningResult(finish, alarm, abnormal));
    }

    [Fact]
    public void Unknown_device_lists_valid_names()
    {
        var devices = new Dictionary<int, string> { [2] = "MC1", [4] = "WSS1" };
        var e = Assert.Throws<ArgumentException>(() => SqlMakinoReader.FindDevice(devices, "nope"));
        Assert.Contains("MC1", e.Message);
        Assert.Contains("WSS1", e.Message);
        Assert.Equal(2, SqlMakinoReader.FindDevice(devices, "mc1"));
        Assert.Null(SqlMakinoReader.FindDevice(devices, null));
        Assert.Null(SqlMakinoReader.FindDevice(devices, " "));
    }

    // SqlException has no public constructor: build one the way SqlClient does, finding members by name and arity
    // so a minor SqlClient update doesn't break the test.
    private static SqlException MakeSqlException(int number, string message)
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        var errorCtor = typeof(SqlError).GetConstructors(Any).OrderByDescending(c => c.GetParameters().Length).First();
        var args = errorCtor.GetParameters().Select(p => p.Name switch
        {
            "infoNumber" => (object?)number,
            "errorClass" => (byte)14,
            "errorState" => (byte)1,
            "errorMessage" => message,
            _ => p.ParameterType == typeof(string) ? "" : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
        }).ToArray();
        var error = (SqlError)errorCtor.Invoke(args);

        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", Any)!.Invoke(errors, [error]);

        var create = typeof(SqlException).GetMethods(Any)
            .Where(m => m.Name == "CreateException")
            .OrderBy(m => m.GetParameters().Length)
            .First(m => m.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));
        var createArgs = create.GetParameters().Select((p, i) => i == 0 ? errors : p.ParameterType == typeof(string) ? (object?)"16.0" : null).ToArray();
        return (SqlException)create.Invoke(null, createArgs)!;
    }
}
