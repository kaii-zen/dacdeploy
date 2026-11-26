using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace DacDeploy;

/// <summary>A deployment refused or failed: a contributor's refusal, a prune with work pending, or DacFx's own error.</summary>
public sealed class DeploymentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// What deploying needs: the project's dacpac, its publish profile, the project's folder, and its data scripts; without
/// a list of them, those in Data/ and Migrations/ in <paramref name="DataScriptsRoot"/>.
/// </summary>
public sealed record DeploymentFiles(string DacpacPath, string ProfilePath, string DataScriptsRoot, IReadOnlyList<DataScript>? DataScripts = null);

/// <summary>The database to deploy to, and the environment it belongs to (part of a plan's identity).</summary>
/// <param name="Variables">The SQLCMD variables to deploy with, by name without $( ).</param>
public sealed record DeploymentTarget(
    string Environment, string Server, string Database, IReadOnlyDictionary<string, string> Variables, string User, string Password)
{
    public PlanTarget Identity => new(Environment, Server, Database, Variables);

    public string ConnectionString => new SqlConnectionStringBuilder
    {
        DataSource = Server,
        UserID = User,
        Password = Password,
        TrustServerCertificate = true,
        ConnectTimeout = 30,
    }.ConnectionString;
}

/// <summary>
/// Plans and applies a project in-process with DacFx and the contributors in this assembly. DacFx can't run a saved
/// script: applying plans again, and <see cref="PlanCheck"/> stops it, before it runs anything, unless that plan is the
/// reviewed one.
/// </summary>
public sealed class Deployer(DeploymentFiles files)
{
    /// <summary>
    /// The contributors every deployment runs, in the order DacFx runs them: keep what isn't in the project, run the
    /// data scripts, then fingerprint the plan (last, so it sees the others' changes). Added after any the publish
    /// profile lists.
    /// </summary>
    public const string Contributors = $"{KeepRemoved.Id};{DataScripts.Id};{PlanCheck.Id}";

    /// <summary>
    /// What deploying the project to <paramref name="target"/> would do. With <paramref name="prune"/>, it drops what the
    /// target has and the project doesn't, and is refused while anything else is pending.
    /// </summary>
    /// <param name="log">Receives everything DacFx and the contributors print.</param>
    public Plan Plan(DeploymentTarget target, bool prune = false, Action<string>? log = null)
    {
        var plan = PlanOnce(target, prune, log);
        // A prune runs nothing but its drops: DacFx would apply anything else pending along with them, and drop and
        // recreate whatever depends on what's dropped. A normal plan of the same target settles it.
        if (prune && PlanOnce(target, prune: false, log).Changed)
        {
            throw new DeploymentException(
                "--prune only drops what isn't in the project, and changes are pending. Apply them first (plan without --prune), then prune.");
        }

        return plan;
    }

    /// <summary>Deploys the project to the target, refused unless DacFx's plan is <paramref name="plan"/>.</summary>
    public void Apply(DeploymentTarget target, Plan plan, Action<string>? log = null)
    {
        using var report = new DeploymentReport();
        Run(target, report, plan.Prune, plan.Fingerprint, publish: true, log);
    }

    /// <summary>
    /// Deploys the project to the target without a reviewed plan, as applying with --yes does after planning.
    /// For test databases; releases plan, review, then <see cref="Apply"/>.
    /// </summary>
    public void Deploy(DeploymentTarget target, Action<string>? log = null)
    {
        using var report = new DeploymentReport();
        Run(target, report, prune: false, expectedFingerprint: null, publish: true, log);
    }

    /// <summary>
    /// The schema changes deploying would still make ("Alter  [dbo].[SomeView]"), empty once the target matches the
    /// project; with <paramref name="includeObjectsOnlyInDatabase"/>, also what the target has and the project doesn't.
    /// </summary>
    public IReadOnlyList<string> Pending(DeploymentTarget target, bool includeObjectsOnlyInDatabase = false, Action<string>? log = null)
    {
        var plan = PlanOnce(target, prune: false, log);
        return includeObjectsOnlyInDatabase ? [.. plan.SchemaChanges, .. plan.NotInProject] : plan.SchemaChanges;
    }

    /// <summary>Extracts the source database's schema into <paramref name="folder"/>, one file per object.</summary>
    public static void Extract(DeploymentTarget source, string folder, Action<string>? log = null)
    {
        var services = new DacServices(source.ConnectionString);
        if (log is not null)
        {
            services.Message += (_, e) => log(e.Message.ToString());
        }

        try
        {
            services.Extract(folder, source.Database, source.Database, new Version(1, 0), extractOptions: new DacExtractOptions
            {
                ExtractTarget = DacExtractTarget.SchemaObjectType,
                VerifyExtraction = false,
            });
        }
        catch (DacServicesException e)
        {
            var details = e.Messages.Select(m => m.ToString()).Concat(Causes(e)).Distinct();
            throw new DeploymentException($"{e.Message}{Environment.NewLine}{string.Join(Environment.NewLine, details)}", e);
        }
    }

    private Plan PlanOnce(DeploymentTarget target, bool prune, Action<string>? log)
    {
        using var report = new DeploymentReport();
        var result = Run(target, report, prune, expectedFingerprint: null, publish: false, log);

        var operations = XDocument.Parse(result.DeploymentReport).Descendants()
            .Where(item => item.Name.LocalName == "Item" && item.Parent?.Name.LocalName == "Operation")
            .Select(item => (Operation: item.Parent!.Attribute("Name")?.Value ?? "", Value: item.Attribute("Value")?.Value ?? ""))
            .ToList();
        // The deploy report leaves out the steps KeepRemoved rewrote.
        operations.AddRange(report.RewrittenTables.Select(table => ("Alter", table)).Where(alter => !operations.Contains(alter)));

        if (report.Fingerprint is not { } fingerprint)
        {
            throw new DeploymentException($"{PlanCheck.Id} reported no fingerprint; DacFx didn't run it.");
        }

        // A prune runs no data scripts; refusing them in the plan PlanCheck fingerprints keeps them out of what's published.
        if (prune && report.DataScripts.Count > 0)
        {
            throw new DeploymentException(
                $"--prune runs no data scripts, and these are pending: {string.Join(", ", report.DataScripts.Select(part => part.Name))}. " +
                "Apply them first (plan without --prune), then prune.");
        }

        return new Plan(target.Identity)
        {
            Prune = prune,
            SchemaChanges = Aligned(operations),
            NotInProject = Aligned(report.NotInProject.Select(item => (item.Type, item.Name + (item.MadeNullable ? " (made nullable)" : "")))),
            // By name inside its folder, what its record names.
            MigrationsBefore = Files(report, DataScriptSlot.Before),
            ReferenceData = Files(report, DataScriptSlot.Data),
            MigrationsAfter = Files(report, DataScriptSlot.After),
            RecordsToRename = [.. report.RecordsToRename.Select(rename => $"{rename.Older} → {rename.Name}")],
            Sql = result.DatabaseScript ?? "",
            Fingerprint = fingerprint,
        };
    }

    private PublishResult Run(
        DeploymentTarget target, DeploymentReport report, bool prune, string? expectedFingerprint, bool publish, Action<string>? log)
    {
        var options = DacProfile.Load(files.ProfilePath).DeployOptions;
        foreach (var (name, value) in target.Variables)
        {
            options.SqlCommandVariableValues[name] = value;
        }

        var ours = Contributors.Split(';');
        options.AdditionalDeploymentContributors = string.Join(";", Split(options.AdditionalDeploymentContributors).Except(ours).Concat(ours));
        // The contributors are this assembly: DacFx loads them from its folder and reuses the loaded assembly, so they
        // fill in the report this process reads.
        options.AdditionalDeploymentContributorPaths = string.Join(
            ";", Split(options.AdditionalDeploymentContributorPaths).Append(Path.GetDirectoryName(typeof(Deployer).Assembly.Location)));
        var arguments = new List<string> { $"{DataScripts.RootArgument}={files.DataScriptsRoot}", report.Argument };
        using var manifest = files.DataScripts is { } scripts ? DataScriptManifest.Write(scripts) : null;
        if (manifest is not null)
        {
            arguments.Add($"{DataScripts.ManifestArgument}={manifest.Path}");
        }

        if (expectedFingerprint is not null)
        {
            arguments.Add($"{PlanCheck.ExpectedArgument}={expectedFingerprint}");
        }

        if (prune)
        {
            arguments.Add($"{KeepRemoved.PruneArgument}=true");
            options.DropObjectsNotInSource = true;
            options.BlockOnPossibleDataLoss = false;
        }

        options.AdditionalDeploymentContributorArguments = string.Join(";", arguments);

        var services = new DacServices(target.ConnectionString);
        var messages = new List<string>();
        services.Message += (_, e) =>
        {
            var message = e.Message.ToString();
            messages.Add(message);
            log?.Invoke(message);
        };

        var publishOptions = new PublishOptions
        {
            DeployOptions = options,
            GenerateDeploymentScript = !publish,
            GenerateDeploymentReport = !publish,
        };
        try
        {
            using var package = DacPackage.Load(files.DacpacPath);
            return publish
                ? services.Publish(package, target.Database, publishOptions)
                : services.Script(package, target.Database, publishOptions);
        }
        catch (DacServicesException e)
        {
            var details = string.Join(Environment.NewLine, e.Messages.Select(m => m.ToString()).Concat(messages).Concat(Causes(e)).Distinct());
            throw new DeploymentException($"{e.Message}{Environment.NewLine}{details}", e);
        }
    }

    private static string[] Split(string? list) =>
        (list ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>What caused a DacFx failure, such as a login refused: DacFx's own message leaves it out.</summary>
    private static IEnumerable<string> Causes(Exception e)
    {
        for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
        {
            yield return inner.Message;
        }
    }

    private static List<string> Files(DeploymentReport report, DataScriptSlot slot) =>
        [.. report.DataScripts.Where(part => part.Slot == slot).Select(part => part.Name)];

    /// <summary>Two columns, the first padded to the widest.</summary>
    private static List<string> Aligned(IEnumerable<(string First, string Second)> pairs)
    {
        var list = pairs.ToList();
        var width = list.Count == 0 ? 0 : list.Max(pair => pair.First.Length);
        return [.. list.Select(pair => $"{pair.First.PadRight(width)}  {pair.Second}")];
    }
}
