using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;

namespace XtraPOS.Api.Data.Central;

public sealed class CentralMigrationRunner
{
    public const string MigrationId = "001_CentralFoundation";
    public const string MigrationName = "Central foundation";
    public const string OwnershipMigrationId = "002_ItemOwnership";
    public const string OwnershipMigrationName = "Item ownership";
    public const string BranchCodeMigrationId = "003_BranchCodeUniqueness";
    public const string BranchCodeMigrationName = "Branch code uniqueness";
    public const string UserAdministrationMigrationId = "004_UserAdministration";
    public const string UserAdministrationMigrationName = "User administration";
    public const string CustomerMasterMigrationId = "005_CustomerMaster";
    public const string CustomerMasterMigrationName = "Customer master";
    public const string ArabicSearchMigrationId = "006_ArabicSearchKeys";
    public const string ArabicSearchMigrationName = "Arabic search keys";
    public const string CustomerVerificationMigrationId = "007_CustomerVerificationAndSpecialPrices";
    public const string CustomerVerificationMigrationName = "Customer verification and special prices";
    public const string TwilioVerificationMigrationId = "008_TwilioPhoneVerification";
    public const string TwilioVerificationMigrationName = "Twilio phone verification";
    public const string RegistrationVerificationMigrationId = "009_RegistrationPhoneVerification";
    public const string RegistrationVerificationMigrationName = "Registration phone verification";
    public const string QuantitySpecialPriceMigrationId = "010_QuantitySpecialPrices";
    public const string QuantitySpecialPriceMigrationName = "Quantity special prices";
    public const string CustomerCreditMigrationId = "011_CustomerCredit";
    public const string CustomerCreditMigrationName = "Customer credit authority";
    public const string CreditCollectionMigrationId = "012_CreditCollections";
    public const string CreditCollectionMigrationName = "Credit collections";
    public const string ReceivableSaleNumberMigrationId = "013_ReceivableSaleNumber";
    public const string CreditReversalMigrationId = "014_CreditReversals";
    public const string CreditReversalMigrationName = "Credit repayment reversals";
    public const string ReceivableSaleNumberMigrationName = "Receivable sale number";
    public const string CentralPosMigrationId = "015_CentralPosAndBranchSql";
    public const string CentralPosMigrationName = "Central POS registry and branch SQL endpoints";

    private static readonly (string Id, string Name, string Resource)[] Migrations =
    [
        (MigrationId, MigrationName, "XtraPOS.Api.Data.Central.Migrations.001_CentralFoundation.sql"),
        (OwnershipMigrationId, OwnershipMigrationName, "XtraPOS.Api.Data.Central.Migrations.002_ItemOwnership.sql"),
        (BranchCodeMigrationId, BranchCodeMigrationName, "XtraPOS.Api.Data.Central.Migrations.003_BranchCodeUniqueness.sql"),
        (UserAdministrationMigrationId, UserAdministrationMigrationName, "XtraPOS.Api.Data.Central.Migrations.004_UserAdministration.sql"),
        (CustomerMasterMigrationId, CustomerMasterMigrationName, "XtraPOS.Api.Data.Central.Migrations.005_CustomerMaster.sql"),
        (ArabicSearchMigrationId, ArabicSearchMigrationName, "XtraPOS.Api.Data.Central.Migrations.006_ArabicSearchKeys.sql"),
        (CustomerVerificationMigrationId, CustomerVerificationMigrationName, "XtraPOS.Api.Data.Central.Migrations.007_CustomerVerificationAndSpecialPrices.sql"),
        (TwilioVerificationMigrationId, TwilioVerificationMigrationName, "XtraPOS.Api.Data.Central.Migrations.008_TwilioPhoneVerification.sql"),
        (RegistrationVerificationMigrationId, RegistrationVerificationMigrationName, "XtraPOS.Api.Data.Central.Migrations.009_RegistrationPhoneVerification.sql"),
        (QuantitySpecialPriceMigrationId, QuantitySpecialPriceMigrationName, "XtraPOS.Api.Data.Central.Migrations.010_QuantitySpecialPrices.sql"),
        (CustomerCreditMigrationId, CustomerCreditMigrationName, "XtraPOS.Api.Data.Central.Migrations.011_CustomerCredit.sql"),
        (CreditCollectionMigrationId, CreditCollectionMigrationName, "XtraPOS.Api.Data.Central.Migrations.012_CreditCollections.sql"),
        (ReceivableSaleNumberMigrationId, ReceivableSaleNumberMigrationName, "XtraPOS.Api.Data.Central.Migrations.013_ReceivableSaleNumber.sql"),
        (CreditReversalMigrationId, CreditReversalMigrationName, "XtraPOS.Api.Data.Central.Migrations.014_CreditReversals.sql"),
        (CentralPosMigrationId, CentralPosMigrationName, "XtraPOS.Api.Data.Central.Migrations.015_CentralPosAndBranchSql.sql"),
    ];

    private static readonly string ApplicationVersion =
        typeof(CentralMigrationRunner).Assembly.GetName().Version?.ToString(3) ?? "0.3.0";

    public async Task ApplyAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var applied = await ReadAppliedAsync(connection, transaction, cancellationToken);
        foreach (var migration in Migrations)
        {
            if (applied.Contains(migration.Id))
            {
                continue;
            }

            var sql = ReadSql(migration.Resource);
            await using (var command = new SqlCommand(sql, connection, transaction))
            {
                command.CommandTimeout = 120;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.DatabaseSchemaHistory (MigrationID, MigrationName, ApplicationVersion, AppliedAt)
                VALUES (@id, @name, @version, SYSUTCDATETIME());
                """,
                connection,
                transaction);
            insert.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = migration.Id;
            insert.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = migration.Name;
            insert.Parameters.Add("@version", SqlDbType.NVarChar, 50).Value = ApplicationVersion;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await ArabicSearchKeyBackfill.ApplyAsync(connection, cancellationToken);
    }

    private static async Task<HashSet<string>> ReadAppliedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using var exists = new SqlCommand(
            "SELECT OBJECT_ID(N'dbo.DatabaseSchemaHistory', N'U');",
            connection,
            transaction);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return applied;
        }

        await using var command = new SqlCommand(
            "SELECT MigrationID FROM dbo.DatabaseSchemaHistory;",
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    private static string ReadSql(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing migration resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
