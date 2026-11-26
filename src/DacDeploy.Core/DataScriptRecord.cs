namespace DacDeploy;

/// <summary>What a data script is: reference data, run again whenever it changes, or a one-off, run once.</summary>
public enum DataScriptKind
{
    ReferenceData,
    Migration,
}

/// <summary>
/// The name of a data script's record on the database, an extended property holding the file's SHA-256: the script's
/// kind and its path inside its folder (<c>dacdeploy:data:Settings.sql</c>,
/// <c>dacdeploy:migration:20990101-backfill.sql#pre</c>), never where the folder is, so the folder can move.
/// </summary>
public static class DataScriptRecord
{
    public const string Prefix = "dacdeploy:";

    /// <summary>The prefix of records named the older way, after the script's path from the project's folder.</summary>
    public const string OlderPrefix = "DataScripts:";

    private const string DataKind = "data:";
    private const string MigrationKind = "migration:";

    // The folders the older names count from: always these, next to the project.
    private const string OlderData = OlderPrefix + "Data/";
    private const string OlderMigrations = OlderPrefix + "Migrations/";

    /// <summary>The record of <paramref name="path"/> (inside its folder), or of one part of a one-off.</summary>
    public static string Name(DataScriptKind kind, string path, string? section) =>
        Prefix + (kind == DataScriptKind.ReferenceData ? DataKind : MigrationKind) + path + (section is null ? "" : "#" + section);

    /// <summary>
    /// The record a record named the older way stands for (<c>DataScripts:Data/Settings.sql</c> is
    /// <c>dacdeploy:data:Settings.sql</c>), or null when <paramref name="name"/> isn't one.
    /// </summary>
    public static string? FromOlderName(string name)
    {
        if (name.StartsWith(OlderData, StringComparison.Ordinal))
        {
            return Prefix + DataKind + name[OlderData.Length..];
        }

        return name.StartsWith(OlderMigrations, StringComparison.Ordinal) ? Prefix + MigrationKind + name[OlderMigrations.Length..] : null;
    }

    /// <summary>Whether an extended property is a data script's record, of either form.</summary>
    public static bool IsRecord(string name) =>
        name.StartsWith(Prefix, StringComparison.Ordinal) || name.StartsWith(OlderPrefix, StringComparison.Ordinal);
}
