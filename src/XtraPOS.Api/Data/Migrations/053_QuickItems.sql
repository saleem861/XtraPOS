-- Quick items shown as one scrollable row on the POS screen.
-- Each save is a new full list (set). BranchID NULL means "all branches", the default for any branch without its own list.
-- The newest set wins. The same tables exist on XtraPOS_Central (038). Adds two tables only. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.QuickItemSets', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QuickItemSets
    (
        SetID uniqueidentifier NOT NULL CONSTRAINT PK_QuickItemSets PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NULL,
        BranchName nvarchar(200) NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_QuickItemSets_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedByUserID uniqueidentifier NULL,
        CreatedByName nvarchar(200) NULL,
        Source nvarchar(20) NOT NULL CONSTRAINT DF_QuickItemSets_Source DEFAULT (N'Branch'),
        RelayedAt datetime2(0) NULL,
        CONSTRAINT CK_QuickItemSets_Source CHECK (Source IN (N'Branch', N'Central'))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QuickItemSets_Scope' AND object_id = OBJECT_ID(N'dbo.QuickItemSets'))
BEGIN
    CREATE INDEX IX_QuickItemSets_Scope ON dbo.QuickItemSets (BusinessID, BranchName, CreatedAt DESC);
END;

IF OBJECT_ID(N'dbo.QuickItemSetLines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QuickItemSetLines
    (
        SetID uniqueidentifier NOT NULL,
        SortOrder int NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(300) NULL,
        Classification nvarchar(200) NULL,
        CONSTRAINT PK_QuickItemSetLines PRIMARY KEY (SetID, SortOrder),
        CONSTRAINT FK_QuickItemSetLines_Sets FOREIGN KEY (SetID) REFERENCES dbo.QuickItemSets (SetID)
    );
END;
