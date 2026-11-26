namespace DacDeploy.IntegrationTests;

/// <summary>
/// The one SQL Server container every test shares (an assembly fixture, see AssemblyFixture.cs), for tests that deploy
/// SampleDatabase: a project of a few tables and one reference data script, so a deploy takes seconds. Each test works
/// in a database of its own (<see cref="NewDatabase"/>), and SampleDatabase changes nothing server-wide, so tests running
/// at once don't meet.
/// </summary>
public sealed class SampleDatabaseFixture : SqlServerFixture
{
    /// <summary>SampleDatabase next to the test binaries, laid out as a database folder: the dacpac, the publish profile, Data/.</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "SampleDatabase");

    public static string DacpacPath => Path.Combine(Directory, "SampleDatabase.dacpac");

    /// <summary>The sample's publish profile.</summary>
    public static string ProfilePath => Path.Combine(Directory, "SampleDatabase.publish.xml");

    /// <summary>The sample as dacdeploy finds it, for running dacdeploy's commands in-process.</summary>
    public static DatabaseProject Project => new(Path.Combine(Directory, "SampleDatabase.sqlproj"), ProfilePath, UserSecretsId: null);

    /// <summary>A Deployer of the sample, its data scripts read from <paramref name="dataScriptsRoot"/> (by default its own).</summary>
    public static Deployer Deployer(string? dataScriptsRoot = null) =>
        new(new DeploymentFiles(DacpacPath, ProfilePath, dataScriptsRoot ?? Directory));

    /// <summary>A database of the calling test's own on this server, not created yet.</summary>
    public TestDatabase NewDatabase() => new(this);
}
