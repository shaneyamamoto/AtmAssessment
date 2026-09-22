using System.Globalization;
using System.Text;
using Atm.Application;

namespace Atm.Infrastructure;

/// <summary>
/// Page cursors for transaction history.
///
/// A cursor holds the ledger position of the oldest entry on the page the client just
/// received; the next page is everything older than that. Clients should treat cursors as
/// opaque strings. Inside, a cursor is "v1:{position}" encoded as URL-safe base64. The
/// "v1:" prefix leaves room to change the format later without misreading old cursors.
/// </summary>
internal static class LedgerCursor
{
    private const string VersionPrefix = "v1:";

    public static string Encode(int ledgerPosition)
    {
        var text = VersionPrefix + ledgerPosition.ToString(CultureInfo.InvariantCulture);
        return ToUrlSafeBase64(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Returns the ledger position inside a cursor. Throws <see cref="InvalidCursorException"/> if it isn't one of ours.</summary>
    public static int Decode(string cursor)
    {
        if (!TryFromUrlSafeBase64(cursor, out var bytes))
        {
            throw new InvalidCursorException();
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (!text.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            throw new InvalidCursorException();
        }

        var positionText = text[VersionPrefix.Length..];

        // NumberStyles.None rejects signs, spaces and decimals: a position is a plain whole number.
        var isValidPosition = int.TryParse(positionText, NumberStyles.None, CultureInfo.InvariantCulture, out var position);
        if (!isValidPosition)
        {
            throw new InvalidCursorException();
        }

        return position;
    }

    /// <summary>
    /// Standard base64 uses '+', '/' and '=' padding, which all need escaping in a URL.
    /// The URL-safe variant swaps '+' for '-' and '/' for '_', and drops the padding.
    /// </summary>
    private static string ToUrlSafeBase64(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryFromUrlSafeBase64(string text, out byte[] bytes)
    {
        var standardBase64 = text.Replace('-', '+').Replace('_', '/');

        // Base64 text must be a multiple of 4 characters long; put back the padding we removed.
        var paddingNeeded = (4 - standardBase64.Length % 4) % 4;
        standardBase64 += new string('=', paddingNeeded);

        try
        {
            bytes = Convert.FromBase64String(standardBase64);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
