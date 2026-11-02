using System.Runtime.InteropServices;
using System.Text;

namespace MakinoMCP.Setup;

/// <summary>Turns a path on a mapped drive into the UNC path behind it, so an install snippet works on PCs that map
/// the share to a different letter (or not at all).</summary>
public static class UncPath
{
    /// <summary><c>F:\A\x.exe</c> → <c>\\srv\share\A\x.exe</c> when <paramref name="driveToUnc"/> knows F:
    /// (it gets "F:" and returns the remote name, or null). UNC paths and unmapped drives come back unchanged.</summary>
    public static string FromMapped(string path, Func<string, string?> driveToUnc)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return path;

        var remote = driveToUnc(char.ToUpperInvariant(path[0]) + ":");
        if (string.IsNullOrEmpty(remote)) return path;

        var rest = path[2..].TrimStart('\\', '/');
        return rest.Length == 0 ? remote.TrimEnd('\\') : remote.TrimEnd('\\') + "\\" + rest;
    }

    /// <summary>The share a drive letter ("F:") is mapped to on this PC, or null when it isn't a mapped drive.</summary>
    public static string? DriveToUnc(string drive)
    {
        try
        {
            var length = 260;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var buffer = new StringBuilder(length);
                var result = WNetGetConnection(drive, buffer, ref length);
                if (result == NoError) return buffer.ToString();
                if (result != MoreData) return null; // 2250 not a network drive, 1200 bad device, …
            }
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private const int NoError = 0;
    private const int MoreData = 234;

    [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
