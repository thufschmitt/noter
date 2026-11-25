namespace Longhand.Services;

/// <summary>Which of the package's apps this is (their Ids in Package.appxmanifest).</summary>
public static class AppKind
{
    static readonly string Id = Windows.ApplicationModel.Core.CoreApplication.Id;

    /// <summary>
    /// True for Longhand Calendar, the package's second app: the same program, opened on one day of the user's calendar instead of
    /// the notebook. Windows runs each app of a package in its own process, and tells it which one it is.
    /// </summary>
    public static bool IsCalendar { get; } = Id == "Calendar";

    /// <summary>True for Longhand Score, the third: the same program, opened on staves to write music on (<see cref="ScorePage"/>).</summary>
    public static bool IsScore { get; } = Id == "Score";

    /// <summary>The app's name, as the Start menu shows it.</summary>
    public static string Name => IsCalendar ? "Longhand Calendar" : IsScore ? "Longhand Score" : "Longhand";
}
