using Microsoft.SqlServer.Dac;

namespace DacDeploy.IntegrationTests;

/// <summary>
/// Planning in the same process as the contributors: they hand their results back as objects, through the
/// <see cref="DeploymentReport"/> the plan passes them.
/// </summary>
public sealed class InProcessDeploymentTests(SampleDatabaseFixture fixture)
{
    [Fact]
    public async Task PlanningInProcess_HandsTheFingerprintBackThroughTheReport()
    {
        var database = await CreateEmptyDatabaseAsync();
        using var report = new DeploymentReport();

        Plan(database, report);

        report.Fingerprint.Should().MatchRegex("^[0-9A-F]{64}$");
    }

    [Fact]
    public async Task PlanningAnEmptyDatabase_ReportsTheDataScriptsItWouldRun()
    {
        var database = await CreateEmptyDatabaseAsync();
        using var report = new DeploymentReport();

        Plan(database, report);

        report.DataScripts.Should().ContainEquivalentOf(new DataScriptPart(DataScriptSlot.Data, "Data/Settings.sql"));
    }

    [Fact]
    public async Task PlanningADatabaseWithAViewNotInTheProject_ReportsItAsKept()
    {
        var database = fixture.NewDatabase();
        await database.DeployAsync();
        await database.ExecuteAsync("CREATE VIEW dbo.RemovedView AS SELECT 1 AS One");
        using var report = new DeploymentReport();

        Plan(database, report);

        report.NotInProject.Should().ContainEquivalentOf(new NotInProjectItem("View", "[dbo].[RemovedView]", Dropped: false, MadeNullable: false));
        report.DataScripts.Should().BeEmpty("the deployed copy has run every data script");
    }

    private async Task<TestDatabase> CreateEmptyDatabaseAsync()
    {
        var database = fixture.NewDatabase();
        await database.CreateAsync();
        return database;
    }

    private void Plan(TestDatabase database, DeploymentReport report)
    {
        var options = DacProfile.Load(SampleDatabaseFixture.ProfilePath).DeployOptions;
        options.AdditionalDeploymentContributors = Deployer.Contributors;
        options.AdditionalDeploymentContributorPaths = Path.GetDirectoryName(typeof(PlanCheck).Assembly.Location);
        options.AdditionalDeploymentContributorArguments =
            $"{DataScripts.RootArgument}={SampleDatabaseFixture.Directory};{report.Argument}";
        using var package = DacPackage.Load(SampleDatabaseFixture.DacpacPath);

        new DacServices(fixture.GetDeploymentTestConnectionString("master")).GenerateDeployScript(package, database.Name, options);
    }
}
