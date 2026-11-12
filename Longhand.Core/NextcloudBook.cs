using System.Collections.Generic;

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
    const string CalendarsKey = "nextcloud-calendars";

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

    /// <summary>The calendars (<see cref="CalendarInfo.Segment"/>) the user chose for Longhand to use; null when no choice was made, meaning all of them.</summary>
    public IReadOnlyCollection<string>? Calendars() => CalendarChoice.Read(_settings, CalendarsKey);

    /// <summary>Keeps the choice of calendars; null goes back to using all of them.</summary>
    public void SaveCalendars(IEnumerable<string>? segments) => CalendarChoice.Write(_settings, CalendarsKey, segments);

    public void Clear()
    {
        _settings.Remove(CalendarsKey);
        _settings.Remove(ServerKey);
        _settings.Remove(LoginKey);
        _secrets.Remove(PasswordName);
    }
}
