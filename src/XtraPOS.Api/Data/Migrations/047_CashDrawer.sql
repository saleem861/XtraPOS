-- Cash drawer denominations, immutable counts, employee cash errors, and supervisor decisions.
-- The same objects exist on the branch database and on XtraPOS_Central.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CashDenominations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CashDenominations
    (
        DenominationID uniqueidentifier NOT NULL CONSTRAINT PK_CashDenominations PRIMARY KEY,
        UnitValue decimal(19, 4) NOT NULL,
        DisplayName nvarchar(80) NOT NULL,
        SortOrder int NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_CashDenominations_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_CashDenominations_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_CashDenominations_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_CashDenominations_UnitValue UNIQUE (UnitValue),
        CONSTRAINT CK_CashDenominations_UnitValue CHECK (UnitValue > 0)
    );
END;

IF OBJECT_ID(N'dbo.CashDrawerCounts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CashDrawerCounts
    (
        CountID uniqueidentifier NOT NULL CONSTRAINT PK_CashDrawerCounts PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        POSStationID uniqueidentifier NOT NULL,
        DeviceID uniqueidentifier NOT NULL,
        CountedByUserID uniqueidentifier NOT NULL,
        CashSessionID uniqueidentifier NOT NULL,
        ParentCountID uniqueidentifier NULL,
        AttemptNumber tinyint NOT NULL,
        CountedAmount decimal(19, 4) NOT NULL,
        ExpectedAmount decimal(19, 4) NOT NULL,
        DifferenceAmount decimal(19, 4) NOT NULL,
        Status nvarchar(20) NOT NULL,
        Justification nvarchar(500) NULL,
        IsFinal bit NOT NULL,
        CountedAt datetime2(0) NOT NULL CONSTRAINT DF_CashDrawerCounts_CountedAt DEFAULT (SYSUTCDATETIME()),
        RelayedAt datetime2(0) NULL,
        CONSTRAINT FK_CashDrawerCounts_Businesses FOREIGN KEY (BusinessID) REFERENCES dbo.Businesses (BusinessID),
        CONSTRAINT FK_CashDrawerCounts_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
        CONSTRAINT FK_CashDrawerCounts_POSStations FOREIGN KEY (POSStationID) REFERENCES dbo.POSStations (StationID),
        CONSTRAINT FK_CashDrawerCounts_Devices FOREIGN KEY (DeviceID) REFERENCES dbo.Devices (DeviceID),
        CONSTRAINT FK_CashDrawerCounts_Users FOREIGN KEY (CountedByUserID) REFERENCES dbo.Users (UserID),
        CONSTRAINT FK_CashDrawerCounts_Parent FOREIGN KEY (ParentCountID) REFERENCES dbo.CashDrawerCounts (CountID),
        CONSTRAINT CK_CashDrawerCounts_Attempt CHECK (AttemptNumber IN (1, 2)),
        CONSTRAINT CK_CashDrawerCounts_Status CHECK (Status IN (N'Matched', N'Shortage', N'Surplus')),
        CONSTRAINT CK_CashDrawerCounts_Amounts CHECK (CountedAmount >= 0 AND ExpectedAmount >= 0),
        CONSTRAINT CK_CashDrawerCounts_Difference CHECK (DifferenceAmount = CountedAmount - ExpectedAmount),
        CONSTRAINT CK_CashDrawerCounts_Parent CHECK
        (
            (AttemptNumber = 1 AND ParentCountID IS NULL)
            OR (AttemptNumber = 2 AND ParentCountID IS NOT NULL)
        ),
        CONSTRAINT CK_CashDrawerCounts_StatusMath CHECK
        (
            (Status = N'Matched' AND DifferenceAmount = 0)
            OR (Status = N'Shortage' AND DifferenceAmount < 0)
            OR (Status = N'Surplus' AND DifferenceAmount > 0)
        ),
        CONSTRAINT CK_CashDrawerCounts_Justification CHECK
        (
            IsFinal = 0
            OR Status = N'Matched'
            OR (Justification IS NOT NULL AND LEN(Justification) > 0)
        )
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_CashDrawerCounts_OpenAttempt' AND object_id = OBJECT_ID(N'dbo.CashDrawerCounts'))
    CREATE UNIQUE INDEX UX_CashDrawerCounts_OpenAttempt ON dbo.CashDrawerCounts (CashSessionID) WHERE IsFinal = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_CashDrawerCounts_Parent' AND object_id = OBJECT_ID(N'dbo.CashDrawerCounts'))
    CREATE UNIQUE INDEX UX_CashDrawerCounts_Parent ON dbo.CashDrawerCounts (ParentCountID) WHERE ParentCountID IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CashDrawerCounts_Branch_Station' AND object_id = OBJECT_ID(N'dbo.CashDrawerCounts'))
    CREATE INDEX IX_CashDrawerCounts_Branch_Station ON dbo.CashDrawerCounts (BranchID, POSStationID, CountedAt);

IF OBJECT_ID(N'dbo.CashDrawerCountLines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CashDrawerCountLines
    (
        CountLineID uniqueidentifier NOT NULL CONSTRAINT PK_CashDrawerCountLines PRIMARY KEY,
        CountID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        POSStationID uniqueidentifier NOT NULL,
        DenominationID uniqueidentifier NOT NULL,
        UnitValue decimal(19, 4) NOT NULL,
        DisplayName nvarchar(80) NOT NULL,
        Quantity int NOT NULL,
        LineAmount decimal(19, 4) NOT NULL,
        RelayedAt datetime2(0) NULL,
        CONSTRAINT FK_CashDrawerCountLines_Counts FOREIGN KEY (CountID) REFERENCES dbo.CashDrawerCounts (CountID),
        CONSTRAINT FK_CashDrawerCountLines_Denominations FOREIGN KEY (DenominationID) REFERENCES dbo.CashDenominations (DenominationID),
        CONSTRAINT CK_CashDrawerCountLines_Quantity CHECK (Quantity >= 0),
        CONSTRAINT CK_CashDrawerCountLines_Amount CHECK (LineAmount = Quantity * UnitValue)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_CashDrawerCountLines_Count_Denomination' AND object_id = OBJECT_ID(N'dbo.CashDrawerCountLines'))
    CREATE UNIQUE INDEX UX_CashDrawerCountLines_Count_Denomination ON dbo.CashDrawerCountLines (CountID, DenominationID);

IF OBJECT_ID(N'dbo.EmployeeCashErrors', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeCashErrors
    (
        ErrorID uniqueidentifier NOT NULL CONSTRAINT PK_EmployeeCashErrors PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        POSStationID uniqueidentifier NOT NULL,
        UserID uniqueidentifier NOT NULL,
        CountID uniqueidentifier NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        ErrorType nvarchar(20) NOT NULL,
        Justification nvarchar(500) NOT NULL,
        OccurredAt datetime2(0) NOT NULL CONSTRAINT DF_EmployeeCashErrors_OccurredAt DEFAULT (SYSUTCDATETIME()),
        RelayedAt datetime2(0) NULL,
        CONSTRAINT FK_EmployeeCashErrors_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
        CONSTRAINT FK_EmployeeCashErrors_POSStations FOREIGN KEY (POSStationID) REFERENCES dbo.POSStations (StationID),
        CONSTRAINT FK_EmployeeCashErrors_Users FOREIGN KEY (UserID) REFERENCES dbo.Users (UserID),
        CONSTRAINT FK_EmployeeCashErrors_Counts FOREIGN KEY (CountID) REFERENCES dbo.CashDrawerCounts (CountID),
        CONSTRAINT UQ_EmployeeCashErrors_CountID UNIQUE (CountID),
        CONSTRAINT CK_EmployeeCashErrors_Amount CHECK (Amount > 0),
        CONSTRAINT CK_EmployeeCashErrors_Type CHECK (ErrorType IN (N'Shortage', N'Surplus'))
    );
END;

IF OBJECT_ID(N'dbo.CashDrawerDecisions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CashDrawerDecisions
    (
        DecisionID uniqueidentifier NOT NULL CONSTRAINT PK_CashDrawerDecisions PRIMARY KEY,
        CountID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        POSStationID uniqueidentifier NOT NULL,
        SupervisorUserID uniqueidentifier NOT NULL,
        DecisionCode nvarchar(20) NOT NULL,
        Note nvarchar(500) NOT NULL,
        DecidedAt datetime2(0) NOT NULL CONSTRAINT DF_CashDrawerDecisions_DecidedAt DEFAULT (SYSUTCDATETIME()),
        RelayedAt datetime2(0) NULL,
        CONSTRAINT FK_CashDrawerDecisions_Counts FOREIGN KEY (CountID) REFERENCES dbo.CashDrawerCounts (CountID),
        CONSTRAINT FK_CashDrawerDecisions_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
        CONSTRAINT FK_CashDrawerDecisions_POSStations FOREIGN KEY (POSStationID) REFERENCES dbo.POSStations (StationID),
        CONSTRAINT FK_CashDrawerDecisions_Users FOREIGN KEY (SupervisorUserID) REFERENCES dbo.Users (UserID),
        CONSTRAINT UQ_CashDrawerDecisions_CountID UNIQUE (CountID),
        CONSTRAINT CK_CashDrawerDecisions_Code CHECK (DecisionCode IN (N'KeepShortage', N'KeepSurplus', N'Waive')),
        CONSTRAINT CK_CashDrawerDecisions_Note CHECK (LEN(Note) > 0)
    );
END;

INSERT INTO dbo.CashDenominations (DenominationID, UnitValue, DisplayName, SortOrder, IsActive, CreatedAt, UpdatedAt)
SELECT seed.DenominationID, seed.UnitValue, seed.DisplayName, seed.SortOrder, 1, SYSUTCDATETIME(), SYSUTCDATETIME()
FROM (VALUES
    ('A1000001-0000-4000-8000-000000000001', CAST(0.5 AS decimal(19, 4)), N'نصف شيكل', 1),
    ('A1000001-0000-4000-8000-000000000002', CAST(1 AS decimal(19, 4)), N'شيكل', 2),
    ('A1000001-0000-4000-8000-000000000003', CAST(2 AS decimal(19, 4)), N'شيكلان', 3),
    ('A1000001-0000-4000-8000-000000000004', CAST(5 AS decimal(19, 4)), N'خمسة شواكل', 4),
    ('A1000001-0000-4000-8000-000000000005', CAST(10 AS decimal(19, 4)), N'عشرة شواكل', 5),
    ('A1000001-0000-4000-8000-000000000006', CAST(20 AS decimal(19, 4)), N'عشرون شيكلاً', 6),
    ('A1000001-0000-4000-8000-000000000007', CAST(50 AS decimal(19, 4)), N'خمسون شيكلاً', 7),
    ('A1000001-0000-4000-8000-000000000008', CAST(100 AS decimal(19, 4)), N'مئة شيكل', 8),
    ('A1000001-0000-4000-8000-000000000009', CAST(200 AS decimal(19, 4)), N'مئتا شيكل', 9)
) AS seed(DenominationID, UnitValue, DisplayName, SortOrder)
WHERE NOT EXISTS (SELECT 1 FROM dbo.CashDenominations AS existing WHERE existing.DenominationID = seed.DenominationID)
  AND NOT EXISTS (SELECT 1 FROM dbo.CashDenominations AS existing WHERE existing.UnitValue = seed.UnitValue);

INSERT INTO dbo.Permissions (PermissionID, PermissionCode, PermissionName, Module, IsActive, CreatedAt)
SELECT NEWID(), code.PermissionCode, code.PermissionName, N'CashDrawer', 1, SYSUTCDATETIME()
FROM (VALUES
    (N'CashDrawer.Count', N'جرد الجرار'),
    (N'CashDrawer.Review', N'مراجعة الجرد'),
    (N'CashDrawer.Configure', N'فئات النقد')
) AS code(PermissionCode, PermissionName)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Permissions AS existing WHERE existing.PermissionCode = code.PermissionCode);

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON permission.PermissionCode IN (N'CashDrawer.Count', N'CashDrawer.Review', N'CashDrawer.Configure')
WHERE role.RoleCode IN (N'Admin', N'Supervisor')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RolePermissions AS existing
      WHERE existing.RoleID = role.RoleID AND existing.PermissionID = permission.PermissionID);

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission ON permission.PermissionCode = N'CashDrawer.Count'
WHERE role.RoleCode = N'User'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RolePermissions AS existing
      WHERE existing.RoleID = role.RoleID AND existing.PermissionID = permission.PermissionID);
