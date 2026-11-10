using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;

namespace Longhand.Services;

/// <summary>
/// Sends the user to the default browser to approve a sign-in, and waits for the browser to come back to the app through the
/// longhand:// address (see <c>callback.html</c> on the update site and the protocol entry in Package.appxmanifest).
/// </summary>
public static class OAuthBroker
{
    static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> Waiting = new();

    /// <summary>Opens the sign-in page and returns the query string the browser came back with.</summary>
    public static async Task<string> AuthorizeAsync(Uri authorizeUrl, string state, CancellationToken ct)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Waiting[state] = done;
        try
        {
            if (!await Launcher.LaunchUriAsync(authorizeUrl)) throw new InvalidOperationException("Couldn't open the browser.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using (timeout.Token.Register(() => done.TrySetCanceled(ct.IsCancellationRequested ? ct : default)))
                return await done.Task;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The sign-in wasn't finished in the browser in time.");
        }
        finally
        {
            Waiting.TryRemove(state, out _);
        }
    }

    /// <summary>Called when the app is opened through longhand://; hands the answer to the sign-in that is waiting for it.</summary>
    public static bool Deliver(Uri uri)
    {
        var query = uri.Query;
        return McpOAuth.ParseQuery(query).TryGetValue("state", out var state)
            && Waiting.TryGetValue(state, out var done)
            && done.TrySetResult(query);
    }
}
