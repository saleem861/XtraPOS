using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;

namespace XtraPOS.Api.Import;

public interface ILegacyItemSource
{
    Task<IReadOnlyList<LegacyItemObservation>> ReadAsync(CancellationToken cancellationToken);
}

public sealed class SqlLegacyItemSource(IOptions<LegacyImportOptions> options) : ILegacyItemSource
{
    public const string ItemSelectSql =
        """
        SELECT
            ItemNumber,
            ItemName,
            CountOfInternalUnits,
            InternalUnitItemNumber,
            InternalUnitItemName,
            ItemType,
            ItemManufacturer,
            ItemColor,
            ItemTaste,
            ItemSize,
            ItemPacking,
            DealerPrice,
            MajorCustomerPrice,
            SpecialCustomerPrice,
            Price0,
            Price1,
            Price2,
            Price3,
            ItemClassification,
            StateOfOrigin,
            product_type,
            ParentItemNumber,
            IsProhibited
        FROM dbo.Items;
        """;

    private static readonly (int PosNumber, string Catalog, string Source)[] BranchCatalogs =
    [
        (22, "Shop1", LegacyCatalogs.Shop1),
        (5, "Shop2", LegacyCatalogs.Shop2),
        (10, "swstore", LegacyCatalogs.Swstore),
    ];

    public async Task<IReadOnlyList<LegacyItemObservation>> ReadAsync(CancellationToken cancellationToken)
    {
        var central = CentralConnectionString();
        var rows = new List<LegacyItemObservation>(40_000);
        rows.AddRange(await ReadCatalogAsync(central, LegacyCatalogs.Central, cancellationToken));
        var branchConnections = await ReadBranchConnectionsAsync(central, cancellationToken);
        foreach (var (catalog, source, connectionString) in branchConnections)
        {
            _ = catalog;
            rows.AddRange(await ReadCatalogAsync(connectionString, source, cancellationToken));
        }

        return rows;
    }

    private string CentralConnectionString()
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Server)
            || !string.Equals(settings.Database, LegacyImportOptions.ExpectedDatabase, StringComparison.Ordinal)
            || settings.ConnectTimeoutSeconds is < 1 or > 120)
        {
            throw new LegacyImportException("Legacy import is not configured for saleemPOSDB.");
        }

        return new SqlConnectionStringBuilder
        {
            DataSource = settings.Server.Trim(),
            InitialCatalog = LegacyImportOptions.ExpectedDatabase,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = settings.ConnectTimeoutSeconds,
            ApplicationName = "XtraPOS.LegacyItemDryRun",
            ApplicationIntent = ApplicationIntent.ReadOnly,
        }.ConnectionString;
    }

    private static async Task<IReadOnlyList<(string Catalog, string Source, string ConnectionString)>> ReadBranchConnectionsAsync(
        string centralConnectionString,
        CancellationToken cancellationToken)
    {
        var found = new List<(int PosNumber, string ConnectionString)>();
        await using var connection = new SqlConnection(centralConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT POSNumber, POSConnectionString
            FROM dbo.POSs
            WHERE IsItReference = 1
              AND POSNumber IN (22, 5, 10);
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(1))
            {
                throw new LegacyImportException($"POS {reader.GetInt32(0)} has no catalog connection.");
            }

            found.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        var resolved = new List<(string Catalog, string Source, string ConnectionString)>();
        foreach (var expected in BranchCatalogs)
        {
            var matches = found.Where(row => row.PosNumber == expected.PosNumber).ToList();
            if (matches.Count != 1)
            {
                throw new LegacyImportException($"POS {expected.PosNumber} did not resolve to one {expected.Catalog} catalog.");
            }

            var builder = new SqlConnectionStringBuilder(matches[0].ConnectionString);
            if (!string.Equals(builder.InitialCatalog, expected.Catalog, StringComparison.OrdinalIgnoreCase))
            {
                throw new LegacyImportException($"POS {expected.PosNumber} did not match the {expected.Catalog} catalog.");
            }

            builder.ApplicationName = "XtraPOS.LegacyItemDryRun";
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
            if (builder.ConnectTimeout < 20)
            {
                builder.ConnectTimeout = 20;
            }

            resolved.Add((expected.Catalog, expected.Source, builder.ConnectionString));
        }

        return resolved;
    }

    private static async Task<List<LegacyItemObservation>> ReadCatalogAsync(
        string connectionString,
        string source,
        CancellationToken cancellationToken)
    {
        var rows = new List<LegacyItemObservation>(source == LegacyCatalogs.Central ? 34_000 : 3_000);
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(ItemSelectSql, connection)
            {
                CommandTimeout = 300,
            };
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var itemNumber = ReadText(reader, 0);
                var itemName = ReadOptional(reader, 1);
                var units = ReadDecimal(reader, 2);
                var unitNumber = ReadOptional(reader, 3);
                var unitName = ReadOptional(reader, 4);
                var itemType = ReadOptional(reader, 5);
                var manufacturer = ReadOptional(reader, 6);
                var color = ReadOptional(reader, 7);
                var taste = ReadOptional(reader, 8);
                var size = ReadOptional(reader, 9);
                var packing = ReadOptional(reader, 10);
                var dealer = ReadDecimal(reader, 11);
                var major = ReadDecimal(reader, 12);
                var special = ReadDecimal(reader, 13);
                var price0 = ReadDecimal(reader, 14);
                var price1 = ReadDecimal(reader, 15);
                var price2 = ReadDecimal(reader, 16);
                var price3 = ReadDecimal(reader, 17);
                var classification = ReadOptional(reader, 18);
                var origin = ReadOptional(reader, 19);
                var productType = ReadOptional(reader, 20);
                var parent = ReadOptional(reader, 21);
                var prohibited = reader.IsDBNull(22) ? (bool?)null : reader.GetBoolean(22);
                rows.Add(new LegacyItemObservation(
                    source,
                    itemNumber,
                    itemName,
                    itemType,
                    manufacturer,
                    color,
                    taste,
                    size,
                    packing,
                    classification,
                    origin,
                    productType,
                    parent,
                    units,
                    unitNumber,
                    unitName,
                    dealer,
                    major,
                    special,
                    price0,
                    price1,
                    price2,
                    price3,
                    prohibited));
            }
        }
        catch (LegacyImportException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new LegacyImportException($"Could not read the {source} item catalog.");
        }

        if (rows.Count == 0)
        {
            throw new LegacyImportException($"The {source} item catalog returned no rows.");
        }

        return rows;
    }

    private static string ReadText(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal))?.Trim() ?? "";

    private static string? ReadOptional(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

    private static decimal? ReadDecimal(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal));
}
