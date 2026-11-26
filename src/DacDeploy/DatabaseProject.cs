using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DacDeploy.Cli;

/// <summary>
/// The database project dacdeploy works on, named by the dacdeploy.json in the current folder or the nearest above it:
/// its <c>Project</c> (by default the one .sqlproj next to dacdeploy.json) and its <c>PublishProfile</c> (by default the
/// project's name with .publish.xml), relative to dacdeploy.json. Your own environments are in the user secrets the
/// project file's <c>&lt;UserSecretsId&gt;</c> names (or, before that, dacdeploy.json's <c>UserSecretsId</c>), if any.
/// </summary>
/// <param name="ProjectPath">The .sqlproj.</param>
/// <param name="ProfilePath">The publish profile.</param>
public sealed record DatabaseProject(string ProjectPath, string ProfilePath, string? UserSecretsId)
{
    public const string SettingsFile = "dacdeploy.json";

    /// <summary>The project's folder, which holds its Data/ and Migrations/ scripts.</summary>
    public string Directory => Path.GetDirectoryName(ProjectPath)!;

    /// <summary>The dacdeploy.json naming the project, read as configuration too.</summary>
    public string? SettingsPath { get; init; }

    /// <exception cref="ToolException">No dacdeploy.json, or no single project or publish profile it leads to.</exception>
    public static DatabaseProject Find(string startDirectory)
    {
        var settingsPath = FindSettings(startDirectory) ?? throw new ToolException(
            $"No {SettingsFile} in {startDirectory} or any folder above it. Run dacdeploy in your database project's folder.");
        var folder = Path.GetDirectoryName(settingsPath)!;
        using var settings = JsonDocument.Parse(
            File.ReadAllText(settingsPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        // Names as configuration reads them, ignoring case.
        var settingValues = settings.RootElement.ValueKind == JsonValueKind.Object
            ? settings.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.OrdinalIgnoreCase)
            : [];

        string? Setting(string name) => settingValues.GetValueOrDefault(name);

        var projectPath = Setting("Project") is { } project ? Path.GetFullPath(project, folder) : TheOnlyProject(folder);
        if (!File.Exists(projectPath))
        {
            throw new ToolException($"No project at {projectPath}, which {SettingsFile} names.");
        }

        var profilePath = Setting("PublishProfile") is { } profile
            ? Path.GetFullPath(profile, folder)
            : Path.Combine(Path.GetDirectoryName(projectPath)!, Path.GetFileNameWithoutExtension(projectPath) + ".publish.xml");
        if (!File.Exists(profilePath))
        {
            throw new ToolException($"No publish profile at {profilePath}: add one, or name yours with \"PublishProfile\" in {SettingsFile}.");
        }

        var userSecretsId = UserSecretsIdIn(projectPath) ?? Setting("UserSecretsId");
        return new DatabaseProject(projectPath, profilePath, userSecretsId) { SettingsPath = settingsPath };
    }

    /// <summary>
    /// The project file's own <c>&lt;UserSecretsId&gt;</c>, where <c>dotnet user-secrets init --project</c> puts it; one
    /// set in a file the project imports isn't seen.
    /// </summary>
    private static string? UserSecretsIdIn(string projectPath)
    {
        try
        {
            return XDocument.Load(projectPath).Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "UserSecretsId" && element.Value.Trim().Length > 0)
                ?.Value.Trim();
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? FindSettings(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, SettingsFile);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string TheOnlyProject(string folder)
    {
        var projects = System.IO.Directory.GetFiles(folder, "*.sqlproj");
        if (projects.Length == 0)
        {
            throw new ToolException($"{folder} has no .sqlproj file: name yours with \"Project\" in {SettingsFile}.");
        }

        return projects.Length == 1
            ? projects[0]
            : throw new ToolException($"{folder} has {projects.Length} .sqlproj files: name one with \"Project\" in {SettingsFile}.");
    }
}
