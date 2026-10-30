using System.Text.RegularExpressions;

namespace XtraPOS.Api.Security;

public static partial class AccountIdentity
{
    public const int UsernameMinLength = 3;
    public const int UsernameMaxLength = 100;
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;

    public static string NormalizeUsername(string username)
    {
        return username.Trim().ToUpperInvariant();
    }

    public static bool IsAcceptableUsername(string username)
    {
        var trimmed = username.Trim();
        return UsernamePattern().IsMatch(trimmed);
    }

    [GeneratedRegex(@"^[\p{L}\p{N}._-]{3,100}$")]
    private static partial Regex UsernamePattern();
}

public static class RoleCodes
{
    public const string Admin = "Admin";
    public const string Supervisor = "Supervisor";
    public const string User = "User";
}

public static class PermissionCodes
{
    public const string SystemAdminister = "System.Administer";
    public const string UsersView = "Users.View";
    public const string UsersManage = "Users.Manage";
    public const string UsersResetPassword = "Users.ResetPassword";
    public const string RolesView = "Roles.View";
    public const string RolesManage = "Roles.Manage";
    public const string DeviceView = "Device.View";
    public const string SetupView = "Setup.View";
    public const string ItemsView = "Items.View";
    public const string ItemsEditMaster = "Items.EditMaster";
    public const string ItemsEditBranchState = "Items.EditBranchState";
    public const string ItemsEditBranchPrice = "Items.EditBranchPrice";
    public const string CustomersView = "Customers.View";
    public const string CustomersCreate = "Customers.Create";
    public const string CustomersManage = "Customers.Manage";
    public const string PosSell = "POS.Sell";
    public const string PosCashSession = "POS.CashSession";
    public const string PosOverrideLinePrice = "POS.OverrideLinePrice";
    public const string CreditView = "Credit.View";
    public const string CreditCreate = "Credit.Create";
    public const string CreditCollect = "Credit.Collect";
    public const string CreditManageLimit = "Credit.ManageLimit";
    public const string CreditSuspend = "Credit.Suspend";
    public const string CreditReverse = "Credit.Reverse";
    public const string CreditAudit = "Credit.Audit";
}

public static class LoginFailureReasons
{
    public const string UnknownUser = "UnknownUser";
    public const string InvalidPassword = "InvalidPassword";
    public const string InactiveUser = "InactiveUser";
    public const string LockedOut = "LockedOut";
    public const string InactiveDevice = "InactiveDevice";
    public const string RevokedDevice = "RevokedDevice";
    public const string InactiveBranch = "InactiveBranch";
    public const string InactiveStation = "InactiveStation";
    public const string NoBranchAccess = "NoBranchAccess";
    public const string UnregisteredDevice = "UnregisteredDevice";
}
