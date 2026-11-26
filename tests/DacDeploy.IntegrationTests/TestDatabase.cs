using Microsoft.Data.SqlClient;

namespace DacDeploy.IntegrationTests;

/// <summary>A database of one test's own on the shared server, named with a GUID; used as admin (sa).</summary>
public sealed class TestDatabase(SampleDatabaseFixture server)
{
    public string Name { get; } = $"Test_{Guid.NewGuid():N}";

    /// <summary>This database as a deploy target.</summary>
    public DeploymentTarget Target
    {
        get
        {
            var admin = server.Admin;
            return new DeploymentTarget("test", admin.DataSource, Name, new Dictionary<string, string>(), admin.UserID, admin.Password);
        }
    }

    /// <summary>Creates the database, empty, unless it exists.</summary>
    public Task CreateAsync() => server.ExecuteAsync("master", $"IF DB_ID(N'{Name}') IS NULL CREATE DATABASE [{Name}];");

    /// <summary>
    /// Deploys SampleDatabase into this database, creating it on first use. <paramref name="extraScripts"/> (path such as
    /// "Migrations/20990101-test.sql", to content) are added to its data scripts, or replace one, for this deploy only.
    /// </summary>
    /// <exception cref="DeploymentException">The deploy failed or was refused.</exception>
    public async Task DeployAsync(IReadOnlyDictionary<string, string>? extraScripts = null)
    {
        await CreateAsync();
        if (extraScripts is not { Count: > 0 })
        {
            await Task.Run(() => SampleDatabaseFixture.Deployer().Deploy(Target));
            return;
        }

        var root = Directory.CreateTempSubdirectory("SampleDataScripts");
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(SampleDatabaseFixture.Directory, "Data")))
            {
                Directory.CreateDirectory(Path.Combine(root.FullName, "Data"));
                File.Copy(file, Path.Combine(root.FullName, "Data", Path.GetFileName(file)));
            }

            foreach (var (path, content) in extraScripts)
            {
                var file = Path.Combine(root.FullName, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, content);
            }

            await Task.Run(() => SampleDatabaseFixture.Deployer(root.FullName).Deploy(Target));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    public Task<SqlConnection> OpenAsync() => server.OpenAsync(Name);

    public Task ExecuteAsync(string sql) => server.ExecuteAsync(Name, sql);

    /// <summary>The first column of the first row <paramref name="sql"/> returns.</summary>
    public Task<T> ScalarAsync<T>(string sql) => server.ScalarAsync<T>(Name, sql);
}
