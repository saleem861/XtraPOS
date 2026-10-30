using System.Security.Cryptography;
using Xunit;
using System.Text;
using System.Text.Json;
using XtraPOS.Api.Customers;

namespace XtraPOS.Api.Tests;

public class PasskeyCeremonyTests
{
    private const string RpId = "test-pos.sharbelfashion.com";
    private static readonly HashSet<string> Origins = ["https://test-pos.sharbelfashion.com"];

    [Fact]
    public void Registration_and_later_assertion_verify_for_the_same_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challenge = PasskeyCeremony.CreateChallenge();
        var credentialId = RandomNumberGenerator.GetBytes(16);
        var registration = Client("webauthn.create", challenge);
        var attestation = Attestation(key, credentialId, flags: 0x45);
        var registered = PasskeyCeremony.ReadRegistration(registration, attestation, PasskeyCeremony.Encode(challenge), RpId, Origins);

        var auth = Authenticator(flags: 0x05);
        var assertion = Client("webauthn.get", challenge);
        var signature = Sign(key, auth, assertion);
        PasskeyCeremony.VerifyAssertion(assertion, auth, signature, registered.X, registered.Y, PasskeyCeremony.Encode(challenge), RpId, Origins);
    }

    [Fact]
    public void Rejects_bad_signature_origin_rp_and_challenge()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challenge = PasskeyCeremony.CreateChallenge();
        var registered = PasskeyCeremony.ReadRegistration(Client("webauthn.create", challenge), Attestation(key, RandomNumberGenerator.GetBytes(16), 0x45), PasskeyCeremony.Encode(challenge), RpId, Origins);
        var auth = Authenticator(0x05);
        var assertion = Client("webauthn.get", challenge);
        var signature = Sign(key, auth, assertion);
        Assert.Throws<PasskeyRejectedException>(() => PasskeyCeremony.VerifyAssertion(assertion, auth, [0, 1, 2], registered.X, registered.Y, PasskeyCeremony.Encode(challenge), RpId, Origins));
        Assert.Equal("BAD_ORIGIN", Assert.Throws<PasskeyRejectedException>(() => PasskeyCeremony.VerifyAssertion(assertion.Replace("https://test-pos", "https://evil"), auth, signature, registered.X, registered.Y, PasskeyCeremony.Encode(challenge), RpId, Origins)).Code);
        Assert.Equal("BAD_RP", Assert.Throws<PasskeyRejectedException>(() => PasskeyCeremony.VerifyAssertion(assertion, auth, signature, registered.X, registered.Y, PasskeyCeremony.Encode(challenge), "pos.sharbelfashion.com", Origins)).Code);
        Assert.Equal("BAD_CHALLENGE", Assert.Throws<PasskeyRejectedException>(() => PasskeyCeremony.VerifyAssertion(assertion, auth, signature, registered.X, registered.Y, PasskeyCeremony.Encode(PasskeyCeremony.CreateChallenge()), RpId, Origins)).Code);
        Assert.Equal("NO_USER_VERIFICATION", Assert.Throws<PasskeyRejectedException>(() => PasskeyCeremony.ReadRegistration(Client("webauthn.create", challenge), Attestation(key, RandomNumberGenerator.GetBytes(16), 0x41), PasskeyCeremony.Encode(challenge), RpId, Origins)).Code);
    }

    private static string Client(string type, byte[] challenge) =>
        JsonSerializer.Serialize(new { type, challenge = PasskeyCeremony.Encode(challenge), origin = Origins.Single() });

    private static byte[] Authenticator(byte flags)
    {
        var data = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(RpId)).CopyTo(data, 0);
        data[32] = flags;
        return data;
    }

    private static string Attestation(ECDsa key, byte[] credentialId, byte flags)
    {
        var parameters = key.ExportParameters(false);
        var coseWriter = new System.Formats.Cbor.CborWriter();
        coseWriter.WriteStartMap(5);
        coseWriter.WriteInt32(1); coseWriter.WriteInt32(2);
        coseWriter.WriteInt32(3); coseWriter.WriteInt32(-7);
        coseWriter.WriteInt32(-1); coseWriter.WriteInt32(1);
        coseWriter.WriteInt32(-2); coseWriter.WriteByteString(parameters.Q.X);
        coseWriter.WriteInt32(-3); coseWriter.WriteByteString(parameters.Q.Y);
        coseWriter.WriteEndMap();
        var cose = coseWriter.Encode();
        var auth = new byte[37 + 16 + 2 + credentialId.Length + cose.Length];
        SHA256.HashData(Encoding.UTF8.GetBytes(RpId)).CopyTo(auth, 0);
        auth[32] = flags;
        var offset = 37 + 16;
        auth[offset] = (byte)(credentialId.Length >> 8);
        auth[offset + 1] = (byte)credentialId.Length;
        credentialId.CopyTo(auth.AsSpan(offset + 2));
        cose.CopyTo(auth.AsSpan(offset + 2 + credentialId.Length));
        var writer = new System.Formats.Cbor.CborWriter();
        writer.WriteStartMap(3);
        writer.WriteTextString("fmt"); writer.WriteTextString("none");
        writer.WriteTextString("attStmt"); writer.WriteStartMap(0); writer.WriteEndMap();
        writer.WriteTextString("authData"); writer.WriteByteString(auth);
        writer.WriteEndMap();
        return PasskeyCeremony.Encode(writer.Encode());
    }

    private static byte[] Sign(ECDsa key, byte[] authenticatorData, string clientData)
    {
        var signed = new byte[authenticatorData.Length + 32];
        authenticatorData.CopyTo(signed, 0);
        SHA256.HashData(Encoding.UTF8.GetBytes(clientData)).CopyTo(signed, authenticatorData.Length);
        return key.SignData(signed, HashAlgorithmName.SHA256);
    }
}
