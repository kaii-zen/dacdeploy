using System.Text.Json;

namespace DacDeploy.Cli;

/// <summary>What building the project gives dacdeploy: the dacpac, and the data scripts to deploy with it.</summary>
public sealed record BuiltProject(string DacpacPath, IReadOnlyList<DataScript>? DataScripts);

/// <summary>
/// Reads what MSBuild says about the built project (<c>-getProperty:TargetPath -getItem:ReferenceData
/// -getItem:Migration</c>): the dacpac's path, and the scripts the project declares as <c>ReferenceData</c> and
/// <c>Migration</c> items, each named by its path under the item's wildcard. A kind the project declares none of is
/// <c>Data/*.sql</c> or <c>Migrations/*.sql</c> next to it.
/// </summary>
public static class BuildOutput
{
    public const string ReferenceDataItem = "ReferenceData";
    public const string MigrationItem = "Migration";

    public static BuiltProject Read(string json, string projectDirectory)
    {
        using var output = JsonDocument.Parse(json);
        var dacpac = output.RootElement.GetProperty("Properties").GetProperty("TargetPath").GetString()
            ?? throw new ToolException("MSBuild gave no TargetPath for the project.");
        var items = output.RootElement.TryGetProperty("Items", out var all) ? all : default;
        return new BuiltProject(dacpac, [.. Scripts(items, ReferenceDataItem, DataScriptKind.ReferenceData, projectDirectory),
            .. Scripts(items, MigrationItem, DataScriptKind.Migration, projectDirectory)]);
    }

    private static IReadOnlyList<DataScript> Scripts(JsonElement items, string item, DataScriptKind kind, string projectDirectory)
    {
        if (items.ValueKind != JsonValueKind.Object || !items.TryGetProperty(item, out var declared) || declared.GetArrayLength() == 0)
        {
            return DataScript.Conventional(projectDirectory, kind);
        }

        return [.. declared.EnumerateArray().Select(script => new DataScript(
            kind,
            // Under the wildcard, with / whatever the OS.
            (Metadata(script, "RecursiveDir") + Metadata(script, "Filename") + Metadata(script, "Extension")).Replace('\\', '/'),
            Metadata(script, "FullPath")))];
    }

    private static string Metadata(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
}
