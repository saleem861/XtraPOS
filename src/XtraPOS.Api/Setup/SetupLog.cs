using Microsoft.Data.SqlClient;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public static class SetupLog
{
    public static string Write(string contentRoot, string environment, string step, string result, int? sqlNumber = null, byte? sqlState = null, string? detail = null)
    {
        var id = "SETUP-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        try
        {
            var directory = Path.Combine(contentRoot, "App_Data", "logs");
            Directory.CreateDirectory(directory);
            var line = $"{DateTime.UtcNow:O} {id} {environment} {step} {Scrub(result)} sql={sqlNumber?.ToString() ?? "-"} state={sqlState?.ToString() ?? "-"}{Environment.NewLine}";
            if (!string.IsNullOrWhiteSpace(detail))
            {
                line += Scrub(detail) + Environment.NewLine;
            }

            File.AppendAllText(Path.Combine(directory, "setup.log"), line);
        }
        catch (Exception)
        {
            // Diagnostics must not block setup.
        }

        return id;
    }

    public static string Fail(string contentRoot, string environment, string step, Exception exception, string? secret = null)
    {
        SqlException? sql = null;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException found)
            {
                sql = found;
                break;
            }
        }

        var id = Write(contentRoot, environment, step, exception.GetType().Name, sql?.Number, sql?.State, exception.ToString());
        var message = SqlEndpointSettings.DescribeConnectionFailure(sql ?? exception);
        if (!string.IsNullOrEmpty(secret))
        {
            message = message.Replace(secret, "", StringComparison.Ordinal);
        }

        return $"{message} رمز التشخيص: {id}";
    }

    private static string Scrub(string value)
    {
        var text = value.Replace("\r", " ").Replace("\n", " | ");
        foreach (var marker in new[] { "Password=", "Pwd=", "User ID=", "UID=" })
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var end = text.IndexOf(';', index);
                if (end < 0)
                {
                    end = Math.Min(text.Length, index + marker.Length + 8);
                }

                text = text.Remove(index, end - index).Insert(index, marker + "***");
                index = text.IndexOf(marker, index + marker.Length + 3, StringComparison.OrdinalIgnoreCase);
            }
        }

        return text;
    }
}
