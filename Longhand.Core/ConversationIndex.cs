using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Longhand.Services;

/// <summary>
/// One conversation in the list. <c>AutoTitle</c> comes from the first exchange; a name the user gave wins over it.
/// <c>Model</c> is the Claude model this conversation is answered by (null for one saved before each had its own).
/// <c>Archived</c> ones are kept, and can still be opened, but are listed apart so they don't clutter the list.
/// </summary>
public sealed record Conversation(string Id, string AutoTitle, string CustomTitle, DateTimeOffset Updated, bool Blank, string? Model = null, bool Archived = false)
{
    [JsonIgnore]
    public string Title => CustomTitle.Length > 0 ? CustomTitle : AutoTitle.Length > 0 ? AutoTitle : "New conversation";
}

/// <summary>
/// The list of conversations and which one is open. Pure bookkeeping, so it can be checked without Windows; the pages
/// themselves live in one folder per conversation (see ConversationStore).
/// </summary>
public sealed class ConversationIndex
{
    public const int MaxTitle = 48;

    public List<Conversation> Items { get; set; } = new();
    public string? CurrentId { get; set; }

    public static ConversationIndex Parse(string? json)
    {
        ConversationIndex index;
        try { index = string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<ConversationIndex>(json) ?? new(); }
        catch (JsonException) { index = new(); }

        // A hand-edited or damaged file shouldn't be able to break the list.
        var seen = new HashSet<string>();
        index.Items = (index.Items ?? new())
            .Where(c => !string.IsNullOrEmpty(c.Id) && seen.Add(c.Id))
            .Select(c => c with { AutoTitle = c.AutoTitle ?? "", CustomTitle = c.CustomTitle ?? "" })
            .ToList();
        if (index.Find(index.CurrentId) is null) index.CurrentId = index.NextToOpen();
        return index;
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public Conversation? Find(string? id) => id is null ? null : Items.FirstOrDefault(c => c.Id == id);

    public IEnumerable<Conversation> Newest() => Items.OrderByDescending(c => c.Updated);

    /// <summary>The conversations in the main list, newest first.</summary>
    public IEnumerable<Conversation> Active() => Newest().Where(c => !c.Archived);

    /// <summary>The archived conversations, newest first.</summary>
    public IEnumerable<Conversation> Archived() => Newest().Where(c => c.Archived);

    /// <summary>What to open when the open one goes away: the newest in the main list, else the newest archived one.</summary>
    string? NextToOpen() => (Active().FirstOrDefault() ?? Newest().FirstOrDefault())?.Id;

    /// <summary>Adds an empty conversation, answered by <paramref name="model"/>, and makes it the open one.</summary>
    public Conversation Create(DateTimeOffset now, string? model = null)
    {
        var created = new Conversation(Guid.NewGuid().ToString("N"), "", "", now, Blank: true, model);
        Items.Add(created);
        CurrentId = created.Id;
        return created;
    }

    void Change(string id, Func<Conversation, Conversation> change)
    {
        var i = Items.FindIndex(c => c.Id == id);
        if (i >= 0) Items[i] = change(Items[i]);
    }

    /// <summary>Records that the conversation was saved. <c>Blank</c> is true when nothing has been written or answered in it.</summary>
    public void Touch(string id, DateTimeOffset now, bool blank) => Change(id, c => c with { Updated = now, Blank = blank });

    /// <summary>Gives the conversation a title from the first thing Claude heard. Later exchanges never change it.</summary>
    public void SetAutoTitle(string id, string heard)
    {
        var title = TitleFrom(heard);
        if (title.Length > 0) Change(id, c => c.AutoTitle.Length > 0 ? c : c with { AutoTitle = title });
    }

    /// <summary>Sets the model the conversation is answered by.</summary>
    public void SetModel(string id, string model) => Change(id, c => c with { Model = ModelChoice.Resolve(model) });

    /// <summary>An empty name goes back to the automatic title.</summary>
    public void Rename(string id, string name) => Change(id, c => c with { CustomTitle = Collapse(name) });

    /// <returns>The conversation to open afterwards (the newest remaining, or null when none are left or another one stays open).</returns>
    public string? Delete(string id)
    {
        Items.RemoveAll(c => c.Id == id);
        if (CurrentId == id) CurrentId = NextToOpen();
        return CurrentId;
    }

    /// <summary>Moves a conversation to the archive, or back to the main list. It keeps its place in time.</summary>
    public void SetArchived(string id, bool archived) => Change(id, c => c with { Archived = archived });

    /// <returns>
    /// What to open after archiving the open conversation: the newest left in the main list, or null when it is empty (a new one
    /// should be started then).
    /// </returns>
    public string? NextAfterArchiving(string id) => Active().FirstOrDefault(c => c.Id != id)?.Id;

    /// <summary>Drops conversations nothing was written in, except the one being kept. Returns their ids.</summary>
    public List<string> RemoveBlank(string? keepId)
    {
        var blank = Items.Where(c => c.Blank && c.Id != keepId).Select(c => c.Id).ToList();
        Items.RemoveAll(c => blank.Contains(c.Id));
        return blank;
    }

    static string Collapse(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    /// <summary>
    /// The one-line transcription, made into a short title. Drawings are described in [brackets]: those are kept only when they
    /// are all there is.
    /// </summary>
    public static string TitleFrom(string heard)
    {
        var text = Collapse(Regex.Replace(heard ?? "", @"\[[^\]]*\]", " "));
        if (text.Length == 0) text = Collapse((heard ?? "").Replace("[", " ").Replace("]", " "));
        if (text.Length <= MaxTitle) return text;
        var cut = text.LastIndexOf(' ', MaxTitle);
        return text[..(cut > MaxTitle / 2 ? cut : MaxTitle)].TrimEnd(',', ';', ':', '.', '-') + "…";
    }

    /// <summary>"14:05" for today, "3 Nov" for earlier days.</summary>
    public static string When(DateTimeOffset updated, DateTimeOffset now)
    {
        var local = updated.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? local.ToString("t") : local.ToString("d MMM");
    }
}
