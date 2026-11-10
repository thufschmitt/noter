using System;
using System.IO;
using System.Threading;

namespace Longhand.Web;

/// <summary>
/// For development: watches the page's files on disk and tells every open tab to reload when one changes. Only used when the
/// server was started with <c>--www &lt;folder&gt;</c>, which also makes it serve the page from that folder instead of from inside
/// the program.
/// </summary>
public sealed class DevReload : IDisposable
{
    readonly FileSystemWatcher _watcher;
    readonly Timer _debounce;

    public event Action? Changed;

    public DevReload(string folder)
    {
        _debounce = new Timer(_ => Changed?.Invoke());
        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        // Editors save in bursts (a temp file, a rename, a touch), so wait for things to settle before telling the tabs.
        void Bump(object? s, FileSystemEventArgs e) => _debounce.Change(150, Timeout.Infinite);
        _watcher.Changed += Bump;
        _watcher.Created += Bump;
        _watcher.Deleted += Bump;
        _watcher.Renamed += (s, e) => Bump(s, e);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
