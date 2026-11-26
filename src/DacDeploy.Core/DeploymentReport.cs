using System.Collections.Concurrent;

namespace DacDeploy;

/// <summary>
/// What the contributors found while planning one deployment, for a caller in the same process. The caller passes
/// <see cref="Argument"/> among the contributor arguments; each contributor fills in the report it names. Reports are
/// told apart by id, so deployments planned at the same time don't mix.
/// </summary>
public sealed class DeploymentReport : IDisposable
{
    public const string IdArgument = "DacDeploy.Report";

    private static readonly ConcurrentDictionary<string, DeploymentReport> Open = new();

    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly List<NotInProjectItem> notInProject = [];
    private readonly List<string> rewrittenTables = [];
    private readonly List<DataScriptPart> dataScripts = [];
    private readonly List<(string Older, string Name)> recordsToRename = [];

    public DeploymentReport() => Open[id] = this;

    /// <summary>The contributor argument naming this report.</summary>
    public string Argument => $"{IdArgument}={id}";

    /// <summary><see cref="PlanCheck"/>'s fingerprint of the final plan.</summary>
    public string? Fingerprint { get; internal set; }

    /// <summary><see cref="KeepRemoved"/>: what the database has and the project doesn't, kept or dropped.</summary>
    public IReadOnlyList<NotInProjectItem> NotInProject => notInProject;

    /// <summary><see cref="KeepRemoved"/>: the tables whose step it rewrote, which the plan still changes.</summary>
    public IReadOnlyList<string> RewrittenTables => rewrittenTables;

    /// <summary><see cref="DataScripts"/>: the parts it runs, in the order it runs them.</summary>
    public IReadOnlyList<DataScriptPart> DataScripts => dataScripts;

    /// <summary><see cref="DataScripts"/>: the records named the older way it renames, with their new names.</summary>
    public IReadOnlyList<(string Older, string Name)> RecordsToRename => recordsToRename;

    public void Dispose() => Open.TryRemove(id, out _);

    internal void Add(NotInProjectItem item) => notInProject.Add(item);

    internal void Add(DataScriptPart part) => dataScripts.Add(part);

    internal void AddRename(string older, string name) => recordsToRename.Add((older, name));

    internal void AddRewrittenTable(string table) => rewrittenTables.Add(table);

    /// <summary>The report a deployment's contributor arguments name, if any.</summary>
    internal static DeploymentReport? For(IDictionary<string, string> arguments) =>
        arguments.TryGetValue(IdArgument, out var id) && Open.TryGetValue(id, out var report) ? report : null;
}

/// <summary>Something the database has and the project doesn't: kept, or dropped when pruning.</summary>
/// <param name="Type">The kind of object, such as View or Column.</param>
/// <param name="Name">Its name, such as [dbo].[OldView].</param>
/// <param name="MadeNullable">A kept NOT NULL column without a default, made nullable so inserts leaving it out work.</param>
public sealed record NotInProjectItem(string Type, string Name, bool Dropped, bool MadeNullable);

/// <summary>Where a data script part runs: before the schema changes, as reference data, or after them.</summary>
public enum DataScriptSlot
{
    Before,
    Data,
    After,
}

/// <summary>A data script part a deployment runs, by its name inside its folder ("20261120-backfill.sql"), what its record names.</summary>
public sealed record DataScriptPart(DataScriptSlot Slot, string Name);
