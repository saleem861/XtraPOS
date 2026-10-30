using Microsoft.Extensions.Configuration;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Setup;

public static class BranchProvisionPolicy
{
    public const string SectionName = "SqlProvisioning";

    public const string ProvisionerLogin = "xtrapos_provisioner";
    public const string RegisterProcedure = "dbo.RegisterProvisionedBranch";

    public const string MissingProvisionerMessage =
        "إنشاء قاعدة فرع يحتاج حساب SQL منفصل في إعداد الخادم SqlProvisioning، مع صلاحية الخادم CREATE ANY DATABASE لذلك الحساب فقط. لا يُمنح dbcreator ولا ALTER ANY DATABASE، ولا تُمنح أي صلاحية إنشاء لحساب التشغيل. لم يتم إنشاء قاعدة.";

    public const string MissingRegistrarMessage =
        "تسجيل الفرع مركزياً يتم فقط عبر الإجراء dbo.RegisterProvisionedBranch داخل XtraPOS_Central، وبصلاحية EXECUTE لذلك الحساب. لا يُمنح INSERT على dbo.Branches أو dbo.UserBranchAccess، ولم يُسجل الفرع.";

    public const string OnboardingClosedMessage =
        "إنشاء فرع مستقل يحتاج تفويضاً صالحاً لمرة واحدة على الخادم. وجود ملف وحده لا يكفي، ولا تُستخدم بيانات مدير فرع المقاطعة، ولم يتم إنشاء قاعدة.";

    public static string? RejectDatabaseName(string? databaseName, string configuredDatabase)
    {
        if (!BranchDatabase.IsTestName(databaseName))
        {
            return "اسم قاعدة الفرع غير مسموح.";
        }

        if (string.Equals(databaseName, configuredDatabase, StringComparison.OrdinalIgnoreCase))
        {
            return "لن تُستبدل قاعدة الفرع الحالية.";
        }

        if (databaseName!.Contains("Central", StringComparison.OrdinalIgnoreCase))
        {
            return "لن يتم إنشاء القاعدة المركزية أو تعديلها.";
        }

        return null;
    }

    public static string? ConfigurationProblem(IConfiguration configuration)
    {
        var server = configuration[$"{SectionName}:Server"];
        var user = configuration[$"{SectionName}:User"];
        var password = configuration[$"{SectionName}:Password"];
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            return MissingProvisionerMessage;
        }

        var runtime = configuration["SqlServer:User"];
        var centralRuntime = configuration["CentralSql:User"];
        if (string.Equals(user, runtime, StringComparison.OrdinalIgnoreCase)
            || string.Equals(user, centralRuntime, StringComparison.OrdinalIgnoreCase))
        {
            return "حساب التجهيز يجب أن يكون مختلفاً عن حساب التشغيل. لم يتم إنشاء قاعدة.";
        }

        return null;
    }
}
