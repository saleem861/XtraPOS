using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Security;

public sealed record UserPasswordResetResult(bool Updated, int MatchCount, string Message, string? TemporaryPassword);

public static class UserPasswordResetCommand
{
    public const string CommandName = "--reset-user-password";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.Ordinal));

    public static string? ReadUserKey(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], CommandName, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    public static bool TryAuthorize(
        string? configuredDatabase,
        string? confirmedDatabase,
        string? userKey,
        out string denial)
    {
        if (string.IsNullOrWhiteSpace(configuredDatabase)
            || string.IsNullOrWhiteSpace(confirmedDatabase)
            || confirmedDatabase.Contains('*')
            || confirmedDatabase.Contains('%')
            || confirmedDatabase.Contains('?')
            || !string.Equals(confirmedDatabase, configuredDatabase, StringComparison.Ordinal)
            || !BranchDatabase.IsAllowedName(confirmedDatabase))
        {
            denial = "Password reset requires --confirm-database with the exact configured database name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(userKey)
            || userKey.Contains('*')
            || userKey.Contains('%')
            || userKey.Contains('?')
            || !string.Equals(userKey, userKey.Trim(), StringComparison.Ordinal))
        {
            denial = "Password reset requires one exact username or employee number.";
            return false;
        }

        denial = "";
        return true;
    }

    public static string CreateTemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%*-_";
        var alphabet = upper + lower + digits + symbols;
        var chars = new char[16];
        chars[0] = upper[RandomIndex(upper.Length)];
        chars[1] = lower[RandomIndex(lower.Length)];
        chars[2] = digits[RandomIndex(digits.Length)];
        chars[3] = symbols[RandomIndex(symbols.Length)];
        for (var index = 4; index < chars.Length; index++)
        {
            chars[index] = alphabet[RandomIndex(alphabet.Length)];
        }

        for (var index = chars.Length - 1; index > 0; index--)
        {
            var swap = RandomIndex(index + 1);
            (chars[index], chars[swap]) = (chars[swap], chars[index]);
        }

        return new string(chars);
    }

    public static async Task<UserPasswordResetResult> ExecuteAsync(
        BranchDbContext db,
        PasswordService passwords,
        string userKey,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var normalized = AccountIdentity.NormalizeUsername(userKey);
        var matches = await db.Users
            .Where(user => user.Username == userKey || user.NormalizedUsername == normalized || user.EmployeeNumber == userKey)
            .ToListAsync(cancellationToken);
        if (matches.Count != 1)
        {
            var message = $"Refusing password reset because {matches.Count} users match '{userKey}'.";
            await output.WriteLineAsync(message);
            return new UserPasswordResetResult(false, matches.Count, message, null);
        }

        var user = matches[0];
        var temporaryPassword = CreateTemporaryPassword();
        user.PasswordHash = passwords.Hash(user, temporaryPassword);
        if (passwords.Verify(user, user.PasswordHash, temporaryPassword) == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
        {
            var message = "Password reset did not verify for the single matching user.";
            await output.WriteLineAsync(message);
            return new UserPasswordResetResult(false, 1, message, null);
        }

        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        user.UpdatedAt = DateTime.UtcNow;
        var updated = await db.SaveChangesAsync(cancellationToken);
        if (updated != 1)
        {
            var message = "Password reset did not update exactly one user.";
            await output.WriteLineAsync(message);
            return new UserPasswordResetResult(false, 1, message, null);
        }

        await output.WriteLineAsync($"Password reset updated 1 user: {user.Username}.");
        return new UserPasswordResetResult(true, 1, "Password reset updated 1 user.", temporaryPassword);
    }

    private static int RandomIndex(int length)
    {
        return RandomNumberGenerator.GetInt32(length);
    }
}
