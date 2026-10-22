using System.Globalization;
using System.Text;

namespace Softwarp.Core;

/// <summary>Text that an agent, a gate or the forge wrote, made safe to print in a message: every control, format, line
/// separator and paragraph separator character (Unicode Cc, Cf, Zl, Zp), newline and tab included, is shown as \uXXXX, so
/// the text can neither drive the terminal, nor disguise itself with bidi or zero-width characters, nor start a line that
/// looks like softwarp's own.</summary>
public static class Untrusted
{
    /// <param name="keepNewlines">Keep raw newlines: only for text whose lines the factory wrote, as a last guard.</param>
    public static string Printable(string text, bool keepNewlines = false)
    {
        var shown = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (keepNewlines && rune.Value == '\n'
                || Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
                shown.Append(rune.ToString());
            else
                shown.Append(rune.IsBmp ? $"\\u{rune.Value:x4}" : $"\\U{rune.Value:x8}");
        }
        return shown.ToString();
    }
}
