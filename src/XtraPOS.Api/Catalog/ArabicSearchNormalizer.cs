using System.Globalization;
using System.Text;

namespace XtraPOS.Api.Catalog;

public static class ArabicSearchNormalizer
{
    public static string NormalizeStrict(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (character == '\u0640')
            {
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var mapped = character switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ی' => 'ي',
                'ک' => 'ك',
                _ => NormalizeDigit(character),
            };
            if (char.IsWhiteSpace(mapped))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(mapped);
        }

        return builder.ToString().Trim();
    }

    public static string NormalizeLoose(string? value)
    {
        var strict = NormalizeStrict(value);
        if (strict.Length == 0)
        {
            return "";
        }

        var words = strict.Split(' ');
        for (var index = 0; index < words.Length; index++)
        {
            var word = words[index];
            if (word.Length > 0 && word[^1] is 'ة' or 'ه')
            {
                words[index] = string.Concat(word.AsSpan(0, word.Length - 1), "ه");
            }
        }

        return string.Join(' ', words);
    }

    public static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(NormalizeDigit(character));
        }

        return builder.ToString();
    }

    private static char NormalizeDigit(char character) => character switch
    {
        >= '٠' and <= '٩' => (char)('0' + (character - '٠')),
        >= '۰' and <= '۹' => (char)('0' + (character - '۰')),
        _ => character,
    };
}
