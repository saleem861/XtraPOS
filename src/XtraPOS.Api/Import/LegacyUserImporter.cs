using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Import;

public sealed record LegacyEmployeeRequest(string Name, string RoleCode);

public enum LegacyImportOutcome
{
    Created,
    Updated,
    Rejected,
}

public sealed record LegacyUserImportItem(
    string RequestedName,
    LegacyImportOutcome Outcome,
    string? Message,
    string? EmployeeNumber,
    string? Username,
    string? DisplayName,
    string? FullName,
    string? RoleCode,
    bool BranchAccess,
    bool? MustChangePassword,
    string? TemporaryPassword);

public sealed record LegacyUserImportReport(
    IReadOnlyList<LegacyUserImportItem> Items,
    bool DevelopmentAdminReused,
    bool DevelopmentAdminRemainedSeparate);

public sealed partial class LegacyUserImporter(
    BranchDbContext db,
    PasswordService passwords,
    ILegacyEmployeeSource source,
    ILogger<LegacyUserImporter> logger)
{
    public static readonly IReadOnlyList<LegacyEmployeeRequest> RequestedEmployees =
    [
        new("سليم جيوسي", RoleCodes.Admin),
        new("طارق دعاس", RoleCodes.Supervisor),
        new("عبدالله ريان", RoleCodes.Supervisor),
    ];

    private static readonly HashSet<string> StandardRoleCodes = new(StringComparer.Ordinal)
    {
        RoleCodes.Admin,
        RoleCodes.Supervisor,
        RoleCodes.User,
        "Manager",
        "Cashier",
    };

    public async Task<LegacyUserImportReport> ImportAsync(CancellationToken cancellationToken)
    {
        var existingAdminIds = await db.UserRoles
            .Where(item => item.Role.RoleCode == RoleCodes.Admin)
            .Select(item => item.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var branches = await db.Branches
            .Where(item => item.IsActive)
            .Select(item => item.BranchId)
            .ToListAsync(cancellationToken);
        if (branches.Count != 1)
        {
            var message = branches.Count == 0
                ? "No active branch is configured."
                : "More than one active branch is configured.";
            return new LegacyUserImportReport(
                RequestedEmployees.Select(item => Rejected(item.Name, message)).ToList(),
                DevelopmentAdminReused: false,
                DevelopmentAdminRemainedSeparate: existingAdminIds.Count > 0);
        }

        var branchId = branches[0];
        IReadOnlyList<LegacyEmployeeRecord> legacyRows;
        try
        {
            legacyRows = await source.ReadAsync(cancellationToken);
        }
        catch (LegacyImportException exception)
        {
            return new LegacyUserImportReport(
                RequestedEmployees.Select(item => Rejected(item.Name, exception.Message)).ToList(),
                DevelopmentAdminReused: false,
                DevelopmentAdminRemainedSeparate: existingAdminIds.Count > 0);
        }

        var employeeNumbersBefore = await db.Users
            .Where(user => user.EmployeeNumber != null)
            .Select(user => new { user.UserId, user.EmployeeNumber })
            .ToListAsync(cancellationToken);

        var items = new List<LegacyUserImportItem>();
        Guid? salimUserId = null;
        foreach (var request in RequestedEmployees)
        {
            var item = await ImportOneAsync(request, legacyRows, branchId, cancellationToken);
            items.Add(item);
            if (request.Name == "سليم جيوسي" && item.Outcome != LegacyImportOutcome.Rejected)
            {
                salimUserId = await db.Users
                    .Where(user => user.EmployeeNumber == item.EmployeeNumber)
                    .Select(user => (Guid?)user.UserId)
                    .SingleOrDefaultAsync(cancellationToken);
            }

            db.ChangeTracker.Clear();
        }

        var salimAlreadyImported = salimUserId is Guid importedId
            && employeeNumbersBefore.Any(user =>
                user.UserId == importedId
                && user.EmployeeNumber == items.Single(item => item.RequestedName == "سليم جيوسي").EmployeeNumber);
        var reused = salimUserId is not null
            && existingAdminIds.Contains(salimUserId.Value)
            && !salimAlreadyImported;
        var remained = existingAdminIds.Any(id => id != salimUserId);
        return new LegacyUserImportReport(items, reused, remained);
    }

    private async Task<LegacyUserImportItem> ImportOneAsync(
        LegacyEmployeeRequest request,
        IReadOnlyList<LegacyEmployeeRecord> legacyRows,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        var requested = Canonical(request.Name);
        var matches = legacyRows
            .Where(row => Canonical(row.UserShortName) == requested || Canonical(row.UserFullName) == requested)
            .ToList();
        if (matches.Count == 0)
        {
            return Rejected(request.Name, "No legacy row matches.");
        }

        if (matches.Count > 1)
        {
            return Rejected(request.Name, "More than one legacy row matches.");
        }

        var legacy = matches[0];
        var employeeNumber = Canonical(legacy.IdNumber);
        if (employeeNumber.Length == 0 || employeeNumber.Length > 50)
        {
            return Rejected(request.Name, "The legacy row has no usable IDNumber.");
        }

        var username = Canonical(legacy.UserShortName);
        if (!IsPreservableUsername(username))
        {
            return Rejected(request.Name, "UserShortName is not a suitable username.");
        }

        var displayName = username;
        var fullName = Canonical(legacy.UserFullName);
        if (displayName.Length > 150 || fullName.Length > 300)
        {
            return Rejected(request.Name, "The legacy name is longer than the user columns allow.");
        }

        var normalizedUsername = AccountIdentity.NormalizeUsername(username);
        var role = await db.Roles.SingleOrDefaultAsync(
            item => item.RoleCode == request.RoleCode && item.IsActive,
            cancellationToken);
        if (role is null)
        {
            return Rejected(request.Name, "The target role is not available.");
        }

        var identityMatches = await FindIdentityMatchesAsync(
            employeeNumber,
            fullName,
            displayName,
            cancellationToken);
        if (identityMatches.Count > 1)
        {
            return Rejected(request.Name, "More than one existing user matches this employee.");
        }

        var usernameOwner = await db.Users.SingleOrDefaultAsync(
            item => item.NormalizedUsername == normalizedUsername,
            cancellationToken);
        if (identityMatches.Count == 0 && usernameOwner is not null)
        {
            return Rejected(request.Name, "Username conflicts with an unrelated user.");
        }

        if (identityMatches.Count == 1
            && usernameOwner is not null
            && usernameOwner.UserId != identityMatches[0].UserId
            && !string.Equals(identityMatches[0].NormalizedUsername, normalizedUsername, StringComparison.Ordinal))
        {
            return Rejected(request.Name, "Username conflicts with an unrelated user.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = UtcNowSeconds();
            AppUser user;
            string? temporaryPassword = null;
            var created = false;
            if (identityMatches.Count == 1)
            {
                user = identityMatches[0];
                user.EmployeeNumber = employeeNumber;
                user.DisplayName = displayName;
                user.FullName = string.IsNullOrEmpty(fullName) ? null : fullName;
                user.UpdatedAt = now;
            }
            else
            {
                temporaryPassword = CreateTemporaryPassword();
                user = new AppUser
                {
                    Username = username,
                    NormalizedUsername = normalizedUsername,
                    DisplayName = displayName,
                    FullName = string.IsNullOrEmpty(fullName) ? null : fullName,
                    EmployeeNumber = employeeNumber,
                    IsActive = true,
                    MustChangePassword = true,
                    FailedLoginCount = 0,
                    CreatedAt = now,
                };
                user.PasswordHash = passwords.Hash(user, temporaryPassword);
                if (string.Equals(user.PasswordHash, temporaryPassword, StringComparison.Ordinal)
                    || passwords.Verify(user, user.PasswordHash, temporaryPassword) == PasswordVerificationResult.Failed)
                {
                    throw new InvalidOperationException("Temporary password was not hashed.");
                }

                db.Users.Add(user);
                created = true;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (user.UserId == Guid.Empty)
            {
                throw new InvalidOperationException("UserID was not generated.");
            }

            await EnsureRoleAsync(user.UserId, role.RoleId, now, cancellationToken);
            await EnsureBranchAccessAsync(user.UserId, branchId, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Legacy employee import {Outcome} for employee {EmployeeNumber}.",
                created ? "created" : "updated",
                employeeNumber);

            return new LegacyUserImportItem(
                request.Name,
                created ? LegacyImportOutcome.Created : LegacyImportOutcome.Updated,
                null,
                employeeNumber,
                user.Username,
                user.DisplayName,
                user.FullName,
                request.RoleCode,
                BranchAccess: true,
                user.MustChangePassword,
                created ? temporaryPassword : null);
        }
        catch (DbUpdateException)
        {
            await SafeRollbackAsync(transaction, cancellationToken);
            db.ChangeTracker.Clear();
            return Rejected(request.Name, "The user could not be saved without creating a duplicate.");
        }
        catch
        {
            await SafeRollbackAsync(transaction, cancellationToken);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<List<AppUser>> FindIdentityMatchesAsync(
        string employeeNumber,
        string fullName,
        string displayName,
        CancellationToken cancellationToken)
    {
        var users = await db.Users.ToListAsync(cancellationToken);
        return users
            .Where(user =>
                string.Equals(Canonical(user.EmployeeNumber), employeeNumber, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(fullName)
                    && string.Equals(Canonical(user.FullName), fullName, StringComparison.Ordinal))
                || string.Equals(Canonical(user.DisplayName), displayName, StringComparison.Ordinal))
            .GroupBy(user => user.UserId)
            .Select(group => group.First())
            .ToList();
    }

    private async Task EnsureRoleAsync(
        Guid userId,
        Guid roleId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var assignments = await db.UserRoles
            .Include(item => item.Role)
            .Where(item => item.UserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var assignment in assignments)
        {
            if (assignment.RoleId != roleId && StandardRoleCodes.Contains(assignment.Role.RoleCode))
            {
                db.UserRoles.Remove(assignment);
            }
        }

        if (assignments.All(item => item.RoleId != roleId))
        {
            db.UserRoles.Add(new UserRole
            {
                UserId = userId,
                RoleId = roleId,
                AssignedAt = now,
            });
        }
    }

    private async Task EnsureBranchAccessAsync(
        Guid userId,
        Guid branchId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var access = await db.UserBranchAccess.SingleOrDefaultAsync(
            item => item.UserId == userId && item.BranchId == branchId,
            cancellationToken);
        if (access is null)
        {
            db.UserBranchAccess.Add(new UserBranchAccess
            {
                UserId = userId,
                BranchId = branchId,
                IsActive = true,
                GrantedAt = now,
            });
            return;
        }

        access.IsActive = true;
    }

    private static LegacyUserImportItem Rejected(string name, string message)
    {
        return new LegacyUserImportItem(
            name,
            LegacyImportOutcome.Rejected,
            message,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            null);
    }

    internal static string Canonical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    internal static bool IsPreservableUsername(string username)
    {
        return PreservableUsernamePattern().IsMatch(username);
    }

    [GeneratedRegex(@"^(?=.{3,100}$)[\p{L}\p{N}._-]+(?: [\p{L}\p{N}._-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PreservableUsernamePattern();

    private static string CreateTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        Span<char> chars = stackalloc char[20];
        Span<byte> bytes = stackalloc byte[20];
        RandomNumberGenerator.Fill(bytes);
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = alphabet[bytes[index] % alphabet.Length];
        }

        return new string(chars);
    }

    private static DateTime UtcNowSeconds()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private static async Task SafeRollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (Exception)
        {
            // The transaction may already be completed.
        }
    }
}
