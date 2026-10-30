using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Sync;

public sealed record SqlEndpointSettings(
    string DatabaseRole,
    string LocationKind,
    string Server,
    string? InstanceName,
    int? Port,
    string DatabaseName,
    string AuthenticationType,
    string? Username,
    string? Password,
    bool Encrypt,
    bool TrustServerCertificate)
{
    public const string Local = "Local";
    public const string Remote = "Remote";
    public const string Integrated = "Integrated";
    public const string SqlLogin = "Sql";
    public const string Linked = "Linked";
    public const string PendingRemoteSync = "PendingRemoteSyncSetup";

    public string BuildConnectionString(string catalog)
    {
        var dataSource = Server.Trim();
        if (!string.IsNullOrWhiteSpace(InstanceName))
        {
            dataSource = dataSource + "\\" + InstanceName.Trim();
        }

        if (Port is > 0)
        {
            dataSource = dataSource + "," + Port.Value;
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = catalog,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = 8,
            ApplicationName = "XtraPOS.Api",
            IntegratedSecurity = string.Equals(AuthenticationType, Integrated, StringComparison.Ordinal),
        };
        if (!builder.IntegratedSecurity)
        {
            builder.UserID = Username ?? "";
            builder.Password = Password ?? "";
        }

        return builder.ConnectionString;
    }

    public static string DescribeConnectionFailure(Exception exception)
    {
        if (exception is CentralRejectedException rejected)
        {
            return rejected.Message;
        }

        if (exception is SqlException sql)
        {
            return DescribeSqlFailure(sql.Number);
        }

        return "تعذر الوصول إلى خادم البيانات.";
    }

    public static string DescribeSqlFailure(int number)
    {
        return number switch
        {
            18456 or 18452 => "فشل تسجيل الدخول إلى خادم البيانات.",
            4060 or 911 => "قاعدة البيانات غير متاحة.",
            208 or 2812 => "قاعدة البيانات غير متوافقة مع القاعدة المركزية.",
            262 or 15247 => "تم الاتصال بالخادم، لكن حساب SQL لا يملك صلاحية إنشاء قاعدة بيانات.",
            229 or 230 or 916 => "تم الاتصال بالخادم، لكن حساب SQL لا يملك الصلاحية المطلوبة.",
            448 or 468 => "تم الاتصال بالخادم، لكن ترتيب الأحرف المطلوب غير متاح.",
            1801 => "قاعدة البيانات موجودة مسبقاً ولم يتم حذفها.",
            5170 => "اسم قاعدة البيانات متاح، لكن توجد ملفات قاعدة بيانات قديمة بنفس الاسم على الخادم.",
            5123 or 5133 or 5105 or 1827 or 1101 or 1105 or 112 => "تم الاتصال بالخادم، لكن تعذر إنشاء ملفات قاعدة البيانات.",
            5 or 53 or -2 or 40 or 233 or 258 or 10060 or 11001 => "تعذر الوصول إلى خادم البيانات.",
            _ => "تم الاتصال بالخادم، لكن تعذر تنفيذ العملية على قاعدة البيانات.",
        };
    }
}

public sealed class CentralRejectedException(string message) : Exception(message);
