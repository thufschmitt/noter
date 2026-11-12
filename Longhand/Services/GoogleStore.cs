namespace Longhand.Services;

/// <summary>The Google Calendar sign-in on Windows: the shared <see cref="GoogleCalendarBook"/> over local settings and Credential Manager.</summary>
public static class GoogleStore
{
    public static readonly GoogleCalendarBook Book = new(WindowsSettings.Local, new WindowsSecrets());
}
