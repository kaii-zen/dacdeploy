namespace DacDeploy.UnitTests;

/// <summary>
/// How a data script's record on the database is named: after the script's kind and its path inside its folder, never
/// after where the folder is, so the folder can move; and how a record named the older way translates.
/// </summary>
public sealed class DataScriptRecordTests
{
    [Theory]
    [InlineData(DataScriptKind.ReferenceData, "Settings.sql", null, "dacdeploy:data:Settings.sql")]
    [InlineData(DataScriptKind.ReferenceData, "lookups/Countries.sql", null, "dacdeploy:data:lookups/Countries.sql")]
    [InlineData(DataScriptKind.Migration, "20990101-backfill.sql", "pre", "dacdeploy:migration:20990101-backfill.sql#pre")]
    [InlineData(DataScriptKind.Migration, "20990101-backfill.sql", "post", "dacdeploy:migration:20990101-backfill.sql#post")]
    public void Name_IsTheKindAndThePathInsideItsFolder(DataScriptKind kind, string path, string? section, string record) =>
        DataScriptRecord.Name(kind, path, section).Should().Be(record);

    [Theory]
    [InlineData("DataScripts:Data/Settings.sql", "dacdeploy:data:Settings.sql")]
    [InlineData("DataScripts:Migrations/20990101-backfill.sql#pre", "dacdeploy:migration:20990101-backfill.sql#pre")]
    [InlineData("DataScripts:Migrations/20990101-backfill.sql#post", "dacdeploy:migration:20990101-backfill.sql#post")]
    public void ARecordNamedTheOlderWay_TranslatesExactly(string older, string record) =>
        DataScriptRecord.FromOlderName(older).Should().Be(record);

    [Theory]
    [InlineData("DataScripts:Elsewhere/Settings.sql")]
    [InlineData("dacdeploy:data:Settings.sql")]
    [InlineData("MS_Description")]
    public void ANameThatIsntAnOlderRecord_DoesntTranslate(string name) =>
        DataScriptRecord.FromOlderName(name).Should().BeNull();

    [Theory]
    [InlineData("dacdeploy:data:Settings.sql", true)]
    [InlineData("dacdeploy:migration:20990101-backfill.sql#post", true)]
    [InlineData("DataScripts:Data/Settings.sql", true)]
    [InlineData("MS_Description", false)]
    public void IsRecord_KnowsBothForms(string name, bool isRecord) =>
        DataScriptRecord.IsRecord(name).Should().Be(isRecord);
}
