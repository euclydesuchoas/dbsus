using System.Globalization;
using System.Text;

namespace DbSus.Client.Transport;

/// <summary>
/// Text that comes from the server (replies, and messages of FluentFTP that repeat them), made safe
/// to put in an exception message that may end up in a log or a terminal.
/// </summary>
internal static class ServerText
{
    /// <summary>
    /// The longest text kept. Replies of the DATASUS server (IIS FTP) and the messages of FluentFTP
    /// that repeat them are a few dozen to about 200 characters, so 512 keeps a whole reply for
    /// diagnosis and still bounds a hostile one.
    /// </summary>
    public const int MaxLength = 512;

    /// <summary>
    /// Escapes control characters (C0, DEL, C1), Unicode format characters (such as the
    /// bidirectional controls U+200E, U+200F, U+202A to U+202E and U+2066 to U+2069) and the line
    /// and paragraph separators (U+2028, U+2029), as <c>\xNN</c> up to U+00FF and <c>\uXXXX</c>
    /// above; and cuts the result at <see cref="MaxLength"/> characters, saying how many were left out.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var output = new StringBuilder(Math.Min(text!.Length, MaxLength) + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            string piece;
            if (IsUnsafe(c))
            {
                piece = c <= 0xFF
                    ? "\\x" + ((int)c).ToString("X2", CultureInfo.InvariantCulture)
                    : "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                piece = text.Substring(i, 2);
            }
            else if (char.IsSurrogate(c))
            {
                piece = "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture); // a lone half of a pair
            }
            else
            {
                piece = c.ToString();
            }

            if (output.Length + piece.Length > MaxLength)
            {
                output.Append("... (").Append((text.Length - i).ToString(CultureInfo.InvariantCulture)).Append(" more characters)");
                break;
            }

            output.Append(piece);
            i += piece.Length == 2 && char.IsHighSurrogate(c) ? 1 : 0;
        }

        return output.ToString();
    }

    private static bool IsUnsafe(char c) => CharUnicodeInfo.GetUnicodeCategory(c) is
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
