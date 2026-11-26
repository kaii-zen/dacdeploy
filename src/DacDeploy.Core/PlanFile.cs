using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DacDeploy;

/// <summary>A plan refused when read: not a plan, edited after it was saved, or made for another target.</summary>
public sealed class PlanFileException(string message) : Exception(message);

/// <summary>
/// A plan saved to review, then apply: a comment header (the target, the fingerprint, the lists) and the SQL. A checksum
/// line, a SHA-256 of the file without it, makes a file edited after it was saved refused. Text uses "\n" line endings;
/// a file saved with "\r\n" reads the same.
/// </summary>
public static partial class PlanFile
{
    private const string Title = "-- dacdeploy plan.";
    private const string PruneKey = "Prune";
    private const string FingerprintKey = "Fingerprint";

    public static string Write(Plan plan)
    {
        var lines = new List<string> { $"{Title} Review it, then apply it with dacdeploy apply." };
        lines.AddRange(plan.Target.Values.Select(value => $"-- {value.Key}: {value.Value}"));
        if (plan.Prune)
        {
            lines.Add($"-- {PruneKey}: yes");
        }

        lines.Add($"-- {FingerprintKey}: {plan.Fingerprint}");
        foreach (var (title, items) in plan.Sections.Where(section => section.Items.Count > 0))
        {
            lines.Add("--");
            lines.Add($"-- {title}:");
            lines.AddRange(items.Select(item => $"--   {item}"));
        }

        // DacFx writes its SQL with "\r\n": checksum the text as it reads back, with "\n" throughout.
        var text = string.Join("\n", lines) + "\n\n" + plan.Sql.ReplaceLineEndings("\n");
        var newline = text.IndexOf('\n');
        return $"{text[..newline]}\n-- Checksum: {Sha256(text)}\n{text[(newline + 1)..]}";
    }

    /// <summary>The plan in <paramref name="text"/>, read from <paramref name="path"/> (named in refusals).</summary>
    /// <exception cref="PlanFileException">It isn't a plan, was edited, has no fingerprint, or was made for another target.</exception>
    public static Plan Read(string text, string path, PlanTarget target)
    {
        text = text.ReplaceLineEndings("\n");
        if (!text.StartsWith(Title, StringComparison.Ordinal))
        {
            throw new PlanFileException($"{path} isn't a plan saved by dacdeploy plan.");
        }

        var checksum = Checksum().Match(text);
        var unchecked_ = checksum.Success ? text.Remove(checksum.Index, checksum.Length) : text;
        if (!checksum.Success || checksum.Groups[1].Value != Sha256(unchecked_))
        {
            throw new PlanFileException($"{path} was edited after it was saved. Plan again and review it.");
        }

        var blank = unchecked_.IndexOf("\n\n", StringComparison.Ordinal);
        var header = blank < 0 ? unchecked_ : unchecked_[..blank];
        var sql = blank < 0 ? "" : unchecked_[(blank + 2)..];

        var plannedFor = new List<KeyValuePair<string, string>>();
        var sections = new Dictionary<string, List<string>>();
        var items = new List<string>();
        foreach (var line in header.Split('\n').Skip(1))
        {
            if (line.StartsWith("--   ", StringComparison.Ordinal))
            {
                items.Add(line["--   ".Length..]);
            }
            else if (line.EndsWith(':'))
            {
                items = sections[line.RemovePrefix("-- ")[..^1]] = [];
            }
            else if (line != "--")
            {
                var keyValue = line.RemovePrefix("-- ");
                var separator = keyValue.IndexOf(": ", StringComparison.Ordinal);
                plannedFor.Add(separator < 0 ? new(keyValue, "") : new(keyValue[..separator], keyValue[(separator + 2)..]));
            }
        }

        var prune = Take(plannedFor, PruneKey) == "yes";
        var fingerprint = Take(plannedFor, FingerprintKey) ?? "";
        if (fingerprint.Length == 0)
        {
            throw new PlanFileException($"{path} has no fingerprint to check the deployment against. Plan again and review it.");
        }

        if (!plannedFor.OrderBy(value => value.Key).SequenceEqual(target.Values.OrderBy(value => value.Key)))
        {
            throw new PlanFileException(
                $"{path} was planned for {PlanTarget.Describe(plannedFor)}, not {PlanTarget.Describe(target.Values)}. Plan again for this target.");
        }

        var lists = new Plan(target) { Prune = prune }.Sections
            .Select(section => (IReadOnlyList<string>)(sections.GetValueOrDefault(section.Title) ?? []))
            .ToList();
        return new Plan(target)
        {
            Prune = prune,
            MigrationsBefore = lists[0],
            SchemaChanges = lists[1],
            ReferenceData = lists[2],
            MigrationsAfter = lists[3],
            NotInProject = lists[4],
            Sql = sql,
            Fingerprint = fingerprint,
        };
    }

    private static string? Take(List<KeyValuePair<string, string>> values, string key)
    {
        var index = values.FindIndex(value => value.Key == key);
        if (index < 0)
        {
            return null;
        }

        var value = values[index].Value;
        values.RemoveAt(index);
        return value;
    }

    private static string RemovePrefix(this string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [GeneratedRegex(@"^-- Checksum: (\w+)\n", RegexOptions.Multiline)]
    private static partial Regex Checksum();
}
