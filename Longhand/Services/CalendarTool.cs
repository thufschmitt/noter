using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Models.Beta.Messages;

namespace Longhand.Services;

/// <summary>Lets Claude read the user's Nextcloud calendar. Read-only: nothing is created, changed or deleted.</summary>
public sealed class CalendarTool : IAgentTool
{
    public const string ToolName = "calendar_events";
    const int MaxDays = 93;

    readonly Nextcloud _nextcloud;
    readonly NextcloudAccount _account;
    readonly TimeZoneInfo _zone;
    readonly Func<DateTimeOffset> _now;

    public CalendarTool(Nextcloud nextcloud, NextcloudAccount account, TimeZoneInfo? zone = null, Func<DateTimeOffset>? now = null)
    {
        _nextcloud = nextcloud;
        _account = account;
        _zone = zone ?? TimeZoneInfo.Local;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string Name => ToolName;
    public string Status => "checking your calendar…";

    public BetaToolUnion Definition => new BetaTool
    {
        Name = ToolName,
        Description =
            "Reads the user's calendar from their Nextcloud server (read-only). Use it for questions about their schedule, availability, " +
            "upcoming or past events, or when something is on. Returns the events in the range, grouped by day, each with its time, " +
            "title, calendar and location. Times are in the user's own time zone. Ask for a short range (a day or a week) unless the " +
            "question needs more; the longest range is about three months.",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["start"] = Json("""{"type":"string","description":"First day or moment of the range: a date like 2026-11-10 or a date-time like 2026-11-10T14:00, in the user's local time."}"""),
                ["end"] = Json("""{"type":"string","description":"Last day or end of the range. A date alone (2026-11-12) includes that whole day; a date-time is the exclusive end. Defaults to a week after start."}"""),
                ["search"] = Json("""{"type":"string","description":"Optional: only events whose title, location or description contain this text."}"""),
            },
            Required = new List<string> { "start" },
        },
    };

    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public async Task<string> RunAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (!TryReadTime(input, "start", out var start, out _)) throw new InvalidOperationException("start must be a date like 2026-11-10 or a date-time like 2026-11-10T14:00.");

        DateTimeOffset end;
        if (input.ContainsKey("end"))
        {
            if (!TryReadTime(input, "end", out end, out var dateOnly)) throw new InvalidOperationException("end must be a date like 2026-11-12 or a date-time like 2026-11-12T18:00.");
            if (dateOnly) end = end.AddDays(1); // a date as the end means "through that day"
        }
        else end = start.AddDays(7);

        if (end <= start) throw new InvalidOperationException("end must be after start.");
        if (end - start > TimeSpan.FromDays(MaxDays)) end = start.AddDays(MaxDays);

        var search = input.TryGetValue("search", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var events = await _nextcloud.EventsAsync(_account, start, end, _zone, ct);
        return CalendarText.Agenda(events, start, end, _zone, search);
    }

    /// <summary>Times without an offset are the user's local time. A date alone sets <c>dateOnly</c>.</summary>
    bool TryReadTime(IReadOnlyDictionary<string, JsonElement> input, string key, out DateTimeOffset time, out bool dateOnly)
    {
        time = default;
        dateOnly = false;
        if (!input.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String) return false;
        var text = element.GetString()!.Trim();

        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            dateOnly = true;
            time = new DateTimeOffset(day, _zone.GetUtcOffset(day));
            return true;
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            // "AssumeLocal" means this PC's zone; redo the offset in the zone the tool was given.
            bool hasOffset = text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$");
            time = hasOffset ? parsed : new DateTimeOffset(DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified), _zone.GetUtcOffset(parsed.DateTime));
            return true;
        }
        return false;
    }
}
