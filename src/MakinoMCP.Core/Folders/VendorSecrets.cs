using System.Text.RegularExpressions;

namespace MakinoMCP.Core.Folders;

/// <summary>
/// Hides password values in vendor .config and .xml files before the folder tools show a line: connection strings
/// (<c>Password=x;</c>, <c>Pwd=x</c>), XML attributes (<c>password="x"</c>), XML elements
/// (<c>&lt;Password&gt;x&lt;/Password&gt;</c>, also with a prefix) and appSettings pairs
/// (<c>key="SmtpPassword" value="x"</c>). Names ending in password or pwd count (DbPassword, SmtpPwd). The value
/// becomes <c>***</c>; the rest of the line, and the number of lines, stay as they are. A value whose element spans
/// several lines is masked only on the line holding the opening tag.
/// </summary>
public static partial class VendorSecrets
{
    private const string Mask = "***";

    /// <summary>True for the file types whose secrets are masked (.config and .xml).</summary>
    public static bool Applies(string fileName)
    {
        var name = Path.GetFileName(fileName.TrimEnd('\\', '/')).TrimEnd(' ', '.');
        return name.EndsWith(".config", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The line with every password value replaced by ***.</summary>
    public static string MaskLine(string line)
    {
        // Most lines name no password at all: they pay one scan and are returned as they are.
        if (line.IndexOf("pass", StringComparison.OrdinalIgnoreCase) < 0
            && line.IndexOf("pwd", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return line;
        }

        // Quoted forms first, so the unquoted rule never sees password="..." (it skips values starting with a quote).
        line = AppSetting().Replace(line, m => IsSecretName(m.Groups["key"].Value)
            ? m.Groups["head"].Value + Quoted(m.Groups["value"].Value)
            : m.Value);
        line = QuotedValue().Replace(line, m => m.Groups["head"].Value + Quoted(m.Groups["value"].Value));
        line = Element().Replace(line, m =>
            string.IsNullOrWhiteSpace(m.Groups["value"].Value) ? m.Value : m.Groups["open"].Value + Mask);
        return UnquotedValue().Replace(line, m => m.Groups["head"].Value + Mask);
    }

    private static bool IsSecretName(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("pwd", StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps the quotes around a quoted value (a missing closing quote stays missing).</summary>
    private static string Quoted(string value)
    {
        var q = value[0];
        return value.Length > 1 && value[^1] == q ? $"{q}{Mask}{q}" : $"{q}{Mask}";
    }

    // The key starts where a name starts (not inside one), so a long line is scanned once, not once per character.
    private const string Key = @"(?<![\w.:-])[\w.:-]*(?:password|pwd)";

    // A quoted value, closed by the same quote or by the end of the line (a cut or multi-line value).
    private const string QuotedText = @"(?<value>""[^""]*(?:""|$)|'[^']*(?:'|$))";

    // Every pattern stays linear on a long line: a part that can fail scans only up to the next quote or angle bracket,
    // and each pattern otherwise ends in a part that cannot fail (it runs to the end of the line at worst).

    // <add key="SmtpPassword" value="x" /> (whether the key names a password is checked in code, keeping this linear)
    [GeneratedRegex(@"(?<head>(?<![\w.:-])key\s*=\s*(?<q>[""'])(?<key>(?>[^""'<>]*))\k<q>\s+value\s*=\s*)" + QuotedText,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppSetting();

    // password="x", Pwd='x', x:Password = "x", and a quoted connection-string value Password='a;b'
    [GeneratedRegex(@"(?<head>" + Key + @"\s*=\s*)" + QuotedText, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedValue();

    // <Password>x</Password>, <ns:SmtpPwd attr="1">x</ns:SmtpPwd>, <Password><![CDATA[x]]></Password>; not
    // <Password/>, </Password> or <PasswordFormat>.
    [GeneratedRegex(@"(?<open><[\w.:-]*(?:password|pwd)(?:\s[^<>]*)?(?<!/)>)(?<value><!\[CDATA\[.*?(?:\]\]>|$)|[^<]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Element();

    // Password=x; Pwd = x  up to the next ; " ' or < (XML text escapes a literal <) or the end of the line.
    [GeneratedRegex(@"(?<head>" + Key + @"\s*=\s*)(?=[^\s;""'<])[^;""'<]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnquotedValue();
}
