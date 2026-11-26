using System.Text.RegularExpressions;

namespace DacDeploy.UnitTests;

/// <summary>Plan files as dacdeploy plan saves them and dacdeploy apply reads them, such as those in Plans/.</summary>
public class PlanFileTests
{
    private static readonly PlanTarget Target = new("dev", "localhost,1433", "Shop", new Dictionary<string, string> { ["OtherDb"] = "Other" });

    private static string Saved(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plans", $"{name}.plan.sql"));

    [Fact]
    public void Read_ASavedPlan_GivesItsListsInOrder()
    {
        var plan = PlanFile.Read(Saved("full"), "full.plan.sql", Target);

        plan.Target.Should().Be(Target);
        plan.Prune.Should().BeFalse();
        plan.MigrationsBefore.Should().Equal("20261120-pre-and-post.sql");
        plan.SchemaChanges.Should().Equal("Create  [dbo].[NewView]", "Alter   [dbo].[Item]");
        plan.ReferenceData.Should().Equal("Settings.sql");
        plan.MigrationsAfter.Should().Equal("20261120-pre-and-post.sql", "20261121-backfill.sql");
        plan.NotInProject.Should().Equal("View    [dbo].[OldView]", "Column  [dbo].[Item].[Removed] (made nullable)");
        plan.Fingerprint.Should().Be("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");
        plan.Sql.Should().StartWith("PRINT N'Altering [dbo].[Item]...';");
        plan.Changed.Should().BeTrue();
    }

    [Fact]
    public void Read_APrunePlan_KnowsItDropsWhatIsNotInTheProject()
    {
        var plan = PlanFile.Read(Saved("prune"), "prune.plan.sql", Target);

        plan.Prune.Should().BeTrue();
        plan.NotInProject.Should().Equal("View    [dbo].[OldView]", "Column  [dbo].[Item].[Removed]");
        plan.Changed.Should().BeFalse();
    }

    [Theory]
    [InlineData("full")]
    [InlineData("prune")]
    [InlineData("nothing-to-apply")]
    public void Write_GivesTheSavedText(string name)
    {
        var saved = Saved(name);

        PlanFile.Write(PlanFile.Read(saved, $"{name}.plan.sql", Target)).Should().Be(saved.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Write_APlanWhoseSqlHasWindowsLineEndings_ReadsBackUnedited()
    {
        var plan = new Plan(Target) { ReferenceData = ["Settings.sql"], Sql = "PRINT N'x';\r\n\r\nGO\r\n", Fingerprint = "AB" };

        var read = PlanFile.Read(PlanFile.Write(plan), "release.plan.sql", Target);

        read.Sql.Should().Be("PRINT N'x';\n\nGO\n");
    }

    [Fact]
    public void Read_APlanSavedWithWindowsLineEndings_StillChecksOut()
    {
        var plan = PlanFile.Read(Saved("full").ReplaceLineEndings("\r\n"), "full.plan.sql", Target);

        plan.Fingerprint.Should().Be("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");
    }

    [Theory]
    [InlineData("(?m)^-- Fingerprint: .*\n", "")]
    [InlineData(@"\z", "\n-- edited\n")]
    [InlineData("(?m)^--   Settings.sql\n", "")]
    public void Read_RefusesAPlanEditedAfterItWasSaved(string pattern, string replacement)
    {
        var edited = Regex.Replace(Saved("full").ReplaceLineEndings("\n"), pattern, replacement);

        var read = () => PlanFile.Read(edited, "release.plan.sql", Target);

        read.Should().Throw<PlanFileException>().WithMessage("release.plan.sql was edited after it was saved. Plan again and review it.");
    }

    [Fact]
    public void Read_RefusesAPlanWithoutAFingerprint()
    {
        var read = () => PlanFile.Read(Saved("no-fingerprint"), "release.plan.sql", Target);

        read.Should().Throw<PlanFileException>()
            .WithMessage("release.plan.sql has no fingerprint to check the deployment against. Plan again and review it.");
    }

    [Fact]
    public void Read_RefusesAPlanMadeForAnotherTarget()
    {
        var read = () => PlanFile.Read(Saved("full"), "release.plan.sql", Target with { Database = "Shop_other" });

        read.Should().Throw<PlanFileException>().WithMessage(
            "release.plan.sql was planned for Environment dev, Server localhost,1433, Database Shop, $(OtherDb) Other, " +
            "not Environment dev, Server localhost,1433, Database Shop_other, $(OtherDb) Other. Plan again for this target.");
    }

    [Fact]
    public void Read_RefusesAPlanMadeWithOtherVariables()
    {
        var read = () => PlanFile.Read(Saved("full"), "release.plan.sql", Target with { Variables = new Dictionary<string, string> { ["OtherDb"] = "OtherTest" } });

        read.Should().Throw<PlanFileException>().WithMessage("*was planned for*$(OtherDb) Other, not*$(OtherDb) OtherTest.*");
    }

    [Fact]
    public void ATarget_EqualsAnotherWithTheSameValues() =>
        Target.Should().Be(new PlanTarget("dev", "localhost,1433", "Shop", new Dictionary<string, string> { ["OtherDb"] = "Other" }));

    [Fact]
    public void Read_RefusesAFileThatIsNotAPlan()
    {
        var read = () => PlanFile.Read("SELECT 1;\n", "notes.sql", Target);

        read.Should().Throw<PlanFileException>().WithMessage("notes.sql isn't a plan saved by dacdeploy plan.");
    }
}
