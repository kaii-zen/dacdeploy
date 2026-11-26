using System.CommandLine;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace DacDeploy.Cli;

/// <summary>What the tool needs from where it runs; <see cref="Program"/> gives the real ones.</summary>
/// <param name="Configuration">The environments, under <c>Environments:&lt;name&gt;</c> (see <see cref="DatabaseEnvironment"/>).</param>
/// <param name="Logins">Where <c>dacdeploy login</c> saves logins.</param>
/// <param name="Project">The project to work on; throws a <see cref="ToolException"/> when there's none.</param>
/// <param name="Build">Builds the project, showing everything when verbose, and gives the path of its dacpac.</param>
/// <param name="CommandName">How dacdeploy was run, for the commands its hints suggest: <c>dacdeploy</c>, or
/// <c>dotnet dacdeploy</c> for a local tool.</param>
public sealed record ToolHost(
    IConfiguration Configuration,
    ILoginStore Logins,
    Func<DatabaseProject> Project,
    Func<bool, string> Build,
    ToolTerminal Terminal,
    string CommandName = "dacdeploy");

/// <summary>The terminal the tool runs in.</summary>
/// <param name="InputIsTerminal">Whether someone can answer: apply's question, login's prompts.</param>
/// <param name="Colour">Whether to colour a plan's marks.</param>
/// <param name="ReadSecret">Reads a line without showing it, for a password.</param>
public sealed record ToolTerminal(bool InputIsTerminal, bool Colour, Func<string?> ReadLine, Func<string?> ReadSecret)
{
    public static ToolTerminal None { get; } = new(InputIsTerminal: false, Colour: false, () => null, () => null);
}

/// <summary>A refusal the tool prints and exits 1 on.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>dacdeploy's commands: <c>plan</c>, <c>apply</c>, <c>extract</c> and <c>login</c>.</summary>
public sealed class Tool(TextWriter output, TextWriter errors, ToolHost host)
{
    public int Run(IReadOnlyList<string> arguments) =>
        Commands().Parse(arguments).Invoke(new InvocationConfiguration { Output = output, Error = errors, EnableDefaultExceptionHandler = false });

    private RootCommand Commands()
    {
        var known = DatabaseEnvironment.Known(host.Configuration);

        Option<string> Environment() => new("--environment", "-E")
        {
            Description = known.Length > 0 ? $"The environment to work on: {known}." : "The environment to work on.",
            HelpName = "name",
        };

        Option<bool> Verbose() => new("--verbose") { Description = "Also show what dotnet build and DacFx print" };

        Option<bool> Prune(string description) => new("--prune") { Description = description };

        var plan = new Command("plan", "Show what applying would do");
        var planEnvironment = Environment();
        var outFile = new Option<string>("--out") { Description = "Also save the plan, with the SQL it runs, to review and then apply", HelpName = "file" };
        var exitCode = new Option<bool>("--exit-code") { Description = "Exit 2 instead of 0 when there are changes" };
        var planPrune = Prune("Plan to drop what the database has and the project doesn't");
        var planVerbose = Verbose();
        plan.Options.Add(planEnvironment);
        plan.Options.Add(outFile);
        plan.Options.Add(exitCode);
        plan.Options.Add(planPrune);
        plan.Options.Add(planVerbose);
        plan.SetAction(result => Guarded(() => Plan(
            result.GetValue(planEnvironment), result.GetValue(outFile), result.GetValue(exitCode), result.GetValue(planPrune), result.GetValue(planVerbose))));

        var apply = new Command("apply", "Apply a saved plan; without one, plan, ask, then apply");
        var applyEnvironment = Environment();
        var planFile = new Argument<string?>("plan") { Description = "A plan saved with plan --out", Arity = ArgumentArity.ZeroOrOne };
        // The parser hands an option apply doesn't take to this argument; refuse it as an option, not a missing file.
        planFile.Validators.Add(result =>
        {
            foreach (var token in result.Tokens.Where(token => token.Value.StartsWith('-')))
            {
                result.AddError($"Unrecognized command or argument '{token.Value}'.");
            }
        });
        var yes = new Option<bool>("--yes", "-y") { Description = "Without a plan, apply without asking" };
        var applyPrune = Prune("Without a plan, also drop what the database has and the project doesn't");
        var applyVerbose = Verbose();
        apply.Arguments.Add(planFile);
        apply.Options.Add(applyEnvironment);
        apply.Options.Add(yes);
        apply.Options.Add(applyPrune);
        apply.Options.Add(applyVerbose);
        apply.SetAction(result => Guarded(() => Apply(
            result.GetValue(applyEnvironment), result.GetValue(planFile), result.GetValue(yes), result.GetValue(applyPrune), result.GetValue(applyVerbose))));

        var extract = new Command("extract", "Show how the database's schema differs from the project's Schema/dbo");
        var extractEnvironment = Environment();
        var write = new Option<bool>("--write") { Description = "Replace the project's Schema/dbo with it" };
        var extractVerbose = Verbose();
        extract.Options.Add(extractEnvironment);
        extract.Options.Add(write);
        extract.Options.Add(extractVerbose);
        extract.SetAction(result => Guarded(() => Extract(result.GetValue(extractEnvironment), result.GetValue(write), result.GetValue(extractVerbose))));

        var login = new Command("login", "Save the environment's login in your OS credential store");
        var loginEnvironment = Environment();
        var readOnly = new Option<bool>("--read-only") { Description = "Save the read-only login, which plan and extract use when there is one" };
        login.Options.Add(loginEnvironment);
        login.Options.Add(readOnly);
        login.SetAction(result => Guarded(() => Login(result.GetValue(loginEnvironment), result.GetValue(readOnly))));

        var root = new RootCommand(
            "Plans and applies a SQL Server database project to a database: plan shows what applying would change, and apply " +
            "applies a plan you saved and reviewed, or a fresh one once you've seen it and said yes.");
        root.Subcommands.Add(plan);
        root.Subcommands.Add(apply);
        root.Subcommands.Add(extract);
        root.Subcommands.Add(login);
        root.SetAction(_ =>
        {
            errors.WriteLine($"Pick a command: plan, apply, extract or login. See {host.CommandName} --help.");
            return 1;
        });
        return root;
    }

    private int Guarded(Func<int> command)
    {
        try
        {
            return command();
        }
        catch (Exception e) when (e is ToolException or DeploymentException or PlanFileException)
        {
            errors.WriteLine(e.Message);
            return 1;
        }
    }

    private int Plan(string? environmentName, string? outPath, bool exitCode, bool prune, bool verbose)
    {
        var project = host.Project();
        var savePath = outPath is null ? null : Path.GetFullPath(outPath);
        var target = Target(environmentName, readOnly: true);
        var plan = Deployer(project, host.Build(verbose)).Plan(target, prune, Log(verbose));
        output.Write(PlanPrinter.Show(plan, host.Terminal.Colour));
        if (savePath is not null)
        {
            File.WriteAllText(savePath, PlanFile.Write(plan).ReplaceLineEndings());
        }

        return plan.Changed && exitCode ? 2 : 0;
    }

    private int Apply(string? environmentName, string? planPath, bool yes, bool prune, bool verbose)
    {
        var project = host.Project();
        var target = Target(environmentName, readOnly: false);
        Plan plan;
        Deployer deployer;
        if (planPath is not null)
        {
            var path = Path.GetFullPath(planPath);
            if (!File.Exists(path))
            {
                throw new ToolException($"No plan at {path}.");
            }

            plan = PlanFile.Read(File.ReadAllText(path), path, target.Identity);
            output.Write(PlanPrinter.Show(plan, host.Terminal.Colour));
            deployer = Deployer(project, host.Build(verbose));
        }
        else
        {
            deployer = Deployer(project, host.Build(verbose));
            plan = deployer.Plan(target, prune, Log(verbose));
            output.Write(PlanPrinter.Show(plan, host.Terminal.Colour));
            if (!plan.Changed)
            {
                return 0;
            }

            Confirm(yes, target);
        }

        deployer.Apply(target, plan, Log(verbose));
        return 0;
    }

    private static readonly string[] ObjectFolders = ["Tables", "Views", "StoredProcedures", "Functions", "UserDefinedTypes"];

    /// <summary>
    /// Shows how the source database differs from the project's Schema/dbo, or with --write replaces it with the
    /// database's schema: one file per object, references to the SQLCMD variables' databases and to itself rewritten as
    /// the project writes them.
    /// </summary>
    private int Extract(string? environmentName, bool write, bool verbose)
    {
        var project = host.Project();
        var source = Target(environmentName, readOnly: true);
        var schema = Path.Combine(project.Directory, "Schema", "dbo");
        var tmp = Directory.CreateTempSubdirectory("extract");
        try
        {
            var raw = Path.Combine(tmp.FullName, "extracted");
            DacDeploy.Deployer.Extract(source, raw, Log(verbose));
            // Security and Storage are environment-specific (users, logins, file layout).
            var fresh = Path.Combine(tmp.FullName, "source");
            foreach (var folder in ObjectFolders.Where(folder => Directory.Exists(Path.Combine(raw, "dbo", folder))))
            {
                CopyDirectory(Path.Combine(raw, "dbo", folder), Path.Combine(fresh, folder));
            }

            if (!Directory.Exists(fresh))
            {
                throw new ToolException(
                    $"Extracted no tables, views, procedures, functions or types from {source.Database}; nothing to compare.");
            }

            var (variables, self) = RewriteReferences(fresh, source);
            errors.WriteLine($"Rewrote {variables} references to SQLCMD variables' databases and {self} self-references");
            if (write)
            {
                if (Directory.Exists(schema))
                {
                    Directory.Delete(schema, recursive: true);
                }

                CopyDirectory(fresh, schema);
                errors.WriteLine($"{schema} replaced; review the changes before committing them.");
                return 0;
            }

            if (Directory.Exists(schema))
            {
                CopyDirectory(schema, Path.Combine(tmp.FullName, "current"));
            }

            var diff = new ProcessStartInfo("git", ["diff", "--no-index", "--no-color", "--stat", "-p", "current", "source"])
            {
                WorkingDirectory = tmp.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(diff)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            // git diff exits 1 when there are differences; anything else means it failed.
            if (process.ExitCode is not (0 or 1))
            {
                throw new ToolException($"git diff failed: {stderr.Trim()}");
            }

            if (stdout.Result.Length > 0)
            {
                output.WriteLine(stdout.Result);
                errors.WriteLine("Run again with --write to apply.");
            }
            else
            {
                errors.WriteLine($"No differences: {schema} matches the source database.");
            }

            return 0;
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    /// <summary>Asks for the environment's login, and saves it once it connects.</summary>
    private int Login(string? environmentName, bool readOnly)
    {
        host.Project();
        var environment = Environment(environmentName);
        var kind = readOnly ? "read-only login" : "login";
        if (!host.Terminal.InputIsTerminal)
        {
            throw new ToolException($"{host.CommandName} login asks for the user name and password: run it in a terminal.");
        }

        RequireReachable(environment.Server);
        output.Write($"User name for the {kind} to {environment.Name} ({environment.Server}): ");
        var user = host.Terminal.ReadLine()?.Trim();
        output.Write("Password: ");
        var password = host.Terminal.ReadSecret();
        output.WriteLine();
        if (string.IsNullOrEmpty(user) || password is null)
        {
            throw new ToolException("Nothing saved.");
        }

        var login = new Login(user, password);
        try
        {
            using var connection = new SqlConnection(Target(environment, login).ConnectionString);
            connection.Open();
        }
        catch (SqlException e)
        {
            throw new ToolException($"Couldn't log in to {environment.Server} as {user}: {e.Message} Nothing saved.");
        }

        host.Logins.Save(environment.Server, readOnly, login);
        output.WriteLine($"Saved the {kind} to {environment.Server} in your OS credential store.");
        return 0;
    }

    /// <summary>Rewrites references to the variables' databases as <c>[$(Name)].[dbo].</c>, and to the source itself as <c>[dbo].</c>.</summary>
    private static (int Variables, int Self) RewriteReferences(string root, DeploymentTarget source)
    {
        static Regex Reference(string name) =>
            new($@"(?:\[{Regex.Escape(name)}\]|\b{Regex.Escape(name)})\.(?:\[dbo\]|dbo)\.", RegexOptions.IgnoreCase);

        var variableReferences = source.Variables.Select(variable => (Pattern: Reference(variable.Value), Replacement: $"[$({variable.Key})].[dbo].")).ToList();
        var selfReference = Reference(source.Database);
        var (variables, self) = (0, 0);
        foreach (var path in Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories))
        {
            // Bytes, so a byte order mark stays exactly as extracted.
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            var v = 0;
            foreach (var (pattern, replacement) in variableReferences)
            {
                v += pattern.Count(text);
                text = pattern.Replace(text, replacement.Replace("$", "$$"));
            }

            var s = selfReference.Count(text);
            text = selfReference.Replace(text, "[dbo].");
            if (v > 0 || s > 0)
            {
                File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
                variables += v;
                self += s;
            }
        }

        return (variables, self);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private void Confirm(bool yes, DeploymentTarget target)
    {
        if (yes)
        {
            return;
        }

        if (!host.Terminal.InputIsTerminal)
        {
            throw new ToolException("Not asking without a terminal: pass --yes, or apply a saved plan.");
        }

        output.Write($"Apply these changes to {target.Database} on {target.Server}? Only 'yes' is accepted: ");
        if (host.Terminal.ReadLine() != "yes")
        {
            throw new ToolException("Nothing applied.");
        }
    }

    private static Deployer Deployer(DatabaseProject project, string dacpacPath) =>
        new(new DeploymentFiles(dacpacPath, project.ProfilePath, project.Directory));

    private Action<string>? Log(bool verbose) => verbose ? errors.WriteLine : null;

    private DatabaseEnvironment Environment(string? name) =>
        name is not null
            ? DatabaseEnvironment.Read(host.Configuration, name)
            : throw new ToolException($"Pick an environment with -E <name>: {DatabaseEnvironment.Known(host.Configuration)}.");

    /// <summary>The environment's database, with the login for <paramref name="readOnly"/> work; stops if it can't be reached.</summary>
    private DeploymentTarget Target(string? environmentName, bool readOnly)
    {
        var environment = Environment(environmentName);
        var login = LoginFor(environment, readOnly) ?? throw new ToolException(readOnly
            ? $"No login for {environment.Name}: run `{host.CommandName} login -E {environment.Name} --read-only`, or without --read-only to save the login that can write."
            : $"No login for {environment.Name}: run `{host.CommandName} login -E {environment.Name}`.");
        RequireReachable(environment.Server);
        return Target(environment, login);
    }

    private static DeploymentTarget Target(DatabaseEnvironment environment, Login login) =>
        new(environment.Name, environment.Server, environment.Database, environment.Variables, login.User, login.Password);

    /// <summary>
    /// Configured logins first, then saved ones. Reading prefers the read-only login, and falls back to the one that
    /// can write; applying uses only the one that can write.
    /// </summary>
    private Login? LoginFor(DatabaseEnvironment environment, bool readOnly) => readOnly
        ? environment.ReadOnlyLogin ?? environment.Login
            ?? host.Logins.Get(environment.Server, readOnly: true) ?? host.Logins.Get(environment.Server, readOnly: false)
        : environment.Login ?? host.Logins.Get(environment.Server, readOnly: false);

    /// <summary>Stops at once when the server can't be reached, instead of DacFx retrying for minutes.</summary>
    private static void RequireReachable(string server)
    {
        if (server.Contains('\\'))
        {
            return; // A named instance: its port isn't known in advance.
        }

        var separator = server.IndexOf(',');
        var hostName = separator < 0 ? server : server[..separator];
        var port = separator < 0 ? 1433 : int.Parse(server[(separator + 1)..]);
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(hostName, port).Wait(TimeSpan.FromSeconds(5)))
            {
                throw new SocketException();
            }
        }
        catch (Exception e) when (e is SocketException or AggregateException)
        {
            throw new ToolException($"Can't reach {server}. Is the database server running, and reachable from here?");
        }
    }
}
