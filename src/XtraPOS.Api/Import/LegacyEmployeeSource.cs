using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;

namespace XtraPOS.Api.Import;

public interface ILegacyEmployeeSource
{
    Task<IReadOnlyList<LegacyEmployeeRecord>> ReadAsync(CancellationToken cancellationToken);
}

public sealed record LegacyEmployeeRecord(
    string IdNumber,
    string? UserShortName,
    string? UserFullName,
    string? Name1,
    string? Name2,
    string? Name3,
    string? Name4,
    string? Position,
    string? TelNumber,
    string? Address,
    DateTime? StartWorkingDate,
    DateTime? EndWork);

public sealed class SqlLegacyEmployeeSource(IOptions<LegacyImportOptions> options) : ILegacyEmployeeSource
{
    public static readonly string[] SelectedColumns =
    [
        "IDNumber",
        "UserShortName",
        "UserFullName",
        "Name1",
        "Name2",
        "Name3",
        "Name4",
        "Position",
        "TelNumber",
        "Address",
        "StartWorkingDate",
        "EndWork",
    ];

    public const string SelectSql =
        """
        SELECT
            IDNumber,
            UserShortName,
            UserFullName,
            Name1,
            Name2,
            Name3,
            Name4,
            Position,
            TelNumber,
            Address,
            StartWorkingDate,
            EndWork
        FROM dbo.UsersTable;
        """;

    public async Task<IReadOnlyList<LegacyEmployeeRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Server)
            || !string.Equals(settings.Database, LegacyImportOptions.ExpectedDatabase, StringComparison.Ordinal)
            || settings.ConnectTimeoutSeconds is < 1 or > 120)
        {
            throw new LegacyImportException("Legacy import is not configured for saleemPOSDB.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server.Trim(),
            InitialCatalog = LegacyImportOptions.ExpectedDatabase,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = settings.ConnectTimeoutSeconds,
            ApplicationName = "XtraPOS.LegacyImport",
            ApplicationIntent = ApplicationIntent.ReadOnly,
        };

        var people = new List<LegacyEmployeeRecord>();
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(SelectSql, connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            people.Add(new LegacyEmployeeRecord(
                ReadText(reader, 0),
                ReadOptional(reader, 1),
                ReadOptional(reader, 2),
                ReadOptional(reader, 3),
                ReadOptional(reader, 4),
                ReadOptional(reader, 5),
                ReadOptional(reader, 6),
                ReadOptional(reader, 7),
                ReadOptional(reader, 8),
                ReadOptional(reader, 9),
                reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                reader.IsDBNull(11) ? null : reader.GetDateTime(11)));
        }

        return people;
    }

    private static string ReadText(SqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
    }

    private static string? ReadOptional(SqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}

public sealed class LegacyImportException(string message) : Exception(message);
