namespace DacDeploy.IntegrationTests;

/// <summary>
/// dacdeploy's commands, run in-process on SampleDatabase against an environment ("it") configured as dacdeploy.json
/// configures one: a plan saved with --out applied by path, logins, and the refusals.
/// </summary>
public sealed class ToolTests(SampleDatabaseFixture fixture) : IDisposable
{
    private readonly TestDatabase database = fixture.NewDatabase();
    private readonly DirectoryInfo plans = Directory.CreateTempSubdirectory("ToolTests");
    // The test project builds the dacpac it deploys.
    private readonly ToolRunner tool = new(SampleDatabaseFixture.Project, SampleDatabaseFixture.DacpacPath);

    private string PlanFile => Path.Combine(plans.FullName, "release.plan.sql");

    [Fact]
    public async Task ApplyingASavedPlan_AppliesIt_ThenNothingIsLeft()
    {
        await database.CreateAsync();

        var plan = Run("plan", "--out", PlanFile);
        plan.ExitCode.Should().Be(0, plan.Errors);
        plan.Output.Should().Contain("Schema changes").And.Contain("Reference data to update").And.Contain("> Settings.sql");
        File.Exists(PlanFile).Should().BeTrue();

        var apply = Run("apply", "--plan", PlanFile);
        apply.ExitCode.Should().Be(0, apply.Errors);

        var replan = Run("plan", "--exit-code");
        replan.ExitCode.Should().Be(0, replan.Errors);
        replan.Output.Should().Contain("Nothing to apply").And.NotContain("Schema changes");
    }

    [Fact]
    public async Task Plan_WithExitCode_Exits2WhenThereAreChanges()
    {
        await database.CreateAsync();

        Run("plan", "--exit-code").ExitCode.Should().Be(2);
    }

    [Fact]
    public void Apply_WithoutAPlanFileOrATerminal_RefusesToAsk()
    {
        var apply = Run("apply");

        apply.ExitCode.Should().Be(1);
        apply.Errors.Should().Contain("Not asking without a terminal");
    }

    [Fact]
    public void Apply_RefusesAMissingPlanFile()
    {
        var apply = Run("apply", "--plan", PlanFile);

        apply.ExitCode.Should().Be(1);
        apply.Errors.Should().Contain("No plan at");
    }

    [Fact]
    public async Task Apply_RefusesAPlanMadeForAnotherDatabase()
    {
        await database.CreateAsync();
        Run("plan", "--out", PlanFile).ExitCode.Should().Be(0);

        var apply = Run(new Dictionary<string, string?> { ["Environments:it:Database"] = database.Name + "_other" }, "apply", "--plan", PlanFile);

        apply.ExitCode.Should().Be(1);
        apply.Errors.Should().Contain("was planned for");
    }

    [Fact]
    public async Task Extract_OfADatabaseWithNoSchema_FailsInsteadOfReportingNoDifferences()
    {
        await database.CreateAsync();

        var extract = Run("extract");

        extract.ExitCode.Should().Be(1);
        extract.Errors.Should().Contain("nothing to compare").And.NotContain("No differences");
    }

    [Fact]
    public async Task Plan_UsesTheStoredLogin_WhenTheEnvironmentConfiguresNone()
    {
        await database.CreateAsync();
        var admin = fixture.Admin;
        tool.Logins.Save(admin.DataSource, readOnly: false, new Login(admin.UserID, admin.Password));

        var plan = Run(new Dictionary<string, string?> { ["Environments:it:User"] = null, ["Environments:it:Password"] = null }, "plan");

        plan.ExitCode.Should().Be(0, plan.Errors);
    }

    [Fact]
    public async Task Plan_PrefersTheReadOnlyLogin()
    {
        await database.CreateAsync();

        var plan = Run(new Dictionary<string, string?> { ["Environments:it:ReadOnlyUser"] = "nobody", ["Environments:it:ReadOnlyPassword"] = "wrong" }, "plan");

        plan.ExitCode.Should().Be(1);
        plan.Errors.Should().Contain("Login failed for user 'nobody'");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Login_SavesALoginThatConnects(bool readOnly)
    {
        var admin = fixture.Admin;

        var login = Run(ToolRunner.Answering(admin.UserID, admin.Password), readOnly ? ["login", "--read-only"] : ["login"]);

        login.ExitCode.Should().Be(0, login.Errors);
        login.Output.Should().Contain("Saved");
        tool.Logins.Saved.Should().Equal(new Dictionary<(string, bool), Login> { [(admin.DataSource, readOnly)] = new(admin.UserID, admin.Password) });
    }

    [Fact]
    public void Login_ThatCantConnect_SavesNothing()
    {
        var login = Run(ToolRunner.Answering("sa", "wrong"), "login");

        login.ExitCode.Should().Be(1);
        login.Errors.Should().Contain("Couldn't log in to").And.Contain("Nothing saved.");
        tool.Logins.Saved.Should().BeEmpty();
    }

    private const string SettingsRecord = "DataScripts:Data/Settings.sql";

    [Fact]
    public async Task WhatIsNotInTheProject_IsLeftInPlaceAndListed_UntilAPrunePlanDropsIt()
    {
        await database.DeployAsync();
        // As if they had been removed from the project: a column inserts would fail without, and a view.
        await database.ExecuteAsync("ALTER TABLE dbo.Item ADD Removed int NOT NULL");
        await database.ExecuteAsync("CREATE VIEW dbo.RemovedView AS SELECT 1 AS One");

        var apply = Run("apply", "--yes");
        apply.ExitCode.Should().Be(0, apply.Errors);
        apply.Output.Should().Contain("In the database but not in the project, kept (drop with --prune)")
            .And.Contain("[dbo].[RemovedView]").And.Contain("[dbo].[Item].[Removed] (made nullable)");
        (await database.ScalarAsync<bool>("SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Item') AND name = 'Removed'")).Should().BeTrue();

        var replan = Run("plan", "--exit-code");
        replan.ExitCode.Should().Be(0, replan.Errors);
        replan.Output.Should().Contain("[dbo].[Item].[Removed]").And.Contain("[dbo].[RemovedView]").And.NotContain("made nullable");

        var prune = Run("plan", "--prune", "--out", PlanFile);
        prune.ExitCode.Should().Be(0, prune.Errors);
        prune.Output.Should().Contain("In the database but not in the project, dropped by this plan");
        var applyPrune = Run("apply", "--plan", PlanFile);
        applyPrune.ExitCode.Should().Be(0, applyPrune.Errors);

        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Item') AND name = 'Removed'")).Should().Be(0);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.views WHERE name = 'RemovedView'")).Should().Be(0);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name LIKE N'DataScripts:%'"))
            .Should().BePositive("pruning leaves the data scripts' records alone");
    }

    [Fact]
    public async Task Prune_RefusesWhilePendingChangesRemain()
    {
        await database.DeployAsync();
        await database.ExecuteAsync("CREATE VIEW dbo.RemovedView AS SELECT 1 AS One");
        // A pending change: the project has this index.
        await database.ExecuteAsync("DROP INDEX IX_Ticket_Owner ON dbo.Ticket");

        var prune = Run("plan", "--prune");

        prune.ExitCode.Should().NotBe(0);
        prune.Errors.Should().Contain("changes are pending");
        prune.Output.Should().NotContain("Plan for");
    }

    [Fact]
    public async Task Prune_RefusesAPlanThatWouldRunDataScripts()
    {
        await database.DeployAsync();
        await database.ExecuteAsync("CREATE VIEW dbo.RemovedView AS SELECT 1 AS One");
        await database.ExecuteAsync($"EXEC sys.sp_dropextendedproperty @name = N'{SettingsRecord}'");

        var prune = Run("plan", "--prune");

        prune.ExitCode.Should().NotBe(0);
        prune.Errors.Should().Contain("runs no data scripts").And.Contain("Data/Settings.sql");
    }

    [Fact]
    public async Task Prune_RefusesAPendingChangeToATableWithSomethingToDrop()
    {
        await database.DeployAsync();
        // Removed from the project: a column. Pending: the project's Category is nvarchar(50), narrowed here.
        await database.ExecuteAsync("ALTER TABLE dbo.Item ADD Removed int NULL; ALTER TABLE dbo.Item ALTER COLUMN Category nvarchar(40) NULL");

        var prune = Run("plan", "--prune");

        prune.ExitCode.Should().NotBe(0, prune.Output);
        prune.Errors.Should().Contain("changes are pending");
    }

    [Fact]
    public async Task ARebuildThatWouldLoseAKeptIndex_SaysToPutItBackInTheProject()
    {
        await database.DeployAsync();
        // Moving Category to the end makes DacFx rebuild the table, which would lose the index the project doesn't have.
        await database.ExecuteAsync(
            "ALTER TABLE dbo.Item DROP COLUMN Category; ALTER TABLE dbo.Item ADD Category nvarchar(50) NULL; " +
            "CREATE INDEX IX_Item_Removed ON dbo.Item (Name);");

        var plan = Run("plan");

        plan.ExitCode.Should().NotBe(0, plan.Output);
        plan.Errors.Should().Contain("IX_Item_Removed").And.Contain("back in the project").And.NotContain("--prune");
    }

    [Fact]
    public async Task AKeptColumnInAPrimaryKeyTheProjectDoesNotHave_IsRefusedByName()
    {
        await database.DeployAsync();
        // The project gives Heap no primary key, so this one is kept, and so is the column it covers.
        await database.ExecuteAsync("ALTER TABLE dbo.Heap ADD Removed int NOT NULL; ALTER TABLE dbo.Heap ADD CONSTRAINT PK_Kept PRIMARY KEY (Removed);");

        var plan = Run("plan");

        plan.ExitCode.Should().NotBe(0, plan.Output);
        plan.Errors.Should().Contain("[dbo].[PK_Kept]").And.Contain("back in the project");
    }

    [Fact]
    public async Task ATableDacFxRebuilds_KeepsTheColumnsNotInTheProject()
    {
        await database.DeployAsync();
        // Moving Category to the end makes DacFx rebuild the table to put it back.
        await database.ExecuteAsync("ALTER TABLE dbo.Item DROP COLUMN Category; ALTER TABLE dbo.Item ADD Category nvarchar(50) NULL, Removed int NOT NULL");
        await database.ExecuteAsync("INSERT dbo.Item (Name, Removed) VALUES (N'kept', 7)");

        var apply = Run("apply", "--yes");

        apply.ExitCode.Should().Be(0, apply.Errors);
        (await database.ScalarAsync<int>("SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Item') AND name = 'Category'")).Should().Be(2);
        (await database.ScalarAsync<int>("SELECT Removed FROM dbo.Item WHERE Name = N'kept'")).Should().Be(7);
        (await database.ScalarAsync<bool>("SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Item') AND name = 'Removed'")).Should().BeTrue();
    }

    [Fact]
    public async Task Plan_WithVerbose_ShowsWhatTheToolsPrint()
    {
        await database.CreateAsync();

        var plan = Run("plan", "--verbose");

        plan.ExitCode.Should().Be(0, plan.Errors);
        plan.Errors.Should().Contain("Analyzing deployment plan");
    }

    [Fact]
    public async Task Apply_RefusesInsidePublish_BeforeRunningAPlanOtherThanTheReviewedOne()
    {
        await database.CreateAsync();
        Run("plan", "--out", PlanFile).ExitCode.Should().Be(0);
        // The database changes after the review: the reference data now looks run, so Publish's plan leaves it out.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(
            Path.Combine(SampleDatabaseFixture.Directory, "Data", "Settings.sql"), TestContext.Current.CancellationToken)));
        await database.ExecuteAsync($"EXEC sys.sp_addextendedproperty @name = N'{SettingsRecord}', @value = N'{hash}'");

        var apply = Run("apply", "--plan", PlanFile);

        apply.ExitCode.Should().NotBe(0);
        apply.Errors.Should().Contain("DeploymentContributor 'DacDeploy.PlanCheck'").And.Contain("out of date");
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0")).Should().Be(0);
    }

    [Fact]
    public async Task Apply_RefusesASavedPlanWithNoChanges_OnceTheDatabaseHasDrifted()
    {
        await database.DeployAsync();
        var plan = Run("plan", "--out", PlanFile);
        plan.ExitCode.Should().Be(0, plan.Errors);
        plan.Output.Should().Contain("Nothing to apply");
        var unchanged = Run("apply", "--plan", PlanFile);
        unchanged.ExitCode.Should().Be(0, unchanged.Errors);
        await database.ExecuteAsync($"EXEC sys.sp_dropextendedproperty @name = N'{SettingsRecord}'");

        var apply = Run("apply", "--plan", PlanFile);

        apply.ExitCode.Should().NotBe(0);
        apply.Errors.Should().Contain("out of date");
        (await database.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = N'{SettingsRecord}'")).Should().Be(0);
    }

    public void Dispose() => plans.Delete(recursive: true);

    private ToolResult Run(params string[] arguments) => Run([], ToolTerminal.None, arguments);

    private ToolResult Run(Dictionary<string, string?> overrides, params string[] arguments) => Run(overrides, ToolTerminal.None, arguments);

    private ToolResult Run(ToolTerminal terminal, params string[] arguments) => Run([], terminal, arguments);

    /// <summary>Runs <c>dacdeploy &lt;command&gt; -E it ...</c>, "it" being this test's database on the shared server.</summary>
    private ToolResult Run(Dictionary<string, string?> overrides, ToolTerminal terminal, string[] arguments)
    {
        var admin = fixture.Admin;
        var settings = new Dictionary<string, string?>
        {
            ["Environments:it:Server"] = admin.DataSource,
            ["Environments:it:Database"] = database.Name,
            ["Environments:it:Variables:OtherDb"] = "Other",
            ["Environments:it:User"] = admin.UserID,
            ["Environments:it:Password"] = admin.Password,
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return tool.Run(settings, terminal, [arguments[0], "-E", "it", .. arguments.Skip(1)]);
    }
}
