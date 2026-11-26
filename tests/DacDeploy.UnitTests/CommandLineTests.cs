namespace DacDeploy.UnitTests;

/// <summary>
/// What dacdeploy refuses before it connects to anything: its arguments, the environment and the login. A server that
/// refuses connections at once (port 1) stands in for one it can't reach.
/// </summary>
public sealed class CommandLineTests
{
    private const string Unreachable = "127.0.0.1,1";

    private static readonly Dictionary<string, string?> TwoEnvironments = new()
    {
        ["Environments:prod:Server"] = Unreachable,
        ["Environments:prod:Database"] = "Shop",
        ["Environments:prod:Variables:OtherDb"] = "Other",
        ["Environments:test:Server"] = Unreachable,
        ["Environments:test:Database"] = "ShopTest",
    };

    private readonly ToolRunner tool = new(
        new DatabaseProject("/nowhere/Shop.sqlproj", "/nowhere/Shop.publish.xml", UserSecretsId: null), "/nowhere/bin/Release/Shop.dacpac");

    [Fact]
    public void Help_ListsTheCommandsAndTheEnvironments()
    {
        var help = Run("--help");

        help.ExitCode.Should().Be(0);
        help.Output.Should().Contain("Usage")
            .And.Contain("plan").And.Contain("apply").And.Contain("extract").And.Contain("login");
        Run("plan", "--help").Output.Should().Contain("prod, test");
    }

    [Fact]
    public void ACommandsHelp_ListsItsOptions() =>
        Run("plan", "--help").Output.Should().Contain("--out").And.Contain("--exit-code").And.Contain("--prune").And.Contain("--environment");

    [Fact]
    public void Version_IsShown()
    {
        var version = Run("--version");

        version.ExitCode.Should().Be(0);
        version.Output.Should().MatchRegex(@"\d+\.\d+");
    }

    [Fact]
    public void NoCommand_Fails()
    {
        var none = Run();

        none.ExitCode.Should().Be(1);
        (none.Output + none.Errors).Should().Contain("plan");
        tool.Builds.Should().Be(0);
    }

    [Fact]
    public void AnUnknownCommand_IsRefused()
    {
        var deploy = Run("deploy", "-E", "prod");

        deploy.ExitCode.Should().Be(1);
        deploy.Errors.Should().Contain("'deploy'");
    }

    [Theory]
    [InlineData("plan", "--yes")]
    [InlineData("plan", "--bogus")]
    [InlineData("apply", "--exit-code")]
    [InlineData("extract", "--prune")]
    [InlineData("login", "--verbose")]
    public void AnOptionTheCommandDoesntTake_IsRefused(string command, string option)
    {
        var refused = Run(command, "-E", "prod", option);

        refused.ExitCode.Should().Be(1);
        refused.Errors.Should().Contain(option);
        tool.Builds.Should().Be(0);
    }

    [Theory]
    [InlineData("-E")]
    [InlineData("--environment")]
    [InlineData("--out")]
    public void AnOptionWithoutItsValue_IsRefused(string option)
    {
        var refused = Run("plan", option);

        refused.ExitCode.Should().Be(1);
        refused.Errors.Should().Contain(option);
    }

    [Fact]
    public void ASecondPlanFile_IsRefused()
    {
        var apply = Run("apply", "-E", "prod", "one.plan.sql", "two.plan.sql");

        apply.ExitCode.Should().Be(1);
        apply.Errors.Should().Contain("'two.plan.sql'");
    }

    [Fact]
    public void WithoutAnEnvironment_NamesTheEnvironments() =>
        Run("plan").Errors.Should().Contain("Pick an environment with -E <name>: prod, test.");

    [Fact]
    public void AnUnknownEnvironment_IsRefused()
    {
        var plan = Run("plan", "-E", "staging");

        plan.ExitCode.Should().Be(1);
        plan.Errors.Should().Contain("No environment named 'staging'").And.Contain("prod, test");
    }

    [Fact]
    public void AnEnvironmentWithoutAServer_IsRefused()
    {
        var settings = new Dictionary<string, string?>(TwoEnvironments) { ["Environments:mine:Database"] = "Shop" };

        Run(settings, "plan", "-E", "mine").Errors.Should().Contain("Environment 'mine' has no Server");
    }

    [Fact]
    public void Plan_WithoutALogin_SaysHowToSaveOne()
    {
        var plan = Run("plan", "-E", "prod");

        plan.ExitCode.Should().Be(1);
        plan.Errors.Should().Contain("No login for prod: run `dacdeploy login -E prod --read-only`");
        tool.Builds.Should().Be(0);
    }

    [Fact]
    public void Hints_NameTheCommandTheWayItWasRun()
    {
        var localTool = new ToolRunner(
            new DatabaseProject("/nowhere/Shop.sqlproj", "/nowhere/Shop.publish.xml", UserSecretsId: null), "/nowhere/Shop.dacpac", "dotnet dacdeploy");

        localTool.Run(TwoEnvironments, "plan", "-E", "prod").Errors.Should().Contain("run `dotnet dacdeploy login -E prod --read-only`");
        localTool.Run(TwoEnvironments, "login", "-E", "prod").Errors.Should().Contain("dotnet dacdeploy login asks for the user name and password");
        localTool.Run(TwoEnvironments).Errors.Should().Contain("See dotnet dacdeploy --help.");
    }

    [Theory]
    [InlineData("/usr/share/dotnet/dotnet", "dotnet dacdeploy")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", "dotnet dacdeploy")]
    [InlineData("/home/me/.dotnet/tools/dacdeploy", "dacdeploy")]
    [InlineData(@"C:\Users\me\.dotnet\tools\dacdeploy.exe", "dacdeploy")]
    [InlineData(null, "dacdeploy")]
    public void TheCommand_IsDotnetDacdeploy_WhenDotnetRunsIt(string? processPath, string command) =>
        Program.CommandName(processPath).Should().Be(command);

    [Fact]
    public void Apply_WithOnlyTheReadOnlyLogin_SaysHowToSaveTheOneThatCanWrite()
    {
        tool.Logins.Save(Unreachable, readOnly: true, new Login("reader", "secret"));

        Run("apply", "-E", "prod").Errors.Should().Contain("No login for prod: run `dacdeploy login -E prod`.");
    }

    [Fact]
    public void Plan_WithAStoredLogin_StopsAtTheServerBeforeBuilding()
    {
        tool.Logins.Save(Unreachable, readOnly: false, new Login("writer", "secret"));

        var plan = Run("plan", "-E", "prod");

        plan.ExitCode.Should().Be(1);
        plan.Errors.Should().Contain($"Can't reach {Unreachable}");
        tool.Builds.Should().Be(0);
    }

    [Fact]
    public void StoredLogins_AreTheServers_SoEveryEnvironmentOnItFindsThem()
    {
        tool.Logins.Save(Unreachable, readOnly: false, new Login("writer", "secret"));

        Run("plan", "-E", "test").Errors.Should().Contain($"Can't reach {Unreachable}");
    }

    [Fact]
    public void Plan_WithAConfiguredLogin_DoesntNeedTheStore()
    {
        var settings = new Dictionary<string, string?>(TwoEnvironments)
        {
            ["Environments:prod:User"] = "writer",
            ["Environments:prod:Password"] = "secret",
        };

        Run(settings, "plan", "-E", "prod").Errors.Should().Contain($"Can't reach {Unreachable}");
        tool.Logins.Lookups.Should().Be(0);
    }

    [Fact]
    public void Login_WithoutATerminal_IsRefused()
    {
        var login = Run("login", "-E", "prod");

        login.ExitCode.Should().Be(1);
        login.Errors.Should().Contain("dacdeploy login asks for the user name and password: run it in a terminal.");
        tool.Logins.Saved.Should().BeEmpty();
    }

    [Fact]
    public void Login_ToAnUnreachableServer_StopsBeforeAsking()
    {
        var input = new Queue<string?>(["writer", "secret"]);

        var login = tool.Run(TwoEnvironments, new ToolTerminal(InputIsTerminal: true, Colour: false, input.Dequeue, input.Dequeue), "login", "-E", "prod");

        login.Errors.Should().Contain($"Can't reach {Unreachable}");
        input.Should().HaveCount(2);
        tool.Logins.Saved.Should().BeEmpty();
    }

    private ToolResult Run(params string[] arguments) => tool.Run(TwoEnvironments, arguments);

    private ToolResult Run(Dictionary<string, string?> settings, params string[] arguments) => tool.Run(settings, arguments);
}
