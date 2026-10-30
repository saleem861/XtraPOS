using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class CustomerMasterTests(BranchApiFixture fixture)
{
    private const string Password = "Admin1234";
    private const string CentralName = "XtraPOS_Central_Test_Customers";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Schema_AllowsNullLegacyIdentity_AndRejectsDiscountOutsideBounds()
    {
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'015_CustomerMaster';"));
        Assert.Equal(1, await QueryIntAsync("SELECT CASE WHEN COL_LENGTH(N'dbo.Customers', N'CustomerID') IS NULL THEN 0 ELSE 1 END;"));
        Assert.Equal(1, await QueryIntAsync("SELECT CAST(is_nullable AS int) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Customers') AND name = N'LegacySource';"));
        Assert.Equal(1, await QueryIntAsync("SELECT CAST(is_nullable AS int) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Customers') AND name = N'Phone';"));
        Assert.Equal(0, await QueryIntAsync("SELECT CAST(is_nullable AS int) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Customers') AND name = N'Name';"));
        Assert.Equal(7, await QueryIntAsync("SELECT CAST(precision AS int) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Customers') AND name = N'VariableValueDiscountPercent';"));
        Assert.Equal(4, await QueryIntAsync("SELECT CAST(scale AS int) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Customers') AND name = N'VariableValueDiscountPercent';"));
        Assert.Equal(0, await QueryIntAsync("SELECT CAST(is_unique AS int) FROM sys.indexes WHERE name = N'IX_Customers_Business_NormalizedPhone';"));
        Assert.Contains("NOT NULL", await QueryStringAsync("SELECT filter_definition FROM sys.indexes WHERE name = N'UX_Customers_LegacyIdentity';"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LegacySource", await QueryStringAsync("SELECT filter_definition FROM sys.indexes WHERE name = N'UX_Customers_LegacyIdentity';"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CustomerNumber", await QueryStringAsync("SELECT filter_definition FROM sys.indexes WHERE name = N'UX_Customers_Business_CustomerNumber';"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await QueryIntAsync("SELECT CAST(is_unique AS int) FROM sys.indexes WHERE name = N'UX_Customers_Business_CustomerNumber';"));
        Assert.Equal(2, await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM dbo.RolePermissions AS rp
            INNER JOIN dbo.Roles AS role ON role.RoleID = rp.RoleID
            INNER JOIN dbo.Permissions AS permission ON permission.PermissionID = rp.PermissionID
            WHERE role.RoleCode = N'User' AND permission.PermissionCode IN (N'Customers.View', N'Customers.Create');
            """));
        Assert.Equal(0, await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM dbo.RolePermissions AS rp
            INNER JOIN dbo.Roles AS role ON role.RoleID = rp.RoleID
            INNER JOIN dbo.Permissions AS permission ON permission.PermissionID = rp.PermissionID
            WHERE role.RoleCode = N'User' AND permission.PermissionCode = N'Customers.Manage';
            """));
        Assert.Equal(1, await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM dbo.RolePermissions AS rp
            INNER JOIN dbo.Roles AS role ON role.RoleID = rp.RoleID
            INNER JOIN dbo.Permissions AS permission ON permission.PermissionID = rp.PermissionID
            WHERE role.RoleCode = N'Supervisor' AND permission.PermissionCode = N'Customers.View';
            """));
        Assert.Equal(1, await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM dbo.RolePermissions AS rp
            INNER JOIN dbo.Roles AS role ON role.RoleID = rp.RoleID
            INNER JOIN dbo.Permissions AS permission ON permission.PermissionID = rp.PermissionID
            WHERE role.RoleCode = N'Supervisor' AND permission.PermissionCode = N'Customers.Manage';
            """));

        var businessId = await QueryGuidAsync("SELECT TOP (1) BusinessID FROM dbo.Businesses;");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        try
        {
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, Phone, NormalizedPhone, VariableValueDiscountPercent, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{first}', '{businessId}', N'أول', N'0590000001', N'0590000001', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       ('{second}', '{businessId}', N'ثان', N'0590000001', N'0590000001', 100, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            Assert.Equal(2, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID IN ('{first}', '{second}');"));
            await ExecuteAsync($"UPDATE dbo.Customers SET LegacySource = N'shop1', LegacyCustomerKey = N'100' WHERE CustomerID = '{first}';");
            var duplicate = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
                $"UPDATE dbo.Customers SET LegacySource = N'shop1', LegacyCustomerKey = N'100' WHERE CustomerID = '{second}';"));
            Assert.True(duplicate.Number is 2601 or 2627);
            var discount = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
                $"UPDATE dbo.Customers SET VariableValueDiscountPercent = 101 WHERE CustomerID = '{first}';"));
            Assert.Equal(547, discount.Number);
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM dbo.Customers WHERE CustomerID IN ('{first}', '{second}');");
        }
    }

    [Fact]
    public async Task Roles_Allow_Create_And_Reject_User_Updates()
    {
        await ExecuteAsync(
            """
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive, MustChangePassword, CreatedAt)
            SELECT NEWID(), N'customer-user', N'CUSTOMER-USER', N'مستخدم الزبائن', PasswordHash, 1, 0, SYSUTCDATETIME()
            FROM dbo.Users WHERE Username = N'admin'
              AND NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username = N'customer-user');
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive, MustChangePassword, CreatedAt)
            SELECT NEWID(), N'customer-supervisor', N'CUSTOMER-SUPERVISOR', N'مشرف الزبائن', PasswordHash, 1, 0, SYSUTCDATETIME()
            FROM dbo.Users WHERE Username = N'admin'
              AND NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username = N'customer-supervisor');
            INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
            SELECT users.UserID, roles.RoleID, SYSUTCDATETIME()
            FROM dbo.Users AS users
            INNER JOIN dbo.Roles AS roles ON roles.RoleCode = CASE users.Username WHEN N'customer-user' THEN N'User' ELSE N'Supervisor' END
            WHERE users.Username IN (N'customer-user', N'customer-supervisor')
              AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles AS existing WHERE existing.UserID = users.UserID AND existing.RoleID = roles.RoleID);
            INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
            SELECT users.UserID, branch.BranchID, 1, SYSUTCDATETIME()
            FROM dbo.Users AS users
            CROSS JOIN dbo.Branches AS branch
            WHERE users.Username IN (N'customer-user', N'customer-supervisor')
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.UserBranchAccess AS existing
                  WHERE existing.UserID = users.UserID AND existing.BranchID = branch.BranchID);
            """);

        Assert.Equal(0, await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM dbo.RolePermissions AS granted
            INNER JOIN dbo.Roles AS role ON role.RoleID = granted.RoleID
            INNER JOIN dbo.Permissions AS permission ON permission.PermissionID = granted.PermissionID
            WHERE role.RoleCode = N'User'
              AND permission.PermissionCode IN (N'Customers.Manage', N'Credit.ManageLimit', N'Credit.Suspend');
            """));

        var customerId = Guid.NewGuid();
        foreach (var username in new[] { "admin", "customer-supervisor", "customer-user" })
        {
            using var client = Client();
            var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = Password });
            Assert.True(login.StatusCode == HttpStatusCode.OK, username + " " + await login.Content.ReadAsStringAsync());

            var list = await client.GetAsync("/api/customers?search=%D8%B9%D8%A7%D8%AF%D9%8A&page=1&pageSize=5");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);

            var create = await client.PostAsJsonAsync("/api/customers", new { name = "" });
            Assert.NotEqual(HttpStatusCode.Forbidden, create.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, create.StatusCode);

            var update = await client.PutAsJsonAsync($"/api/customers/{customerId}", new { name = "تغيير" });
            if (username == "customer-user")
            {
                Assert.True(update.StatusCode == HttpStatusCode.Forbidden, await update.Content.ReadAsStringAsync());
            }
            else
            {
                Assert.NotEqual(HttpStatusCode.Forbidden, update.StatusCode);
            }
        }
    }

    [Fact]
    public async Task StandaloneCustomer_PreservesPhone_WarnsOnDuplicate_AndDoesNotInventLegacyIdentity()
    {
        try
        {
            using var client = Client();
            await LoginAsync(client);
            var priceTypeId = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
            var businessId = await QueryGuidAsync("SELECT TOP (1) BusinessID FROM dbo.Businesses;");
            var classificationId = Guid.NewGuid();
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.CustomerClassifications
                    (CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID, DefaultVariableValueDiscountPercent, IsDefault, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{classificationId}', '{businessId}', N'REGULAR', N'عادي', '{priceTypeId}', 10, 1, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);

            var rejected = await client.PostAsJsonAsync("/api/customers", new { name = "مرور", phone = "1" });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Customers;"));

            var bounds = await client.PostAsJsonAsync("/api/customers", new { name = "حدود", variableValueDiscountPercent = 101 });
            Assert.Equal(HttpStatusCode.BadRequest, bounds.StatusCode);

            var created = await CreateAsync(client, new
            {
                name = "أحمد",
                customerNumber = "C-1",
                phone = "059 123 4567",
                normalizedPhone = "SHOULD-BE-IGNORED",
                secondaryPhone = "(059)0000000",
                customerClassificationId = classificationId,
                priceTypeId,
                variableValueDiscountPercent = 22m,
                notes = "ملاحظة",
                legacySource = "shop1",
                legacyCustomerKey = "1",
                isActive = true,
            });
            Assert.Equal("0591234567", created.Customer!.NormalizedPhone);
            Assert.Equal("059 123 4567", created.Customer.Phone);
            Assert.Equal("0590000000", created.Customer.SecondaryNormalizedPhone);
            Assert.Equal(22.0000m, created.Customer.VariableValueDiscountPercent);
            Assert.Equal(priceTypeId, created.Customer.PriceTypeId);
            Assert.Equal(classificationId, created.Customer.CustomerClassificationId);
            Assert.Null(created.DuplicatePhoneWarning);
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{created.Customer.CustomerId}' AND (LegacySource IS NOT NULL OR LegacyCustomerKey IS NOT NULL);"));
            Assert.Equal("CustomerCreated", await QueryStringAsync($"SELECT Action FROM dbo.CustomerAdministrationAudit WHERE CustomerID = '{created.Customer.CustomerId}';"));

            var arabic = await CreateAsync(client, new { name = "أرقام", phone = "٠٥٩١٢٣٤٥٦٧", isActive = true });
            Assert.Equal("0591234567", arabic.Customer!.NormalizedPhone);
            Assert.Equal("٠٥٩١٢٣٤٥٦٧", arabic.Customer.Phone);
            Assert.NotNull(arabic.DuplicatePhoneWarning);
            Assert.Contains(arabic.DuplicatePhoneWarning!.Customers!, match => match.CustomerId == created.Customer.CustomerId);
            Assert.Equal(2, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Customers WHERE NormalizedPhone = N'0591234567';"));

            var page = await client.GetFromJsonAsync<CustomerPageBody>("/api/customers?search=0591234567&pageSize=1", Json);
            Assert.Equal(2, page!.TotalCount);
            Assert.Single(page.Customers!);
            var inactive = await client.PostAsJsonAsync(
                $"/api/customers/{arabic.Customer.CustomerId}/activation",
                new { isActive = false, rowVersion = arabic.Customer.RowVersion });
            Assert.Equal(HttpStatusCode.OK, inactive.StatusCode);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{arabic.Customer.CustomerId}' AND IsActive = 0;"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.CustomerAdministrationAudit WHERE CustomerID = '{arabic.Customer.CustomerId}' AND Action = N'CustomerDeactivated';"));

            var stale = await client.PutAsJsonAsync(
                $"/api/customers/{created.Customer.CustomerId}",
                new { name = "أحمد معدل", phone = "0591234567", rowVersion = "AAAAAAAAAAA=", variableValueDiscountPercent = 22, isActive = true });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var updated = await client.PutAsJsonAsync(
                $"/api/customers/{created.Customer.CustomerId}",
                new
                {
                    name = "أحمد معدل",
                    phone = "0591234567",
                    customerClassificationId = classificationId,
                    priceTypeId,
                    rowVersion = created.Customer.RowVersion,
                    variableValueDiscountPercent = 31.8182m,
                    isActive = true,
                });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.Equal(31.8182m, await QueryDecimalAsync($"SELECT VariableValueDiscountPercent FROM dbo.Customers WHERE CustomerID = '{created.Customer.CustomerId}';"));

            var wrongPrice = await client.PostAsJsonAsync("/api/customers", new { name = "سعر", priceTypeId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.BadRequest, wrongPrice.StatusCode);
            var wrongClass = await client.PostAsJsonAsync("/api/customers", new { name = "تصنيف", customerClassificationId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.BadRequest, wrongClass.StatusCode);
        }
        finally
        {
            await CleanupBranchAsync();
        }
    }

    [Fact]
    public async Task CentralLinkedCustomer_KeepsTheSameId_AndBootstrapIsIdempotent()
    {
        var beforeBalances = await QueryIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;");
        var beforeInventory = await QueryIntAsync("SELECT COUNT(*) FROM dbo.ItemBranchInventory;");
        var beforeMovements = await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;");
        var beforePrices = await QueryIntAsync("SELECT COUNT(*) FROM dbo.ItemPrices;");
        var beforeUsers = await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;");
        try
        {
            await PrepareCentralAsync();
            using var scope = fixture.Factory.Services.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<SharedDataSync>();
            var settings = CentralSettings(CentralName);
            var customerId = Guid.NewGuid();
            var classificationId = Guid.NewGuid();
            var businessId = await QueryGuidAsync("SELECT TOP (1) BusinessID FROM dbo.Businesses;");
            var priceTypeId = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
            await CentralExecuteAsync(
                $"""
                INSERT INTO dbo.CustomerClassifications
                    (CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID, DefaultVariableValueDiscountPercent, IsDefault, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{classificationId}', '{businessId}', N'GOLD', N'ذهبي', '{priceTypeId}', 22, 1, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Customers
                    (CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone, CustomerClassificationID, PriceTypeID,
                     VariableValueDiscountPercent, IsActive, Notes, LegacySource, LegacyCustomerKey, CreatedAt, UpdatedAt)
                VALUES
                    ('{customerId}', '{businessId}', N'C-9', N'زبون مركزي', N'0591111111', N'0591111111', '{classificationId}', '{priceTypeId}',
                     22, 1, N'ملاحظة', N'shop1', N'42', SYSUTCDATETIME(), SYSUTCDATETIME());
                """);

            await sync.BootstrapAsync(settings, CancellationToken.None);
            await sync.BootstrapAsync(settings, CancellationToken.None);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{customerId}';"));
            Assert.Equal(priceTypeId, await QueryGuidAsync($"SELECT PriceTypeID FROM dbo.Customers WHERE CustomerID = '{customerId}';"));
            Assert.Equal(classificationId, await QueryGuidAsync($"SELECT CustomerClassificationID FROM dbo.Customers WHERE CustomerID = '{customerId}';"));
            Assert.Equal("shop1", await QueryStringAsync($"SELECT LegacySource FROM dbo.Customers WHERE CustomerID = '{customerId}';"));
            Assert.Equal(22.0000m, await QueryDecimalAsync($"SELECT VariableValueDiscountPercent FROM dbo.Customers WHERE CustomerID = '{customerId}';"));

            await CentralExecuteAsync($"UPDATE dbo.Customers SET Name = N'بعد المزامنة', IsActive = 0, VariableValueDiscountPercent = 10 WHERE CustomerID = '{customerId}';");
            await sync.BootstrapAsync(settings, CancellationToken.None);
            Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Customers;"));
            Assert.Equal("بعد المزامنة", await QueryStringAsync($"SELECT Name FROM dbo.Customers WHERE CustomerID = '{customerId}';"));
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{customerId}' AND IsActive = 1;"));
            Assert.Equal(10.0000m, await QueryDecimalAsync($"SELECT VariableValueDiscountPercent FROM dbo.Customers WHERE CustomerID = '{customerId}';"));
            Assert.Equal(beforeBalances, await QueryIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;"));
            Assert.Equal(beforeInventory, await QueryIntAsync("SELECT COUNT(*) FROM dbo.ItemBranchInventory;"));
            Assert.Equal(beforeMovements, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;"));
            Assert.Equal(beforePrices, await QueryIntAsync("SELECT COUNT(*) FROM dbo.ItemPrices;"));
            Assert.Equal(beforeUsers, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;"));

            await CleanupBranchAsync();
            await SaveCentralEndpointAsync();
            using var client = Client();
            await LoginAsync(client);
            var created = await CreateAsync(client, new { name = "نسخة الفرع", phone = "0592222222", priceTypeId, variableValueDiscountPercent = 10, isActive = true });
            Assert.Equal(1, await CentralIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{created.Customer!.CustomerId}' AND Name = N'نسخة الفرع';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{created.Customer.CustomerId}';"));
            Assert.Equal("CustomerCreated", await QueryStringAsync($"SELECT Action FROM dbo.CustomerAdministrationAudit WHERE CustomerID = '{created.Customer.CustomerId}';"));
            var deactivated = await client.PostAsJsonAsync(
                $"/api/customers/{created.Customer.CustomerId}/activation",
                new { isActive = false, rowVersion = created.Customer.RowVersion });
            Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
            Assert.Equal(0, await CentralIntAsync($"SELECT IsActive FROM dbo.Customers WHERE CustomerID = '{created.Customer.CustomerId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{created.Customer.CustomerId}' AND IsActive = 0;"));
            Assert.Equal(beforeBalances, await QueryIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;"));
            Assert.Equal(beforeMovements, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;"));
        }
        finally
        {
            await CleanupBranchAsync();
            await DropCentralAsync();
        }
    }

    [Fact]
    public async Task LocalReplica_RemainsSearchable_WhenCentralEndpointIsUnreachable()
    {
        var customerId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var businessId = await QueryGuidAsync("SELECT TOP (1) BusinessID FROM dbo.Businesses;");
        try
        {
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.Customers
                    (CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone, VariableValueDiscountPercent, IsActive, CreatedAt, UpdatedAt)
                VALUES
                    ('{customerId}', '{businessId}', N'OFF-1', N'زبون دون اتصال', N'0593333333', N'0593333333', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.SyncEndpoints
                    (EndpointID, DatabaseRole, LocationKind, ServerName, Port, DatabaseName, AuthenticationType, EncryptConnection, TrustServerCertificate, Status, LastError)
                VALUES
                    ('{endpointId}', N'Central', N'Remote', N'127.0.0.1', 1, N'XtraPOS_Central', N'Integrated', 1, 1, N'Linked', N'central unreachable');
                INSERT INTO dbo.SyncCheckpoints (EndpointID, StreamName, LastAppliedVersion)
                VALUES ('{endpointId}', N'CentralMaster', 1);
                IF EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'CustomerReplicaState')
                    UPDATE dbo.SystemSettings SET SettingValue = N'Stale' WHERE SettingKey = N'CustomerReplicaState';
                ELSE
                    INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'CustomerReplicaState', N'Stale');
                """);
            using var client = Client();
            await LoginAsync(client);
            var page = await client.GetFromJsonAsync<CustomerPageBody>("/api/customers?search=0593333333&pageSize=5", Json);
            Assert.NotNull(page);
            Assert.Equal(CustomerMasterService.ReplicaAuthority, page.Authority);
            Assert.Equal(CustomerMasterService.SyncOffline, page.SyncState);
            Assert.Contains(page.Customers!, customer => customer.CustomerId == customerId);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{customerId}';"));
        }
        finally
        {
            await CleanupBranchAsync();
        }
    }

    [Fact]
    public async Task ExceptionalPrice_SyncsFromCentralChangeLog_WithoutDuplicates()
    {
        var customerId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var specialId = Guid.NewGuid();
        var saleAtSix = Guid.NewGuid();
        var saleAfter = Guid.NewGuid();
        try
        {
            await DropCentralAsync();
            await PrepareCentralAsync();
            var businessId = await QueryGuidAsync("SELECT TOP (1) BusinessID FROM dbo.Businesses;");
            var priceTypeId = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
            await CentralExecuteAsync(
                $"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsProhibited, IsPublishedOnline, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{itemId}', N'SPEC-6', N'صنف استثنائي', 0, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Customers
                    (CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone, PriceTypeID, VariableValueDiscountPercent, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{customerId}', '{businessId}', N'SPEC-C', N'زبون استثنائي', N'0596666666', N'0596666666', '{priceTypeId}', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerItemSpecialPrices
                    (CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{specialId}', '{businessId}', '{customerId}', '{itemId}', 7, 1, 7, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES ('{itemId}', N'SPEC-6', N'صنف استثنائي');
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{itemId}', '{priceTypeId}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            await SaveCentralEndpointAsync();
            var endpointId = await QueryGuidAsync("SELECT EndpointID FROM dbo.SyncEndpoints WHERE DatabaseRole = N'Central';");
            using var scope = fixture.Factory.Services.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<SharedDataSync>();
            Assert.Equal(SharedDataSync.ReplicaReady, await sync.EnsureCustomerReplicaAsync(endpointId, CentralSettings(CentralName), CancellationToken.None));
            Assert.Equal(7m, await QueryDecimalAsync($"SELECT SpecialPrice FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = '{specialId}';"));

            await CentralExecuteAsync($"UPDATE dbo.CustomerItemSpecialPrices SET SpecialPrice = 6, BundlePrice = 6, UpdatedAt = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE CustomerItemSpecialPriceID = '{specialId}';");
            await LogSpecialAsync(specialId, endpointId);
            var lowered = await sync.PullAsync(endpointId, CentralSettings(CentralName), CancellationToken.None);
            var repeated = await sync.PullAsync(endpointId, CentralSettings(CentralName), CancellationToken.None);
            Assert.Equal(1, lowered.Applied);
            Assert.Equal(0, repeated.Applied);
            Assert.Equal(6m, await QueryDecimalAsync($"SELECT SpecialPrice FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = '{specialId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.CustomerItemSpecialPrices WHERE CustomerID = '{customerId}' AND ItemID = '{itemId}';"));

            using var client = Client();
            await LoginAsync(client);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/pos/cash-session/open", new { openingCashAmount = 30m })).StatusCode);
            var atSix = await CheckoutAsync(client, saleAtSix, customerId, itemId, 6m);
            Assert.True(atSix.IsSuccessStatusCode, await atSix.Content.ReadAsStringAsync());

            await CentralExecuteAsync($"UPDATE dbo.CustomerItemSpecialPrices SET IsActive = 0, UpdatedAt = DATEADD(minute, 10, SYSUTCDATETIME()) WHERE CustomerItemSpecialPriceID = '{specialId}';");
            await LogSpecialAsync(specialId, endpointId);
            var deactivated = await sync.PullAsync(endpointId, CentralSettings(CentralName), CancellationToken.None);
            Assert.Equal(1, deactivated.Applied);
            Assert.Equal(0, await QueryIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = '{specialId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = '{specialId}';"));
            var after = await CheckoutAsync(client, saleAfter, customerId, itemId, 10m);
            Assert.True(after.IsSuccessStatusCode, await after.Content.ReadAsStringAsync());
            Assert.Equal(10m, await QueryDecimalAsync($"SELECT Total FROM dbo.Sales WHERE CartID = '{saleAfter}';"));
        }
        finally
        {
            await ExecuteAsync(
                $"""
                DELETE FROM dbo.StockMovements WHERE ItemID = '{itemId}';
                DELETE FROM dbo.SalePayments WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE CartID IN ('{saleAtSix}', '{saleAfter}'));
                DELETE FROM dbo.SaleItems WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE CartID IN ('{saleAtSix}', '{saleAfter}'));
                DELETE FROM dbo.Sales WHERE CartID IN ('{saleAtSix}', '{saleAfter}');
                DELETE FROM dbo.POSCashSessions WHERE DeviceID = '{fixture.DeviceId}' AND Status = N'Open';
                DELETE FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';
                DELETE FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = '{specialId}';
                DELETE FROM dbo.ItemPrices WHERE ItemID = '{itemId}';
                DELETE FROM dbo.Items WHERE ItemID = '{itemId}';
                DELETE FROM dbo.Customers WHERE CustomerID = '{customerId}';
                DELETE FROM dbo.SyncInbox;
                DELETE FROM dbo.SyncCheckpoints;
                DELETE FROM dbo.SyncEndpoints;
                DELETE FROM dbo.SystemSettings WHERE SettingKey = N'CustomerReplicaState';
                """);
            await DropCentralAsync();
        }
    }

    private async Task LogSpecialAsync(Guid specialId, Guid endpointId)
    {
        await CentralExecuteAsync(
            $"""
            INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload)
            SELECT N'CustomerItemSpecialPrices', CustomerItemSpecialPriceID, N'Upsert',
                   (SELECT CustomerItemSpecialPriceID AS customerItemSpecialPriceId, BusinessID AS businessId, CustomerID AS customerId,
                           ItemID AS itemId, SpecialPrice AS specialPrice, MinimumQuantity AS minimumQuantity, BundlePrice AS bundlePrice,
                           IsActive AS isActive, ValidFrom AS validFrom, ValidTo AS validTo, Notes AS notes, CreatedAt AS createdAt, UpdatedAt AS updatedAt
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
            FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = '{specialId}';
            """);
        _ = endpointId;
    }

    [Fact]
    public async Task OfflinePricing_UsesSyncedSpecial_AndRejectsUnconfirmedSpecial()
    {
        var customerId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var specialId = Guid.NewGuid();
        var expiredId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var businessId = await QueryGuidAsync("SELECT TOP (1) BusinessID FROM dbo.Businesses;");
        var priceTypeId = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var standardCart = Guid.NewGuid();
        var expiredCart = Guid.NewGuid();
        var listCart = Guid.NewGuid();
        var specialCart = Guid.NewGuid();
        var blockedCart = Guid.NewGuid();
        try
        {
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.Customers
                    (CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone, PriceTypeID, VariableValueDiscountPercent, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{customerId}', '{businessId}', N'PRICE-1', N'زبون سعر', N'0595555555', N'0595555555', '{priceTypeId}', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES ('{itemId}', N'PRICE-ITEM', N'صنف سعر');
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{itemId}', '{priceTypeId}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerItemSpecialPrices
                    (CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice, IsActive, ValidTo, CreatedAt, UpdatedAt)
                VALUES
                    ('{expiredId}', '{businessId}', '{customerId}', '{itemId}', 1, 1, 1, 1, DATEADD(day, -1, SYSUTCDATETIME()), SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.SyncEndpoints
                    (EndpointID, DatabaseRole, LocationKind, ServerName, Port, DatabaseName, AuthenticationType, EncryptConnection, TrustServerCertificate, Status, LastError)
                VALUES ('{endpointId}', N'Central', N'Remote', N'127.0.0.1', 1, N'XtraPOS_Central', N'Integrated', 1, 1, N'Linked', N'تعذر الوصول إلى خادم البيانات.');
                IF EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'CustomerReplicaState')
                    UPDATE dbo.SystemSettings SET SettingValue = N'Ready' WHERE SettingKey = N'CustomerReplicaState';
                ELSE INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'CustomerReplicaState', N'Ready');
                """);
            using var client = Client();
            await LoginAsync(client);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/pos/cash-session/open", new { openingCashAmount = 50m })).StatusCode);
            var standard = await CheckoutAsync(client, standardCart, null, itemId, 10m);
            Assert.True(standard.IsSuccessStatusCode, await standard.Content.ReadAsStringAsync());
            var expired = await CheckoutAsync(client, expiredCart, customerId, itemId, 10m);
            Assert.True(expired.IsSuccessStatusCode, await expired.Content.ReadAsStringAsync());
            Assert.Equal(10m, await QueryDecimalAsync($"SELECT Total FROM dbo.Sales WHERE CartID = '{expiredCart}';"));
            await ExecuteAsync($"DELETE FROM dbo.CustomerItemSpecialPrices WHERE CustomerID = '{customerId}';");
            var listOnly = await CheckoutAsync(client, listCart, customerId, itemId, 10m);
            Assert.True(listOnly.IsSuccessStatusCode, await listOnly.Content.ReadAsStringAsync());
            Assert.Equal(10m, await QueryDecimalAsync($"SELECT Total FROM dbo.Sales WHERE CartID = '{listCart}';"));
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.CustomerItemSpecialPrices
                    (CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{specialId}', '{businessId}', '{customerId}', '{itemId}', 7, 1, 7, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var discounted = await CheckoutAsync(client, specialCart, customerId, itemId, 7m);
            Assert.True(discounted.IsSuccessStatusCode, await discounted.Content.ReadAsStringAsync());
            Assert.Equal(7m, await QueryDecimalAsync($"SELECT Total FROM dbo.Sales WHERE CartID = '{specialCart}';"));
            await ExecuteAsync("UPDATE dbo.SystemSettings SET SettingValue = N'NotInitialized' WHERE SettingKey = N'CustomerReplicaState'; DELETE FROM dbo.CustomerItemSpecialPrices WHERE CustomerID = '" + customerId + "';");
            var blocked = await CheckoutAsync(client, blockedCart, customerId, itemId, 10m);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Contains("SPECIAL_PRICE_UNCONFIRMED", await blocked.Content.ReadAsStringAsync());
            var credit = await client.GetAsync($"/api/customers/{customerId}/credit-policy");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, credit.StatusCode);
        }
        finally
        {
            await ExecuteAsync(
                $"""
                DELETE FROM dbo.StockMovements WHERE ItemID = '{itemId}';
                DELETE FROM dbo.SalePayments WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE CustomerID = '{customerId}' OR CartID IN ('{standardCart}', '{expiredCart}', '{listCart}', '{specialCart}', '{blockedCart}'));
                DELETE FROM dbo.SaleItems WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE CustomerID = '{customerId}' OR CartID IN ('{standardCart}', '{expiredCart}', '{listCart}', '{specialCart}', '{blockedCart}'));
                DELETE FROM dbo.Sales WHERE CustomerID = '{customerId}' OR CartID IN ('{standardCart}', '{expiredCart}', '{listCart}', '{specialCart}', '{blockedCart}');
                DELETE FROM dbo.POSCashSessions WHERE DeviceID = '{fixture.DeviceId}' AND Status = N'Open';
                DELETE FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';
                DELETE FROM dbo.CustomerItemSpecialPrices WHERE CustomerID = '{customerId}';
                DELETE FROM dbo.ItemPrices WHERE ItemID = '{itemId}';
                DELETE FROM dbo.Items WHERE ItemID = '{itemId}';
                DELETE FROM dbo.Customers WHERE CustomerID = '{customerId}';
                DELETE FROM dbo.SyncEndpoints WHERE EndpointID = '{endpointId}';
                DELETE FROM dbo.SystemSettings WHERE SettingKey = N'CustomerReplicaState';
                """);
        }
    }

    private static Task<HttpResponseMessage> CheckoutAsync(HttpClient client, Guid cartId, Guid? customerId, Guid itemId, decimal price) =>
        client.PostAsJsonAsync("/api/pos/checkout", new
        {
            cartId,
            customerId,
            lines = new[] { new { cartLineId = Guid.NewGuid(), lineType = "Fixed", itemId, quantity = 1m, expectedUnitPrice = price, expectedStockOwnerQuantity = 1m } },
            payments = new[] { new { clientPaymentKey = Guid.NewGuid(), method = "Cash", amount = price, tenderedAmount = 20m } },
        });

    [Fact]
    public async Task CustomerReplica_Bootstraps_SellsOffline_AndCatchesUp()
    {
        var keptId = Guid.NewGuid();
        var retiredId = Guid.NewGuid();
        var partialId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var cartId = Guid.NewGuid();
        try
        {
            await DropCentralAsync();
            await PrepareCentralAsync();
            var businessId = await QueryGuidAsync("SELECT TOP (1) BusinessID FROM dbo.Businesses;");
            var priceTypeId = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
            await CentralExecuteAsync(
                $"""
                INSERT INTO dbo.Customers
                    (CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone, PriceTypeID, VariableValueDiscountPercent, IsActive, CreatedAt, UpdatedAt)
                VALUES
                    ('{keptId}', '{businessId}', N'KEEP-1', N'زبون محفوظ', N'0594444441', N'0594444441', '{priceTypeId}', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    ('{retiredId}', '{businessId}', N'RET-1', N'زبون موقف', N'0594444442', N'0594444442', '{priceTypeId}', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            await SaveCentralEndpointAsync();
            var endpointId = await QueryGuidAsync("SELECT EndpointID FROM dbo.SyncEndpoints WHERE DatabaseRole = N'Central';");
            using var scope = fixture.Factory.Services.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<SharedDataSync>();
            var ready = await sync.EnsureCustomerReplicaAsync(endpointId, CentralSettings(CentralName), CancellationToken.None);
            Assert.Equal(SharedDataSync.ReplicaReady, ready);
            Assert.Equal(2, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID IN ('{keptId}', '{retiredId}');"));
            Assert.Equal(keptId, await QueryGuidAsync($"SELECT CustomerID FROM dbo.Customers WHERE CustomerNumber = N'KEEP-1';"));

            var updated = DateTime.UtcNow.AddMinutes(5);
            var payload = CentralChangeApply.Serialize(new CustomerSyncPayload(
                retiredId, businessId, "RET-1", "زبون موقف", "0594444442", "0594444442", null, null, null, priceTypeId, "Price0", 0, false, null, null, null, updated.AddMinutes(-1), updated, null));
            var safePayload = payload.Replace("'", "''");
            await CentralExecuteAsync(
                $"""
                UPDATE dbo.Customers SET Name = N'زبون موقف', IsActive = 0, UpdatedAt = SYSUTCDATETIME() WHERE CustomerID = '{retiredId}';
                INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload)
                VALUES (N'Customers', '{retiredId}', N'Upsert', N'{safePayload}');
                """);
            var pull = await sync.PullAsync(endpointId, CentralSettings(CentralName), CancellationToken.None);
            var again = await sync.PullAsync(endpointId, CentralSettings(CentralName), CancellationToken.None);
            Assert.True(pull.CentralReachable);
            Assert.Equal(1, pull.Applied);
            Assert.Equal(0, again.Applied);
            Assert.Equal(0, await QueryIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.Customers WHERE CustomerID = '{retiredId}';"));
            Assert.Equal(2, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID IN ('{keptId}', '{retiredId}');"));

            await ExecuteAsync(
                $"""
                UPDATE dbo.SyncEndpoints
                SET ServerName = N'127.0.0.1', Port = 1, LastError = N'تعذر الوصول إلى خادم البيانات.'
                WHERE EndpointID = '{endpointId}';
                UPDATE dbo.SystemSettings SET SettingValue = N'Stale' WHERE SettingKey = N'CustomerReplicaState';
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES ('{itemId}', N'OFF-CASH', N'صنف نقدي دون اتصال');
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{itemId}', '{priceTypeId}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var failedPull = await sync.PullAsync(endpointId, CentralSettings(CentralName) with { Server = "127.0.0.1", Port = 1 }, CancellationToken.None);
            Assert.False(failedPull.CentralReachable);
            Assert.Equal(keptId, await QueryGuidAsync($"SELECT CustomerID FROM dbo.Customers WHERE CustomerNumber = N'KEEP-1';"));

            using var client = Client();
            await LoginAsync(client);
            var page = await client.GetFromJsonAsync<CustomerPageBody>("/api/customers?search=0594444441&pageSize=5", Json);
            Assert.Equal(CustomerMasterService.ReplicaAuthority, page!.Authority);
            Assert.Equal(CustomerMasterService.SyncOffline, page.SyncState);
            Assert.Contains(page.Customers!, customer => customer.CustomerId == keptId);

            var opened = await client.PostAsJsonAsync("/api/pos/cash-session/open", new { openingCashAmount = 20m });
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            var sold = await client.PostAsJsonAsync("/api/pos/checkout", new
            {
                cartId,
                customerId = keptId,
                lines = new[] { new { cartLineId = Guid.NewGuid(), lineType = "Fixed", itemId, quantity = 1m, expectedUnitPrice = 10m, expectedStockOwnerQuantity = 1m } },
                payments = new[] { new { clientPaymentKey = Guid.NewGuid(), method = "Cash", amount = 10m, tenderedAmount = 10m } },
            });
            var soldBody = await sold.Content.ReadAsStringAsync();
            Assert.True(sold.IsSuccessStatusCode, soldBody);
            Assert.Equal(keptId, await QueryGuidAsync($"SELECT CustomerID FROM dbo.Sales WHERE CartID = '{cartId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.SalePayments WHERE SaleID = (SELECT SaleID FROM dbo.Sales WHERE CartID = '{cartId}');"));
            Assert.Equal(-1m, await QueryDecimalAsync($"SELECT QuantityDelta FROM dbo.StockMovements WHERE SourceDocumentID = (SELECT SaleID FROM dbo.Sales WHERE CartID = '{cartId}');"));

            var credit = await client.GetAsync($"/api/customers/{keptId}/credit-policy");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, credit.StatusCode);
            Assert.Contains("CENTRAL_UNAVAILABLE", await credit.Content.ReadAsStringAsync());

            await ExecuteAsync(
                $"""
                UPDATE dbo.SystemSettings SET SettingValue = N'Bootstrapping' WHERE SettingKey = N'CustomerReplicaState';
                INSERT INTO dbo.Customers
                    (CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone, VariableValueDiscountPercent, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{partialId}', '{businessId}', N'PART-1', N'نسخة ناقصة', N'0594444449', N'0594444449', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var partial = await client.GetAsync("/api/customers?search=0594444449&pageSize=5");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode);
            Assert.DoesNotContain(partialId.ToString(), await partial.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ExecuteAsync(
                $"""
                DELETE FROM dbo.StockMovements WHERE SourceDocumentID IN (SELECT SaleID FROM dbo.Sales WHERE CartID = '{cartId}');
                DELETE FROM dbo.SalePayments WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE CartID = '{cartId}');
                DELETE FROM dbo.SaleItems WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE CartID = '{cartId}');
                DELETE FROM dbo.Sales WHERE CartID = '{cartId}';
                DELETE FROM dbo.POSCashSessions WHERE DeviceID = '{fixture.DeviceId}' AND Status = N'Open';
                DELETE FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';
                DELETE FROM dbo.ItemPrices WHERE ItemID = '{itemId}';
                DELETE FROM dbo.Items WHERE ItemID = '{itemId}';
                """);
            await CleanupBranchAsync();
            await DropCentralAsync();
        }
    }

    [Fact]
    public async Task Verified_registration_save_creates_one_customer_and_keeps_the_challenge_when_validation_fails()
    {
        var businessId = await QueryGuidAsync("SELECT TOP (1) BusinessID FROM dbo.Businesses;");
        var priceTypeId = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var classificationId = Guid.NewGuid();
        var verificationId = Guid.NewGuid();
        const string number = "0590000191";
        const string phone = "+972590000191";
        try
        {
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.CustomerClassifications
                    (CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID, DefaultVariableValueDiscountPercent, IsDefault, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{classificationId}', '{businessId}', N'REG-OTP', N'تسجيل', '{priceTypeId}', 0, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerPhoneVerificationChallenges
                    (VerificationID, BusinessID, CustomerID, CustomerNumber, DestinationE164, Channel, Provider, Status, CreatedAt, ExpiresAt, LastSentAt, ResendAvailableAt, AttemptCount, MaxAttempts, VerifiedAt)
                VALUES
                    ('{verificationId}', '{businessId}', NULL, N'{number}', N'{phone}', N'SMS', N'TwilioVerify', N'Verified', SYSUTCDATETIME(), DATEADD(MINUTE, 5, SYSUTCDATETIME()), SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 5, SYSUTCDATETIME());
                """);
            using var client = Client();
            await LoginAsync(client);
            var invalidClass = await client.PostAsJsonAsync("/api/customers", new
            {
                customerNumber = number,
                name = "تسجيل",
                phone = "590000191",
                whatsAppCountryCode = "972",
                customerClassificationId = Guid.NewGuid(),
                priceTypeId,
                notes = "otp",
                verificationId,
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalidClass.StatusCode);
            Assert.Contains("نوع الزبون غير صالح", await invalidClass.Content.ReadAsStringAsync());
            var invalidPrice = await client.PostAsJsonAsync("/api/customers", new
            {
                customerNumber = number,
                name = "تسجيل",
                phone = "590000191",
                whatsAppCountryCode = "972",
                customerClassificationId = classificationId,
                priceTypeId = Guid.NewGuid(),
                notes = "otp",
                verificationId,
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalidPrice.StatusCode);
            Assert.Contains("التسعيرة المحددة غير صالحة", await invalidPrice.Content.ReadAsStringAsync());
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerNumber = N'{number}';"));
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.CustomerPhoneVerificationChallenges WHERE VerificationID = '{verificationId}' AND CustomerID IS NOT NULL;"));

            var created = await CreateAsync(client, new
            {
                customerNumber = number,
                name = "تسجيل",
                phone = "590000191",
                whatsAppCountryCode = "972",
                customerClassificationId = classificationId,
                priceTypeId,
                notes = "otp",
                verificationId,
            });
            Assert.Equal(classificationId, created.Customer!.CustomerClassificationId);
            Assert.Equal(priceTypeId, created.Customer.PriceTypeId);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerNumber = N'{number}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerNumber = N'{number}' AND PhoneVerificationStatus = N'Verified' AND PhoneVerifiedE164 = N'{phone}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.CustomerPhoneVerificationChallenges WHERE VerificationID = '{verificationId}' AND CustomerID IS NOT NULL AND ConsumedAt IS NOT NULL;"));

            var reused = await client.PostAsJsonAsync("/api/customers", new
            {
                customerNumber = "0590000192",
                name = "تسجيل",
                phone = "590000192",
                whatsAppCountryCode = "972",
                customerClassificationId = classificationId,
                priceTypeId,
                verificationId,
            });
            Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
            Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Customers WHERE CustomerNumber = N'0590000192';"));
        }
        finally
        {
            await ExecuteAsync(
                $"""
                DELETE FROM dbo.CustomerAdministrationAudit WHERE CustomerID IN (SELECT CustomerID FROM dbo.Customers WHERE CustomerNumber IN (N'{number}', N'0590000192'));
                DELETE FROM dbo.CustomerPhoneVerificationChallenges WHERE VerificationID = '{verificationId}';
                DELETE FROM dbo.Customers WHERE CustomerNumber IN (N'{number}', N'0590000192');
                DELETE FROM dbo.CustomerClassifications WHERE CustomerClassificationID = '{classificationId}';
                """);
        }
    }

    [Fact]
    public async Task Branch_apply_reconciles_an_unreferenced_number_and_is_idempotent()
    {
        var businessId = await QueryGuidAsync("SELECT TOP (1) BusinessID FROM dbo.Businesses;");
        var priceTypeId = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var centralId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        var dependentId = Guid.NewGuid();
        const string number = "0590000291";
        const string dependentNumber = "0590000292";
        var created = new DateTime(2026, 10, 25, 18, 0, 0, DateTimeKind.Utc);
        try
        {
            var payload = new CustomerSyncPayload(
                centralId, businessId, number, "مزامنة", "0590000291", "0590000291", null, null,
                null, priceTypeId, "Price0", 0, true, null, null, null, created, created, null,
                "+972590000291", "Verified", created, "+972590000291");
            await ApplyCustomerAsync(payload);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{centralId}' AND PhoneVerificationStatus = N'Verified' AND PhoneVerifiedE164 = N'+972590000291';"));

            payload = payload with { Name = "مزامنة معدلة", UpdatedAt = created.AddMinutes(1) };
            await ApplyCustomerAsync(payload);
            await ApplyCustomerAsync(payload);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerNumber = N'{number}';"));
            Assert.Equal("مزامنة معدلة", await QueryStringAsync($"SELECT Name FROM dbo.Customers WHERE CustomerID = '{centralId}';"));

            await ExecuteAsync(
                $"""
                INSERT INTO dbo.Customers (CustomerID, BusinessID, CustomerNumber, Name, VariableValueDiscountPercent, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{staleId}', '{businessId}', N'0590000293', N'قديم', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerAdministrationAudit (AuditID, ActorUserID, CustomerID, Action, OccurredAt)
                SELECT NEWID(), (SELECT TOP (1) UserID FROM dbo.Users), '{staleId}', N'CustomerCreated', SYSUTCDATETIME();
                """);
            var replacement = payload with { CustomerNumber = "0590000293", Phone = "0590000293", NormalizedPhone = "0590000293" };
            await ApplyCustomerAsync(replacement);
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{staleId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{centralId}' AND CustomerNumber = N'0590000293';"));

            await ExecuteAsync(
                $"""
                INSERT INTO dbo.Customers (CustomerID, BusinessID, CustomerNumber, Name, VariableValueDiscountPercent, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{dependentId}', '{businessId}', N'{dependentNumber}', N'له حركة', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var saleCustomer = payload with
            {
                CustomerId = Guid.NewGuid(),
                CustomerNumber = dependentNumber,
                Phone = dependentNumber,
                NormalizedPhone = dependentNumber,
                WhatsAppPhoneE164 = "+972590000292",
                PhoneVerifiedE164 = "+972590000292",
            };
            var branchId = await QueryGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
            var stationId = await QueryGuidAsync($"SELECT StationID FROM dbo.Devices WHERE DeviceID = '{fixture.DeviceId}';");
            var userId = await QueryGuidAsync("SELECT TOP (1) UserID FROM dbo.Users;");
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.Sales
                    (SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID, SaleNumber, Status, Subtotal, DiscountTotal, Total)
                VALUES
                    (NEWID(), '{businessId}', '{branchId}', '{stationId}', '{fixture.DeviceId}', NEWID(), '{dependentId}', '{userId}', NEXT VALUE FOR dbo.SaleNumberSequence, N'Draft', 0, 0, 0);
                """);
            var blocked = await Assert.ThrowsAnyAsync<Exception>(() => ApplyCustomerAsync(saleCustomer));
            Assert.Contains("dependent records", blocked.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = '{dependentId}';"));
        }
        finally
        {
            await ExecuteAsync(
                $"""
                DELETE FROM dbo.Sales WHERE CustomerID = '{dependentId}';
                DELETE FROM dbo.CustomerAdministrationAudit WHERE CustomerID IN (SELECT CustomerID FROM dbo.Customers WHERE CustomerNumber IN (N'{number}', N'0590000293', N'{dependentNumber}'));
                DELETE FROM dbo.Customers WHERE CustomerNumber IN (N'{number}', N'0590000293', N'{dependentNumber}') OR CustomerID IN ('{centralId}', '{staleId}', '{dependentId}');
                """);
        }
    }

    private static async Task ApplyCustomerAsync(CustomerSyncPayload payload)
    {
        await using var connection = Open();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await CentralChangeApply.ApplyAsync(connection, transaction, "Customers", payload.CustomerId, CentralChangeApply.Serialize(payload), CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task<MutationBody> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/customers", body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<MutationBody>(text, Json)!;
    }

    private HttpClient Client()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
        return client;
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task PrepareCentralAsync()
    {
        var settings = CentralSettings(CentralName);
        await using (var master = new SqlConnection(settings.BuildConnectionString("master")))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand(
                $"IF DB_ID(N'{CentralName}') IS NULL CREATE DATABASE [{CentralName}] COLLATE Arabic_CI_AS;",
                master);
            await create.ExecuteNonQueryAsync();
        }

        await fixture.Factory.Services.GetRequiredService<CentralMigrationRunner>()
            .ApplyAsync(settings.BuildConnectionString(CentralName), CancellationToken.None);

        await using var source = Open();
        await using var read = new SqlCommand(
            """
            SELECT u.UserID, u.Username, u.NormalizedUsername, u.DisplayName, u.FullName, u.EmployeeNumber, u.PasswordHash, u.IsActive, u.MustChangePassword,
                   b.BusinessID, bus.BusinessName, b.BranchID, b.BranchCode, b.BranchName, b.BranchType,
                   p.PriceTypeID, p.PriceCode, p.PriceName, p.SortOrder
            FROM dbo.Users AS u
            CROSS JOIN dbo.Branches AS b
            INNER JOIN dbo.Businesses AS bus ON bus.BusinessID = b.BusinessID
            INNER JOIN dbo.PriceTypes AS p ON p.PriceCode = N'Price0'
            WHERE u.Username = N'admin';
            """,
            source);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var userId = reader.GetGuid(0);
        var username = reader.GetString(1);
        var normalized = reader.GetString(2);
        var displayName = reader.GetString(3);
        var fullName = reader.IsDBNull(4) ? null : reader.GetString(4);
        var employeeNumber = reader.IsDBNull(5) ? null : reader.GetString(5);
        var hash = reader.GetString(6);
        var isActive = reader.GetBoolean(7);
        var mustChange = reader.GetBoolean(8);
        var businessId = reader.GetGuid(9);
        var businessName = reader.GetString(10);
        var branchId = reader.GetGuid(11);
        var branchCode = reader.IsDBNull(12) ? null : reader.GetString(12);
        var branchName = reader.GetString(13);
        var branchType = reader.GetString(14);
        var priceTypeId = reader.GetGuid(15);
        var priceCode = reader.GetString(16);
        var priceName = reader.GetString(17);
        var sortOrder = reader.GetInt32(18);
        await reader.CloseAsync();

        await using var central = new SqlConnection(settings.BuildConnectionString(CentralName));
        await central.OpenAsync();
        await using var seed = new SqlCommand(
            """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt)
            VALUES (@businessId, @businessName, 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
            VALUES (@branchId, @businessId, @branchCode, @branchName, @branchType, 1, SYSUTCDATETIME());
            INSERT INTO dbo.Roles (RoleID, RoleCode, RoleName, IsSystemRole, IsActive, CreatedAt)
            VALUES (NEWID(), N'Admin', N'مدير النظام', 1, 1, SYSUTCDATETIME()),
                   (NEWID(), N'Supervisor', N'مشرف', 1, 1, SYSUTCDATETIME()),
                   (NEWID(), N'User', N'مستخدم', 1, 1, SYSUTCDATETIME());
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, CreatedAt)
            VALUES (@userId, @username, @normalized, @displayName, @fullName, @employeeNumber, @hash, @active, @mustChange, SYSUTCDATETIME());
            INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
            SELECT @userId, RoleID, SYSUTCDATETIME() FROM dbo.Roles WHERE RoleCode = N'Admin';
            INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
            VALUES (@userId, @branchId, 1, SYSUTCDATETIME());
            INSERT INTO dbo.PriceTypes (PriceTypeID, PriceCode, PriceName, SortOrder, IsActive, CreatedAt)
            VALUES (@priceTypeId, @priceCode, @priceName, @sortOrder, 1, SYSUTCDATETIME());
            """,
            central);
        seed.Parameters.AddWithValue("@businessId", businessId);
        seed.Parameters.AddWithValue("@businessName", businessName);
        seed.Parameters.AddWithValue("@branchId", branchId);
        seed.Parameters.AddWithValue("@branchCode", (object?)branchCode ?? DBNull.Value);
        seed.Parameters.AddWithValue("@branchName", branchName);
        seed.Parameters.AddWithValue("@branchType", branchType);
        seed.Parameters.AddWithValue("@userId", userId);
        seed.Parameters.AddWithValue("@username", username);
        seed.Parameters.AddWithValue("@normalized", normalized);
        seed.Parameters.AddWithValue("@displayName", displayName);
        seed.Parameters.AddWithValue("@fullName", (object?)fullName ?? DBNull.Value);
        seed.Parameters.AddWithValue("@employeeNumber", (object?)employeeNumber ?? DBNull.Value);
        seed.Parameters.AddWithValue("@hash", hash);
        seed.Parameters.AddWithValue("@active", isActive);
        seed.Parameters.AddWithValue("@mustChange", mustChange);
        seed.Parameters.AddWithValue("@priceTypeId", priceTypeId);
        seed.Parameters.AddWithValue("@priceCode", priceCode);
        seed.Parameters.AddWithValue("@priceName", priceName);
        seed.Parameters.AddWithValue("@sortOrder", sortOrder);
        await seed.ExecuteNonQueryAsync();
    }

    private async Task SaveCentralEndpointAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SyncEndpointStore>();
        await store.SaveAsync(CentralSettings(CentralName), SqlEndpointSettings.Linked, CancellationToken.None);
    }

    private static SqlEndpointSettings CentralSettings(string databaseName)
    {
        return new SqlEndpointSettings(
            DatabaseRole.Central,
            SqlEndpointSettings.Local,
            "localhost",
            null,
            null,
            databaseName,
            SqlEndpointSettings.Integrated,
            null,
            null,
            true,
            true);
    }

    private static async Task CleanupBranchAsync()
    {
        await ExecuteAsync(
            """
            DELETE FROM dbo.CustomerAdministrationAudit;
            DELETE FROM dbo.Customers;
            DELETE FROM dbo.CustomerClassifications;
            DELETE FROM dbo.SyncCheckpoints;
            DELETE FROM dbo.SyncInbox;
            DELETE FROM dbo.SyncOutbox;
            DELETE FROM dbo.SyncEndpoints;
            DELETE FROM dbo.SystemSettings WHERE SettingKey = N'CustomerReplicaState';
            """);
    }

    private static async Task DropCentralAsync()
    {
        if (!CentralDatabase.IsTestName(CentralName))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(CentralSettings(CentralName).BuildConnectionString("master"));
        await master.OpenAsync();
        await using var drop = new SqlCommand(
            $"""
            IF DB_ID(N'{CentralName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{CentralName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{CentralName}];
            END
            """,
            master);
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CentralExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(CentralSettings(CentralName).BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> QueryIntAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<int> CentralIntAsync(string sql)
    {
        await using var connection = new SqlConnection(CentralSettings(CentralName).BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<decimal> QueryDecimalAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (decimal)(await command.ExecuteScalarAsync() ?? 0m);
    }

    private static async Task<Guid> QueryGuidAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task<string> QueryStringAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }

    private sealed class MutationBody
    {
        public CustomerBody? Customer { get; set; }
        public DuplicateBody? DuplicatePhoneWarning { get; set; }
    }

    private sealed class CustomerBody
    {
        public Guid CustomerId { get; set; }
        public string? Phone { get; set; }
        public string? NormalizedPhone { get; set; }
        public string? SecondaryNormalizedPhone { get; set; }
        public Guid? PriceTypeId { get; set; }
        public Guid? CustomerClassificationId { get; set; }
        public decimal VariableValueDiscountPercent { get; set; }
        public string? RowVersion { get; set; }
    }

    private sealed class DuplicateBody
    {
        public List<DuplicateMatch>? Customers { get; set; }
    }

    private sealed class DuplicateMatch
    {
        public Guid CustomerId { get; set; }
    }

    private sealed class CustomerPageBody
    {
        public string? Authority { get; set; }
        public string? SyncState { get; set; }
        public int TotalCount { get; set; }
        public List<CustomerBody>? Customers { get; set; }
    }
}
