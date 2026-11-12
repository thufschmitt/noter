namespace Longhand.Services;

/// <summary>The connected Nextcloud account on Windows: the shared <see cref="NextcloudBook"/> over local settings and Credential Manager.</summary>
public static class NextcloudStore
{
    static readonly NextcloudBook Book = new(WindowsSettings.Local, new WindowsSecrets());

    public static NextcloudAccount? Load() => Book.Load();
    public static void Save(NextcloudAccount account) => Book.Save(account);
    public static void Clear() => Book.Clear();

    /// <summary>The calendars the user chose for the model to use (see <see cref="NextcloudBook.Calendars"/>); null when they haven't chosen.</summary>
    public static System.Collections.Generic.IReadOnlyCollection<string>? Calendars() => Book.Calendars();
    public static void SaveCalendars(System.Collections.Generic.IEnumerable<string>? segments) => Book.SaveCalendars(segments);
}
