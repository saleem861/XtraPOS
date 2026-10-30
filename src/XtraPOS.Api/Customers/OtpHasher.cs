using System.Security.Cryptography;

namespace XtraPOS.Api.Customers;

public static class OtpHasher
{
    public static string CreateCode() => CreateCode(PhoneVerificationCode.Length);

    public static string CreateCode(int length)
    {
        if (length != PhoneVerificationCode.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var upper = 1;
        for (var digit = 0; digit < length; digit++)
        {
            upper *= 10;
        }

        return RandomNumberGenerator.GetInt32(0, upper).ToString("D" + length);
    }

    public static string Hash(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(code, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public static bool Matches(string code, string stored)
    {
        var parts = stored.Split('.', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(code, salt, 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
