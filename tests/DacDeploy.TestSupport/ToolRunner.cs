using Microsoft.Extensions.Configuration;

namespace DacDeploy.TestSupport;

/// <summary>What a command printed and the code it exited with.</summary>
public sealed record ToolResult(int ExitCode, string Output, string Errors);

/// <summary>
/// Runs dacdeploy's commands in-process, on <paramref name="project"/>, with the configuration each run gives and the
/// logins in <see cref="Logins"/>. Building is left out: <paramref name="dacpacPath"/> stands for the build's output, and
/// <see cref="Builds"/> counts the commands that got as far as building.
/// </summary>
public sealed class ToolRunner(DatabaseProject project, string dacpacPath)
{
    public FakeLogins Logins { get; } = new();

    public int Builds { get; private set; }

    public ToolResult Run(IEnumerable<KeyValuePair<string, string?>> settings, params string[] arguments) =>
        Run(settings, ToolTerminal.None, arguments);

    public ToolResult Run(IEnumerable<KeyValuePair<string, string?>> settings, ToolTerminal terminal, params string[] arguments) =>
        Run(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), terminal, arguments);

    /// <summary>Runs a command with <paramref name="configuration"/> as it is, sources and all.</summary>
    public ToolResult Run(IConfiguration configuration, params string[] arguments) => Run(configuration, ToolTerminal.None, arguments);

    private ToolResult Run(IConfiguration configuration, ToolTerminal terminal, string[] arguments)
    {
        var host = new ToolHost(configuration, Logins, () => project, _ =>
        {
            Builds++;
            return dacpacPath;
        }, terminal);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = new Tool(output, errors, host).Run(arguments);
        return new ToolResult(exitCode, output.ToString(), errors.ToString());
    }

    /// <summary>A terminal someone answers on with <paramref name="answers"/>, in order: lines, then secrets.</summary>
    public static ToolTerminal Answering(params string?[] answers)
    {
        var input = new Queue<string?>(answers);
        return new ToolTerminal(InputIsTerminal: true, Colour: false, input.Dequeue, input.Dequeue);
    }
}
