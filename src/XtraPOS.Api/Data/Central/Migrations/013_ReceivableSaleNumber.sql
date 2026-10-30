-- Immutable origin sale number copied when the receivable is created. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.CustomerReceivables', N'SourceSaleNumber') IS NULL
    ALTER TABLE dbo.CustomerReceivables ADD SourceSaleNumber bigint NULL;
