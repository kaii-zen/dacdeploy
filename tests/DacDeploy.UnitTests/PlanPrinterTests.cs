namespace DacDeploy.UnitTests;

/// <summary>How plan and apply show a plan: its lists, each line marked with the kind of change, then a summary.</summary>
public class PlanPrinterTests
{
    private static readonly PlanTarget Target = new("dev", "localhost,1433", "Shop", new Dictionary<string, string> { ["OtherDb"] = "Other" });

    private static string Saved(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plans", file));

    [Theory]
    [InlineData("full")]
    [InlineData("prune")]
    [InlineData("nothing-to-apply")]
    public void Show_PrintsTheListsAndASummary(string name)
    {
        var plan = PlanFile.Read(Saved($"{name}.plan.sql"), $"{name}.plan.sql", Target);

        PlanPrinter.Show(plan, color: false).Should().Be(Saved($"{name}.shown.txt").ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Show_InATerminal_ColoursEachKindOfChange()
    {
        var plan = PlanFile.Read(Saved("full.plan.sql"), "full.plan.sql", Target);

        var shown = PlanPrinter.Show(plan, color: true);

        shown.Should().Contain("  \u001b[32m+\u001b[0m create  [dbo].[NewView]")
            .And.Contain("  \u001b[33m~\u001b[0m alter   [dbo].[Item]")
            .And.Contain("  \u001b[36m>\u001b[0m Settings.sql")
            .And.Contain("    view    [dbo].[OldView]");
    }
}
