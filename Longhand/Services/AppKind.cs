namespace Longhand.Services;

/// <summary>Which of the package's two apps this is (their Ids in Package.appxmanifest).</summary>
public static class AppKind
{
    /// <summary>
    /// True for Longhand Calendar, the package's second app: the same program, opened on one day of the user's calendar instead of
    /// the notebook. Windows runs each app of a package in its own process, and tells it which one it is.
    /// </summary>
    public static bool IsCalendar { get; } = Windows.ApplicationModel.Core.CoreApplication.Id == "Calendar";
}
