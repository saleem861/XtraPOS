using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XtraPOS.Api.Customers;

public sealed class PasskeyRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class PasskeyCeremony
{
    public static byte[] CreateChallenge() => RandomNumberGenerator.GetBytes(32);

    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    public static byte[] Hash(ReadOnlySpan<byte> value) => SHA256.HashData(value);

    public static RegistrationChecked ReadRegistration(string clientDataJson, string attestationObject, string challenge, string rpId, IReadOnlySet<string> origins)
    {
        var client = ReadClient(clientDataJson, "webauthn.create", challenge, origins);
        var authData = ReadAuthData(attestationObject);
        RequireAuthenticator(authData, rpId);
        var key = ReadCose(authData.CredentialPublicKey);
        return new RegistrationChecked(authData.CredentialId, key.X, key.Y, client.Origin);
    }

    public static void VerifyAssertion(string clientDataJson, byte[] authenticatorData, byte[] signature, byte[] publicX, byte[] publicY, string challenge, string rpId, IReadOnlySet<string> origins)
    {
        ReadClient(clientDataJson, "webauthn.get", challenge, origins);
        var auth = ParseAuth(authenticatorData);
        RequireAuthenticator(auth, rpId);
        var signed = new byte[authenticatorData.Length + 32];
        authenticatorData.CopyTo(signed, 0);
        SHA256.HashData(Encoding.UTF8.GetBytes(clientDataJson)).CopyTo(signed, authenticatorData.Length);
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicX, Y = publicY },
        });
        if (!ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256))
        {
            throw new PasskeyRejectedException("BAD_SIGNATURE", "توقيع Passkey غير صالح.");
        }
    }

    private static ClientBits ReadClient(string clientDataJson, string expectedType, string challenge, IReadOnlySet<string> origins)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(clientDataJson);
        }
        catch (JsonException)
        {
            throw new PasskeyRejectedException("BAD_CLIENT", "بيانات المتصفح غير صالحة.");
        }

        using (document)
        {
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            var origin = root.TryGetProperty("origin", out var originValue) ? originValue.GetString() : null;
            var rawChallenge = root.TryGetProperty("challenge", out var challengeValue) ? challengeValue.GetString() : null;
            if (!string.Equals(type, expectedType, StringComparison.Ordinal) || origin is null || rawChallenge is null)
            {
                throw new PasskeyRejectedException("BAD_CLIENT", "بيانات المتصفح غير مكتملة.");
            }

            if (!origins.Contains(origin))
            {
                throw new PasskeyRejectedException("BAD_ORIGIN", "مصدر الصفحة غير مقبول.");
            }

            byte[] presented;
            try
            {
                presented = Decode(rawChallenge);
            }
            catch (FormatException)
            {
                throw new PasskeyRejectedException("BAD_CHALLENGE", "تحدي التحقق غير صالح.");
            }

            var expected = Decode(challenge);
            if (!CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                throw new PasskeyRejectedException("BAD_CHALLENGE", "تحدي التحقق لا يطابق الطلب.");
            }

            return new ClientBits(origin);
        }
    }

    private static AuthBits ReadAuthData(string attestationObject)
    {
        byte[] cbor;
        try
        {
            cbor = Decode(attestationObject);
        }
        catch (FormatException)
        {
            throw new PasskeyRejectedException("BAD_ATTESTATION", "بيانات التسجيل غير صالحة.");
        }

        var reader = new System.Formats.Cbor.CborReader(cbor);
        var remaining = reader.ReadStartMap();
        if (remaining < 1)
        {
            throw new PasskeyRejectedException("BAD_ATTESTATION", "بيانات التسجيل غير مكتملة.");
        }

        byte[]? authData = null;
        for (var index = 0; index < remaining; index++)
        {
            var key = reader.ReadTextString();
            if (key == "authData")
            {
                authData = reader.ReadByteString();
            }
            else
            {
                reader.SkipValue();
            }
        }

        reader.ReadEndMap();
        if (authData is null)
        {
            throw new PasskeyRejectedException("BAD_ATTESTATION", "بيانات المصادق غير موجودة.");
        }

        return ParseAuth(authData);
    }

    private static AuthBits ParseAuth(byte[] authData)
    {
        if (authData.Length < 37)
        {
            throw new PasskeyRejectedException("BAD_AUTH", "بيانات المصادق قصيرة.");
        }

        var rpHash = authData.AsSpan(0, 32).ToArray();
        var flags = authData[32];
        var offset = 37;
        if ((flags & 0x40) == 0)
        {
            return new AuthBits(rpHash, flags, [], [], []);
        }

        if (authData.Length < offset + 18)
        {
            throw new PasskeyRejectedException("BAD_AUTH", "معرّف Passkey غير مكتمل.");
        }

        offset += 16;
        var credLen = (authData[offset] << 8) | authData[offset + 1];
        offset += 2;
        if (credLen < 1 || authData.Length < offset + credLen)
        {
            throw new PasskeyRejectedException("BAD_AUTH", "معرّف Passkey غير صالح.");
        }

        var credId = authData.AsSpan(offset, credLen).ToArray();
        offset += credLen;
        var cose = authData.AsSpan(offset).ToArray();
        return new AuthBits(rpHash, flags, credId, cose, authData);
    }

    private static void RequireAuthenticator(AuthBits auth, string rpId)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));
        if (!CryptographicOperations.FixedTimeEquals(auth.RpIdHash, expected))
        {
            throw new PasskeyRejectedException("BAD_RP", "معرّف الجهة غير مطابق.");
        }

        if ((auth.Flags & 0x01) == 0 || (auth.Flags & 0x04) == 0)
        {
            throw new PasskeyRejectedException("NO_USER_VERIFICATION", "يلزم تأكيد المستخدم على جهازه.");
        }
    }

    private static CoseKey ReadCose(byte[] cose)
    {
        var reader = new System.Formats.Cbor.CborReader(cose);
        var count = reader.ReadStartMap();
        byte[]? x = null;
        byte[]? y = null;
        int? kty = null;
        int? alg = null;
        int? crv = null;
        for (var index = 0; index < count; index++)
        {
            var key = reader.PeekState() == System.Formats.Cbor.CborReaderState.NegativeInteger
                ? (int)reader.ReadCborNegativeIntegerRepresentation() * -1 - 1
                : reader.ReadInt32();
            switch (key)
            {
                case 1: kty = reader.ReadInt32(); break;
                case 3: alg = reader.ReadInt32(); break;
                case -1: crv = reader.ReadInt32(); break;
                case -2: x = reader.ReadByteString(); break;
                case -3: y = reader.ReadByteString(); break;
                default: reader.SkipValue(); break;
            }
        }

        reader.ReadEndMap();
        if (kty != 2 || alg != -7 || crv != 1 || x is not { Length: 32 } || y is not { Length: 32 })
        {
            throw new PasskeyRejectedException("BAD_KEY", "مفتاح Passkey يجب أن يكون ES256.");
        }

        return new CoseKey(x, y);
    }

    private readonly record struct ClientBits(string Origin);
    private readonly record struct CoseKey(byte[] X, byte[] Y);

    private readonly record struct AuthBits(byte[] RpIdHash, byte Flags, byte[] CredentialId, byte[] CredentialPublicKey, byte[] Raw);

    public readonly record struct RegistrationChecked(byte[] CredentialId, byte[] X, byte[] Y, string Origin);
}
