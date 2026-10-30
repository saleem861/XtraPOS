using Microsoft.AspNetCore.Identity;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Security;

public sealed class PasswordService
{
    private readonly PasswordHasher<AppUser> _hasher = new();

    public string Hash(AppUser user, string password)
    {
        return _hasher.HashPassword(user, password);
    }

    public PasswordVerificationResult Verify(AppUser user, string passwordHash, string password)
    {
        return _hasher.VerifyHashedPassword(user, passwordHash, password);
    }
}
