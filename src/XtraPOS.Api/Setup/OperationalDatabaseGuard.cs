using XtraPOS.Api.Data;

namespace XtraPOS.Api.Setup;

public static class OperationalDatabaseGuard
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "XtraPOS",
        "XtraPOS_Central",
        "XtraPOS_Central_Test_Staging",
        "XtraPOS_Branch_01",
        "XtraPOS_Branch_02",
        "XtraPOS_Branch_Test_Staging",
        "XtraPOS_Branch_Test_Cloud",
        "saleemPOSDB",
        "Shop1",
        "Shop2",
        "swstore",
        "master",
        "model",
        "msdb",
        "tempdb",
    };

    public static bool IsProductionCatalog(string? name)
    {
        if (!BranchDatabase.IsSafeIdentifier(name))
        {
            return false;
        }

        if (name!.Equals("XtraPOS_Central", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("XtraPOS_Central_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsProtected(name);
    }

    public static bool IsProtected(string? name)
    {
        var value = name?.Trim() ?? "";
        return value.Length == 0 || Protected.Contains(value) || ProductionDatabaseNames.IsForbidden(value);
    }
}

public static class OperationalBranchAddress
{
    public static string Normalize(string? dataSource)
    {
        var value = (dataSource ?? "").Trim();
        var port = 1433;
        var instance = "";
        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..], out var parsed))
        {
            port = parsed;
            value = value[..comma];
        }

        var slash = value.IndexOf('\\');
        if (slash >= 0)
        {
            instance = value[(slash + 1)..];
            value = value[..slash];
        }

        value = value.Trim().ToLowerInvariant();
        if (value is "localhost" or "127.0.0.1" or "." or "(local)")
        {
            value = "localhost";
        }

        return value + "\\" + instance.ToLowerInvariant() + "," + port;
    }
}
