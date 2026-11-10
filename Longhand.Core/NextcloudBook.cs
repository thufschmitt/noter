namespace Longhand.Services;

/// <summary>
/// The connected Nextcloud account. The server address and login name are kept in the settings; the app password, which is the
/// secret, is kept as a secret.
/// </summary>
public sealed class NextcloudBook
{
    const string PasswordName = "nextcloud-app-password";
    const string ServerKey = "nextcloud-server";
    const string LoginKey = "nextcloud-login";

    readonly ISettings _settings;
    readonly ISecrets _secrets;

    public NextcloudBook(ISettings settings, ISecrets secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public NextcloudAccount? Load()
    {
        if (_settings.Get(ServerKey) is not { } server || _settings.Get(LoginKey) is not { } login) return null;
        return _secrets.Get(PasswordName) is { Length: > 0 } password ? new NextcloudAccount(server, login, password) : null;
    }

    public void Save(NextcloudAccount account)
    {
        Clear();
        _settings.Set(ServerKey, account.Server);
        _settings.Set(LoginKey, account.User);
        _secrets.Set(PasswordName, account.Password);
    }

    public void Clear()
    {
        _settings.Remove(ServerKey);
        _settings.Remove(LoginKey);
        _secrets.Remove(PasswordName);
    }
}
