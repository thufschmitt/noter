using Windows.Security.Credentials;
using Windows.Storage;

namespace Longhand.Services;

/// <summary>
/// The connected Nextcloud account. The server address and login name are kept in local settings; the app password, which
/// is the secret, is kept in Windows Credential Manager next to the API key.
/// </summary>
public static class NextcloudStore
{
    const string Resource = "Longhand";
    const string User = "nextcloud-app-password";
    const string ServerKey = "nextcloud-server";
    const string LoginKey = "nextcloud-login";

    static ApplicationDataContainer Settings => ApplicationData.Current.LocalSettings;

    public static NextcloudAccount? Load()
    {
        if (Settings.Values[ServerKey] is not string server || Settings.Values[LoginKey] is not string login) return null;
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, User);
            credential.RetrievePassword();
            return string.IsNullOrEmpty(credential.Password) ? null : new NextcloudAccount(server, login, credential.Password);
        }
        catch
        {
            return null; // Retrieve throws when nothing is stored.
        }
    }

    public static void Save(NextcloudAccount account)
    {
        Clear();
        Settings.Values[ServerKey] = account.Server;
        Settings.Values[LoginKey] = account.User;
        new PasswordVault().Add(new PasswordCredential(Resource, User, account.Password));
    }

    public static void Clear()
    {
        Settings.Values.Remove(ServerKey);
        Settings.Values.Remove(LoginKey);
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(Resource, User));
        }
        catch
        {
            // Nothing stored.
        }
    }
}
