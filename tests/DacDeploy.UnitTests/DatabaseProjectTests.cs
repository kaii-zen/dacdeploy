namespace DacDeploy.UnitTests;

/// <summary>
/// How dacdeploy finds the project it works on, from the dacdeploy.json in the current folder or the nearest above it,
/// and what the project's build gives it: the dacpac and the data scripts it declares.
/// </summary>
public sealed class DatabaseProjectTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("DatabaseProjectTests");

    [Fact]
    public void Find_FromAFolderBelow_UsesTheProjectNextToDacdeployJson()
    {
        Write("db/dacdeploy.json", "{}");
        Write("db/Shop.sqlproj", "<Project />");
        Write("db/Shop.publish.xml", "<Project />");
        var below = Directory.CreateDirectory(Path.Combine(root.FullName, "db", "Schema", "dbo"));

        var project = DatabaseProject.Find(below.FullName);

        project.ProjectPath.Should().Be(Path.Combine(root.FullName, "db", "Shop.sqlproj"));
        project.ProfilePath.Should().Be(Path.Combine(root.FullName, "db", "Shop.publish.xml"));
        project.Directory.Should().Be(Path.Combine(root.FullName, "db"));
        project.UserSecretsId.Should().BeNull();
    }

    [Fact]
    public void Find_UsesTheProjectProfileAndUserSecretsDacdeployJsonNames()
    {
        Write("dacdeploy.json", /*lang=json*/ """{ "Project": "src/Db/Db.sqlproj", "PublishProfile": "deploy.xml", "UserSecretsId": "abc" }""");
        Write("src/Db/Db.sqlproj", "<Project />");
        Write("deploy.xml", "<Project />");

        var project = DatabaseProject.Find(root.FullName);

        project.ProjectPath.Should().Be(Path.Combine(root.FullName, "src", "Db", "Db.sqlproj"));
        project.ProfilePath.Should().Be(Path.Combine(root.FullName, "deploy.xml"));
        project.UserSecretsId.Should().Be("abc");
    }

    [Fact]
    public void Find_ReadsTheUserSecretsIdFromTheProjectFile()
    {
        Write("dacdeploy.json", "{}");
        Write("Shop.sqlproj", """
            <Project Sdk="Microsoft.Build.Sql/2.4.0">
              <PropertyGroup>
                <Name>Shop</Name>
                <UserSecretsId>3f2b8c1e-0000-4000-8000-000000000001</UserSecretsId>
              </PropertyGroup>
            </Project>
            """);
        Write("Shop.publish.xml", "<Project />");

        DatabaseProject.Find(root.FullName).UserSecretsId.Should().Be("3f2b8c1e-0000-4000-8000-000000000001");
    }

    [Fact]
    public void Find_PrefersTheProjectFilesUserSecretsId_ToDacdeployJsons()
    {
        Write("dacdeploy.json", /*lang=json*/ """{ "UserSecretsId": "from-dacdeploy-json" }""");
        Write("Shop.sqlproj", "<Project><PropertyGroup><UserSecretsId>from-the-project</UserSecretsId></PropertyGroup></Project>");
        Write("Shop.publish.xml", "<Project />");

        DatabaseProject.Find(root.FullName).UserSecretsId.Should().Be("from-the-project");
    }

    [Fact]
    public void Find_WithoutDacdeployJson_SaysWhereItLooked()
    {
        var find = () => DatabaseProject.Find(root.FullName);

        find.Should().Throw<ToolException>().WithMessage($"No dacdeploy.json in {root.FullName} or any folder above it.*");
    }

    [Fact]
    public void Find_WithTwoProjectsAndNoneNamed_AsksForOne()
    {
        Write("dacdeploy.json", "{}");
        Write("One.sqlproj", "<Project />");
        Write("Two.sqlproj", "<Project />");

        var find = () => DatabaseProject.Find(root.FullName);

        find.Should().Throw<ToolException>().WithMessage("*2 .sqlproj files*name one with \"Project\"*");
    }

    [Fact]
    public void Find_WithoutAPublishProfile_SaysWhichOneItExpected()
    {
        Write("dacdeploy.json", "{}");
        Write("Shop.sqlproj", "<Project />");

        var find = () => DatabaseProject.Find(root.FullName);

        find.Should().Throw<ToolException>().WithMessage($"No publish profile at {Path.Combine(root.FullName, "Shop.publish.xml")}*");
    }

    [Fact]
    public void BuildOutput_GivesTheDacpacAndTheScriptsTheProjectDeclares()
    {
        var json = $$"""
            {
              "Properties": { "TargetPath": "{{Json(root.FullName)}}/bin/Release/Shop.dacpac" },
              "Items": {
                "ReferenceData": [
                  { "FullPath": "{{Json(root.FullName)}}/reference/lookups/Countries.sql", "RecursiveDir": "lookups/", "Filename": "Countries", "Extension": ".sql" },
                  { "FullPath": "{{Json(root.FullName)}}/reference/Settings.sql", "RecursiveDir": "", "Filename": "Settings", "Extension": ".sql" }
                ],
                "Migration": [
                  { "FullPath": "{{Json(root.FullName)}}/oneoffs/20990101-backfill.sql", "RecursiveDir": "", "Filename": "20990101-backfill", "Extension": ".sql" }
                ]
              }
            }
            """;

        var built = BuildOutput.Read(json, root.FullName);

        built.DacpacPath.Should().Be($"{root.FullName}/bin/Release/Shop.dacpac");
        built.DataScripts.Should().Equal(
            new DataScript(DataScriptKind.ReferenceData, "lookups/Countries.sql", $"{root.FullName}/reference/lookups/Countries.sql"),
            new DataScript(DataScriptKind.ReferenceData, "Settings.sql", $"{root.FullName}/reference/Settings.sql"),
            new DataScript(DataScriptKind.Migration, "20990101-backfill.sql", $"{root.FullName}/oneoffs/20990101-backfill.sql"));
    }

    [Fact]
    public void BuildOutput_WithoutDeclaredScripts_TakesDataAndMigrationsNextToTheProject()
    {
        Write("Data/Settings.sql", "SELECT 1;");
        Write("Data/drafts/Unused.sql", "SELECT 2;");
        Write("Migrations/20990101-backfill.sql", "SELECT 3;");
        var json = $$"""{ "Properties": { "TargetPath": "{{Json(root.FullName)}}/bin/Release/Shop.dacpac" } }""";

        var built = BuildOutput.Read(json, root.FullName);

        built.DataScripts.Should().Equal(
            new DataScript(DataScriptKind.ReferenceData, "Settings.sql", Path.Combine(root.FullName, "Data", "Settings.sql")),
            new DataScript(DataScriptKind.Migration, "20990101-backfill.sql", Path.Combine(root.FullName, "Migrations", "20990101-backfill.sql")));
    }

    public void Dispose() => root.Delete(recursive: true);

    private static string Json(string text) => text.Replace("\\", "\\\\");

    private void Write(string path, string content)
    {
        var file = Path.Combine(root.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }
}
