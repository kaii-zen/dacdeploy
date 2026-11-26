namespace DacDeploy.TestSupport;

/// <summary>Logins kept in memory instead of the OS credential store, by server, counting lookups.</summary>
public sealed class FakeLogins : ILoginStore
{
    public Dictionary<(string Server, bool ReadOnly), Login> Saved { get; } = [];

    public int Lookups { get; private set; }

    public Login? Get(string server, bool readOnly)
    {
        Lookups++;
        return Saved.GetValueOrDefault((server, readOnly));
    }

    public void Save(string server, bool readOnly, Login login) => Saved[(server, readOnly)] = login;
}
