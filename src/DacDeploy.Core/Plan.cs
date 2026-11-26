namespace DacDeploy;

/// <summary>What a plan is made for; applying it anywhere else is refused.</summary>
/// <param name="Variables">The SQLCMD variables the deployment sets, by name without $( ).</param>
public sealed record PlanTarget(string Environment, string Server, string Database, IReadOnlyDictionary<string, string> Variables)
{
    /// <summary>The target's values in the order a plan file lists them; each variable as "$(Name)".</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Values =>
    [
        new(nameof(Environment), Environment),
        new(nameof(Server), Server),
        new(nameof(Database), Database),
        .. Variables.OrderBy(variable => variable.Key, StringComparer.Ordinal)
            .Select(variable => new KeyValuePair<string, string>($"$({variable.Key})", variable.Value)),
    ];

    public bool Equals(PlanTarget? other) => other is not null && Values.SequenceEqual(other.Values);

    public override int GetHashCode() => Values.Aggregate(0, (hash, value) => HashCode.Combine(hash, value.Key, value.Value));

    public static string Describe(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join(", ", values.Select(value => $"{value.Key} {value.Value}"));
}

/// <summary>What applying the project would do to one target, in the order it would do it, and the SQL it would run.</summary>
public sealed record Plan(PlanTarget Target)
{
    /// <summary>Whether the plan drops what's not in the project, instead of leaving it in place.</summary>
    public bool Prune { get; init; }

    public IReadOnlyList<string> SchemaChanges { get; init; } = [];

    /// <summary>What the target has and the project doesn't ("View    [dbo].[OldView]").</summary>
    public IReadOnlyList<string> NotInProject { get; init; } = [];

    /// <summary>The scripts to run, by file name ("20261120-backfill.sql").</summary>
    public IReadOnlyList<string> MigrationsBefore { get; init; } = [];

    public IReadOnlyList<string> ReferenceData { get; init; } = [];

    public IReadOnlyList<string> MigrationsAfter { get; init; } = [];

    /// <summary>Records named the older way, renamed as this deployment runs ("DataScripts:Data/x.sql → dacdeploy:data:x.sql").</summary>
    public IReadOnlyList<string> RecordsToRename { get; init; } = [];

    public string Sql { get; init; } = "";

    /// <summary><see cref="PlanCheck"/>'s fingerprint of the plan; applying refuses a plan without it.</summary>
    public string Fingerprint { get; init; } = "";

    public bool Changed =>
        SchemaChanges.Count > 0 || MigrationsBefore.Count > 0 || ReferenceData.Count > 0 || MigrationsAfter.Count > 0
        || RecordsToRename.Count > 0;

    /// <summary>The plan's lists, titled, in the order the plan does them; then what the target has that the project doesn't.</summary>
    public IReadOnlyList<(string Title, IReadOnlyList<string> Items)> Sections =>
    [
        ("Records to rename from their older names", RecordsToRename),
        ("Migrations to run before the schema changes", MigrationsBefore),
        ("Schema changes", SchemaChanges),
        ("Reference data to update", ReferenceData),
        ("Migrations to run after the schema changes", MigrationsAfter),
        (Prune
            ? "In the database but not in the project, dropped by this plan"
            : "In the database but not in the project, kept (drop with --prune)", NotInProject),
    ];
}
