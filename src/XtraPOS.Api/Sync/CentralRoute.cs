namespace XtraPOS.Api.Sync;

public static class CentralRoute
{
    public static SqlEndpointSettings Bind(
        string? locationKind,
        string? server,
        string? instanceName,
        int? port,
        string databaseName,
        string? authenticationType,
        string? username,
        string? password,
        bool encrypt,
        bool trustServerCertificate)
    {
        var remote = string.Equals(locationKind, SqlEndpointSettings.Remote, StringComparison.Ordinal);
        var parsed = Split(server, port);
        var instance = string.IsNullOrWhiteSpace(instanceName) ? null : instanceName.Trim();
        if (remote && string.IsNullOrWhiteSpace(parsed.Server))
        {
            throw new Data.SetupRejectedException("الخادم المركزي البعيد غير محدد. لن يتم استخدام القاعدة المركزية المحلية.");
        }

        if (remote && IsLocal(parsed.Server) && instance is null && parsed.Port is not > 0)
        {
            throw new Data.SetupRejectedException("الخادم المركزي البعيد غير محدد. لن يتم استخدام القاعدة المركزية المحلية.");
        }

        var host = string.IsNullOrWhiteSpace(parsed.Server) ? "localhost" : parsed.Server;
        if (!remote && IsLocal(host))
        {
            host = "localhost";
        }

        return new SqlEndpointSettings(
            Data.DatabaseRole.Central,
            remote ? SqlEndpointSettings.Remote : SqlEndpointSettings.Local,
            host,
            instance,
            parsed.Port,
            databaseName.Trim(),
            string.Equals(authenticationType, SqlEndpointSettings.SqlLogin, StringComparison.Ordinal)
                ? SqlEndpointSettings.SqlLogin
                : SqlEndpointSettings.Integrated,
            username,
            password,
            encrypt,
            trustServerCertificate);
    }

    public static string Describe(SqlEndpointSettings settings)
    {
        var host = settings.Server.Trim();
        if (!string.IsNullOrWhiteSpace(settings.InstanceName))
        {
            host += "\\" + settings.InstanceName.Trim();
        }

        if (settings.Port is > 0)
        {
            host += "," + settings.Port.Value;
        }

        return host + "/" + settings.DatabaseName.Trim();
    }

    public static bool SameInstance(string branchServer, SqlEndpointSettings central)
    {
        if (string.Equals(central.LocationKind, SqlEndpointSettings.Remote, StringComparison.Ordinal))
        {
            return false;
        }

        var branch = Split(branchServer, null);
        var centralHost = Split(central.Server, central.Port);
        return string.IsNullOrWhiteSpace(central.InstanceName)
            && HostEquals(branch.Server, centralHost.Server)
            && branch.Port == centralHost.Port;
    }

    public static (string Server, int? Port) Split(string? server, int? explicitPort)
    {
        var text = server?.Trim() ?? "";
        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..], out var parsedPort) && parsedPort is > 0 and <= 65535)
        {
            var host = text[..comma].Trim();
            if (explicitPort is > 0 && explicitPort != parsedPort)
            {
                throw new Data.SetupRejectedException("منفذ الخادم المركزي غير متطابق.");
            }

            return (host, explicitPort is > 0 ? explicitPort : parsedPort);
        }

        return (text, explicitPort is > 0 ? explicitPort : null);
    }

    private static bool IsLocal(string server)
    {
        return server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || server.Equals(".", StringComparison.OrdinalIgnoreCase)
            || server.Equals("(local)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HostEquals(string left, string right)
    {
        var a = string.IsNullOrWhiteSpace(left) ? "localhost" : left.Trim();
        var b = string.IsNullOrWhiteSpace(right) ? "localhost" : right.Trim();
        if (IsLocal(a) && IsLocal(b))
        {
            return true;
        }

        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }
}
