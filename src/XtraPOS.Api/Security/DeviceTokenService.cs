using System.Security.Cryptography;
using System.Text;

namespace XtraPOS.Api.Security;

public sealed class DeviceTokenService
{
    public const string DeviceTypePos = "POS";

    public static string Generate()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public static byte[] Hash(string token)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public static bool Matches(byte[]? storedHash, string token)
    {
        if (storedHash is null || storedHash.Length == 0 || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var actual = Hash(token);
        return storedHash.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(storedHash, actual);
    }
}
