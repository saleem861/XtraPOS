-- Permanent record of explicit data imports.
-- Creating this table does not import or delete catalog data.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.DataImportHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DataImportHistory
    (
        ImportID uniqueidentifier NOT NULL CONSTRAINT PK_DataImportHistory PRIMARY KEY,
        ImportType nvarchar(64) NOT NULL,
        Source nvarchar(128) NOT NULL,
        StartedAt datetime2(3) NOT NULL,
        CompletedAt datetime2(3) NULL,
        Status nvarchar(32) NOT NULL,
        RowsProcessed int NOT NULL,
        Notes nvarchar(400) NULL
    );
END;
