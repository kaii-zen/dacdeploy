using System.Text;
using System.Text.RegularExpressions;

namespace DacDeploy;

/// <summary>Shows a plan: its non-empty lists, each line marked with the kind of change, then a summary.</summary>
public static partial class PlanPrinter
{
    // Each kind of change's symbol, and its colour in a terminal (an ANSI code); other kinds are changes, "~".
    private static readonly Dictionary<string, (string Symbol, string Code)> Symbols = new()
    {
        ["create"] = ("+", "32"),
        ["alter"] = ("~", "33"),
        ["drop"] = ("-", "31"),
        ["run"] = (">", "36"),
    };

    /// <param name="color">Colour the symbols: true when writing to a terminal and NO_COLOR isn't set.</param>
    public static string Show(Plan plan, bool color)
    {
        string Mark(string? change)
        {
            if (change is null)
            {
                return " ";
            }

            var (symbol, code) = Symbols.GetValueOrDefault(change, ("~", "33"));
            return Styled(symbol, code, color);
        }

        var text = new StringBuilder();
        text.Append($"Plan for {plan.Target.Database} on {plan.Target.Server}\n");
        foreach (var (title, items) in plan.Sections.Where(section => section.Items.Count > 0))
        {
            text.Append($"\n{title}:\n");
            if (ReferenceEquals(items, plan.SchemaChanges) || ReferenceEquals(items, plan.NotInProject))
            {
                // Each line is a change, or a type for what's not in the project, and what it's to.
                var rows = items.Select(item => Columns().Split(item, 2)).Select(parts => (Kind: parts[0], What: parts.Length > 1 ? parts[1] : "")).ToList();
                var width = rows.Max(row => row.Kind.Length);
                var notInProjectMark = Mark(plan.Prune ? "drop" : null);
                foreach (var (kind, what) in rows)
                {
                    var symbol = ReferenceEquals(items, plan.SchemaChanges) ? Mark(kind.ToLowerInvariant()) : notInProjectMark;
                    text.Append($"  {symbol} {kind.ToLowerInvariant().PadRight(width)}  {what}\n");
                }
            }
            else
            {
                // Scripts run; renamed records change.
                var mark = Mark(ReferenceEquals(items, plan.RecordsToRename) ? "rename" : "run");
                foreach (var item in items)
                {
                    text.Append($"  {mark} {item}\n");
                }
            }
        }

        text.Append($"\n{Summary(plan)}\n");
        return text.ToString();
    }

    /// <summary>The text in an ANSI style (a colour, or 1 for bold) when colouring.</summary>
    public static string Styled(string text, string code, bool color) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;

    /// <summary>One line counting what the plan does, then what it leaves or drops that isn't in the project.</summary>
    private static string Summary(Plan plan)
    {
        var scripts = plan.MigrationsBefore.Count + plan.ReferenceData.Count + plan.MigrationsAfter.Count;
        var counts = new[]
        {
            (plan.SchemaChanges.Count, One: "schema change", Many: "schema changes"),
            (Count: scripts, One: "script to run", Many: "scripts to run"),
            (plan.RecordsToRename.Count, One: "record to rename", Many: "records to rename"),
        };
        var done = string.Join(", ", counts.Where(c => c.Count > 0).Select(c => $"{c.Count} {(c.Count == 1 ? c.One : c.Many)}"));
        var summary = done.Length > 0 ? $"{done}." : "Nothing to apply.";
        if (plan.NotInProject.Count is var kept and > 0)
        {
            summary += $" {kept} {(kept == 1 ? "object" : "objects")} not in the project {(plan.Prune ? "dropped" : "kept")}.";
        }

        return summary;
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Columns();
}
