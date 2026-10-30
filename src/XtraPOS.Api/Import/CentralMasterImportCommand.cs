using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Import;

public static class CentralMasterImportCommand
{
    public const string CommandName = "--import-central-master-data";
    public const string PrepareTestCommand = "--prepare-central-import-test";
    public const string DropTestCommand = "--drop-central-import-test";
    public const string SourceServerOption = "--source-server";
    public const string DryRunOption = "--dry-run";
    public const string TestDatabaseName = "XtraPOS_Central_Test_Import";
    public const int AuditedItemCount = 2814;

    private static readonly string[] ForbiddenTargets =
    [
        BranchDatabase.Name,
        "XtraPOS_Central2",
        "SharbelManager",
        "SharbelPOS",
        "SharbelServer",
        "SharbelStore",
        LegacyCatalogs.Central,
        LegacyCatalogs.Shop1,
        LegacyCatalogs.Shop2,
        LegacyCatalogs.Swstore,
    ];

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.Ordinal)
            || string.Equals(arg, PrepareTestCommand, StringComparison.Ordinal)
            || string.Equals(arg, DropTestCommand, StringComparison.Ordinal));

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        SqlEndpointSettings central,
        PasswordService passwords,
        CentralMigrationRunner migrations,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var confirmed = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
        if (!TryAuthorizeTarget(confirmed, out var denial))
        {
            output.WriteLine(denial);
            return 1;
        }

        if (args.Any(arg => string.Equals(arg, DropTestCommand, StringComparison.Ordinal)))
        {
            return await DropTestAsync(central, confirmed!, output, cancellationToken);
        }

        if (args.Any(arg => string.Equals(arg, PrepareTestCommand, StringComparison.Ordinal)))
        {
            return await PrepareTestAsync(central, confirmed!, migrations, output, cancellationToken);
        }

        var sourceServer = ReadOption(args, SourceServerOption);
        if (!SourceMatches(central, sourceServer))
        {
            output.WriteLine("Import requires --source-server matching the saved Central SQL server.");
            return 1;
        }

        var dryRun = args.Any(arg => string.Equals(arg, DryRunOption, StringComparison.Ordinal));
        var observations = new List<LegacyItemObservation>();
        foreach (var source in CentralMasterCatalog.SourceOrder)
        {
            observations.AddRange(await ReadItemsAsync(central, source, cancellationToken));
        }

        var employees = await ReadEmployeesAsync(central, cancellationToken);
        var plan = CentralMasterCatalog.Plan(observations, employees);
        WritePlan(output, plan);
        await using var target = await OpenAsync(central, confirmed!, readOnly: dryRun, cancellationToken);
        if (!await TargetIsReadyAsync(target, output, cancellationToken))
        {
            return 1;
        }

        var comparison = await CompareAsync(target, plan, cancellationToken);
        WriteComparison(output, comparison);
        if (plan.Items.Count != AuditedItemCount || plan.Anomalies.Any(item => item.Contains("was not found in UsersTable", StringComparison.Ordinal)))
        {
            output.WriteLine("BASELINE_MISMATCH");
            return 2;
        }

        if (dryRun)
        {
            output.WriteLine("DRY_RUN_COMPLETE");
            return 0;
        }

        output.WriteLine("== BEFORE");
        await WriteSamplesAsync(target, output, cancellationToken);
        if (args.Any(arg => string.Equals(arg, "--require-empty-target", StringComparison.Ordinal))
            && !await OfficialTargetIsEmptyAsync(target, output, cancellationToken))
        {
            return 2;
        }

        await ExecutePlanAsync(target, plan, passwords, output, cancellationToken);
        output.WriteLine("== AFTER");
        await WriteSamplesAsync(target, output, cancellationToken);
        output.WriteLine("IMPORT_COMPLETE");
        return 0;
    }

    public static bool TryAuthorizeTarget(string? confirmed, out string denial)
    {
        if (!CentralDatabase.IsAllowedName(confirmed) || ForbiddenTargets.Contains(confirmed, StringComparer.OrdinalIgnoreCase))
        {
            denial = "Central import requires --confirm-database XtraPOS_Central or a XtraPOS_Central_Test database.";
            return false;
        }

        denial = "";
        return true;
    }

    private static async Task<int> PrepareTestAsync(
        SqlEndpointSettings central,
        string confirmed,
        CentralMigrationRunner migrations,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmed, TestDatabaseName, StringComparison.Ordinal))
        {
            output.WriteLine("Test preparation only allows XtraPOS_Central_Test_Import.");
            return 1;
        }

        await using var master = await OpenAsync(central, "master", readOnly: false, cancellationToken);
        await using (var exists = new SqlCommand("SELECT DB_ID(@name);", master))
        {
            exists.Parameters.AddWithValue("@name", confirmed);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null or DBNull)
            {
                await CentralDatabaseFiles.CreateNewAsync(master, confirmed, cancellationToken);
                output.WriteLine("TEST_DATABASE_CREATED");
            }
        }

        await migrations.ApplyAsync(ConnectionString(central, confirmed, readOnly: false), cancellationToken);
        await using var target = await OpenAsync(central, confirmed, readOnly: false, cancellationToken);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.Businesses;", target);
        if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, BusinessDisplayName, Description, Address, IsActive, CreatedAt)
                VALUES (NEWID(), N'Smoker World', N'SW', N'البيانات المركزية', NULL, 1, SYSUTCDATETIME());
                """,
                target);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        output.WriteLine("TEST_DATABASE_READY");
        return 0;
    }

    private static async Task<int> DropTestAsync(
        SqlEndpointSettings central,
        string confirmed,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmed, TestDatabaseName, StringComparison.Ordinal) || !CentralDatabase.IsTestName(confirmed))
        {
            output.WriteLine("Refusing to drop a database that is not XtraPOS_Central_Test_Import.");
            return 1;
        }

        SqlConnection.ClearAllPools();
        await using var master = await OpenAsync(central, "master", readOnly: false, cancellationToken);
        await using var command = new SqlCommand(
            """
            IF DB_ID(@name) IS NOT NULL
            BEGIN
                ALTER DATABASE [XtraPOS_Central_Test_Import] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [XtraPOS_Central_Test_Import];
            END
            """,
            master);
        command.Parameters.AddWithValue("@name", confirmed);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync(cancellationToken);
        output.WriteLine("TEST_DATABASE_DROPPED");
        return 0;
    }

    private static async Task ExecutePlanAsync(
        SqlConnection connection,
        CentralMasterPlan plan,
        PasswordService passwords,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await InTransaction(connection, async transaction =>
        {
            await EnsureSecurityAsync(connection, transaction, cancellationToken);
            await HistoryAsync(connection, transaction, "CentralMasterSecurity", plan.Users.Count, "Roles and permissions ensured.", cancellationToken);
        }, cancellationToken);
        var items = 0;
        var units = 0;
        var prices = 0;
        var users = 0;
        await InTransaction(connection, async transaction =>
        {
            items = await UpsertItemsAsync(connection, transaction, plan, cancellationToken);
            await HistoryAsync(connection, transaction, "CentralMasterItems", items, "Canonical items imported.", cancellationToken);
        }, cancellationToken);
        await InTransaction(connection, async transaction =>
        {
            units = await UpsertUnitsAsync(connection, transaction, plan, cancellationToken);
            await HistoryAsync(connection, transaction, "CentralMasterUnits", units, "Item unit links imported.", cancellationToken);
        }, cancellationToken);
        await InTransaction(connection, async transaction =>
        {
            prices = await UpsertPricesAsync(connection, transaction, plan, cancellationToken);
            await HistoryAsync(connection, transaction, "CentralMasterPrices", prices, "Master prices imported. Negative prices excluded. Zero prices kept.", cancellationToken);
        }, cancellationToken);
        await InTransaction(connection, async transaction =>
        {
            users = await UpsertUsersAsync(connection, transaction, plan, passwords, cancellationToken);
            await HistoryAsync(connection, transaction, "CentralMasterUsers", users, "Employees imported. Passwords were hashed and not recorded.", cancellationToken);
        }, cancellationToken);
        output.WriteLine($"ChangedItems={items} ChangedUnits={units} ChangedPrices={prices} ChangedUsers={users}");
    }

    private static async Task EnsureSecurityAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        foreach (var price in CentralMasterCatalog.PriceTypes)
        {
            await using var command = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.PriceTypes WHERE PriceCode = @code)
                    INSERT INTO dbo.PriceTypes (PriceTypeID, PriceCode, PriceName, SortOrder, IsActive, CreatedAt)
                    VALUES (NEWID(), @code, @name, @sort, 1, SYSUTCDATETIME());
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@code", price.Code);
            command.Parameters.AddWithValue("@name", price.Name);
            command.Parameters.AddWithValue("@sort", price.SortOrder);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var role in CentralMasterCatalog.Roles)
        {
            await using var command = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleCode = @code)
                    INSERT INTO dbo.Roles (RoleID, RoleCode, RoleName, IsSystemRole, IsActive, CreatedAt)
                    VALUES (NEWID(), @code, @name, 1, 1, SYSUTCDATETIME());
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@code", role.Code);
            command.Parameters.AddWithValue("@name", role.Name);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var permission in CentralMasterCatalog.Permissions)
        {
            await using var command = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = @code)
                    INSERT INTO dbo.Permissions (PermissionID, PermissionCode, PermissionName, Module, IsActive, CreatedAt)
                    VALUES (NEWID(), @code, @name, @module, 1, SYSUTCDATETIME());
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@code", permission.Code);
            command.Parameters.AddWithValue("@name", permission.Name);
            command.Parameters.AddWithValue("@module", permission.Module);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var grants = new SqlCommand(
            """
            INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
            SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
            FROM dbo.Roles AS role
            INNER JOIN dbo.Permissions AS permission
                ON (
                    role.RoleCode = N'Admin'
                    OR (role.RoleCode = N'Supervisor' AND permission.PermissionCode IN (
                        N'Users.View', N'Roles.View', N'Setup.View', N'Device.View',
                        N'Items.View', N'Items.EditMaster', N'Items.EditBranchState', N'Items.EditBranchPrice',
                        N'Customers.View'))
                    OR (role.RoleCode = N'User' AND permission.PermissionCode = N'Items.View')
                )
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.RolePermissions AS existing
                WHERE existing.RoleID = role.RoleID AND existing.PermissionID = permission.PermissionID);
            """,
            connection,
            transaction);
        await grants.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> UpsertItemsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CentralMasterPlan plan,
        CancellationToken cancellationToken)
    {
        var table = new DataTable();
        foreach (var (name, type) in new (string, Type)[]
        {
            ("ItemNumber", typeof(string)), ("ItemName", typeof(string)), ("ItemType", typeof(string)),
            ("Manufacturer", typeof(string)), ("Color", typeof(string)), ("Taste", typeof(string)),
            ("Size", typeof(string)), ("Packing", typeof(string)), ("Classification", typeof(string)),
            ("CountryOfOrigin", typeof(string)), ("ProductType", typeof(string)), ("ParentItemNumber", typeof(string)),
            ("IsProhibited", typeof(bool)), ("IsActive", typeof(bool)),
        })
        {
            table.Columns.Add(name, type);
        }

        foreach (var item in plan.Items)
        {
            table.Rows.Add(
                item.ItemNumber, Fit(item.ItemName, 500), Db(Fit(item.ItemType, 100)), Db(Fit(item.Manufacturer, 100)),
                Db(Fit(item.Color, 100)), Db(Fit(item.Taste, 100)), Db(Fit(item.Size, 100)), Db(Fit(item.Packing, 100)),
                Db(Fit(item.Classification, 200)), Db(Fit(item.CountryOfOrigin, 100)), Db(Fit(item.ProductType, 50)),
                Db(item.ParentItemNumber), item.IsProhibited, item.IsActive);
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE #Items (
                ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                ItemName nvarchar(500) COLLATE DATABASE_DEFAULT NOT NULL,
                ItemType nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Manufacturer nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Color nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Taste nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Size nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Packing nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Classification nvarchar(200) COLLATE DATABASE_DEFAULT NULL,
                CountryOfOrigin nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                ProductType nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                ParentItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                IsProhibited bit NOT NULL,
                IsActive bit NOT NULL);
            """, cancellationToken);
        await BulkAsync(connection, transaction, "#Items", table, cancellationToken);
        return await ScalarAsync(connection, transaction, """
            DECLARE @changed int = 0;
            INSERT INTO dbo.Items
                (ItemID, ItemNumber, ItemName, ItemType, Manufacturer, Color, Taste, Size, Packing, Classification, CountryOfOrigin, ProductType, ParentItemID, IsProhibited, IsPublishedOnline, IsActive, CreatedAt, UpdatedAt)
            SELECT NEWID(), source.ItemNumber, source.ItemName, source.ItemType, source.Manufacturer, source.Color, source.Taste, source.Size, source.Packing, source.Classification, source.CountryOfOrigin, source.ProductType, NULL, source.IsProhibited, 0, source.IsActive, SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM #Items AS source
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Items AS existing WHERE existing.ItemNumber = source.ItemNumber);
            SET @changed = @@ROWCOUNT;
            UPDATE existing
            SET ItemName = source.ItemName, ItemType = source.ItemType, Manufacturer = source.Manufacturer, Color = source.Color,
                Taste = source.Taste, Size = source.Size, Packing = source.Packing, Classification = source.Classification,
                CountryOfOrigin = source.CountryOfOrigin, ProductType = source.ProductType, IsProhibited = source.IsProhibited,
                IsActive = source.IsActive, UpdatedAt = SYSUTCDATETIME()
            FROM dbo.Items AS existing
            INNER JOIN #Items AS source ON source.ItemNumber = existing.ItemNumber
            WHERE existing.ItemName <> source.ItemName
               OR ISNULL(existing.ItemType, N'') <> ISNULL(source.ItemType, N'')
               OR ISNULL(existing.Manufacturer, N'') <> ISNULL(source.Manufacturer, N'')
               OR ISNULL(existing.Color, N'') <> ISNULL(source.Color, N'')
               OR ISNULL(existing.Taste, N'') <> ISNULL(source.Taste, N'')
               OR ISNULL(existing.Size, N'') <> ISNULL(source.Size, N'')
               OR ISNULL(existing.Packing, N'') <> ISNULL(source.Packing, N'')
               OR ISNULL(existing.Classification, N'') <> ISNULL(source.Classification, N'')
               OR ISNULL(existing.CountryOfOrigin, N'') <> ISNULL(source.CountryOfOrigin, N'')
               OR ISNULL(existing.ProductType, N'') <> ISNULL(source.ProductType, N'')
               OR existing.IsProhibited <> source.IsProhibited
               OR existing.IsActive <> source.IsActive;
            SET @changed = @changed + @@ROWCOUNT;
            UPDATE child
            SET ParentItemID = parent.ItemID, UpdatedAt = SYSUTCDATETIME()
            FROM dbo.Items AS child
            INNER JOIN #Items AS source ON source.ItemNumber = child.ItemNumber
            INNER JOIN dbo.Items AS parent ON parent.ItemNumber = source.ParentItemNumber
            WHERE source.ParentItemNumber IS NOT NULL
              AND (child.ParentItemID IS NULL OR child.ParentItemID <> parent.ItemID);
            SELECT @changed + @@ROWCOUNT;
            """, cancellationToken);
    }

    private static async Task<int> UpsertUnitsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CentralMasterPlan plan,
        CancellationToken cancellationToken)
    {
        var table = new DataTable();
        table.Columns.Add("ItemNumber", typeof(string));
        table.Columns.Add("UnitsPerPackage", typeof(decimal));
        table.Columns.Add("InternalUnitItemNumber", typeof(string));
        table.Columns.Add("InternalUnitItemName", typeof(string));
        foreach (var unit in plan.Units)
        {
            table.Rows.Add(unit.ItemNumber, unit.UnitsPerPackage, unit.InternalUnitItemNumber, Db(Fit(unit.InternalUnitItemName, 500)));
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE #Units (
                ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                UnitsPerPackage decimal(18, 4) NOT NULL,
                InternalUnitItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                InternalUnitItemName nvarchar(500) COLLATE DATABASE_DEFAULT NULL);
            """, cancellationToken);
        await BulkAsync(connection, transaction, "#Units", table, cancellationToken);
        return await ScalarAsync(connection, transaction, """
            DECLARE @changed int = 0;
            INSERT INTO dbo.ItemUnits
                (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit, IsActive, CreatedAt, UpdatedAt)
            SELECT NEWID(), item.ItemID, source.UnitsPerPackage, target.ItemID, source.InternalUnitItemNumber, source.InternalUnitItemName, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM #Units AS source
            INNER JOIN dbo.Items AS item ON item.ItemNumber = source.ItemNumber
            INNER JOIN dbo.Items AS target ON target.ItemNumber = source.InternalUnitItemNumber
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.ItemUnits AS existing
                WHERE existing.ItemID = item.ItemID AND existing.InternalUnitItemNumber = source.InternalUnitItemNumber);
            SET @changed = @@ROWCOUNT;
            UPDATE existing
            SET UnitsPerPackage = source.UnitsPerPackage, InternalUnitItemID = target.ItemID, InternalUnitItemName = source.InternalUnitItemName, UpdatedAt = SYSUTCDATETIME()
            FROM dbo.ItemUnits AS existing
            INNER JOIN dbo.Items AS item ON item.ItemID = existing.ItemID
            INNER JOIN #Units AS source ON source.ItemNumber = item.ItemNumber AND source.InternalUnitItemNumber = existing.InternalUnitItemNumber
            INNER JOIN dbo.Items AS target ON target.ItemNumber = source.InternalUnitItemNumber
            WHERE existing.UnitsPerPackage <> source.UnitsPerPackage
               OR ISNULL(existing.InternalUnitItemName, N'') <> ISNULL(source.InternalUnitItemName, N'')
               OR existing.InternalUnitItemID IS NULL
               OR existing.InternalUnitItemID <> target.ItemID;
            SELECT @changed + @@ROWCOUNT;
            """, cancellationToken);
    }

    private static async Task<int> UpsertPricesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CentralMasterPlan plan,
        CancellationToken cancellationToken)
    {
        var table = new DataTable();
        table.Columns.Add("ItemNumber", typeof(string));
        table.Columns.Add("PriceCode", typeof(string));
        table.Columns.Add("Price", typeof(decimal));
        foreach (var price in plan.Prices)
        {
            table.Rows.Add(price.ItemNumber, price.PriceCode, price.Price);
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE #Prices (
                ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                PriceCode nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL,
                Price decimal(19, 4) NOT NULL);
            """, cancellationToken);
        await BulkAsync(connection, transaction, "#Prices", table, cancellationToken);
        return await ScalarAsync(connection, transaction, """
            DECLARE @changed int = 0;
            INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, Price, IsActive, CreatedAt, UpdatedAt)
            SELECT NEWID(), item.ItemID, priceType.PriceTypeID, source.Price, 1, SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM #Prices AS source
            INNER JOIN dbo.Items AS item ON item.ItemNumber = source.ItemNumber
            INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceCode = source.PriceCode
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.ItemPrices AS existing
                WHERE existing.ItemID = item.ItemID AND existing.PriceTypeID = priceType.PriceTypeID);
            SET @changed = @@ROWCOUNT;
            UPDATE existing
            SET Price = source.Price, UpdatedAt = SYSUTCDATETIME()
            FROM dbo.ItemPrices AS existing
            INNER JOIN dbo.Items AS item ON item.ItemID = existing.ItemID
            INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = existing.PriceTypeID
            INNER JOIN #Prices AS source ON source.ItemNumber = item.ItemNumber AND source.PriceCode = priceType.PriceCode
            WHERE existing.Price <> source.Price;
            SELECT @changed + @@ROWCOUNT;
            """, cancellationToken);
    }

    private static async Task<int> UpsertUsersAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CentralMasterPlan plan,
        PasswordService passwords,
        CancellationToken cancellationToken)
    {
        var table = new DataTable();
        table.Columns.Add("EmployeeNumber", typeof(string));
        table.Columns.Add("Username", typeof(string));
        table.Columns.Add("NormalizedUsername", typeof(string));
        table.Columns.Add("DisplayName", typeof(string));
        table.Columns.Add("FullName", typeof(string));
        table.Columns.Add("PasswordHash", typeof(string));
        table.Columns.Add("IsActive", typeof(bool));
        table.Columns.Add("RoleCode", typeof(string));
        foreach (var user in plan.Users)
        {
            var hash = passwords.Hash(new AppUser { Username = user.Username }, CreatePassword());
            table.Rows.Add(
                user.EmployeeNumber,
                user.Username,
                AccountIdentity.NormalizeUsername(user.Username),
                Fit(user.DisplayName, 150),
                Db(Fit(user.FullName, 300)),
                hash,
                user.IsActive,
                user.RoleCode);
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE #Users (
                EmployeeNumber nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                Username nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                NormalizedUsername nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                DisplayName nvarchar(150) COLLATE DATABASE_DEFAULT NOT NULL,
                FullName nvarchar(300) COLLATE DATABASE_DEFAULT NULL,
                PasswordHash nvarchar(1000) COLLATE DATABASE_DEFAULT NOT NULL,
                IsActive bit NOT NULL,
                RoleCode nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL);
            """, cancellationToken);
        await BulkAsync(connection, transaction, "#Users", table, cancellationToken);
        return await ScalarAsync(connection, transaction, """
            DECLARE @changed int = 0;
            INSERT INTO dbo.Users
                (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, CreatedAt)
            SELECT NEWID(), source.Username, source.NormalizedUsername, source.DisplayName, source.FullName, source.EmployeeNumber, source.PasswordHash, source.IsActive, 1, SYSUTCDATETIME()
            FROM #Users AS source
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Users AS existing WHERE existing.EmployeeNumber = source.EmployeeNumber);
            SET @changed = @@ROWCOUNT;
            UPDATE existing
            SET DisplayName = source.DisplayName, FullName = source.FullName, IsActive = source.IsActive, UpdatedAt = SYSUTCDATETIME()
            FROM dbo.Users AS existing
            INNER JOIN #Users AS source ON source.EmployeeNumber = existing.EmployeeNumber
            WHERE existing.DisplayName <> source.DisplayName
               OR ISNULL(existing.FullName, N'') <> ISNULL(source.FullName, N'')
               OR existing.IsActive <> source.IsActive;
            SET @changed = @changed + @@ROWCOUNT;
            INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
            SELECT usr.UserID, role.RoleID, SYSUTCDATETIME()
            FROM #Users AS source
            INNER JOIN dbo.Users AS usr ON usr.EmployeeNumber = source.EmployeeNumber
            INNER JOIN dbo.Roles AS role ON role.RoleCode = source.RoleCode
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.UserRoles AS existing
                WHERE existing.UserID = usr.UserID AND existing.RoleID = role.RoleID);
            SELECT @changed;
            """, cancellationToken);
    }

    private static async Task<bool> TargetIsReadyAsync(SqlConnection connection, TextWriter output, CancellationToken cancellationToken)
    {
        await using var role = new SqlCommand(
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';",
            connection);
        var value = await role.ExecuteScalarAsync(cancellationToken) as string;
        if (!string.Equals(value, DatabaseRole.Central, StringComparison.Ordinal))
        {
            output.WriteLine("Target database role is not Central.");
            return false;
        }

        await using var businesses = new SqlCommand("SELECT COUNT(*) FROM dbo.Businesses;", connection);
        if (Convert.ToInt32(await businesses.ExecuteScalarAsync(cancellationToken)) < 1)
        {
            output.WriteLine("Target Central database has no business identity.");
            return false;
        }

        return true;
    }

    private static async Task<bool> OfficialTargetIsEmptyAsync(
        SqlConnection connection,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM dbo.Businesses),
                (SELECT COUNT(*) FROM dbo.Branches),
                (SELECT COUNT(*) FROM dbo.Items),
                (SELECT COUNT(*) FROM dbo.ItemUnits),
                (SELECT COUNT(*) FROM dbo.Users);
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var businesses = reader.GetInt32(0);
        var branches = reader.GetInt32(1);
        var items = reader.GetInt32(2);
        var units = reader.GetInt32(3);
        var users = reader.GetInt32(4);
        if (businesses == 1 && branches == 0 && items == 0 && units == 0 && users == 0)
        {
            return true;
        }

        output.WriteLine("OFFICIAL_TARGET_NOT_EMPTY");
        return false;
    }

    private static async Task<(int Insert, int Update, int Unchanged)> CompareAsync(
        SqlConnection connection,
        CentralMasterPlan plan,
        CancellationToken cancellationToken)
    {
        var existing = await MapAsync(connection, null, "SELECT ItemNumber, ItemName FROM dbo.Items;", cancellationToken);
        var insert = plan.Items.Count(item => !existing.ContainsKey(item.ItemNumber));
        var present = plan.Items.Count - insert;
        return (insert, 0, present);
    }

    private static async Task WriteSamplesAsync(SqlConnection connection, TextWriter output, CancellationToken cancellationToken)
    {
        output.WriteLine("== FINGERPRINT");
        foreach (var row in await RowsAsync(connection, """
            SELECT 'Businesses', COUNT(*) FROM dbo.Businesses
            UNION ALL SELECT 'Branches', COUNT(*) FROM dbo.Branches
            UNION ALL SELECT 'Items', COUNT(*) FROM dbo.Items
            UNION ALL SELECT 'ItemUnits', COUNT(*) FROM dbo.ItemUnits
            UNION ALL SELECT 'PriceTypes', COUNT(*) FROM dbo.PriceTypes
            UNION ALL SELECT 'ItemPrices', COUNT(*) FROM dbo.ItemPrices
            UNION ALL SELECT 'Users', COUNT(*) FROM dbo.Users
            UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
            UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
            UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
            UNION ALL SELECT 'ActiveItems', COUNT(*) FROM dbo.Items WHERE IsActive = 1
            UNION ALL SELECT 'InactiveItems', COUNT(*) FROM dbo.Items WHERE IsActive = 0
            UNION ALL SELECT 'ActiveUsers', COUNT(*) FROM dbo.Users WHERE IsActive = 1
            UNION ALL SELECT 'InactiveUsers', COUNT(*) FROM dbo.Users WHERE IsActive = 0
            UNION ALL SELECT 'NegativePrices', COUNT(*) FROM dbo.ItemPrices WHERE Price < 0
            UNION ALL SELECT 'TechnicalItem', COUNT(*) FROM dbo.Items WHERE ItemNumber = N'XTRAPOS_DELETE_TEST_20260917115506'
            UNION ALL SELECT 'Item50', COUNT(*) FROM dbo.Items WHERE ItemNumber = N'50'
            UNION ALL SELECT 'DuplicateItemNumbers', COUNT(*) FROM (SELECT ItemNumber FROM dbo.Items GROUP BY ItemNumber HAVING COUNT(*) > 1) AS d
            UNION ALL SELECT 'OrphanUnitItems', COUNT(*) FROM dbo.ItemUnits AS u WHERE NOT EXISTS (SELECT 1 FROM dbo.Items AS i WHERE i.ItemID = u.ItemID)
            UNION ALL SELECT 'OrphanUnitTargets', COUNT(*) FROM dbo.ItemUnits AS u WHERE u.InternalUnitItemID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.Items AS i WHERE i.ItemID = u.InternalUnitItemID)
            UNION ALL SELECT 'DuplicatePriceCodes', COUNT(*) FROM (SELECT PriceCode FROM dbo.PriceTypes GROUP BY PriceCode HAVING COUNT(*) > 1) AS d
            UNION ALL SELECT 'DuplicateItemPrices', COUNT(*) FROM (SELECT ItemID, PriceTypeID FROM dbo.ItemPrices GROUP BY ItemID, PriceTypeID HAVING COUNT(*) > 1) AS d
            UNION ALL SELECT 'DuplicateUsernames', COUNT(*) FROM (SELECT NormalizedUsername FROM dbo.Users GROUP BY NormalizedUsername HAVING COUNT(*) > 1) AS d
            UNION ALL SELECT 'DuplicateEmployeeNumbers', COUNT(*) FROM (SELECT EmployeeNumber FROM dbo.Users WHERE EmployeeNumber IS NOT NULL GROUP BY EmployeeNumber HAVING COUNT(*) > 1) AS d
            UNION ALL SELECT 'DuplicateUserRoles', COUNT(*) FROM (SELECT UserID, RoleID FROM dbo.UserRoles GROUP BY UserID, RoleID HAVING COUNT(*) > 1) AS d
            UNION ALL SELECT 'UsersWithMultipleRoles', COUNT(*) FROM (SELECT UserID FROM dbo.UserRoles GROUP BY UserID HAVING COUNT(*) > 1) AS d;
            SELECT i.ItemNumber, i.ItemName, u.UnitsPerPackage, u.InternalUnitItemNumber, u.InternalUnitItemName
            FROM dbo.Items AS i
            LEFT JOIN dbo.ItemUnits AS u ON u.ItemID = i.ItemID
            WHERE i.ItemNumber IN (N'1087', N'1300', N'1350', N'1360');
            SELECT u.EmployeeNumber, r.RoleCode
            FROM dbo.Users AS u
            INNER JOIN dbo.UserRoles AS ur ON ur.UserID = u.UserID
            INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
            WHERE u.EmployeeNumber IN (N'905017083', N'402120067', N'421600008');
            SELECT name FROM sys.tables
            WHERE name IN (N'POSStations', N'Devices', N'ItemBranchInventory', N'BranchStockBalances', N'StockMovements', N'Sales');
            """, cancellationToken))
        {
            output.WriteLine(row);
        }
    }

    private static void WritePlan(TextWriter output, CentralMasterPlan plan)
    {
        output.WriteLine("Precedence=" + CentralMasterCatalog.PrecedenceText);
        output.WriteLine("Items=" + plan.Items.Count);
        output.WriteLine("ActiveItems=" + plan.ActiveItems);
        output.WriteLine("InactiveItems=" + plan.InactiveItems);
        output.WriteLine("BlankItemRows=" + plan.BlankItemRows);
        output.WriteLine("TechnicalRowsExcluded=" + plan.TechnicalRowsExcluded);
        output.WriteLine("TechnicalIdentitiesExcluded=" + plan.TechnicalIdentitiesExcluded);
        output.WriteLine("SelfLinks=" + plan.SelfLinks);
        output.WriteLine("ItemUnits=" + plan.Units.Count);
        output.WriteLine("OrphanLinksSkipped=" + plan.OrphanLinksSkipped);
        output.WriteLine("MultiHopLinks=" + plan.MultiHopLinks);
        output.WriteLine("CycleLinksSkipped=" + plan.CycleLinksSkipped);
        output.WriteLine("PriceTypes=" + CentralMasterCatalog.PriceTypes.Length);
        output.WriteLine("ItemPrices=" + plan.Prices.Count);
        output.WriteLine("ZeroPrices=" + plan.ZeroPrices);
        output.WriteLine("NegativeMasterPricesExcluded=" + plan.NegativeMasterPricesExcluded);
        output.WriteLine("ShopPricesIgnored=" + plan.ShopPricesIgnored);
        output.WriteLine("Users=" + plan.Users.Count);
        output.WriteLine("ActiveUsers=" + plan.ActiveUsers);
        output.WriteLine("InactiveUsers=" + plan.InactiveUsers);
        output.WriteLine("Roles=" + CentralMasterCatalog.Roles.Length);
        output.WriteLine("UserRoles=" + plan.Users.Count);
        foreach (var choice in plan.DuplicateChoices)
        {
            output.WriteLine("Duplicate " + choice.ItemNumber + ": " + choice.Reason);
        }

        output.WriteLine("Anomalies=" + plan.Anomalies.Count);
        foreach (var anomaly in plan.Anomalies.Where(item => !item.Contains("orphan unit target", StringComparison.Ordinal)))
        {
            output.WriteLine("Anomaly " + anomaly);
        }
    }

    private static void WriteComparison(TextWriter output, (int Insert, int Update, int Unchanged) comparison)
    {
        output.WriteLine("ItemsInsert=" + comparison.Insert);
        output.WriteLine("ItemsUpdate=" + comparison.Update);
        output.WriteLine("ItemsUnchanged=" + comparison.Unchanged);
    }

    private static async Task<List<LegacyItemObservation>> ReadItemsAsync(
        SqlEndpointSettings central,
        string source,
        CancellationToken cancellationToken)
    {
        if (!CentralMasterCatalog.SourceOrder.Contains(source, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Refusing a legacy catalog outside the four approved sources.");
        }

        var rows = new List<LegacyItemObservation>();
        await using var connection = await OpenAsync(central, source, readOnly: true, cancellationToken);
        await using var command = new SqlCommand(SqlLegacyItemSource.ItemSelectSql, connection) { CommandTimeout = 180 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyItemObservation(
                source,
                ReadText(reader, 0),
                ReadOptional(reader, 1),
                ReadOptional(reader, 5),
                ReadOptional(reader, 6),
                ReadOptional(reader, 7),
                ReadOptional(reader, 8),
                ReadOptional(reader, 9),
                ReadOptional(reader, 10),
                ReadOptional(reader, 18),
                ReadOptional(reader, 19),
                ReadOptional(reader, 20),
                ReadOptional(reader, 21),
                ReadDecimal(reader, 2),
                ReadOptional(reader, 3),
                ReadOptional(reader, 4),
                ReadDecimal(reader, 11),
                ReadDecimal(reader, 12),
                ReadDecimal(reader, 13),
                ReadDecimal(reader, 14),
                ReadDecimal(reader, 15),
                ReadDecimal(reader, 16),
                ReadDecimal(reader, 17),
                reader.IsDBNull(22) ? null : reader.GetBoolean(22)));
        }

        return rows;
    }

    private static async Task<List<LegacyEmployeeRecord>> ReadEmployeesAsync(
        SqlEndpointSettings central,
        CancellationToken cancellationToken)
    {
        var people = new List<LegacyEmployeeRecord>();
        await using var connection = await OpenAsync(central, LegacyCatalogs.Central, readOnly: true, cancellationToken);
        await using var command = new SqlCommand(SqlLegacyEmployeeSource.SelectSql, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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

    private static async Task<SqlConnection> OpenAsync(
        SqlEndpointSettings central,
        string catalog,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(ConnectionString(central, catalog, readOnly));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string ConnectionString(SqlEndpointSettings central, string catalog, bool readOnly)
    {
        var dataSource = central.Port is > 0 ? central.Server + "," + central.Port.Value : central.Server;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = catalog,
            UserID = central.Username,
            Password = central.Password,
            Encrypt = central.Encrypt,
            TrustServerCertificate = central.TrustServerCertificate,
            ConnectTimeout = 30,
            ApplicationName = "XtraPOS.CentralMasterImport",
        };
        if (readOnly)
        {
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        }

        return builder.ConnectionString;
    }

    private static bool SourceMatches(SqlEndpointSettings central, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return false;
        }

        var expected = central.Port is > 0 ? central.Server + "," + central.Port.Value : central.Server;
        return string.Equals(requested.Trim(), expected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested.Trim(), central.Server, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task InTransaction(
        SqlConnection connection,
        Func<SqlTransaction, Task> action,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await action(transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task HistoryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string importType,
        int rows,
        string notes,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.DataImportHistory (ImportID, ImportType, Source, StartedAt, CompletedAt, Status, RowsProcessed, Notes)
            VALUES (NEWID(), @type, N'saleemPOSDB,Shop1,Shop2,swstore', SYSUTCDATETIME(), SYSUTCDATETIME(), N'Completed', @rows, @notes);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@type", importType);
        command.Parameters.AddWithValue("@rows", rows);
        command.Parameters.AddWithValue("@notes", notes.Length <= 400 ? notes : notes[..400]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 180 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ScalarAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 180 };
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task BulkAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string destination,
        DataTable table,
        CancellationToken cancellationToken)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = 180,
        };
        foreach (DataColumn column in table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(table, cancellationToken);
    }

    private static async Task<Dictionary<string, string>> MapAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 180 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                map[reader.GetString(0)] = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1)) ?? "";
            }
        }

        return map;
    }

    private static async Task<List<string>> RowsAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var rows = new List<string>();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new string[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    values[index] = reader.IsDBNull(index) ? "NULL" : Convert.ToString(reader.GetValue(index)) ?? "";
                }

                rows.Add(string.Join(" | ", values));
            }
        }
        while (await reader.NextResultAsync(cancellationToken));
        return rows;
    }

    private static string CreatePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        Span<char> chars = stackalloc char[20];
        Span<byte> bytes = stackalloc byte[20];
        RandomNumberGenerator.Fill(bytes);
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = alphabet[bytes[index] % alphabet.Length];
        }

        return new string(chars);
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string ReadText(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal))?.Trim() ?? "";

    private static string? ReadOptional(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

    private static decimal? ReadDecimal(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal));

    private static string? Fit(string? value, int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= length ? value : value[..length];
    }

    private static object Db(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;
}
