-- Employee cash error ledger actions: offset (مقاصة), settle (تسوية), defer (تأجيل) and supervisor notes.
-- Append-only: an action never changes the error rows or another action. The same objects exist on every branch database (051). Branches relay their actions here through BranchOperationLog (EmployeeCashErrorAction).
-- Adds two tables only. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.EmployeeCashErrorActions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeCashErrorActions
    (
        ActionID uniqueidentifier NOT NULL CONSTRAINT PK_EmployeeCashErrorActions PRIMARY KEY,
        ActionType nvarchar(20) NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        EmployeeUserID uniqueidentifier NOT NULL,
        Amount decimal(19, 4) NOT NULL CONSTRAINT DF_EmployeeCashErrorActions_Amount DEFAULT (0),
        Method nvarchar(20) NULL,
        DeferToDate date NULL,
        Note nvarchar(500) NULL,
        ActorUserID uniqueidentifier NOT NULL,
        ActorName nvarchar(200) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_EmployeeCashErrorActions_CreatedAt DEFAULT (SYSUTCDATETIME()),
        RelayedAt datetime2(0) NULL,
        CONSTRAINT CK_EmployeeCashErrorActions_Type CHECK (ActionType IN (N'Offset', N'Settle', N'Defer', N'Note')),
        CONSTRAINT CK_EmployeeCashErrorActions_Method CHECK (Method IS NULL OR Method IN (N'Cash', N'Payroll', N'Other')),
        CONSTRAINT CK_EmployeeCashErrorActions_Shape CHECK (
            (ActionType = N'Offset' AND Method IS NULL AND DeferToDate IS NULL)
            OR (ActionType = N'Settle' AND Method IS NOT NULL AND DeferToDate IS NULL)
            OR (ActionType = N'Defer' AND DeferToDate IS NOT NULL AND Amount = 0)
            OR (ActionType = N'Note' AND Note IS NOT NULL AND Amount = 0))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmployeeCashErrorActions_Employee' AND object_id = OBJECT_ID(N'dbo.EmployeeCashErrorActions'))
BEGIN
    CREATE INDEX IX_EmployeeCashErrorActions_Employee ON dbo.EmployeeCashErrorActions (EmployeeUserID, CreatedAt);
END;

IF OBJECT_ID(N'dbo.EmployeeCashErrorActionItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeCashErrorActionItems
    (
        ActionID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemAmount decimal(19, 4) NOT NULL,
        CONSTRAINT PK_EmployeeCashErrorActionItems PRIMARY KEY (ActionID, ItemID),
        CONSTRAINT FK_EmployeeCashErrorActionItems_Actions FOREIGN KEY (ActionID) REFERENCES dbo.EmployeeCashErrorActions (ActionID)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmployeeCashErrorActionItems_Item' AND object_id = OBJECT_ID(N'dbo.EmployeeCashErrorActionItems'))
BEGIN
    CREATE INDEX IX_EmployeeCashErrorActionItems_Item ON dbo.EmployeeCashErrorActionItems (ItemID);
END;
