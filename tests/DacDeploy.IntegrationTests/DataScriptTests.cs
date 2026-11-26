namespace DacDeploy.IntegrationTests;

/// <summary>
/// How the deploy runs Data/ and Migrations/ (the DataScripts contributor): SampleDatabase deployed into a database of
/// each test's own, with test scripts added for one deploy at a time.
/// </summary>
public class DataScriptTests(SampleDatabaseFixture fixture)
{
    private readonly TestDatabase database = fixture.NewDatabase();

    // Each test script logs its runs here; the table isn't in the project, so the deploy leaves it in place.
    private const string Log = """
        IF OBJECT_ID('dbo.DataScriptRuns') IS NULL
            CREATE TABLE dbo.DataScriptRuns (Id INT IDENTITY PRIMARY KEY, Script NVARCHAR(100) NOT NULL, Note NVARCHAR(100) NULL);
        """;

    [Fact]
    public async Task OneOff_RunsOncePerDatabase()
    {
        var scripts = Script("Migrations/20990101-runs-once.sql", $"{Log}\nGO\n{Insert("runs-once")}");

        await database.DeployAsync(scripts);
        await database.DeployAsync(scripts);

        (await RunsAsync("runs-once")).Should().ContainSingle();
    }

    [Fact]
    public async Task OneOff_EditedAfterItRan_StopsTheDeploy()
    {
        const string path = "Migrations/20990101-edited.sql";
        await database.DeployAsync(Script(path, "SELECT 1;"));

        var redeploy = () => database.DeployAsync(Script(path, "SELECT 2;"));

        (await redeploy.Should().ThrowAsync<DeploymentException>()).Which.Message.Should().Contain("changed after it ran");
    }

    [Fact]
    public async Task OneOff_PreAndPostParts_RunAroundTheSchemaChanges()
    {
        // The first deploy creates Item, so each part can tell whether the schema changes ran yet.
        const string schemaChangesRan = "CASE WHEN OBJECT_ID('dbo.Item') IS NULL THEN N'before the schema changes' ELSE N'after them' END";

        await database.DeployAsync(Script("Migrations/20990101-pre-and-post.sql", $"""
            -- A one-off with a part on each side of the schema changes.

            --# pre
            {Log}
            GO
            INSERT dbo.DataScriptRuns (Script, Note) VALUES (N'pre-and-post', N'pre: ' + {schemaChangesRan});

            --# post
            INSERT dbo.DataScriptRuns (Script, Note) VALUES (N'pre-and-post', N'post: ' + {schemaChangesRan});
            """));

        (await RunsAsync("pre-and-post")).Should().Equal("pre: before the schema changes", "post: after them");
    }

    [Fact]
    public async Task OneOff_ThatFails_IsRolledBackAndRunsAgainOnceFixed()
    {
        const string path = "Migrations/20990101-fails.sql";
        await database.CreateAsync();
        await database.ExecuteAsync(Log);

        var failing = () => database.DeployAsync(Script(path, $"{Insert("fails")}\nGO\nSELECT 1 / 0;"));
        await failing.Should().ThrowAsync<DeploymentException>();
        (await RunsAsync("fails")).Should().BeEmpty();

        await database.DeployAsync(Script(path, Insert("fails")));
        (await RunsAsync("fails")).Should().ContainSingle();
    }

    [Fact]
    public async Task OneOff_NamedWithoutADate_StopsTheDeploy()
    {
        var deploy = () => database.DeployAsync(Script("Migrations/backfill.sql", "SELECT 1;"));

        (await deploy.Should().ThrowAsync<DeploymentException>()).Which.Message.Should().Contain("<yyyyMMdd>-<what>.sql");
    }

    [Fact]
    public async Task OneOff_WithWindowsLineEndings_IsStillSplitOnGo()
    {
        var sql = $"{Log}\nGO\n{Insert("crlf")}".ReplaceLineEndings("\r\n");

        await database.DeployAsync(Script("Migrations/20990101-crlf.sql", sql));

        (await RunsAsync("crlf")).Should().ContainSingle();
    }

    [Fact]
    public async Task OneOff_WithAnUnknownDirective_StopsTheDeploy()
    {
        var deploy = () => database.DeployAsync(Script("Migrations/20990101-typo.sql", "--# before\nSELECT 1;"));

        (await deploy.Should().ThrowAsync<DeploymentException>()).Which.Message.Should().Contain("isn't valid here");
    }

    [Fact]
    public async Task AFailingDeploy_LeavesTheDatabaseAsItWas()
    {
        // The project indexes Ticket by Owner; indexed by Status here, the deploy has to drop the index and create it again.
        await database.DeployAsync();
        await database.ExecuteAsync("DROP INDEX IX_Ticket_Owner ON dbo.Ticket; CREATE NONCLUSTERED INDEX IX_Ticket_Owner ON dbo.Ticket ([Status]);");
        var scripts = new Dictionary<string, string>
        {
            ["Data/zz-test-atomic.sql"] = $"{Log}\nGO\n{Insert("atomic")}",
            ["Migrations/20990101-atomic-fails.sql"] = "SELECT 1 / 0;",
        };

        var deploy = () => database.DeployAsync(scripts);

        await deploy.Should().ThrowAsync<DeploymentException>();
        (await database.ScalarAsync<string>(IndexedColumn)).Should().Be("Status", "the index was dropped and created again inside the transaction");
        (await RunsAsync("atomic")).Should().BeEmpty();
        (await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = N'dacdeploy:data:zz-test-atomic.sql'")).Should().Be(0);
    }

    [Fact]
    public async Task ADeployOfDataScriptsAlone_IsAllOrNothingToo()
    {
        // Brought up to date first, so the deploy below has data scripts and no schema changes.
        await database.DeployAsync();
        await database.ExecuteAsync(Log);
        var scripts = new Dictionary<string, string>
        {
            ["Data/zz-test-alone.sql"] = Insert("alone"),
            ["Migrations/20990101-alone-fails.sql"] = "SELECT 1 / 0;",
        };

        var deploy = () => database.DeployAsync(scripts);

        await deploy.Should().ThrowAsync<DeploymentException>();
        (await RunsAsync("alone")).Should().BeEmpty();
    }

    [Fact]
    public async Task ReferenceData_RunsAgainOnlyWhenItChanges()
    {
        const string path = "Data/zz-test-reference.sql";
        var first = Script(path, $"{Log}\nGO\n{Insert("reference", "first")}");

        await database.DeployAsync(first);
        await database.DeployAsync(first);
        await database.DeployAsync(Script(path, $"{Log}\nGO\n{Insert("reference", "second")}"));

        (await RunsAsync("reference")).Should().Equal("first", "second");
    }

    [Fact]
    public async Task OlderRecords_AreRenamed_AndTheirScriptsDontRunAgain()
    {
        var scripts = Script("Migrations/20990101-older.sql", $"{Log}\nGO\n{Insert("older")}");
        await database.DeployAsync(scripts);
        // As an older dacdeploy recorded them: named after their paths.
        await database.RenameRecordAsync("dacdeploy:data:Settings.sql", "DataScripts:Data/Settings.sql");
        await database.RenameRecordAsync("dacdeploy:migration:20990101-older.sql#post", "DataScripts:Migrations/20990101-older.sql#post");

        await database.DeployAsync(scripts);

        (await RunsAsync("older")).Should().ContainSingle();
        (await database.RecordsAsync()).Should().Equal("dacdeploy:data:Settings.sql", "dacdeploy:migration:20990101-older.sql#post");
    }

    private const string IndexedColumn = """
        SELECT c.name FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID('dbo.Ticket') AND i.name = 'IX_Ticket_Owner'
        """;

    private static Dictionary<string, string> Script(string path, string sql) => new() { [path] = sql };

    private static string Insert(string script, string note = "") =>
        $"INSERT dbo.DataScriptRuns (Script, Note) VALUES (N'{script}', N'{note}');";

    private async Task<List<string>> RunsAsync(string script)
    {
        await using var connection = await database.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            $"IF OBJECT_ID('dbo.DataScriptRuns') IS NOT NULL SELECT Note FROM dbo.DataScriptRuns WHERE Script = N'{script}' ORDER BY Id",
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var notes = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            notes.Add(reader.GetString(0));
        }

        return notes;
    }
}
