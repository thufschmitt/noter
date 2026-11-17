using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.Xaml;

namespace Longhand.Services;

/// <summary>
/// What went wrong, kept for the user to pass on: every error nothing else caught is written to crash.log in the app's local folder,
/// with when, which app and version, which Windows, where it happened and the whole exception. The page's own handlers catch and
/// log their errors (MainPage.Guard) and carry on. An error that gets past them reaches the UI thread's last-chance handler, which
/// asks Windows to carry on but can't be sure it will (async errors can still end the app): it is logged as possibly fatal, and if
/// the app is gone a few seconds later, the next start says so, as for an error that ends the app.
/// </summary>
public static class CrashLog
{
    const string FileName = "crash.log", UnreadKey = "crash-unread", MaybeKey = "crash-maybe";
    const int MaxBytes = 256 * 1024, EntriesCopied = 3;
    const string Separator = "==================== ";
    static readonly object Gate = new();

    /// <summary>An error was caught and the app carries on: the page shows a short note.</summary>
    public static event Action<string>? Reported;

    static string PathOf() => Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);

    /// <summary>
    /// The last few exceptions thrown anywhere, with the stack where each was thrown, caught or not. An error that reaches the UI
    /// thread's handler from an async method has lost its stack by then; the one it came from is usually among these, still with it.
    /// </summary>
    sealed record Thrown(DateTimeOffset At, int Thread, string Type, string Message, int HResult, string Stack);
    const int ThrownKept = 6;
    static readonly Queue<Thrown> Recent = new();
    [ThreadStatic] static bool _recording;

    static void Record(Exception error)
    {
        if (_recording) return; // an exception thrown while recording one isn't recorded
        _recording = true;
        try
        {
            // The handler runs where the exception was thrown, so the current stack is the place it came from.
            var stack = new System.Diagnostics.StackTrace(2, true).ToString();
            var lines = stack.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(30);
            var thrown = new Thrown(DateTimeOffset.Now, Environment.CurrentManagedThreadId, error.GetType().FullName ?? "?", error.Message, error.HResult, string.Join("\n", lines));
            lock (Recent)
            {
                Recent.Enqueue(thrown);
                while (Recent.Count > ThrownKept) Recent.Dequeue();
            }
        }
        catch (Exception) { }
        finally { _recording = false; }
    }

    /// <summary>Catches what nothing else does: on the UI thread, in tasks nobody waited for, and anything that ends the process.</summary>
    public static void Install(Application app)
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => Record(e.Exception);
        app.UnhandledException += (_, e) =>
        {
            // The message Windows gives here is sometimes all that is left of the original error, so both go in.
            Write(e.Exception, "UI thread (a button, the pen, a page event)", fatal: false, e.Message, mayClose: true);
            e.Handled = true;
            Reported?.Invoke(e.Exception?.Message ?? e.Message);
            // Still here a few seconds later: it didn't end the app after all.
            var alive = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            alive.Tick += (_, _) => { alive.Stop(); WindowsSettings.Local.Remove(MaybeKey); };
            alive.Start();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, "a background task nobody waited for", fatal: false);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception, "the app (it closed)", fatal: true, e.ExceptionObject?.ToString());
    }

    /// <summary>Appends an entry. Never throws: a log that can't be written mustn't make things worse.</summary>
    /// <param name="mayClose">The app may end because of it, or not: the entry says so, and the next start asks if it did.</param>
    public static void Write(Exception? error, string where, bool fatal, string? message = null, bool mayClose = false)
    {
        try
        {
            var sb = new StringBuilder();
            var now = DateTimeOffset.Now;
            sb.Append(Separator).Append(now.ToString("yyyy-MM-dd HH:mm:ss zzz")).Append(fatal ? " — the app closed" : mayClose ? " — reached the last-chance handler (the app may have closed)" : " — caught, the app carried on").Append('\n');
            sb.Append("App: ").Append(AppKind.IsCalendar ? "Longhand Calendar" : "Longhand").Append(' ').Append(Version()).Append('\n');
            sb.Append("Windows: ").Append(WindowsVersion()).Append(", ").Append(RuntimeInformation.ProcessArchitecture).Append(", .NET ").Append(Environment.Version).Append('\n');
            sb.Append("Where: ").Append(where).Append('\n');
            if (!string.IsNullOrWhiteSpace(message) && message != error?.ToString()) sb.Append("Message: ").Append(message.Trim()).Append('\n');
            sb.Append(error?.ToString() ?? "(no exception was given)").Append('\n');
            if (error is not null && error.HResult != 0) sb.Append("HResult: 0x").Append(error.HResult.ToString("X8")).Append('\n');
            if (error is null || string.IsNullOrEmpty(error.StackTrace))
            {
                // No stack: say where the latest exceptions were thrown, the same kind first, newest first.
                List<Thrown> recent;
                lock (Recent) recent = Recent.Reverse().ToList();
                var likely = recent.OrderByDescending(t => error is not null && t.HResult == error.HResult).ToList();
                if (likely.Count > 0)
                {
                    sb.Append("This error came without its stack. The latest exceptions thrown before it, and where (the same kind first):\n");
                    foreach (var t in likely)
                        sb.Append("- ").Append(t.At.ToString("HH:mm:ss.fff")).Append(" thread ").Append(t.Thread).Append(": ").Append(t.Type).Append(" (0x").Append(t.HResult.ToString("X8")).Append(") ").Append(t.Message.Trim()).Append('\n')
                          .Append(string.Join("\n", t.Stack.Split('\n').Select(l => "    " + l.Trim()))).Append('\n');
                }
            }
            lock (Gate)
            {
                var path = PathOf();
                File.AppendAllText(path, sb.ToString() + "\n");
                // Only the latest entries are kept.
                var info = new FileInfo(path);
                if (info.Length > MaxBytes)
                {
                    var text = File.ReadAllText(path);
                    var cut = text.IndexOf(Separator, text.Length - MaxBytes / 2, StringComparison.Ordinal);
                    File.WriteAllText(path, cut > 0 ? text[cut..] : text[^(MaxBytes / 2)..]);
                }
            }
            if (fatal) WindowsSettings.Local.Set(UnreadKey, now.ToString("o"));
            else if (mayClose) WindowsSettings.Local.Set(MaybeKey, now.ToString("o"));
        }
        catch (Exception) { /* nothing more can be done */ }
    }

    static string Version()
    {
        try { var v = Windows.ApplicationModel.Package.Current.Id.Version; return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"; }
        catch (Exception) { return "(unknown version)"; }
    }

    static string WindowsVersion()
    {
        try
        {
            ulong v = ulong.Parse(Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamilyVersion);
            return $"{(v >> 48) & 0xFFFF}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
        }
        catch (Exception) { return Environment.OSVersion.VersionString; }
    }

    /// <summary>True when the app closed because of an error since the user last looked.</summary>
    public static bool HasUnreadCrash => WindowsSettings.Local.Get(UnreadKey) is { Length: > 0 } || WindowsSettings.Local.Get(MaybeKey) is { Length: > 0 };

    public static void MarkRead()
    {
        WindowsSettings.Local.Remove(UnreadKey);
        WindowsSettings.Local.Remove(MaybeKey);
    }

    /// <summary>Whether there is anything in the log.</summary>
    public static bool HasEntries
    {
        get { try { return File.Exists(PathOf()) && new FileInfo(PathOf()).Length > 0; } catch (Exception) { return false; } }
    }

    /// <summary>The latest entries, ready to paste into a bug report or a chat.</summary>
    public static string Report()
    {
        string text;
        try { lock (Gate) text = File.Exists(PathOf()) ? File.ReadAllText(PathOf()) : ""; }
        catch (Exception ex) { return "Couldn't read the crash log: " + ex.Message; }
        var entries = text.Split(Separator, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        if (entries.Count == 0) return "";
        var latest = entries.Skip(Math.Max(0, entries.Count - EntriesCopied)).ToList();
        var sb = new StringBuilder();
        sb.Append($"Longhand crash report: the latest {latest.Count} of {entries.Count} entries, oldest first.\n\n```\n");
        foreach (var e in latest) sb.Append(Separator).Append(e).Append("\n\n");
        sb.Append("```\n");
        return sb.ToString();
    }

    /// <summary>Puts <see cref="Report"/> on the clipboard; false when there is nothing to copy.</summary>
    public static bool CopyReport()
    {
        var report = Report();
        if (report.Length == 0) return false;
        var package = new DataPackage();
        package.SetText(report);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return true;
    }

    public static void Clear()
    {
        try { lock (Gate) File.Delete(PathOf()); } catch (Exception) { }
        MarkRead();
    }
}
