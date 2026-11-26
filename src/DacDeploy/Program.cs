using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace DacDeploy.Cli;

/// <summary>dacdeploy: <c>dacdeploy &lt;command&gt; -E &lt;environment&gt; ...</c> (see <see cref="Tool"/>).</summary>
public static class Program
{
    // The configuration the project is built and deployed in.
    private const string Configuration = "Release";

    public static int Main(string[] args)
    {
        DatabaseProject? project = null;
        ToolException? noProject = null;
        try
        {
            project = DatabaseProject.Find(Directory.GetCurrentDirectory());
        }
        catch (ToolException e)
        {
            // Help and --version work anywhere; the commands report this.
            noProject = e;
        }

        var configuration = new ConfigurationBuilder();
        if (project is not null)
        {
            configuration.AddJsonFile(project.SettingsPath!);
            if (project.UserSecretsId is { } userSecretsId)
            {
                configuration.AddUserSecrets(userSecretsId);
            }
        }

        configuration.AddEnvironmentVariables("DACDEPLOY_");
        var terminal = new ToolTerminal(
            InputIsTerminal: !Console.IsInputRedirected,
            Colour: !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null,
            Console.ReadLine,
            ReadSecret);
        var host = new ToolHost(
            configuration.Build(),
            new OsLoginStore(),
            () => project ?? throw noProject!,
            verbose => Build(project!, verbose),
            terminal,
            CommandName(Environment.ProcessPath));
        return new Tool(Console.Out, Console.Error, host).Run(args);
    }

    /// <summary>
    /// How dacdeploy was run, from the path of the process running it: <c>dotnet dacdeploy</c> when that's the dotnet
    /// host (a local tool, or <c>dotnet dacdeploy.dll</c>), <c>dacdeploy</c> otherwise (a global tool, or on the PATH).
    /// </summary>
    public static string CommandName(string? processPath)
    {
        // Either separator, whatever the OS the path comes from.
        var file = processPath?[(processPath.LastIndexOfAny(['/', '\\']) + 1)..];
        return string.Equals(Path.GetFileNameWithoutExtension(file), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? "dotnet dacdeploy"
            : "dacdeploy";
    }

    /// <summary>
    /// Builds the project, so a plan is of the schema as it is now, and gives the path of its dacpac. The build's output
    /// goes to standard error, so standard output is only the plan: all of it when verbose, otherwise only when the
    /// build fails.
    /// </summary>
    private static string Build(DatabaseProject project, bool verbose)
    {
        var (built, buildOutput) = Dotnet(project, ["build", project.ProjectPath, "-c", Configuration, "--nologo", .. verbose ? Array.Empty<string>() : ["-v:q"]]);
        if (verbose || built != 0)
        {
            Console.Error.Write(buildOutput);
        }

        if (built != 0)
        {
            throw new ToolException($"{Path.GetFileName(project.ProjectPath)} didn't build; see above.");
        }

        var (found, targetPath) = Dotnet(project, ["msbuild", project.ProjectPath, "-nologo", $"-p:Configuration={Configuration}", "-getProperty:TargetPath"]);
        return found == 0 && targetPath.Trim() is { Length: > 0 } dacpac
            ? dacpac
            : throw new ToolException($"Couldn't tell where {Path.GetFileName(project.ProjectPath)} builds its dacpac: {targetPath.Trim()}");
    }

    private static (int ExitCode, string Output) Dotnet(DatabaseProject project, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = project.Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr.Result);
    }

    /// <summary>A line typed without showing it.</summary>
    private static string ReadSecret()
    {
        var secret = new StringBuilder();
        for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                secret.Length = Math.Max(0, secret.Length - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }

        return secret.ToString();
    }
}
