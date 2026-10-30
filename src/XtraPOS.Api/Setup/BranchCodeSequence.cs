using System.Globalization;

namespace XtraPOS.Api.Setup;

public static class BranchCodeSequence
{
    public const string DuplicateMessage = "رمز الفرع مستخدم مسبقًا. اختر رمزًا آخر.";

    public static string Suggest(IEnumerable<string?> codes)
    {
        var used = new HashSet<int>();
        var width = 0;
        foreach (var code in codes)
        {
            if (!TryNumeric(code, out var value, out var digits))
            {
                continue;
            }

            if (value > 0)
            {
                used.Add(value);
            }

            if (digits.Length > 1 && digits[0] == '0')
            {
                width = Math.Max(width, digits.Length);
            }
        }

        var next = 1;
        while (used.Contains(next))
        {
            next++;
        }

        if (width == 0)
        {
            return used.Count == 0 ? "01" : next.ToString(CultureInfo.InvariantCulture);
        }

        var text = next.ToString(CultureInfo.InvariantCulture);
        return text.Length >= width ? text : text.PadLeft(width, '0');
    }

    public static bool IsDuplicate(IEnumerable<string?> codes, string? candidate)
    {
        var wanted = candidate?.Trim() ?? "";
        if (wanted.Length == 0)
        {
            return false;
        }

        foreach (var code in codes)
        {
            if (code is not null && string.Equals(code.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryNumeric(string? code, out int value, out string digits)
    {
        value = 0;
        digits = code?.Trim() ?? "";
        if (digits.Length == 0 || digits.Length > 9)
        {
            return false;
        }

        foreach (var character in digits)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
