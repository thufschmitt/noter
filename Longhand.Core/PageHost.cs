using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>
/// Everything the page needs from the screen it is shown on: the ink, the notes as drawn things, and a few messages. The
/// Windows app implements it over XAML; the web server implements it by talking to a browser.
/// </summary>
public interface IPageView
{
    // ----- the ink -----
    IReadOnlyList<StrokeSpan> Strokes();
    /// <summary>Moves every stroke whose top is at or below <paramref name="anchor"/> by <paramref name="delta"/>, except the ones in <paramref name="keep"/>.</summary>
    void ShiftStrokes(double anchor, double delta, IReadOnlySet<uint> keep);
    /// <returns>False when there is no such stroke any more.</returns>
    bool DeleteStroke(uint id);
    /// <summary>The points a stroke was drawn through, in page coordinates and drawing order; null when there is no such stroke.</summary>
    IReadOnlyList<(double X, double Y)>? StrokePoints(uint id);
    /// <summary>Removes a stroke and returns what <see cref="RestoreStrokes"/> needs to put it back; null when there is no such stroke.</summary>
    object? TakeStroke(uint id);
    /// <summary>Puts back strokes that were erased; <paramref name="token"/> is what <see cref="PageSession.StrokesErased"/> was given.</summary>
    void RestoreStrokes(object token);
    void ClearStrokes();

    // ----- the notes -----
    void NoteAdded(NoteModel note);
    /// <summary>The note's <see cref="NoteModel.Shown"/> text changed.</summary>
    void NoteChanged(NoteModel note);
    void NoteRemoved(NoteModel note);
    void DrawingAdded(NoteModel note, DrawingModel drawing);
    void DrawingsCleared(NoteModel note);
    /// <summary>The drawing's <see cref="DrawingModel.Markup"/> was replaced (by the model, or by undoing an edit): show it again. It will be measured next.</summary>
    void DrawingChanged(NoteModel note, DrawingModel drawing);
    /// <summary>Whether the user can select, move, turn and delete the editable parts of a drawing here. The model is told, so it doesn't offer what can't be done.</summary>
    bool CanEdit { get; }
    /// <summary>Works out how many rule lines the note's words and each drawing take (sets <see cref="NoteModel.Lines"/> and <see cref="DrawingModel.Lines"/>).</summary>
    Task MeasureAsync(NoteModel note);
    /// <summary>Puts the note's pieces where its <see cref="NoteModel.Y"/> says.</summary>
    void Place(NoteModel note);
    /// <summary>Something on the page grew or moved, so the sheet may need to be taller.</summary>
    void ContentChanged();
    void EnsureVisible(double bottom);
    /// <summary>A conversation has just been put on the page.</summary>
    void PageShown();

    /// <summary>
    /// The picture Claude reads: the strokes with these ids in black on white. With a context, what they were written over too:
    /// the earlier ink in grey and the replies in blue.
    /// </summary>
    Task<byte[]> RenderSnapshotAsync(IReadOnlyList<uint> strokeIds, SnapshotContext? context);

    // ----- messages -----
    /// <summary>Asks for the API key; null when the user doesn't give one.</summary>
    /// <summary>Asks the user to paste their ChatGPT sign-in (the Codex CLI's auth.json), stores it and returns it; null if they don't.</summary>
    Task<CodexSignIn?> RequestSignInAsync();
    void Toast(string message, double seconds = 3.5);
    /// <summary>A typed note couldn't be sent, so the words go back where they were typed.</summary>
    void RestoreTyped(string text);
    void StateChanged(PageState state);
}

/// <summary>What one Ask needs, gathered fresh each time so changes to settings and connected apps take effect at once.</summary>
/// <param name="Unavailable">Connected apps left out of this answer: their sign-in couldn't be renewed, or they couldn't be reached.</param>
public sealed record PreparedAsk(CodexService Model, IReadOnlyList<LegendEntry> Legend, IReadOnlyList<string>? Unavailable = null);

/// <summary>Where the conversations and settings live: Windows storage in the app, a data folder on the server.</summary>
public interface IPageData
{
    /// <summary>The stored ChatGPT sign-in, or null when there is none.</summary>
    CodexSignIn? SignIn { get; }
    /// <summary>The model a new conversation starts with: the one last chosen.</summary>
    string DefaultModel { get; set; }
    Task<ConversationIndex> LoadIndexAsync();
    Task SaveIndexAsync(ConversationIndex index);
    /// <summary>Loads a conversation's notes, and puts its ink on the page (the platform keeps the ink). Null when nothing is saved yet.</summary>
    Task<List<SavedNote>?> LoadPageAsync(string id);
    Task SavePageAsync(string id, IReadOnlyList<SavedNote> notes);
    Task DeletePageAsync(string id);
    /// <param name="model">The model the open conversation is answered by.</param>
    Task<PreparedAsk> PrepareAskAsync(CodexSignIn signIn, string model, CancellationToken ct);
}
