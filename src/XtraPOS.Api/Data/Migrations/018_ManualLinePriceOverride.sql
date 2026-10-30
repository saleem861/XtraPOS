-- Branch sale-line audit for manual commercial price overrides.
-- ResolvedUnitPrice/ResolvedPriceSource keep the system price.
-- UnitPrice/LineTotal remain the charged commercial amounts.

IF COL_LENGTH(N'dbo.SaleItems', N'ResolvedUnitPrice') IS NULL
    ALTER TABLE dbo.SaleItems ADD ResolvedUnitPrice decimal(19, 4) NULL;

IF COL_LENGTH(N'dbo.SaleItems', N'ResolvedPriceSource') IS NULL
    ALTER TABLE dbo.SaleItems ADD ResolvedPriceSource nvarchar(50) NULL;

IF COL_LENGTH(N'dbo.SaleItems', N'PriceOverrideMode') IS NULL
    ALTER TABLE dbo.SaleItems ADD PriceOverrideMode nvarchar(20) NOT NULL
        CONSTRAINT DF_SaleItems_PriceOverrideMode DEFAULT (N'None');

-- Later statements are dynamic so SQL Server compiles them after the new columns exist.
EXEC(N'
UPDATE dbo.SaleItems
SET ResolvedUnitPrice = UnitPrice
WHERE ResolvedUnitPrice IS NULL;

UPDATE dbo.SaleItems
SET ResolvedPriceSource = PriceSource
WHERE ResolvedPriceSource IS NULL;

UPDATE dbo.SaleItems
SET PriceOverrideMode = N''None''
WHERE PriceOverrideMode IS NULL OR PriceOverrideMode = N'''';

ALTER TABLE dbo.SaleItems ALTER COLUMN ResolvedUnitPrice decimal(19, 4) NOT NULL;
ALTER TABLE dbo.SaleItems ALTER COLUMN ResolvedPriceSource nvarchar(50) NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N''CK_SaleItems_PriceOverrideMode'' AND parent_object_id = OBJECT_ID(N''dbo.SaleItems''))
    ALTER TABLE dbo.SaleItems ADD CONSTRAINT CK_SaleItems_PriceOverrideMode
        CHECK (PriceOverrideMode IN (N''None'', N''UnitPrice'', N''LineTotal''));

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N''CK_SaleItems_ResolvedUnitPrice'' AND parent_object_id = OBJECT_ID(N''dbo.SaleItems''))
    ALTER TABLE dbo.SaleItems ADD CONSTRAINT CK_SaleItems_ResolvedUnitPrice CHECK (ResolvedUnitPrice >= 0);
');

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'POS.OverrideLinePrice')
BEGIN
    INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (N'POS.OverrideLinePrice', N'تعديل سعر سطر البيع', N'POS', N'تعديل سعر الوحدة أو إجمالي سطر البيع يدوياً', 1, SYSUTCDATETIME());
END;

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON permission.PermissionCode = N'POS.OverrideLinePrice'
   AND role.RoleCode IN (N'Admin', N'Supervisor', N'User')
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.RolePermissions AS existing
    WHERE existing.RoleID = role.RoleID
      AND existing.PermissionID = permission.PermissionID);
