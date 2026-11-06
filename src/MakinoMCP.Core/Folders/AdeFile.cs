using System.Globalization;
using System.Text.Json.Serialization;
using System.Xml;

namespace MakinoMCP.Core.Folders;

/// <summary>An ADE (automatic data entry) file explained against the MAS-A5 data entry rules: each item's section,
/// action and key, what the cell will do with it, and the problems that make the cell reject the file (move it to its
/// error folder). Only rules confirmed in the vendor's file processor are applied; what depends on the cell's own
/// data is listed in NotChecked.</summary>
public sealed record AdeExplanation(string Path, string Verdict, IReadOnlyList<AdeSection> Sections, IReadOnlyList<AdeItem> Items,
    IReadOnlyList<string> Problems, IReadOnlyList<string> NotChecked)
{
    /// <summary>More items existed than the row limit let through (Sections still count them all).</summary>
    public bool Capped { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

/// <summary>A top-level section: how many items it holds, and Ignored when the cell doesn't read it at all.</summary>
public sealed record AdeSection(string Name, int Items)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Ignored { get; init; }
}

/// <summary>One item: its section, element, action (add, update, delete) and key (name, number, …), what the cell
/// does with it, and the problems that fail it (one failing item rejects the whole file).</summary>
public sealed record AdeItem(string Section, string Element, string? Action, string? Key)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effect { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Problems { get; init; }
}

/// <summary>Reads an ADE file's text (a MASData XML document) and explains it. Never touches the file system.</summary>
public static class AdeFile
{
    private sealed record SectionRule(string Item, string[] Actions, bool NeedsAction, string KeyAttribute, string? SecondKey = null)
    {
        public string? OtherItem { get; init; }
    }

    // The sections the cell reads, the item element each expects, the actions it carries out and the key attribute.
    // Orders, Parts and CommonFixtures have no delete: a delete item fails. OrderQuantities items carry no action.
    private static readonly Dictionary<string, SectionRule> Rules = new(StringComparer.Ordinal)
    {
        ["NCPrograms"] = new("NCProgram", ["add", "update", "delete"], true, "name"),
        ["OffsetFiles"] = new("OffsetFile", ["add", "update", "delete"], true, "name"),
        ["Parts"] = new("Part", ["add", "update"], true, "name", "revision"),
        ["CommonFixtures"] = new("CommonFixture", ["add", "update"], true, "name"),
        ["Orders"] = new("Order", ["add", "update"], true, "name"),
        ["OrderQuantities"] = new("OrderQuantity", [], false, "orderName"),
        ["FunctionalTools"] = new("FunctionalTool", ["add", "update", "delete"], true, "number"),
        ["IndividualTools"] = new("IndividualTool", ["add", "update", "delete"], true, "number"),
        ["Kits"] = new("Kit", ["add", "update", "delete"], true, "name") { OtherItem = "KitOrder" },
    };

    private static readonly string[] Actions = ["add", "update", "delete"];

    // NCProgram Type values the cell accepts: main program, shared subprogram, dataserver program.
    private static readonly Dictionary<int, string> ProgramTypes = new() { [1] = "main program", [4] = "shared subprogram", [8] = "dataserver program" };

    public static readonly IReadOnlyList<string> NotCheckedList =
    [
        "whether each named part, order, program file, offset file or tool already exists on the cell (an add of an existing order, part, common fixture, program, offset file or tool becomes an update)",
        "whether the program and offset files a <Filename> names are in the ADE folder and pass the cell's scan",
        "dates (StartDate, DueDate): the cell parses them with its own DateFormat setting",
        "for OrderQuantity, whether the process number is within the part's processes",
        "the details inside parts (processes, jobs, operations) and common fixtures (processes, pallet assignments), which the cell checks against its own data",
    ];

    /// <summary>Explains the file text. <paramref name="truncated"/>: the text was cut at the size limit, so the XML
    /// can't be read whole. Lists at most <paramref name="maxItems"/> items.</summary>
    public static AdeExplanation Explain(string path, string text, bool truncated, int maxItems)
    {
        var fileName = System.IO.Path.GetFileName(path);
        var note = fileName.StartsWith('[') && fileName.Contains(']')
            ? "The name starts with [date_time]: this is how the cell names a file it rejected and moved to its error folder. The cell doesn't record why; the problems below are the reasons this tool can find."
            : null;

        if (truncated)
        {
            return new AdeExplanation(path, "not explained: the file is larger than the read size limit", [], [],
                ["The file is larger than the size limit (maxFileKB in the settings), so it couldn't be read whole."], NotCheckedList) { Note = note };
        }

        XmlDocument doc;
        try
        {
            doc = Load(text);
        }
        catch (XmlException e)
        {
            return new AdeExplanation(path, "rejected: not well-formed XML", [], [],
                [$"Not well-formed XML (line {e.LineNumber}, position {e.LinePosition}): the cell can't load it and rejects the whole file."],
                NotCheckedList) { Note = note };
        }

        var root = doc.DocumentElement;
        if (root is null || root.Name != "MASData")
        {
            return new AdeExplanation(path, "rejected: the root element isn't <MASData>", [], [],
                [$"The root element is <{root?.Name ?? "(none)"}>; the cell only reads files whose root is <MASData> and rejects any other."],
                NotCheckedList) { Note = note };
        }

        var sections = new List<AdeSection>();
        var items = new List<AdeItem>();
        var fileProblems = new List<string>();
        var failing = 0;
        var total = 0;
        foreach (XmlNode section in root.ChildNodes)
        {
            if (section.NodeType != XmlNodeType.Element) continue; // the cell skips anything that isn't a section it knows
            if (!Rules.TryGetValue(section.Name, out var rule))
            {
                sections.Add(new AdeSection(section.Name, section.ChildNodes.Count) { Ignored = true });
                continue;
            }

            var count = 0;
            var children = section.ChildNodes.Cast<XmlNode>().ToList();
            foreach (var node in children)
            {
                count++;
                total++;
                var item = ExplainItem(section.Name, rule, node);
                if (item.Problems is { Count: > 0 })
                {
                    // The cell judges an OffsetFiles section by its last item only; an earlier failing item's offset
                    // file is moved to the error folder, but the XML file isn't rejected for it.
                    if (section.Name != "OffsetFiles" || ReferenceEquals(node, children[^1])) failing++;
                    else item = item with { Effect = "fails: its offset file goes to the error folder, but only the section's last item decides whether the XML file is rejected" };
                }
                if (items.Count < maxItems) items.Add(item);
            }
            sections.Add(new AdeSection(section.Name, count));
        }

        if (total == 0) fileProblems.Add("The file holds no items the cell reads, so it does nothing.");
        var verdict = failing > 0
            ? $"rejected: {failing} item{(failing == 1 ? "" : "s")} fail{(failing == 1 ? "s" : "")}, so the cell moves the whole file to its error folder (the other items are still carried out)"
            : total > 0 ? "no problem found by the rules this tool checks (see notChecked)" : "nothing to do";
        return new AdeExplanation(path, verdict, sections, items, fileProblems, NotCheckedList)
        {
            Capped = total > items.Count, Note = note,
        };
    }

    private static XmlDocument Load(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(reader);
        return doc;
    }

    private static AdeItem ExplainItem(string section, SectionRule rule, XmlNode node)
    {
        var problems = new List<string>();
        if (node.NodeType != XmlNodeType.Element || (node.Name != rule.Item && node.Name != rule.OtherItem))
        {
            var what = node.NodeType switch
            {
                XmlNodeType.Comment => "an XML comment",
                XmlNodeType.Text or XmlNodeType.CDATA => "loose text",
                XmlNodeType.ProcessingInstruction => "a processing instruction",
                _ => $"<{node.Name}>",
            };
            var expected = rule.OtherItem is null ? $"<{rule.Item}>" : $"<{rule.Item}> or <{rule.OtherItem}>";
            problems.Add($"{what} inside <{section}>: the cell expects only {expected} there and counts anything else as a failed item");
            return new AdeItem(section, node.NodeType == XmlNodeType.Element ? node.Name : node.NodeType.ToString().ToLowerInvariant(), null, null)
            {
                Problems = problems,
            };
        }

        var element = (XmlElement)node;
        string? action = null;
        if (rule.NeedsAction)
        {
            var raw = element.GetAttribute("action").Trim();
            action = raw.Length > 0 ? raw : null;
            var known = Actions.FirstOrDefault(a => string.Equals(a, raw, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                problems.Add(raw.Length == 0 ? "no action attribute: the item fails (use add, update or delete)" : $"action '{raw}' isn't add, update or delete: the item fails");
            else if (!rule.Actions.Contains(known))
                problems.Add($"{section} items can't be deleted: a {known} item fails");
            action = known ?? action;
        }

        var keyValue = element.GetAttribute(rule.KeyAttribute).Trim();
        var key = keyValue.Length > 0 ? $"{rule.KeyAttribute}={keyValue}" : null;
        if (rule.SecondKey is { } second && element.GetAttribute(second).Trim() is { Length: > 0 } secondValue)
            key = $"{key}, {second}={secondValue}";
        if (rule.KeyAttribute == "number")
        {
            if (!int.TryParse(keyValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
                problems.Add(keyValue.Length == 0 ? "no number attribute: the item fails" : $"number '{keyValue}' isn't a positive whole number: the item fails");
        }
        else if (keyValue.Length == 0)
        {
            problems.Add($"no {rule.KeyAttribute} attribute: the item fails");
        }

        string? effect = action switch
        {
            "add" when section is "Orders" or "Parts" or "CommonFixtures" or "FunctionalTools" or "IndividualTools" or "NCPrograms" or "OffsetFiles" =>
                "adds it, or updates it when it already exists",
            "add" => "adds it",
            "update" => "updates it",
            "delete" => "deletes it",
            _ => section == "OrderQuantities" ? "sets the order's quantities for one process (remaining, normal, scrap)" : null,
        };

        switch (section)
        {
            case "Orders":
                CheckOrder(element, action, problems);
                break;
            case "NCPrograms":
                CheckProgram(element, action, problems);
                break;
            case "OffsetFiles" when action == "add" && Child(element, "Filename") is not { Length: > 0 }:
                problems.Add("an add needs a <Filename>: the item fails");
                break;
        }

        return new AdeItem(section, element.Name, action, key)
        {
            Effect = effect,
            Problems = problems.Count > 0 ? problems : null,
        };
    }

    private static void CheckOrder(XmlElement order, string? action, List<string> problems)
    {
        var priority = Number(order, "Priority");
        if (priority is > 999) problems.Add($"Priority {priority} is above 999: the item fails");
        else if (priority is < 0 && action == "add") problems.Add($"Priority {priority} is below 0: the item fails when the order is new");

        var status = Number(order, "Status");
        if (status is not null and not (-1 or 0 or 4))
        {
            problems.Add(status == 6
                ? "Status 6 (cancel) works only on an order that already exists; for a new order the item fails"
                : $"Status {status} isn't one the cell accepts (-1 or none: the cell's default, 0, 4: suspended, or 6: cancel an existing order): the item fails");
        }
        if (Child(order, "Status") is { Length: > 0 } rawStatus && status is null)
            problems.Add($"Status '{rawStatus}' isn't a number: the cell reads it as -1 (its default)");

        if (action == "add" && Child(order, "PartName") is not { Length: > 0 })
            problems.Add("no <PartName>: an add of a new order fails (the order's part must already exist on the cell)");
    }

    private static void CheckProgram(XmlElement program, string? action, List<string> problems)
    {
        var type = Number(program, "Type");
        if (type is null || !ProgramTypes.ContainsKey(type.Value))
            problems.Add($"<Type> {(type is null ? "is missing" : $"is {type}")}: the cell accepts 1 (main program), 4 (shared subprogram) or 8 (dataserver program), so the item fails");
        if (action == "add" && Child(program, "Filename") is not { Length: > 0 })
            problems.Add("an add needs a <Filename>: the item fails");
    }

    private static string? Child(XmlElement parent, string name) =>
        parent.SelectSingleNode(name)?.InnerText?.Trim();

    private static int? Number(XmlElement parent, string name) =>
        Child(parent, name) is { Length: > 0 } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
