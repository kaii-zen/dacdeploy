using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace DacDeploy.IntegrationTests;

/// <summary>A SQL Server container, with admin (sa) connections to it.</summary>
public class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
        // SQL Server otherwise treats 80% of the Docker VM's memory as its own, so a few test containers on one machine
        // starve it. 2 GB is the least SQL Server supports; test databases are small.
        .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
        .Build();

    public virtual async ValueTask InitializeAsync() => await container.StartAsync();

    public virtual async ValueTask DisposeAsync()
    {
        await container.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>The admin (sa) connection to the server: its address, user and password.</summary>
    public SqlConnectionStringBuilder Admin => new(GetDeploymentTestConnectionString("master"));

    /// <summary>An admin (sa) connection string to <paramref name="databaseName"/> on this fixture's server.</summary>
    public string GetDeploymentTestConnectionString(string databaseName) =>
        new SqlConnectionStringBuilder(container.GetConnectionString()) { InitialCatalog = databaseName, TrustServerCertificate = true }
            .ConnectionString;

    /// <summary>An open admin (sa) connection to <paramref name="databaseName"/>.</summary>
    public async Task<SqlConnection> OpenAsync(string databaseName)
    {
        var connection = new SqlConnection(GetDeploymentTestConnectionString(databaseName));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    /// <summary>Runs <paramref name="sql"/> in <paramref name="databaseName"/>, as admin (sa).</summary>
    public async Task ExecuteAsync(string databaseName, string sql)
    {
        await using var connection = await OpenAsync(databaseName);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The first column of the first row <paramref name="sql"/> returns in <paramref name="databaseName"/>.</summary>
    public async Task<T> ScalarAsync<T>(string databaseName, string sql)
    {
        await using var connection = await OpenAsync(databaseName);
        await using var command = new SqlCommand(sql, connection);
        return (T)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
