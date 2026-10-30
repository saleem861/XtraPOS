using System.Text;

namespace XtraPOS.Api.Customers;

public static class PhoneNormalizer
{
    public static string? Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var builder = new StringBuilder(phone.Length);
        foreach (var character in phone.Trim())
        {
            if (char.IsWhiteSpace(character) || IsRemovedSeparator(character))
            {
                continue;
            }

            builder.Append(MapDigit(character));
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    public static bool IsWalkInPlaceholder(string? normalizedPhone)
    {
        return string.Equals(normalizedPhone, "1", StringComparison.Ordinal);
    }

    private static bool IsRemovedSeparator(char character)
    {
        return character is '-' or '(' or ')' or '\u2010' or '\u2011' or '\u2013' or '\u2014';
    }

    private static char MapDigit(char character)
    {
        if (character is >= '\u0660' and <= '\u0669')
        {
            return (char)('0' + (character - '\u0660'));
        }

        if (character is >= '\u06F0' and <= '\u06F9')
        {
            return (char)('0' + (character - '\u06F0'));
        }

        return character;
    }
}
