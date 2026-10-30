using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Catalog;

public static class ArabicSearchKeyBackfill
{
    public static async Task ApplyAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await FillAsync(connection, "dbo.Items", "ItemID", "ItemName", "ItemNameSearchStrict", "ItemNameSearchLoose", cancellationToken);
        if (await TableExistsAsync(connection, "dbo.Customers", cancellationToken))
        {
            await FillAsync(connection, "dbo.Customers", "CustomerID", "Name", "NameSearchStrict", "NameSearchLoose", cancellationToken);
        }
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(@table, N'U');", connection);
        command.Parameters.AddWithValue("@table", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null and not DBNull;
    }

    private static async Task FillAsync(
        SqlConnection connection,
        string table,
        string idColumn,
        string nameColumn,
        string strictColumn,
        string looseColumn,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, table, cancellationToken))
        {
            return;
        }

        await using var select = new SqlCommand(
            $"SELECT {idColumn}, {nameColumn} FROM {table} WHERE {strictColumn} IS NULL OR {looseColumn} IS NULL;",
            connection);
        select.CommandTimeout = 120;
        var rows = new List<(Guid Id, string Name)>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetGuid(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
            }
        }

        foreach (var row in rows)
        {
            await using var update = new SqlCommand(
                $"UPDATE {table} SET {strictColumn} = @strict, {looseColumn} = @loose WHERE {idColumn} = @id;",
                connection);
            update.Parameters.AddWithValue("@id", row.Id);
            update.Parameters.AddWithValue("@strict", ArabicSearchNormalizer.NormalizeStrict(row.Name));
            update.Parameters.AddWithValue("@loose", ArabicSearchNormalizer.NormalizeLoose(row.Name));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
