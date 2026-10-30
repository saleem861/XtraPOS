using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Sync;

public sealed record CentralDatabaseDirectories(string DataDirectory, string LogDirectory);

public sealed record CentralDatabaseFilePlan(
    string DataLogicalName,
    string DataFileName,
    string LogLogicalName,
    string LogFileName);

public static partial class CentralDatabaseFiles
{
    public static async Task<CentralDatabaseFilePlan> CreateNewAsync(
        SqlConnection master,
        string databaseName,
        CancellationToken cancellationToken)
    {
        return await CreateNewAsync(master, databaseName, null, cancellationToken);
    }

    public static async Task<CentralDatabaseFilePlan> CreateNewAsync(
        SqlConnection master,
        string databaseName,
        IReadOnlyList<string>? suffixAttempts,
        CancellationToken cancellationToken)
    {
        var safeName = CentralDatabase.Require(databaseName);
        if (await DatabaseExistsAsync(master, safeName, cancellationToken))
        {
            throw new CentralRejectedException("قاعدة البيانات موجودة مسبقاً ولم يتم حذفها.");
        }

        var directories = await ReadDirectoriesAsync(master, cancellationToken);
        var attempts = suffixAttempts is { Count: > 0 }
            ? suffixAttempts
            : Enumerable.Range(0, 8).Select(_ => NewSuffix()).ToArray();
        SqlException? fileCollision = null;
        foreach (var suffix in attempts)
        {
            var plan = CreatePlan(safeName, directories, suffix);
            if (await PathsBelongToDatabaseAsync(master, plan, cancellationToken))
            {
                continue;
            }

            if (await DatabaseExistsAsync(master, safeName, cancellationToken))
            {
                throw new CentralRejectedException("قاعدة البيانات موجودة مسبقاً ولم يتم حذفها.");
            }

            try
            {
                await using var create = new SqlCommand(BuildCreateSql(safeName, plan), master);
                create.CommandTimeout = 60;
                await create.ExecuteNonQueryAsync(cancellationToken);
                return plan;
            }
            catch (SqlException exception) when (exception.Number == 5170)
            {
                fileCollision = exception;
            }
        }

        if (fileCollision is not null)
        {
            throw fileCollision;
        }

        throw new CentralRejectedException("تعذر اختيار ملفات جديدة لقاعدة البيانات.");
    }

    public static async Task<CentralDatabaseDirectories> ReadDirectoriesAsync(
        SqlConnection master,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT
                CONVERT(nvarchar(400), SERVERPROPERTY('InstanceDefaultDataPath')),
                CONVERT(nvarchar(400), SERVERPROPERTY('InstanceDefaultLogPath'));
            """,
            master);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CentralRejectedException("تعذر تحديد مجلد ملفات قاعدة البيانات على الخادم.");
        }

        var data = reader.IsDBNull(0) ? null : reader.GetString(0);
        var log = reader.IsDBNull(1) ? null : reader.GetString(1);
        await reader.CloseAsync();
        if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(log))
        {
            var fallback = await ReadMasterDirectoriesAsync(master, cancellationToken);
            data = string.IsNullOrWhiteSpace(data) ? fallback.DataDirectory : data;
            log = string.IsNullOrWhiteSpace(log) ? fallback.LogDirectory : log;
        }

        return new CentralDatabaseDirectories(EnsureDirectory(data), EnsureDirectory(log));
    }

    public static CentralDatabaseFilePlan CreatePlan(
        string databaseName,
        CentralDatabaseDirectories directories,
        string? suffix = null)
    {
        var safeName = CentralDatabase.Require(databaseName);
        var token = suffix ?? NewSuffix();
        if (!SuffixPattern().IsMatch(token))
        {
            throw new InvalidOperationException("Physical file suffix is not valid.");
        }

        var stem = safeName + "_" + token;
        if (stem.Length > 116)
        {
            stem = "XtraPOS_" + token;
        }

        return new CentralDatabaseFilePlan(
            stem,
            Combine(directories.DataDirectory, stem + ".mdf"),
            stem + "_log",
            Combine(directories.LogDirectory, stem + "_log.ldf"));
    }

    public static string BuildCreateSql(string databaseName, CentralDatabaseFilePlan plan)
    {
        var safeName = CentralDatabase.Require(databaseName);
        return $"""
            CREATE DATABASE {QuoteIdentifier(safeName)}
            ON PRIMARY
            (
                NAME = {SqlString(plan.DataLogicalName)},
                FILENAME = {SqlString(plan.DataFileName)}
            )
            LOG ON
            (
                NAME = {SqlString(plan.LogLogicalName)},
                FILENAME = {SqlString(plan.LogFileName)}
            )
            COLLATE {CentralDatabase.Collation};
            """;
    }

    public static string Combine(string directory, string fileName)
    {
        var separator = directory.Contains('/') ? '/' : '\\';
        return directory.TrimEnd('\\', '/') + separator + fileName;
    }

    public static string SqlString(string value)
    {
        return "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static async Task<CentralDatabaseDirectories> ReadMasterDirectoriesAsync(
        SqlConnection master,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT type_desc, physical_name
            FROM sys.master_files
            WHERE database_id = 1 AND type_desc IN (N'ROWS', N'LOG');
            """,
            master);
        string? dataFile = null;
        string? logFile = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = reader.GetString(0);
            var path = reader.GetString(1);
            if (kind == "ROWS" && dataFile is null)
            {
                dataFile = path;
            }
            else if (kind == "LOG" && logFile is null)
            {
                logFile = path;
            }
        }

        if (dataFile is null || logFile is null)
        {
            throw new CentralRejectedException("تعذر تحديد مجلد ملفات قاعدة البيانات على الخادم.");
        }

        return new CentralDatabaseDirectories(DirectoryOf(dataFile), DirectoryOf(logFile));
    }

    private static async Task<bool> DatabaseExistsAsync(
        SqlConnection master,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT DB_ID(@name);", master);
        command.Parameters.AddWithValue("@name", databaseName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null and not DBNull;
    }

    private static async Task<bool> PathsBelongToDatabaseAsync(
        SqlConnection master,
        CentralDatabaseFilePlan plan,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.master_files
            WHERE physical_name = @data OR physical_name = @log;
            """,
            master);
        command.Parameters.AddWithValue("@data", plan.DataFileName);
        command.Parameters.AddWithValue("@log", plan.LogFileName);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) > 0;
    }

    private static string EnsureDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (path.IndexOf('\\') < 0 && path.IndexOf('/') < 0))
        {
            throw new CentralRejectedException("تعذر تحديد مجلد ملفات قاعدة البيانات على الخادم.");
        }

        return path;
    }

    private static string DirectoryOf(string physicalName)
    {
        var index = Math.Max(physicalName.LastIndexOf('\\'), physicalName.LastIndexOf('/'));
        if (index < 0)
        {
            throw new CentralRejectedException("تعذر تحديد مجلد ملفات قاعدة البيانات على الخادم.");
        }

        return physicalName[..(index + 1)];
    }

    private static string QuoteIdentifier(string name)
    {
        return "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    private static string NewSuffix()
    {
        return Guid.NewGuid().ToString("N")[..12];
    }

    [GeneratedRegex("^[0-9a-fA-F]{12}$")]
    private static partial Regex SuffixPattern();
}
