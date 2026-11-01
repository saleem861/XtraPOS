-- Draft identity for a passkey created before the customer row is saved.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.CustomerPasskeySessions', N'DraftName') IS NULL
    ALTER TABLE dbo.CustomerPasskeySessions ADD DraftName nvarchar(300) NULL;

IF COL_LENGTH(N'dbo.CustomerPasskeySessions', N'DraftPhone') IS NULL
    ALTER TABLE dbo.CustomerPasskeySessions ADD DraftPhone nvarchar(50) NULL;
