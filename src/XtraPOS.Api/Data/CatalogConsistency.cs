using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Data;

public static class CatalogConsistency
{
    public const string EmptyAfterInitializationMessage =
        "Master catalog is empty although this database was previously initialized.";

    public static bool ShouldWarn(bool setupExists, bool masterCatalogCompleted, int itemCount) =>
        setupExists && masterCatalogCompleted && itemCount == 0;

    public static async Task<bool> MasterCatalogIsUnexpectedlyEmptyAsync(
        SqlConnectionFactory connections,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            DECLARE @setup int = 0;
            DECLARE @imports int = 0;
            DECLARE @items int = 0;

            IF OBJECT_ID(N'dbo.Businesses', N'U') IS NOT NULL
                SELECT @setup = COUNT(*) FROM dbo.Businesses;

            IF OBJECT_ID(N'dbo.DataImportHistory', N'U') IS NOT NULL
                SELECT @imports = COUNT(*)
                FROM dbo.DataImportHistory
                WHERE ImportType = N'MasterCatalog' AND Status = N'Completed';

            IF OBJECT_ID(N'dbo.Items', N'U') IS NOT NULL
                SELECT @items = COUNT(*) FROM dbo.Items;

            SELECT @setup, @imports, @items;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        return ShouldWarn(reader.GetInt32(0) > 0, reader.GetInt32(1) > 0, reader.GetInt32(2));
    }

    public static async Task RecordMasterCatalogAsync(
        SqlConnection connection,
        int rowsProcessed,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF OBJECT_ID(N'dbo.DataImportHistory', N'U') IS NULL
                RETURN;

            INSERT INTO dbo.DataImportHistory
                (ImportID, ImportType, Source, StartedAt, CompletedAt, Status, RowsProcessed, Notes)
            VALUES
                (NEWID(), N'MasterCatalog', N'LegacyCentral', SYSUTCDATETIME(), SYSUTCDATETIME(), N'Completed', @rows, N'Explicit master catalog commit.');
            """,
            connection);
        command.Parameters.AddWithValue("@rows", rowsProcessed);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
