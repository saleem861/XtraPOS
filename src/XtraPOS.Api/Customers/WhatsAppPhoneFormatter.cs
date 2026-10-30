namespace XtraPOS.Api.Customers;

public sealed record WhatsAppPhone(string LocalNumber, string E164, string CountryCode);

public static class WhatsAppPhoneFormatter
{
    public const string Palestine = "970";
    public const string Israel = "972";

    public static WhatsAppPhone Format(string? input, string? selectedCountryCode)
    {
        var digits = Digits(input);
        if (digits.Length == 0)
        {
            throw new CustomerMasterException("يرجى إدخال رقم الهاتف.");
        }

        if (HasInternationalPrefix(input) && TryInternational(digits, out var detected))
        {
            return detected;
        }

        var country = NormalizeCountry(selectedCountryCode) ?? Palestine;
        var national = digits.TrimStart('0');
        if (national.Length < 8 || national.Length > 12)
        {
            throw new CustomerMasterException("رقم الهاتف غير صالح لواتساب.");
        }

        return new WhatsAppPhone("0" + national, "+" + country + national, country);
    }

    public static string Mask(string e164)
    {
        if (e164.Length < 6)
        {
            return e164;
        }

        return e164[..6] + new string('*', Math.Max(0, e164.Length - 8)) + e164[^2..];
    }

    private static bool TryInternational(string digits, out WhatsAppPhone phone)
    {
        foreach (var country in CallingCodes.All.Select(country => country.Code).OrderByDescending(code => code.Length))
        {
            var body = digits.StartsWith("00" + country, StringComparison.Ordinal)
                ? digits[(2 + country.Length)..]
                : digits.StartsWith(country, StringComparison.Ordinal) && digits.Length > country.Length + 6
                    ? digits[country.Length..]
                    : null;
            if (body is null)
            {
                continue;
            }

            body = body.TrimStart('0');
            phone = new WhatsAppPhone("0" + body, "+" + country + body, country);
            return true;
        }

        phone = null!;
        return false;
    }

    private static string? NormalizeCountry(string? value)
    {
        var digits = Digits(value);
        if (digits.Length is >= 1 and <= 4 && CallingCodes.All.Any(country => country.Code == digits))
        {
            return digits;
        }

        return string.IsNullOrEmpty(digits) ? null : throw new CustomerMasterException("مقدمة الدولة غير معروفة.");
    }

    private static bool HasInternationalPrefix(string? input)
    {
        var trimmed = input?.Trim() ?? "";
        return trimmed.StartsWith('+') || trimmed.StartsWith("00", StringComparison.Ordinal);
    }

    private static string Digits(string? value)
    {
        return PhoneNormalizer.Normalize(value)?.TrimStart('+') ?? "";
    }
}
