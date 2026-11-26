using GitCredentialManager;

namespace DacDeploy.Cli;

/// <summary>Where <c>dacdeploy login</c> saves logins: one per server, and a read-only one apart.</summary>
public interface ILoginStore
{
    Login? Get(string server, bool readOnly);

    void Save(string server, bool readOnly, Login login);
}

/// <summary>
/// The OS credential store, through Git Credential Manager's implementation: Keychain on macOS, Credential Manager on
/// Windows, Secret Service on Linux. Entries are named <c>dacdeploy:&lt;server&gt;</c> and
/// <c>dacdeploy:&lt;server&gt; (read-only)</c>: a login belongs to the server, so every environment on it finds it.
/// </summary>
public sealed class OsLoginStore : ILoginStore
{
    // Opened on first use, so commands that read no login never touch the store.
    private readonly Lazy<ICredentialStore> store = new(() => CredentialManager.Create("dacdeploy"));

    public Login? Get(string server, bool readOnly) =>
        store.Value.Get(Service(server, readOnly), account: null) is { } credential
            ? new Login(credential.Account, credential.Password)
            : null;

    public void Save(string server, bool readOnly, Login login)
    {
        var service = Service(server, readOnly);
        // The store keys entries by user name too: drop the ones saved before, so only this login is found.
        foreach (var account in store.Value.GetAccounts(service).Where(account => account != login.User))
        {
            store.Value.Remove(service, account);
        }

        store.Value.AddOrUpdate(service, login.User, login.Password);
    }

    // Plain names rather than URLs, which the store matches loosely: "db (read-only)" must never answer for "db".
    private static string Service(string server, bool readOnly) => readOnly ? $"{server} (read-only)" : server;
}
