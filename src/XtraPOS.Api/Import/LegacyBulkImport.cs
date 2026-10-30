using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Import;

public static class LegacyImportReasons
{
    public const string MissingIdentity = "MissingIdentity";
    public const string DuplicateEmployeeNumber = "DuplicateEmployeeNumber";
    public const string UsernameConflict = "UsernameConflict";
    public const string AmbiguousExistingMatch = "AmbiguousExistingMatch";
    public const string SaveFailed = "SaveFailed";
}

public sealed record LegacyBulkImportItem(
    string? EmployeeNumber,
    string? UserShortName,
    string? UserFullName,
    string? Username,
    string? DisplayName,
    string? FullName,
    bool? IsActive,
    string? RoleCode,
    bool? BranchAccessActive,
    bool? MustChangePassword,
    string Outcome,
    string? Reason,
    string? TemporaryPassword);

public sealed record LegacyBulkImportReport(
    int Examined,
    int Matched,
    int Created,
    int Updated,
    int ActiveCreated,
    int InactiveCreated,
    int Skipped,
    int Failed,
    IReadOnlyList<LegacyBulkImportItem> Items);

public sealed partial class LegacyUserImporter
{
    public async Task<LegacyBulkImportReport> ImportRemainingAsync(CancellationToken cancellationToken)
    {
        var branches = await db.Branches
            .Where(item => item.IsActive)
            .Select(item => item.BranchId)
            .ToListAsync(cancellationToken);
        IReadOnlyList<LegacyEmployeeRecord> legacyRows;
        try
        {
            legacyRows = await source.ReadAsync(cancellationToken);
        }
        catch (LegacyImportException exception)
        {
            return EmptyFailure(exception.Message);
        }

        if (branches.Count != 1)
        {
            var message = branches.Count == 0
                ? "No active branch is configured."
                : "More than one active branch is configured.";
            return new LegacyBulkImportReport(
                legacyRows.Count,
                0,
                0,
                0,
                0,
                0,
                0,
                legacyRows.Count,
                legacyRows.Select(row => FailedRow(row, message)).ToList());
        }

        var branchId = branches[0];
        var userRole = await db.Roles.SingleOrDefaultAsync(
            item => item.RoleCode == RoleCodes.User && item.IsActive,
            cancellationToken);
        if (userRole is null)
        {
            return new LegacyBulkImportReport(
                legacyRows.Count,
                0,
                0,
                0,
                0,
                0,
                0,
                legacyRows.Count,
                legacyRows.Select(row => FailedRow(row, "The User role is not available.")).ToList());
        }

        var catalogRoles = await db.Roles
            .Where(item => item.IsActive && (item.RoleCode == RoleCodes.Admin || item.RoleCode == RoleCodes.Supervisor))
            .ToDictionaryAsync(item => item.RoleCode, item => item, StringComparer.Ordinal, cancellationToken);

        var duplicateEmployeeNumbers = legacyRows
            .Select(row => Canonical(row.IdNumber))
            .Where(number => number.Length > 0)
            .GroupBy(number => number, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var shortNameCounts = legacyRows
            .Select(row => Canonical(row.UserShortName))
            .Where(name => name.Length > 0)
            .GroupBy(name => name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var items = new List<LegacyBulkImportItem>();
        foreach (var row in legacyRows.OrderBy(item => Canonical(item.IdNumber), StringComparer.Ordinal))
        {
            items.Add(await ImportRemainingRowAsync(
                row,
                legacyRows,
                duplicateEmployeeNumbers,
                shortNameCounts,
                branchId,
                userRole,
                catalogRoles,
                cancellationToken));
            db.ChangeTracker.Clear();
        }

        return Summarize(legacyRows.Count, items);
    }

    private async Task<LegacyBulkImportItem> ImportRemainingRowAsync(
        LegacyEmployeeRecord row,
        IReadOnlyList<LegacyEmployeeRecord> legacyRows,
        HashSet<string> duplicateEmployeeNumbers,
        Dictionary<string, int> shortNameCounts,
        Guid branchId,
        Role userRole,
        IReadOnlyDictionary<string, Role> catalogRoles,
        CancellationToken cancellationToken)
    {
        var employeeNumber = Canonical(row.IdNumber);
        var shortName = Canonical(row.UserShortName);
        var fullName = BuildFullName(row);
        if (employeeNumber.Length == 0 || employeeNumber.Length > 50)
        {
            return Skipped(row, LegacyImportReasons.MissingIdentity);
        }

        if (duplicateEmployeeNumbers.Contains(employeeNumber))
        {
            return Skipped(row, LegacyImportReasons.DuplicateEmployeeNumber);
        }

        if (fullName.Length > 300)
        {
            return Skipped(row, LegacyImportReasons.MissingIdentity);
        }

        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken);
        var employeeMatches = users
            .Where(user => string.Equals(Canonical(user.EmployeeNumber), employeeNumber, StringComparison.Ordinal))
            .ToList();
        if (employeeMatches.Count > 1)
        {
            return Skipped(row, LegacyImportReasons.AmbiguousExistingMatch);
        }

        var authoritative = employeeMatches.Count == 1;
        AppUser? existing = authoritative ? employeeMatches[0] : null;
        var username = ChooseUsername(row, shortName, fullName, shortNameCounts, users, existing);
        if (username is null)
        {
            return existing is null && !HasUsernameCandidate(row, shortNameCounts)
                ? Skipped(row, LegacyImportReasons.MissingIdentity)
                : Skipped(row, LegacyImportReasons.UsernameConflict);
        }

        if (!authoritative)
        {
            var owner = users.SingleOrDefault(user =>
                string.Equals(user.NormalizedUsername, AccountIdentity.NormalizeUsername(username), StringComparison.Ordinal));
            if (owner is not null)
            {
                if (!IsClearlySamePerson(owner, shortName, fullName, username))
                {
                    return Skipped(row, LegacyImportReasons.UsernameConflict);
                }

                existing = owner;
            }
        }

        var displayName = shortName.Length > 0 ? shortName : fullName;
        if (displayName.Length == 0)
        {
            displayName = employeeNumber;
        }

        if (displayName.Length > 150)
        {
            return Skipped(row, LegacyImportReasons.MissingIdentity);
        }

        var legacyActive = IsLegacyActive(row.EndWork);
        var catalogRoleCode = CatalogRoleCode(row, legacyRows);
        var desiredRole = catalogRoleCode is not null && catalogRoles.TryGetValue(catalogRoleCode, out var catalogRole)
            ? catalogRole
            : userRole;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = UtcNowSeconds();
            var created = false;
            string? temporaryPassword = null;
            AppUser user;
            if (existing is null)
            {
                temporaryPassword = CreateTemporaryPassword();
                user = new AppUser
                {
                    Username = username,
                    NormalizedUsername = AccountIdentity.NormalizeUsername(username),
                    DisplayName = displayName,
                    FullName = fullName.Length == 0 ? null : fullName,
                    EmployeeNumber = employeeNumber,
                    IsActive = legacyActive,
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
            else
            {
                user = await db.Users.SingleAsync(item => item.UserId == existing.UserId, cancellationToken);
                user.EmployeeNumber = employeeNumber;
                user.DisplayName = displayName;
                user.FullName = fullName.Length == 0 ? null : fullName;
                user.UpdatedAt = now;
                if (authoritative)
                {
                    user.IsActive = legacyActive;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            if (user.UserId == Guid.Empty)
            {
                throw new InvalidOperationException("UserID was not generated.");
            }

            var roleCode = await EnsureRolePreservingExistingAsync(
                user.UserId,
                created ? desiredRole : null,
                desiredRole,
                now,
                cancellationToken);
            bool? accessActive = created ? legacyActive : user.IsActive;
            if (created || authoritative)
            {
                await EnsureBranchAccessStateAsync(user.UserId, branchId, accessActive.Value, now, cancellationToken);
            }
            else
            {
                accessActive = await db.UserBranchAccess
                    .Where(item => item.UserId == user.UserId && item.BranchId == branchId)
                    .Select(item => (bool?)item.IsActive)
                    .SingleOrDefaultAsync(cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Legacy employee import {Outcome} for employee {EmployeeNumber}.",
                created ? "created" : "updated",
                employeeNumber);

            return new LegacyBulkImportItem(
                employeeNumber,
                BlankToNull(shortName),
                BlankToNull(fullName),
                user.Username,
                user.DisplayName,
                user.FullName,
                user.IsActive,
                roleCode,
                created || authoritative ? accessActive : null,
                user.MustChangePassword,
                created ? "Created" : "Matched",
                null,
                created && legacyActive ? temporaryPassword : null);
        }
        catch (DbUpdateException)
        {
            await SafeRollbackAsync(transaction, cancellationToken);
            db.ChangeTracker.Clear();
            return FailedRow(row, LegacyImportReasons.SaveFailed);
        }
        catch
        {
            await SafeRollbackAsync(transaction, cancellationToken);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<string> EnsureRolePreservingExistingAsync(
        Guid userId,
        Role? roleForNewUser,
        Role fallbackRole,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var assignments = await db.UserRoles
            .Include(item => item.Role)
            .Where(item => item.UserId == userId)
            .ToListAsync(cancellationToken);
        var standard = assignments
            .Where(item => item.Role.RoleCode is RoleCodes.Admin or RoleCodes.Supervisor or RoleCodes.User)
            .Select(item => item.Role.RoleCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (standard.Count > 0)
        {
            if (standard.Contains(RoleCodes.Admin, StringComparer.Ordinal))
            {
                return RoleCodes.Admin;
            }

            if (standard.Contains(RoleCodes.Supervisor, StringComparer.Ordinal))
            {
                return RoleCodes.Supervisor;
            }

            return RoleCodes.User;
        }

        var role = roleForNewUser ?? fallbackRole;
        db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            RoleId = role.RoleId,
            AssignedAt = now,
        });
        return role.RoleCode;
    }

    private async Task EnsureBranchAccessStateAsync(
        Guid userId,
        Guid branchId,
        bool isActive,
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
                IsActive = isActive,
                GrantedAt = now,
            });
            return;
        }

        access.IsActive = isActive;
    }

    private static string? ChooseUsername(
        LegacyEmployeeRecord row,
        string shortName,
        string fullName,
        IReadOnlyDictionary<string, int> shortNameCounts,
        IReadOnlyList<AppUser> users,
        AppUser? employeeMatch)
    {
        foreach (var candidate in UsernameCandidates(row, shortNameCounts))
        {
            var normalized = AccountIdentity.NormalizeUsername(candidate);
            var owner = users.SingleOrDefault(user =>
                string.Equals(user.NormalizedUsername, normalized, StringComparison.Ordinal));
            if (owner is null)
            {
                return employeeMatch is null ? candidate : employeeMatch.Username;
            }

            if (employeeMatch is not null && owner.UserId == employeeMatch.UserId)
            {
                return employeeMatch.Username;
            }

            if (employeeMatch is null && IsClearlySamePerson(owner, shortName, fullName, candidate))
            {
                return owner.Username;
            }
        }

        return null;
    }

    private static bool HasUsernameCandidate(
        LegacyEmployeeRecord row,
        IReadOnlyDictionary<string, int> shortNameCounts)
    {
        return UsernameCandidates(row, shortNameCounts).Count > 0;
    }

    private static List<string> UsernameCandidates(
        LegacyEmployeeRecord row,
        IReadOnlyDictionary<string, int> shortNameCounts)
    {
        var candidates = new List<string>();
        var shortName = Canonical(row.UserShortName);
        var employeeNumber = Canonical(row.IdNumber);
        if (shortName.Length > 0
            && shortNameCounts.TryGetValue(shortName, out var count)
            && count == 1
            && IsPreservableUsername(shortName))
        {
            candidates.Add(shortName);
        }

        if (IsPreservableUsername(employeeNumber)
            && !candidates.Contains(employeeNumber, StringComparer.Ordinal))
        {
            candidates.Add(employeeNumber);
        }

        return candidates;
    }

    private static bool IsClearlySamePerson(AppUser owner, string shortName, string fullName, string username)
    {
        var ownerEmployeeNumber = Canonical(owner.EmployeeNumber);
        if (ownerEmployeeNumber.Length > 0)
        {
            return false;
        }

        var ownerDisplay = Canonical(owner.DisplayName);
        var ownerFull = Canonical(owner.FullName);
        return (shortName.Length > 0 && string.Equals(ownerDisplay, shortName, StringComparison.Ordinal))
            || (fullName.Length > 0 && string.Equals(ownerFull, fullName, StringComparison.Ordinal))
            || string.Equals(Canonical(owner.Username), username, StringComparison.Ordinal)
                && string.Equals(ownerDisplay, username, StringComparison.Ordinal);
    }

    private static string? CatalogRoleCode(
        LegacyEmployeeRecord row,
        IReadOnlyList<LegacyEmployeeRecord> legacyRows)
    {
        var employeeNumber = Canonical(row.IdNumber);
        foreach (var request in RequestedEmployees)
        {
            var name = Canonical(request.Name);
            var matches = legacyRows
                .Where(candidate =>
                    string.Equals(Canonical(candidate.UserShortName), name, StringComparison.Ordinal)
                    || string.Equals(Canonical(candidate.UserFullName), name, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 1 && string.Equals(Canonical(matches[0].IdNumber), employeeNumber, StringComparison.Ordinal))
            {
                return request.RoleCode;
            }
        }

        return null;
    }

    public static string BuildFullName(LegacyEmployeeRecord row)
    {
        var fullName = Canonical(row.UserFullName);
        if (fullName.Length > 0)
        {
            return fullName;
        }

        var parts = new[] { row.Name1, row.Name2, row.Name3, row.Name4 }
            .Select(Canonical)
            .Where(part => part.Length > 0);
        return string.Join(' ', parts);
    }

    public static bool IsLegacyActive(DateTime? endWork)
    {
        if (endWork is null)
        {
            return true;
        }

        return endWork.Value.Date > DateTime.UtcNow.Date;
    }

    private static LegacyBulkImportReport Summarize(int examined, IReadOnlyList<LegacyBulkImportItem> items)
    {
        var created = items.Where(item => item.Outcome == "Created").ToList();
        var updated = items.Count(item => item.Outcome == "Matched");
        return new LegacyBulkImportReport(
            examined,
            updated,
            created.Count,
            updated,
            created.Count(item => item.IsActive == true),
            created.Count(item => item.IsActive == false),
            items.Count(item => item.Outcome == "Skipped"),
            items.Count(item => item.Outcome == "Failed"),
            items);
    }

    private static LegacyBulkImportReport EmptyFailure(string reason)
    {
        return new LegacyBulkImportReport(0, 0, 0, 0, 0, 0, 0, 1, [FailedRow(null, reason)]);
    }

    private static LegacyBulkImportItem Skipped(LegacyEmployeeRecord row, string reason)
    {
        return new LegacyBulkImportItem(
            BlankToNull(Canonical(row.IdNumber)),
            BlankToNull(Canonical(row.UserShortName)),
            BlankToNull(BuildFullName(row)),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "Skipped",
            reason,
            null);
    }

    private static LegacyBulkImportItem FailedRow(LegacyEmployeeRecord? row, string reason)
    {
        return new LegacyBulkImportItem(
            row is null ? null : BlankToNull(Canonical(row.IdNumber)),
            row is null ? null : BlankToNull(Canonical(row.UserShortName)),
            row is null ? null : BlankToNull(BuildFullName(row)),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "Failed",
            reason,
            null);
    }

    private static string? BlankToNull(string value)
    {
        return value.Length == 0 ? null : value;
    }
}
