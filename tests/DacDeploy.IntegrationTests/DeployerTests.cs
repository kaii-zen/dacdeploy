namespace DacDeploy.IntegrationTests;

/// <summary>
/// Planning and applying SampleDatabase in-process, as dacdeploy plan and dacdeploy apply do.
/// </summary>
public sealed class DeployerTests(SampleDatabaseFixture fixture)
{
    private static readonly Deployer Deployer = SampleDatabaseFixture.Deployer();

    [Fact]
    public async Task Plan_ForAnEmptyDatabase_ListsTheSchemaChangesAndTheReferenceData()
    {
        var database = await EmptyDatabaseAsync();

        var plan = Deployer.Plan(database.Target);

        plan.Changed.Should().BeTrue();
        plan.SchemaChanges.Should().Contain(change => change.StartsWith("Create") && change.EndsWith("[dbo].[Item]"));
        plan.ReferenceData.Should().Contain("Settings.sql");
        plan.Fingerprint.Should().MatchRegex("^[0-9A-F]{64}$");
        plan.Sql.Should().Contain("CREATE TABLE [dbo].[Item]");
        plan.Target.Should().Be(database.Target.Identity);
    }

    [Fact]
    public async Task Plan_ForADeployedDatabaseWithAViewNotInTheProject_ListsItAsKept_AndNothingToApply()
    {
        var database = await DeployedDatabaseAsync();
        await database.ExecuteAsync("CREATE VIEW dbo.RemovedView AS SELECT 1 AS One");

        var plan = Deployer.Plan(database.Target);

        plan.Changed.Should().BeFalse();
        plan.NotInProject.Should().Equal("View  [dbo].[RemovedView]");
    }

    [Fact]
    public async Task Apply_DeploysThePlan_ThenNothingIsLeftToApply()
    {
        var database = await EmptyDatabaseAsync();
        var plan = Deployer.Plan(database.Target);

        Deployer.Apply(database.Target, plan);

        Deployer.Plan(database.Target).Changed.Should().BeFalse();
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = N'DataScripts:Data/Settings.sql'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Apply_RefusesAPlanTheDatabaseHasMovedOnFrom()
    {
        var database = await EmptyDatabaseAsync();
        var plan = Deployer.Plan(database.Target);
        // The reference data now looks run, so Publish's plan leaves it out: not the plan that was reviewed.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(
            Path.Combine(SampleDatabaseFixture.Directory, "Data", "Settings.sql"), TestContext.Current.CancellationToken)));
        await database.ExecuteAsync($"EXEC sys.sp_addextendedproperty @name = N'DataScripts:Data/Settings.sql', @value = N'{hash}'");

        var apply = () => Deployer.Apply(database.Target, plan);

        apply.Should().Throw<DeploymentException>().WithMessage("*out of date*");
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0")).Should().Be(0);
    }

    [Fact]
    public async Task Plan_WithPrune_RefusesWhilePendingChangesRemain()
    {
        var database = await DeployedDatabaseAsync();
        await database.ExecuteAsync("CREATE VIEW dbo.RemovedView AS SELECT 1 AS One; ");
        await database.ExecuteAsync("DROP INDEX IX_Ticket_Owner ON dbo.Ticket");

        var plan = () => Deployer.Plan(database.Target, prune: true);

        plan.Should().Throw<DeploymentException>().WithMessage("*changes are pending*");
    }

    private async Task<TestDatabase> EmptyDatabaseAsync()
    {
        var database = fixture.NewDatabase();
        await database.CreateAsync();
        return database;
    }

    private async Task<TestDatabase> DeployedDatabaseAsync()
    {
        var database = fixture.NewDatabase();
        await database.DeployAsync();
        return database;
    }
}
