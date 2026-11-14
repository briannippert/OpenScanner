using System.Buffers.Text;
using System.Text;

namespace OpenScanner.Server.Models;

/// <summary>
/// Filters and paging position for <c>GET /api/transmissions</c>. Every filter is optional;
/// they combine with AND. Timestamps are round-trip ("o") UTC strings, matching how
/// transmissions are stored, so they compare lexicographically.
/// </summary>
public record TransmissionQuery
{
    /// <summary>Maximum rows to return.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Return only rows older than this position (from a previous page).</summary>
    public TransmissionCursor? Cursor { get; init; }

    /// <summary>Inclusive lower bound on timestamp.</summary>
    public string? SinceUtc { get; init; }

    /// <summary>Exclusive upper bound on timestamp.</summary>
    public string? UntilUtc { get; init; }

    /// <summary>Exact channel alpha tag.</summary>
    public string? Channel { get; init; }

    /// <summary>Frequency in MHz, matched to within half a kHz.</summary>
    public double? Frequency { get; init; }

    /// <summary>Text matched against transcription, description, alpha tag and frequency.</summary>
    public string? Search { get; init; }

    /// <summary>Only starred transmissions.</summary>
    public bool FavoritesOnly { get; init; }

    /// <summary>Only fire tone-outs: a detected tone, or referenced by a TONE_OUT event.</summary>
    public bool TonesOnly { get; init; }

    /// <summary>Only transmissions the AI analysis classed as incidents.</summary>
    public bool IncidentsOnly { get; init; }

    /// <summary>
    /// Fetch exactly these ids (newest first). When set, the cursor and the other
    /// filters are ignored and no next page is returned.
    /// </summary>
    public IReadOnlyList<string>? Ids { get; init; }
}

/// <summary>One page of transmissions, newest first.</summary>
public class TransmissionPage
{
    /// <summary>The transmissions on this page.</summary>
    public List<CallLog> Items { get; set; } = new();

    /// <summary>Pass back as <c>cursor</c> to get the next (older) page; null on the last page.</summary>
    public string? NextCursor { get; set; }
}

/// <summary>
/// Keyset position in the (timestamp DESC, id DESC) ordering. Encoded as base64url so
/// clients treat it as opaque; rows inserted or deleted meanwhile don't shift pages.
/// </summary>
public readonly record struct TransmissionCursor(string Timestamp, string Id)
{
    /// <summary>The cursor that continues after <paramref name="log"/>.</summary>
    public static TransmissionCursor After(CallLog log) => new(log.Timestamp, log.Id);

    /// <summary>Opaque string form for clients.</summary>
    public string Encode() => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{Timestamp}|{Id}"));

    /// <summary>Parses a value produced by <see cref="Encode"/>; false if it is malformed.</summary>
    public static bool TryDecode(string? value, out TransmissionCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
            var sep = text.IndexOf('|');
            if (sep <= 0 || sep == text.Length - 1) return false;
            cursor = new TransmissionCursor(text[..sep], text[(sep + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
