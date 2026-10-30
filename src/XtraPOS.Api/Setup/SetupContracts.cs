using XtraPOS.Api.Devices;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public static class SetupSteps
{
    public const string CheckServer = "checkServer";
    public const string CreateDatabase = "createDatabase";
    public const string CreateTables = "createTables";
    public const string CreateBusiness = "createBusiness";
    public const string CreateBranch = "createBranch";
    public const string CreateStation = "createStation";
    public const string RegisterDevice = "registerDevice";
    public const string CreateAdmin = "createAdmin";
    public const string Verify = "verify";
    public const string InitialSync = "initialSync";
}

public static class SetupStatuses
{
    public const string NotConfigured = "NotConfigured";
    public const string Configuring = "Configuring";
    public const string Configured = "Configured";
    public const string ConfiguredButAdminMissing = "ConfiguredButAdminMissing";
    public const string Error = "Error";
    public const string ConfigurationError = "ConfigurationError";
    public const string DatabaseUnavailable = "DatabaseUnavailable";
}

public sealed record TopologyPlan(
    string OperatingMode,
    bool CreateCentral,
    bool LinkToCentral,
    SqlEndpointSettings? Central,
    SqlEndpointSettings? BranchRemote,
    Guid? CentralAdminUserId = null);

public sealed class CreateBranchRequest
{
    public string? BusinessName { get; set; }
    public string? BusinessDisplayName { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? BranchName { get; set; }
    public string? BranchCode { get; set; }
    public string? BranchType { get; set; }
    public int StationNumber { get; set; }
    public string? StationName { get; set; }
    public string? DeviceName { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string? FullName { get; set; }
    public string? EmployeeNumber { get; set; }
    public string? Password { get; set; }
    public string? ConfirmPassword { get; set; }
    public string? OperatingMode { get; set; }
    public string? CentralAction { get; set; }
    public string? CentralLocation { get; set; }
    public string? CentralServer { get; set; }
    public string? CentralInstance { get; set; }
    public int? CentralPort { get; set; }
    public string? CentralDatabaseName { get; set; }
    public string? CentralAuthentication { get; set; }
    public string? CentralUsername { get; set; }
    public string? CentralPassword { get; set; }
    public bool? CentralEncrypt { get; set; }
    public bool? CentralTrustServerCertificate { get; set; }
    public string? LinkToCentral { get; set; }
    public string? BranchLocation { get; set; }
    public string? BranchRemoteChoice { get; set; }
    public string? BranchRemoteServer { get; set; }
    public int? BranchRemotePort { get; set; }
    public string? BranchRemoteDatabaseName { get; set; }
    public string? BranchRemoteAuthentication { get; set; }
    public string? BranchRemoteUsername { get; set; }
    public string? BranchRemotePassword { get; set; }
    public string? CentralAdminUserId { get; set; }
}

public sealed record AdminAccount(
    string Username,
    string DisplayName,
    string? FullName,
    string? EmployeeNumber,
    string Password);

public sealed record BranchSetup(
    string BusinessName,
    string? BusinessDisplayName,
    string? Phone,
    string? Address,
    string BranchName,
    string? BranchCode,
    string BranchType,
    int StationNumber,
    string StationName,
    string DeviceName,
    AdminAccount Admin);

public sealed class SetupEvent
{
    public string Type { get; init; } = "";
    public string? Step { get; init; }
    public string? State { get; init; }
    public string? Message { get; init; }
    public DeviceRegistrationResponse? Device { get; init; }

    public static SetupEvent Progress(string step, string state) =>
        new() { Type = "progress", Step = step, State = state };

    public static SetupEvent Failed(string message) =>
        new() { Type = "failed", Message = message };

    public bool AlreadyConfigured { get; init; }

    public static SetupEvent Completed(DeviceRegistrationResponse device, bool alreadyConfigured = false) =>
        new() { Type = "completed", Device = device, AlreadyConfigured = alreadyConfigured };
}

public sealed class DeviceRegistrationResponse
{
    public Guid DeviceId { get; init; }
    public Guid BusinessId { get; init; }
    public Guid BranchId { get; init; }
    public Guid StationId { get; init; }
    public string DeviceName { get; init; } = "";
    public string DeviceType { get; init; } = "";
    public string? DeviceToken { get; init; }
    public DateTime RegisteredAt { get; init; }
    public string BusinessName { get; init; } = "";
    public string? BusinessDisplayName { get; init; }
    public string BranchName { get; init; } = "";
    public string? BranchCode { get; init; }
    public string BranchType { get; init; } = "";
    public string StationName { get; init; } = "";
    public int StationNumber { get; init; }
}

public sealed class SetupStatusResponse
{
    public string Status { get; init; } = "";
    public string? Message { get; init; }
    public Guid? BusinessId { get; init; }
    public string? BusinessName { get; init; }
    public Guid? BranchId { get; init; }
    public string? BranchName { get; init; }
    public string? BranchCode { get; init; }
    public IReadOnlyList<LocalRecoveryStation>? Stations { get; init; }
}

public static class BranchSetupValidator
{
    private static readonly HashSet<string> BranchTypes = new(StringComparer.Ordinal)
    {
        "Retail",
        "Warehouse",
        "RetailAndWarehouse",
    };

    public static bool TryValidate(CreateBranchRequest? request, out BranchSetup setup, out string message)
    {
        setup = null!;
        if (request is null)
        {
            message = "بيانات الإعداد غير صالحة.";
            return false;
        }

        if (!TryRequired(request.BusinessName, 200, "يرجى إدخال اسم المنشأة", out var businessName, out message))
        {
            return false;
        }

        if (!TryOptional(request.BusinessDisplayName, 200, out var displayName, out message))
        {
            return false;
        }

        if (!TryOptional(request.Phone, 50, out var phone, out message))
        {
            return false;
        }

        if (!TryOptional(request.Address, 500, out var address, out message))
        {
            return false;
        }

        if (!TryRequired(request.BranchName, 200, "يرجى إدخال اسم الفرع", out var branchName, out message))
        {
            return false;
        }

        if (!TryOptional(request.BranchCode, 50, out var branchCode, out message))
        {
            return false;
        }

        var branchType = request.BranchType?.Trim() ?? "";
        if (!BranchTypes.Contains(branchType))
        {
            message = "يرجى اختيار نوع الفرع";
            return false;
        }

        if (request.StationNumber <= 0)
        {
            message = "يرجى إدخال رقم نقطة البيع";
            return false;
        }

        if (!TryRequired(request.StationName, 100, "يرجى إدخال اسم نقطة البيع", out var stationName, out message))
        {
            return false;
        }

        if (!TryRequired(request.DeviceName, 150, "يرجى إدخال اسم الجهاز", out var deviceName, out message))
        {
            return false;
        }

        AdminAccount admin;
        if (ReusesCentralAdmin(request, out _))
        {
            admin = new AdminAccount("central-admin", "central-admin", null, null, "");
        }
        else if (!TryValidateAdmin(
                request.Username,
                request.DisplayName,
                request.FullName,
                request.EmployeeNumber,
                request.Password,
                request.ConfirmPassword,
                out admin,
                out message))
        {
            return false;
        }

        setup = new BranchSetup(
            businessName,
            displayName,
            phone,
            address,
            branchName,
            branchCode,
            branchType,
            request.StationNumber,
            stationName,
            deviceName,
            admin);
        message = "";
        return true;
    }

    public static bool ReusesCentralAdmin(CreateBranchRequest request, out Guid userId)
    {
        userId = Guid.Empty;
        return string.Equals(request.LinkToCentral, "yes", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(request.CentralAdminUserId, out userId)
            && userId != Guid.Empty;
    }

    public static bool TryValidateAdmin(
        string? usernameValue,
        string? displayNameValue,
        string? fullNameValue,
        string? employeeNumberValue,
        string? passwordValue,
        string? confirmPassword,
        out AdminAccount admin,
        out string message)
    {
        admin = null!;
        var username = usernameValue?.Trim() ?? "";
        if (!AccountIdentity.IsAcceptableUsername(username))
        {
            message = "يرجى إدخال اسم مستخدم صالح (من 3 إلى 100 حرفاً).";
            return false;
        }

        if (!TryRequired(displayNameValue, 150, "يرجى إدخال الاسم الظاهر", out var adminDisplayName, out message))
        {
            return false;
        }

        if (!TryOptional(fullNameValue, 300, out var fullName, out message))
        {
            return false;
        }

        if (!TryOptional(employeeNumberValue, 50, out var employeeNumber, out message))
        {
            return false;
        }

        var password = passwordValue ?? "";
        if (password.Length < AccountIdentity.PasswordMinLength
            || password.Length > AccountIdentity.PasswordMaxLength)
        {
            message = "كلمة المرور يجب أن تكون 8 أحرف على الأقل.";
            return false;
        }

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            message = "تأكيد كلمة المرور غير مطابق.";
            return false;
        }

        admin = new AdminAccount(username, adminDisplayName, fullName, employeeNumber, password);
        message = "";
        return true;
    }

    private static bool TryRequired(
        string? value,
        int maxLength,
        string requiredMessage,
        out string cleaned,
        out string message)
    {
        cleaned = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            message = requiredMessage;
            return false;
        }

        cleaned = value.Trim();
        if (cleaned.Length > maxLength)
        {
            message = "أحد الحقول أطول من المسموح.";
            return false;
        }

        message = "";
        return true;
    }

    private static bool TryOptional(string? value, int maxLength, out string? cleaned, out string message)
    {
        cleaned = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            message = "";
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            message = "أحد الحقول أطول من المسموح.";
            return false;
        }

        cleaned = trimmed;
        message = "";
        return true;
    }
}
