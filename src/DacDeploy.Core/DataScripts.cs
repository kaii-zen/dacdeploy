using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.Dac.Deployment;
using Microsoft.SqlServer.Dac.Model;

namespace DacDeploy;

/// <summary>
/// A deployment contributor that runs the data scripts in the same deployment as the schema:
/// <c>Data/*.sql</c> (reference data) whenever the file changes, and <c>Migrations/*.sql</c> (one-offs) once.
/// <para>
/// A one-off may split into a part that runs before the schema changes (<c>--# pre</c>) and one that runs after
/// (<c>--# post</c>); one without either runs after. The order is: every pre part, the schema, <c>Data/</c>, every
/// post part, each by file name.
/// </para>
/// <para>
/// Each part runs with its record, in the deployment's one transaction (see <see cref="RequireAllInTransaction"/>): an
/// extended property on the database, named after the part, holding the file's SHA-256. DacFx drops extended properties the project doesn't have; this keeps its
/// own. A one-off whose file changed after a part of it ran stops the deployment.
/// </para>
/// <para>
/// It reads the scripts from the folder given as <see cref="RootArgument"/> (the project's), and records the parts
/// it runs, in order, in the deployment's <see cref="DeploymentReport"/>.
/// </para>
/// </summary>
[ExportDeploymentPlanModifier(Id, "1.0.0.0")]
public sealed partial class DataScripts : DeploymentPlanModifier
{
    public const string Id = "DacDeploy.DataScripts";

    public const string RootArgument = "DacDeploy.DataScripts.Root";

    private const string RecordPrefix = "DataScripts:";

    private const int MaxRecordName = 128;

    private enum Slot
    {
        Before,
        Data,
        After,
    }

    /// <summary>A part of a script: <paramref name="File"/> is its path under the root ("Migrations/x.sql").</summary>
    private sealed record Part(string File, string? Section, Slot Slot, string Hash, string Sql)
    {
        public string Record => RecordPrefix + File + (Section is null ? "" : "#" + Section);
    }

    [GeneratedRegex(@"^[ \t]*GO[ \t]*(?:--[^\r\n]*)?\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Go();

    [GeneratedRegex(@"^--#(?<directive>.*?)[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex Directive();

    protected override void OnExecute(DeploymentPlanContributorContext context)
    {
        if (!context.Arguments.TryGetValue(RootArgument, out var root))
        {
            throw new DeploymentFailedException($"{Id} needs the {RootArgument} argument: the folder holding Data/ and Migrations/.");
        }

        var report = DeploymentReport.For(context.Arguments);
        var steps = PlanSteps.All(context.PlanHandle);
        foreach (var drop in steps.OfType<DropElementStep>().Where(d => IsRecord(d.TargetElement)))
        {
            Remove(context.PlanHandle, drop);
        }

        var recorded = context.Target.GetObjects(DacQueryScopes.All, ExtendedProperty.TypeClass)
            .Where(IsRecord)
            // The value is a SQL literal, read as a string through the untyped accessor; the typed one refuses.
            .ToDictionary(p => p.Name.Parts[^1], p => Unquote(p.GetProperty(ExtendedProperty.Value)?.ToString() ?? ""), StringComparer.Ordinal);
        var parts = Parts(root).ToList();
        if (parts.FirstOrDefault(p => p.Slot != Slot.Data && recorded.TryGetValue(p.Record, out var hash) && hash != p.Hash) is { } changed)
        {
            throw new DeploymentFailedException($"{changed.File} changed after it ran. A one-off never changes once it has run anywhere; write a new one.");
        }

        var pending = parts.Where(p => !recorded.ContainsKey(p.Record) || (p.Slot == Slot.Data && recorded[p.Record] != p.Hash)).ToList();
        if (pending.Count > 0)
        {
            // Inside the deployment's one transaction: pre parts right after it starts, the others right before it ends.
            var (begin, end) = Transaction(context.PlanHandle, steps);
            var pre = begin.Next;
            foreach (var part in pending.OrderBy(p => p.Slot).ThenBy(p => p.File, StringComparer.Ordinal))
            {
                AddBefore(context.PlanHandle, part.Slot == Slot.Before ? pre : end, new DeploymentScriptStep(Batches(part)));
                report?.Add(new DataScriptPart(Enum.Parse<DataScriptSlot>(part.Slot.ToString()), part.File));
            }
        }

        RequireAllInTransaction(context.PlanHandle);
    }

    /// <summary>
    /// The steps that start and end the deployment's transaction: DacFx's (IncludeTransactionalScripts), or, in a
    /// plan with no schema changes, where DacFx opens none, a pair of its own around the pre- and post-deployment
    /// slots.
    /// </summary>
    private (DeploymentStep Begin, DeploymentStep End) Transaction(DeploymentPlanHandle plan, List<DeploymentStep> steps)
    {
        if (steps.OfType<SqlBeginTransactionStep>().FirstOrDefault() is { } begin && steps.OfType<SqlEndTransactionStep>().FirstOrDefault() is { } end)
        {
            return (begin, end);
        }

        var ownBegin = new DeploymentScriptStep(["SET XACT_ABORT ON;\nBEGIN TRANSACTION;"]);
        var ownEnd = new DeploymentScriptStep(["COMMIT TRANSACTION;"]);
        AddBefore(plan, steps.OfType<EndPreDeploymentScriptStep>().Single(), ownBegin);
        AddBefore(plan, steps.OfType<EndPostDeploymentScriptStep>().Single(), ownEnd);
        return (ownBegin, ownEnd);
    }

    /// <summary>
    /// Whether a step only frames the deployment (its script's header and settings, USE, messages, section markers), so it
    /// may run outside the transaction. DacFx writes the changes themselves as element steps; these aren't.
    /// </summary>
    private static bool IsFraming(DeploymentStep step) => step is DeploymentScriptStep or DeploymentScriptDomStep or SqlChangeDatabaseStep
        or SqlPrintStep or EmptyStep or BeginPreDeploymentScriptStep or EndPreDeploymentScriptStep or BeginPostDeploymentScriptStep
        or EndPostDeploymentScriptStep or SqlBeginPreservationStep or SqlEndPreservationStep or SqlBeginDropsStep or SqlEndDropsStep
        or SqlBeginAltersStep or SqlEndAltersStep or SqlBeginTransactionStep or SqlEndTransactionStep;

    /// <summary>Refuses a plan with a change outside its transaction, which a failure further on wouldn't undo.</summary>
    private static void RequireAllInTransaction(DeploymentPlanHandle plan)
    {
        var inside = false;
        var outside = new List<string>();
        for (var step = plan.Head; step != null; step = step.Next)
        {
            inside = step switch
            {
                SqlBeginTransactionStep => true,
                SqlEndTransactionStep => false,
                _ => inside,
            };
            if (!inside && !IsFraming(step))
            {
                outside.Add(string.Join(" ", step.GenerateTSQL()).Trim() is var sql && sql.Length > 80 ? sql[..80] + "..." : sql);
            }
        }

        if (outside.Count > 0)
        {
            throw new DeploymentFailedException(
                "These changes would run outside the deployment's transaction, so a failure wouldn't undo them; " +
                $"check IncludeTransactionalScripts in the publish profile:\n{string.Join("\n", outside)}");
        }
    }

    private static bool IsRecord(TSqlObject? element) =>
        element?.ObjectType == ExtendedProperty.TypeClass && element.Name.Parts[^1].StartsWith(RecordPrefix, StringComparison.Ordinal);

    /// <summary>An extended property's value as the model holds it, a string literal (N'...'), without the quotes.</summary>
    private static string Unquote(string literal) =>
        literal.StartsWith("N'", StringComparison.Ordinal) ? literal[2..^1].Replace("''", "'") : literal.Trim('\'');

    private static IEnumerable<Part> Parts(string root)
    {
        foreach (var file in Files(root, "Data"))
        {
            var bytes = File.ReadAllBytes(file);
            yield return new Part(Relative(root, file), null, Slot.Data, Sha256(bytes), File.ReadAllText(file));
        }

        foreach (var file in Files(root, "Migrations"))
        {
            RequireDated(Relative(root, file));
            var bytes = File.ReadAllBytes(file);
            foreach (var (section, sql) in Sections(Relative(root, file), File.ReadAllText(file)))
            {
                yield return new Part(Relative(root, file), section, section == "pre" ? Slot.Before : Slot.After, Sha256(bytes), sql);
            }
        }
    }

    /// <summary>One-offs run in file name order, so each name starts with the date: <c>yyyyMMdd-</c>.</summary>
    private static void RequireDated(string file)
    {
        var name = Path.GetFileName(file);
        if (name.Length < 10 || name[8] != '-'
            || !DateTime.TryParseExact(name[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new DeploymentFailedException($"{file}: name one-offs <yyyyMMdd>-<what>.sql (such as 20261120-backfill-client-defaults.sql), so they run in date order.");
        }
    }

    private static IEnumerable<string> Files(string root, string folder)
    {
        var directory = Path.Combine(root, folder);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.sql").Order(StringComparer.Ordinal)
            : [];
    }

    /// <summary>A one-off's parts: ("post", everything) without directives, else each directive's section.</summary>
    private static List<(string Section, string Sql)> Sections(string file, string text)
    {
        var directives = Directive().Matches(text);
        if (directives.Count == 0)
        {
            return [("post", text)];
        }

        var head = text[..directives[0].Index];
        if (head.Split('\n').Any(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith("--", StringComparison.Ordinal)))
        {
            throw new DeploymentFailedException($"{file}: only comments may come before its first --# line.");
        }

        var sections = new List<(string Section, string Sql)>();
        for (var i = 0; i < directives.Count; i++)
        {
            var section = directives[i].Groups["directive"].Value.Trim();
            var expected = sections.Count == 0 ? new[] { "pre", "post" } : ["post"];
            if (!expected.Contains(section, StringComparer.Ordinal))
            {
                throw new DeploymentFailedException(
                    $"{file}: \"--#{directives[i].Groups["directive"].Value}\" isn't valid here. A one-off may have --# pre, then --# post, each at most once.");
            }

            var start = directives[i].Index + directives[i].Length;
            var end = i + 1 < directives.Count ? directives[i + 1].Index : text.Length;
            sections.Add((section, text[start..end]));
        }

        return sections;
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>The part's batches, then the batch that records it; the deployment's transaction holds them.</summary>
    private static List<string> Batches(Part part)
    {
        if (part.Record.Length > MaxRecordName)
        {
            throw new DeploymentFailedException($"{part.File}: its path is too long to record; keep it under {MaxRecordName - RecordPrefix.Length - 5} characters.");
        }

        var name = part.Record.Replace("'", "''");
        var label = (part.File + (part.Section is null ? "" : $" ({part.Section})")).Replace("'", "''");
        return
        [
            $"PRINT N'Running {label}...';",
            .. Go().Split(part.Sql).Where(batch => !string.IsNullOrWhiteSpace(batch)),
            $"""
            IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 0 AND name = N'{name}')
                EXEC sys.sp_updateextendedproperty @name = N'{name}', @value = N'{part.Hash}';
            ELSE
                EXEC sys.sp_addextendedproperty @name = N'{name}', @value = N'{part.Hash}';
            """,
        ];
    }
}
