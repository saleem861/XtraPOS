using XtraPOS.Api.Data;

namespace XtraPOS.Api.Devices;

public static class ProtectedDatabasePolicy
{
    public static bool IsProtected(string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return true;
        }

        var name = databaseName.Trim();
        if (name.Equals("XtraPOS_Central", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("master", StringComparison.OrdinalIgnoreCase)
            || name.Equals("model", StringComparison.OrdinalIgnoreCase)
            || name.Equals("msdb", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tempdb", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Contains("المقاطعة", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static bool AllowsInstallationReset(string? databaseName)
    {
        if (IsProtected(databaseName) || !BranchDatabase.IsAllowedName(databaseName))
        {
            return false;
        }

        return !string.Equals(databaseName!.Trim(), BranchDatabase.Name, StringComparison.OrdinalIgnoreCase);
    }

    public static string? RefuseDrop(string? databaseName, bool exclusiveLocal, bool backupVerified, bool unsyncedFinancial)
    {
        if (IsProtected(databaseName))
        {
            return "حذف هذه القاعدة مرفوض. القواعد المركزية والمشتركة وقواعد النظام محمية.";
        }

        if (!exclusiveLocal)
        {
            return "حذف القاعدة متاح فقط لقاعدة محلية مثبت أنها حصرية لهذا الجهاز.";
        }

        if (!backupVerified)
        {
            return "يلزم نسخة احتياطية ناجحة قبل حذف القاعدة المحلية.";
        }

        if (unsyncedFinancial)
        {
            return "توجد حركات مالية غير متزامنة. الحذف متوقف حتى التسوية.";
        }

        return null;
    }
}
