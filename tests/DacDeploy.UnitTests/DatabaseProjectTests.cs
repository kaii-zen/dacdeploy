namespace DacDeploy.UnitTests;

/// <summary>How dacdeploy finds the project it works on: the dacdeploy.json in the current folder or the nearest above it.</summary>
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
        Write("dacdeploy.json", """{ "Project": "src/Db/Db.sqlproj", "PublishProfile": "deploy.xml", "UserSecretsId": "abc" }""");
        Write("src/Db/Db.sqlproj", "<Project />");
        Write("deploy.xml", "<Project />");

        var project = DatabaseProject.Find(root.FullName);

        project.ProjectPath.Should().Be(Path.Combine(root.FullName, "src", "Db", "Db.sqlproj"));
        project.ProfilePath.Should().Be(Path.Combine(root.FullName, "deploy.xml"));
        project.UserSecretsId.Should().Be("abc");
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

    public void Dispose() => root.Delete(recursive: true);

    private void Write(string path, string content)
    {
        var file = Path.Combine(root.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }
}
