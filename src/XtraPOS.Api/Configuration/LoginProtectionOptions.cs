namespace XtraPOS.Api.Configuration;

public sealed class LoginProtectionOptions
{
    public const string SectionName = "LoginProtection";

    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    public int ResolvedMaxFailedAttempts => MaxFailedAttempts < 1 ? 5 : MaxFailedAttempts;

    public int ResolvedLockoutMinutes => LockoutMinutes < 1 ? 15 : LockoutMinutes;
}
