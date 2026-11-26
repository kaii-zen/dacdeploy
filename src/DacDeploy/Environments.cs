using Microsoft.Extensions.Configuration;

namespace DacDeploy.Cli;

/// <summary>A SQL Server login: a user name and its password.</summary>
public sealed record Login(string User, string Password);

/// <summary>
/// A database dacdeploy deploys to, configured under <c>Environments:&lt;name&gt;</c>: in dacdeploy.json, your user
/// secrets, or DACDEPLOY_ environment variables (<c>DACDEPLOY_Environments__prod__Server</c>), each overriding the one
/// before.
/// </summary>
/// <param name="Variables">The SQLCMD variables to deploy with (Variables:&lt;name&gt;).</param>
/// <param name="Login">The configured login that can write (User, Password), if any.</param>
/// <param name="ReadOnlyLogin">The configured read-only login (ReadOnlyUser, ReadOnlyPassword), if any.</param>
public sealed record DatabaseEnvironment(
    string Name, string Server, string Database, IReadOnlyDictionary<string, string> Variables, Login? Login, Login? ReadOnlyLogin)
{
    public static IReadOnlyList<string> Names(IConfiguration configuration) =>
        [.. configuration.GetSection("Environments").GetChildren().Select(environment => environment.Key)];

    /// <exception cref="ToolException">There's no such environment, or it lacks a server or a database name.</exception>
    public static DatabaseEnvironment Read(IConfiguration configuration, string name)
    {
        // By the name as configured, so "-E Prod" plans for "prod", the name a saved plan is checked against.
        var section = configuration.GetSection("Environments").GetChildren()
            .FirstOrDefault(environment => string.Equals(environment.Key, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException(
                $"No environment named '{name}'. Use one of {Known(configuration)}, or add it to dacdeploy.json or your user secrets.");

        string Required(string key) =>
            section[key] is { Length: > 0 } value ? value : throw new ToolException($"Environment '{section.Key}' has no {key}.");

        Login? Configured(string prefix) =>
            section[$"{prefix}User"] is { Length: > 0 } user && section[$"{prefix}Password"] is { } password ? new Login(user, password) : null;

        var variables = section.GetSection("Variables").GetChildren()
            .Where(variable => variable.Value is not null)
            .ToDictionary(variable => variable.Key, variable => variable.Value!);
        return new DatabaseEnvironment(section.Key, Required("Server"), Required("Database"), variables, Configured(""), Configured("ReadOnly"));
    }

    /// <summary>The configured environments' names, for messages: "prod, test".</summary>
    public static string Known(IConfiguration configuration) => string.Join(", ", Names(configuration));
}
