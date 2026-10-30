using System.Security.Cryptography;
using System.Text;

namespace XtraPOS.Api.Devices;

public sealed class DevicePairingService
{
    public const string InvalidCodeMessage = "رمز الربط غير صالح أو انتهت صلاحيته.";
    public const string MissingTokenMessage = "يجب التحقق من رمز ربط الجهاز قبل تسجيله.";
    public const string ForbiddenStationMessage = "لا توجد صلاحية لإنشاء نقطة بيع.";

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly object _gate = new();
    private readonly Dictionary<string, CodeGrant> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenGrant> _tokens = new(StringComparer.Ordinal);

    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public IssuedPairingCode Issue(Guid branchId, bool canCreateStation)
    {
        if (branchId == Guid.Empty)
        {
            throw new PairingRejectedException(InvalidCodeMessage);
        }

        lock (_gate)
        {
            var now = UtcNow();
            var code = NewCode();
            var hash = Fingerprint(code);
            while (_codes.ContainsKey(hash))
            {
                code = NewCode();
                hash = Fingerprint(code);
            }

            var expiresAt = now.Add(CodeLifetime);
            _codes[hash] = new CodeGrant(branchId, canCreateStation, expiresAt);
            return new IssuedPairingCode(code, expiresAt);
        }
    }

    public VerifiedPairing Verify(Guid branchId, string? code)
    {
        var normalized = (code ?? "").Trim();
        if (branchId == Guid.Empty || normalized.Length == 0)
        {
            throw new PairingRejectedException(InvalidCodeMessage);
        }

        lock (_gate)
        {
            var hash = Fingerprint(normalized);
            if (!_codes.TryGetValue(hash, out var grant))
            {
                throw new PairingRejectedException(InvalidCodeMessage);
            }

            if (grant.Used || grant.ExpiresAt <= UtcNow() || grant.BranchId != branchId)
            {
                grant.Used = true;
                throw new PairingRejectedException(InvalidCodeMessage);
            }

            grant.Used = true;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expiresAt = UtcNow().Add(TokenLifetime);
            _tokens[Fingerprint(token)] = new TokenGrant(grant.BranchId, grant.CanCreateStation, expiresAt);
            return new VerifiedPairing(token, expiresAt, grant.CanCreateStation);
        }
    }

    public ActivePairing Peek(string? token, Guid branchId)
    {
        return Read(token, branchId, consume: false);
    }

    public ActivePairing Consume(string? token, Guid branchId)
    {
        return Read(token, branchId, consume: true);
    }

    private ActivePairing Read(string? token, Guid branchId, bool consume)
    {
        var normalized = (token ?? "").Trim();
        if (normalized.Length == 0)
        {
            throw new PairingRejectedException(MissingTokenMessage);
        }

        lock (_gate)
        {
            if (!_tokens.TryGetValue(Fingerprint(normalized), out var grant)
                || grant.Consumed
                || grant.ExpiresAt <= UtcNow()
                || grant.BranchId != branchId)
            {
                if (grant is not null && grant.ExpiresAt <= UtcNow())
                {
                    grant.Consumed = true;
                }

                throw new PairingRejectedException(grant is null && normalized.Length == 0
                    ? MissingTokenMessage
                    : InvalidCodeMessage);
            }

            if (consume)
            {
                grant.Consumed = true;
            }

            return new ActivePairing(grant.BranchId, grant.CanCreateStation);
        }
    }

    private static string NewCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        var chars = new char[8];
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = Alphabet[bytes[index] % Alphabet.Length];
        }

        var code = new string(chars);
        if (code.Contains('=') || code.Contains(';') || code.Contains(' '))
        {
            throw new InvalidOperationException("Pairing code format is invalid.");
        }

        return code;
    }

    private static string Fingerprint(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed class CodeGrant(Guid branchId, bool canCreateStation, DateTime expiresAt)
    {
        public Guid BranchId { get; } = branchId;
        public bool CanCreateStation { get; } = canCreateStation;
        public DateTime ExpiresAt { get; } = expiresAt;
        public bool Used { get; set; }
    }

    private sealed class TokenGrant(Guid branchId, bool canCreateStation, DateTime expiresAt)
    {
        public Guid BranchId { get; } = branchId;
        public bool CanCreateStation { get; } = canCreateStation;
        public DateTime ExpiresAt { get; } = expiresAt;
        public bool Consumed { get; set; }
    }
}

public sealed record IssuedPairingCode(string Code, DateTime ExpiresAt);

public sealed record VerifiedPairing(string PairingToken, DateTime ExpiresAt, bool CanCreateStation);

public readonly record struct ActivePairing(Guid BranchId, bool CanCreateStation);

public sealed class PairingRejectedException(string message) : Exception(message);
