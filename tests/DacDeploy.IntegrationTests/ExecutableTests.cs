using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;

namespace DacDeploy.IntegrationTests;

/// <summary>
/// Runs the built dacdeploy as its own process, the way people run it, in a copy of SampleDatabase's folder: that it
/// finds dacdeploy.json from a folder below it, reads environments from it and from DACDEPLOY_ variables, builds the
/// project and finds its dacpac, resolves plan files against the folder it runs in, and sees no terminal to ask on.
/// What the commands do is covered in-process by <see cref="ToolTests"/>.
/// </summary>
public sealed class ExecutableTests(SampleDatabaseFixture fixture) : IDisposable
{
    private readonly TestDatabase database = fixture.NewDatabase();
    private readonly DirectoryInfo project = CopySampleProject();

    /// <summary>A folder below the project's, where dacdeploy runs.</summary>
    private string WorkingDirectory => Directory.CreateDirectory(Path.Combine(project.FullName, "plans")).FullName;

    /// <summary>The repository: the folder holding DacDeploy.slnx, above the test binaries.</summary>
    private static readonly string Repository = FindRepository();

    /// <summary>
    /// dacdeploy as built in its own project, with its own dependencies: in this test run's configuration, for the
    /// framework the tests use it for (the tool's, not the tests').
    /// </summary>
    private static string Executable
    {
        get
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
            var framework = new FrameworkName(typeof(Tool).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
            return Path.Combine(Repository, "src", "DacDeploy", "bin", configuration, $"net{framework.Version.ToString(2)}", "dacdeploy.dll");
        }
    }

    [Fact]
    public async Task ApplyingASavedPlan_AppliesTheSchemaAndDataScripts_ThenNothingIsLeft()
    {
        await database.CreateAsync();

        var plan = await RunAsync("plan", "--out", "release.plan.sql");
        plan.ExitCode.Should().Be(0, plan.Errors);
        plan.Output.Should().Contain("Schema changes").And.Contain("> Settings.sql");
        plan.Errors.Should().NotContain("Analyzing deployment plan");
        File.Exists(Path.Combine(WorkingDirectory, "release.plan.sql")).Should().BeTrue();

        var apply = await RunAsync("apply", "release.plan.sql");
        apply.ExitCode.Should().Be(0, apply.Errors);
        (await database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Setting")).Should().Be(1);

        var replan = await RunAsync("plan", "--exit-code");
        replan.ExitCode.Should().Be(0, replan.Errors);
        replan.Output.Should().Contain("Nothing to apply");
    }

    [Fact]
    public async Task Apply_WithoutAPlanFileOrATerminal_RefusesToAsk()
    {
        await database.CreateAsync();

        var apply = await RunAsync("apply");

        apply.ExitCode.Should().NotBe(0);
        apply.Errors.Should().Contain("Not asking without a terminal");
    }

    public void Dispose() => project.Delete(recursive: true);

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DacDeploy.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("DacDeploy.slnx not found above the test binaries.");
    }

    /// <summary>
    /// SampleDatabase's sources in a folder of their own, with the repository's global.json, so the project builds with
    /// the same SDK, and a dacdeploy.json naming one environment, "it".
    /// </summary>
    private static DirectoryInfo CopySampleProject()
    {
        var source = Path.Combine(Repository, "tests", "DacDeploy.IntegrationTests", "SampleDatabase");
        var copy = Directory.CreateTempSubdirectory("ExecutableTests");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar)[0] is "bin" or "obj")
            {
                continue;
            }

            var target = Path.Combine(copy.FullName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        File.Copy(Path.Combine(Repository, "global.json"), Path.Combine(copy.FullName, "global.json"));

        File.WriteAllText(Path.Combine(copy.FullName, DatabaseProject.SettingsFile), /*lang=json*/ """
            {
              "Environments": {
                "it": { "Variables": { "OtherDb": "Other" } }
              }
            }
            """);
        return copy;
    }

    /// <summary>Runs <c>dacdeploy &lt;command&gt; -E it ...</c> in <see cref="WorkingDirectory"/>.</summary>
    private async Task<ToolResult> RunAsync(params string[] arguments)
    {
        var connection = fixture.Admin;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = WorkingDirectory,
            // Closed right away, so dacdeploy sees no terminal to ask on.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in new[] { Executable, arguments[0], "-E", "it" }.Concat(arguments.Skip(1)))
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var inherited in startInfo.Environment.Keys.Where(name => name.StartsWith("DACDEPLOY_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(inherited);
        }

        // The rest of "it", which dacdeploy.json leaves out, through environment variables.
        startInfo.Environment["DACDEPLOY_Environments__it__Server"] = connection.DataSource;
        startInfo.Environment["DACDEPLOY_Environments__it__Database"] = database.Name;
        startInfo.Environment["DACDEPLOY_Environments__it__User"] = connection.UserID;
        startInfo.Environment["DACDEPLOY_Environments__it__Password"] = connection.Password;

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        // A command takes seconds; one still running after minutes is stuck (MSBuild waits 15 for a task host that won't start).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return new ToolResult(process.ExitCode, await stdout, await stderr);
    }
}
