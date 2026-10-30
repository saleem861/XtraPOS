-- Branch-owned POS cash sessions and sale payments.
-- Does not complete sales, post stock, create refunds, or contact Central.
-- Safe to run again.
--
-- Expected cash is calculated by the application, not stored as a generated column:
--   OpeningCashAmount
--   + Amount of Cash SalePayments whose sale Status is Completed.
-- Future components are intentionally absent:
--   - cash refunds
--   + cash-in drawer adjustments
--   - cash-out drawer adjustments.
-- A cash payment changes the drawer by Amount (applied to the sale), not TenderedAmount.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POSCashSessions
    (
        CashSessionID uniqueidentifier NOT NULL CONSTRAINT PK_POSCashSessions PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        POSStationID uniqueidentifier NOT NULL,
        DeviceID uniqueidentifier NOT NULL,
        OpenedByUserID uniqueidentifier NOT NULL,
        OpenedAt datetime2(0) NOT NULL,
        OpeningCashAmount decimal(19, 4) NOT NULL,
        Status nvarchar(20) NOT NULL,
        ClosedByUserID uniqueidentifier NULL,
        ClosedAt datetime2(0) NULL,
        ExpectedCashAmount decimal(19, 4) NULL,
        CountedCashAmount decimal(19, 4) NULL,
        CashDifference decimal(19, 4) NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_POSCashSessions_CreatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_POSCashSessions_Businesses FOREIGN KEY (BusinessID) REFERENCES dbo.Businesses (BusinessID),
        CONSTRAINT FK_POSCashSessions_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
        CONSTRAINT FK_POSCashSessions_POSStations FOREIGN KEY (POSStationID) REFERENCES dbo.POSStations (StationID),
        CONSTRAINT FK_POSCashSessions_Devices FOREIGN KEY (DeviceID) REFERENCES dbo.Devices (DeviceID),
        CONSTRAINT FK_POSCashSessions_OpenedBy FOREIGN KEY (OpenedByUserID) REFERENCES dbo.Users (UserID),
        CONSTRAINT FK_POSCashSessions_ClosedBy FOREIGN KEY (ClosedByUserID) REFERENCES dbo.Users (UserID),
        CONSTRAINT CK_POSCashSessions_Status CHECK (Status IN (N'Open', N'Closed')),
        CONSTRAINT CK_POSCashSessions_Opening CHECK (OpeningCashAmount >= 0),
        CONSTRAINT CK_POSCashSessions_Counted CHECK (CountedCashAmount IS NULL OR CountedCashAmount >= 0),
        CONSTRAINT CK_POSCashSessions_Lifecycle CHECK
        (
            (
                Status = N'Open'
                AND ClosedByUserID IS NULL
                AND ClosedAt IS NULL
                AND ExpectedCashAmount IS NULL
                AND CountedCashAmount IS NULL
                AND CashDifference IS NULL
            )
            OR
            (
                Status = N'Closed'
                AND ClosedByUserID IS NOT NULL
                AND ClosedAt IS NOT NULL
                AND ExpectedCashAmount IS NOT NULL
                AND CountedCashAmount IS NOT NULL
                AND CashDifference = CountedCashAmount - ExpectedCashAmount
            )
        )
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_POSCashSessions_CashSessionID_BranchID'
      AND object_id = OBJECT_ID(N'dbo.POSCashSessions')
)
    CREATE UNIQUE INDEX UX_POSCashSessions_CashSessionID_BranchID
        ON dbo.POSCashSessions (CashSessionID, BranchID);

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_POSCashSessions_BranchDevice_Open'
      AND object_id = OBJECT_ID(N'dbo.POSCashSessions')
)
    CREATE UNIQUE INDEX UX_POSCashSessions_BranchDevice_Open
        ON dbo.POSCashSessions (BranchID, DeviceID)
        WHERE Status = N'Open';

IF COL_LENGTH(N'dbo.Sales', N'CashSessionID') IS NULL
    ALTER TABLE dbo.Sales ADD CashSessionID uniqueidentifier NULL;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_Sales_SaleID_BranchID'
      AND object_id = OBJECT_ID(N'dbo.Sales')
)
    CREATE UNIQUE INDEX UX_Sales_SaleID_BranchID
        ON dbo.Sales (SaleID, BranchID);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Sales_CashSessionBranch')
    EXEC(N'
    ALTER TABLE dbo.Sales ADD CONSTRAINT FK_Sales_CashSessionBranch
        FOREIGN KEY (CashSessionID, BranchID) REFERENCES dbo.POSCashSessions (CashSessionID, BranchID);
    ');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_Sales_CashSessionID'
      AND object_id = OBJECT_ID(N'dbo.Sales')
)
    EXEC(N'
    CREATE INDEX IX_Sales_CashSessionID
        ON dbo.Sales (CashSessionID)
        WHERE CashSessionID IS NOT NULL;
    ');

IF OBJECT_ID(N'dbo.SalePayments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalePayments
    (
        SalePaymentID uniqueidentifier NOT NULL CONSTRAINT PK_SalePayments PRIMARY KEY,
        SaleID uniqueidentifier NOT NULL,
        CashSessionID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        ClientPaymentKey uniqueidentifier NOT NULL,
        PaymentMethodCode nvarchar(20) NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        TenderedAmount decimal(19, 4) NULL,
        ChangeAmount decimal(19, 4) NOT NULL
            CONSTRAINT DF_SalePayments_ChangeAmount DEFAULT (0),
        ExternalReference nvarchar(100) NULL,
        CreatedByUserID uniqueidentifier NOT NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_SalePayments_CreatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_SalePayments_SaleBranch FOREIGN KEY (SaleID, BranchID) REFERENCES dbo.Sales (SaleID, BranchID),
        CONSTRAINT FK_SalePayments_SessionBranch FOREIGN KEY (CashSessionID, BranchID) REFERENCES dbo.POSCashSessions (CashSessionID, BranchID),
        CONSTRAINT FK_SalePayments_Users FOREIGN KEY (CreatedByUserID) REFERENCES dbo.Users (UserID),
        CONSTRAINT CK_SalePayments_Method CHECK (PaymentMethodCode IN (N'Cash', N'Card')),
        CONSTRAINT CK_SalePayments_Amount CHECK (Amount > 0),
        CONSTRAINT CK_SalePayments_Change CHECK (ChangeAmount >= 0),
        CONSTRAINT CK_SalePayments_Cash CHECK
        (
            PaymentMethodCode <> N'Cash'
            OR
            (
                TenderedAmount IS NOT NULL
                AND TenderedAmount >= Amount
                AND ChangeAmount = TenderedAmount - Amount
            )
        ),
        CONSTRAINT CK_SalePayments_Card CHECK
        (
            PaymentMethodCode <> N'Card'
            OR (TenderedAmount IS NULL AND ChangeAmount = 0)
        )
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_SalePayments_Sale_ClientKey'
      AND object_id = OBJECT_ID(N'dbo.SalePayments')
)
    CREATE UNIQUE INDEX UX_SalePayments_Sale_ClientKey
        ON dbo.SalePayments (SaleID, ClientPaymentKey);

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_SalePayments_CashSessionID'
      AND object_id = OBJECT_ID(N'dbo.SalePayments')
)
    CREATE INDEX IX_SalePayments_CashSessionID
        ON dbo.SalePayments (CashSessionID);

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'POS.Sell')
BEGIN
    INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (N'POS.Sell', N'بيع نقطة البيع', N'POS', N'تنفيذ البيع من نقطة البيع', 1, SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'POS.CashSession')
BEGIN
    INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (N'POS.CashSession', N'إدارة صندوق نقطة البيع', N'POS', N'فتح وقراءة وإغلاق صندوق نقطة البيع', 1, SYSUTCDATETIME());
END;

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON permission.PermissionCode IN (N'POS.Sell', N'POS.CashSession')
   AND role.RoleCode IN (N'Admin', N'Supervisor', N'User')
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.RolePermissions AS existing
    WHERE existing.RoleID = role.RoleID
      AND existing.PermissionID = permission.PermissionID);
