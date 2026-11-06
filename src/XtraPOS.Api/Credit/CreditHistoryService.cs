using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Credit;

public sealed record CreditMovementView(
    string OperationId,
    string EventType,
    decimal Amount,
    decimal SignedEffect,
    decimal BalanceAfter,
    DateTime ConfirmedAt,
    Guid? SaleId,
    string? Reference,
    string? BranchName,
    string? Cashier,
    string? Notes,
    bool Pending);

public sealed record CreditHistoryView(
    string Source,
    bool Stale,
    decimal ConfirmedOutstanding,
    decimal DebtTotal,
    decimal RepaymentTotal,
    int TotalCount,
    IReadOnlyList<CreditMovementView> Movements);

public sealed class CreditHistoryService
{
    public async Task<CreditHistoryView> ReadAsync(
        SqlConnection? central,
        SqlConnection branch,
        Guid businessId,
        Guid customerId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        if (central is not null)
        {
            return await ReadCentralAsync(central, businessId, customerId, skip, take, cancellationToken);
        }

        return await ReadReplicaAsync(branch, businessId, customerId, skip, take, cancellationToken);
    }

    public async Task<string?> DocumentAsync(
        SqlConnection? central,
        SqlConnection branch,
        Guid businessId,
        Guid? saleId,
        string? operationId,
        CancellationToken cancellationToken)
    {
        if (saleId is Guid sale && await LocalSaleExistsAsync(branch, sale, cancellationToken))
        {
            return await CreditDocumentSnapshot.ReadAsync(branch, sale, 0m, cancellationToken);
        }

        var payload = await PayloadAsync(central, branch, businessId, saleId, operationId, cancellationToken);
        return string.IsNullOrWhiteSpace(payload) ? null : payload;
    }

    private static async Task<CreditHistoryView> ReadCentralAsync(SqlConnection central, Guid businessId, Guid customerId, int skip, int take, CancellationToken cancellationToken)
    {
        var outstanding = await MoneyAsync(central, """
            SELECT ISNULL(SUM(receivable.OutstandingAmount), 0)
            FROM dbo.CustomerReceivables AS receivable
            INNER JOIN dbo.Customers AS customer ON customer.CustomerID = receivable.CustomerID
            WHERE receivable.CustomerID = @customer AND customer.BusinessID = @business AND receivable.Status IN (N'Open', N'Partial');
            """, businessId, customerId, cancellationToken);
        var rows = await MovementsAsync(central, CentralSql, businessId, customerId, skip, take, cancellationToken);
        var totals = await TotalsAsync(central, businessId, customerId, cancellationToken);
        return new CreditHistoryView("central", false, outstanding, totals.Debt, totals.Repaid, totals.Count, rows);
    }

    private static async Task<CreditHistoryView> ReadReplicaAsync(SqlConnection branch, Guid businessId, Guid customerId, int skip, int take, CancellationToken cancellationToken)
    {
        var rows = await MovementsAsync(branch, ReplicaSql, businessId, customerId, skip, take, cancellationToken);
        decimal balance = 0m;
        await using (var command = new SqlCommand(
            "SELECT ISNULL(SUM(SignedEffect), 0) FROM dbo.BranchFinancialInbox WHERE BusinessID = @business AND CustomerID = @customer;",
            branch))
        {
            command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
            command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
            balance = Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken));
        }

        var debt = rows.Where(row => row.SignedEffect > 0).Sum(row => row.SignedEffect);
        var repaid = rows.Where(row => row.SignedEffect < 0).Sum(row => -row.SignedEffect);
        return new CreditHistoryView("replica", true, balance, debt, repaid, rows.Count, rows);
    }

    private const string CentralSql = """
        SELECT ledger.IdempotencyKey,
               CASE
                   WHEN ledger.TransactionType = N'Reversal' AND ledger.PaymentID IS NULL THEN N'DebtReversal'
                   WHEN ledger.TransactionType = N'Reversal' THEN N'RepaymentReversal'
                   ELSE ledger.TransactionType
               END,
               ledger.Amount,
               CASE
                   WHEN ledger.TransactionType = N'CreditSale' THEN ledger.Amount
                   WHEN ledger.TransactionType = N'Repayment' THEN -ledger.Amount
                   WHEN ledger.TransactionType = N'Reversal' AND ledger.PaymentID IS NULL THEN -ledger.Amount
                   WHEN ledger.TransactionType = N'Reversal' THEN ledger.Amount
                   ELSE ledger.Amount
               END,
               SUM(CASE
                   WHEN ledger.TransactionType = N'CreditSale' THEN ledger.Amount
                   WHEN ledger.TransactionType = N'Repayment' THEN -ledger.Amount
                   WHEN ledger.TransactionType = N'Reversal' AND ledger.PaymentID IS NULL THEN -ledger.Amount
                   WHEN ledger.TransactionType = N'Reversal' THEN ledger.Amount
                   ELSE ledger.Amount
               END) OVER (ORDER BY ledger.OccurredAt, ledger.LedgerEntryID ROWS UNBOUNDED PRECEDING),
               ledger.OccurredAt,
               ledger.SourceSaleID,
               event.Payload
        FROM dbo.CustomerCreditLedger AS ledger
        INNER JOIN dbo.Customers AS customer ON customer.CustomerID = ledger.CustomerID AND customer.BusinessID = @business
        LEFT JOIN dbo.CentralFinancialEvents AS event ON event.OperationID = ledger.IdempotencyKey
        WHERE ledger.CustomerID = @customer
        ORDER BY ledger.OccurredAt DESC, ledger.LedgerEntryID DESC
        OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
        """;

    private const string ReplicaSql = """
        SELECT OperationID, EventType, Amount, SignedEffect,
               SUM(SignedEffect) OVER (ORDER BY ConfirmedAt, OperationID ROWS UNBOUNDED PRECEDING),
               ConfirmedAt, SourceSaleID, Payload
        FROM dbo.BranchFinancialInbox
        WHERE BusinessID = @business AND CustomerID = @customer
        ORDER BY ConfirmedAt DESC, OperationID DESC
        OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
        """;

    private static async Task<IReadOnlyList<CreditMovementView>> MovementsAsync(SqlConnection connection, string sql, Guid businessId, Guid customerId, int skip, int take, CancellationToken cancellationToken)
    {
        var rows = new List<CreditMovementView>();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        command.Parameters.Add("@skip", SqlDbType.Int).Value = skip;
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader.IsDBNull(7) ? null : reader.GetString(7);
            var names = Names(payload);
            rows.Add(new CreditMovementView(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                names.Reference,
                names.Branch,
                names.Cashier,
                names.Notes,
                false));
        }

        return rows;
    }

    private static async Task<(decimal Debt, decimal Repaid, int Count)> TotalsAsync(SqlConnection central, Guid businessId, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT
                ISNULL(SUM(CASE WHEN TransactionType = N'CreditSale' THEN Amount ELSE 0 END), 0),
                ISNULL(SUM(CASE WHEN TransactionType = N'Repayment' THEN Amount ELSE 0 END), 0),
                COUNT(*)
            FROM dbo.CustomerCreditLedger AS ledger
            INNER JOIN dbo.Customers AS customer ON customer.CustomerID = ledger.CustomerID AND customer.BusinessID = @business
            WHERE ledger.CustomerID = @customer;
            """,
            central);
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetInt32(2));
    }

    private static async Task<decimal> MoneyAsync(SqlConnection connection, string sql, Guid businessId, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> LocalSaleExistsAsync(SqlConnection branch, Guid saleId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.Sales WHERE SaleID = @sale;", branch);
        command.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<string?> PayloadAsync(SqlConnection? central, SqlConnection branch, Guid businessId, Guid? saleId, string? operationId, CancellationToken cancellationToken)
    {
        if (central is not null)
        {
            var centralPayload = await OneAsync(central, """
                SELECT TOP (1) Payload FROM dbo.CentralFinancialEvents
                WHERE BusinessID = @business AND (@operation IS NULL OR OperationID = @operation) AND (@sale IS NULL OR SourceSaleID = @sale)
                ORDER BY EventSequence DESC;
                """, businessId, saleId, operationId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(centralPayload))
            {
                return centralPayload;
            }
        }

        return await OneAsync(branch, """
            SELECT TOP (1) Payload FROM dbo.BranchFinancialInbox
            WHERE BusinessID = @business AND (@operation IS NULL OR OperationID = @operation) AND (@sale IS NULL OR SourceSaleID = @sale)
            ORDER BY EventSequence DESC;
            """, businessId, saleId, operationId, cancellationToken);
    }

    private static async Task<string?> OneAsync(SqlConnection connection, string sql, Guid businessId, Guid? saleId, string? operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        command.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = (object?)saleId ?? DBNull.Value;
        command.Parameters.Add("@operation", SqlDbType.NVarChar, 80).Value = (object?)operationId ?? DBNull.Value;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    private static (string? Reference, string? Branch, string? Cashier, string? Notes) Names(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (null, null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return (
                Text(root, "saleNumber"),
                Text(root, "branchName"),
                Text(root, "cashier"),
                Text(root, "notes"));
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    private static string? Text(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
