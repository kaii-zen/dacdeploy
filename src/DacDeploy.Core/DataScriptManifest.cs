using System.Text.Json;

namespace DacDeploy;

/// <summary>
/// A deployment's data scripts written to a file for the DataScripts contributor, whose arguments are only text;
/// deleted when disposed.
/// </summary>
internal sealed class DataScriptManifest : IDisposable
{
    private DataScriptManifest(string path) => Path = path;

    public string Path { get; }

    public static DataScriptManifest Write(IReadOnlyList<DataScript> scripts)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dacdeploy-scripts-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(scripts));
        return new DataScriptManifest(path);
    }

    public static IReadOnlyList<DataScript> Read(string path) =>
        JsonSerializer.Deserialize<List<DataScript>>(File.ReadAllText(path)) ?? [];

    public void Dispose() => File.Delete(Path);
}
