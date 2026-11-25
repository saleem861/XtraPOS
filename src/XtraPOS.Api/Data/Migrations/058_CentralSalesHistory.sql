-- Central computes each branch's sales rate from the movements it receives (owner decision, 25 November 2026).
-- Movements written before 057 were never sent to central, so central would see only a day or two of sales.
-- This sends the last 31 days of sales and sale returns once, marked CentralHistoryOnly: central stores them for
-- the sales rate and does not add them to its balance (the balance already includes them).
-- Only movements that were never linked to the relay (RelayOperationID IS NULL) are resent. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.StockMovements', N'CentralHistoryOnly') IS NULL
    ALTER TABLE dbo.StockMovements ADD CentralHistoryOnly bit NULL;

IF COL_LENGTH(N'dbo.StockMovements', N'CentralRelayedAt') IS NOT NULL
    EXEC (N'UPDATE dbo.StockMovements
           SET CentralRelayedAt = NULL, CentralHistoryOnly = 1
           WHERE CentralHistoryOnly IS NULL
             AND RelayOperationID IS NULL
             AND CentralRelayedAt IS NOT NULL
             AND SourceType IN (N''Sale'', N''SaleReturn'')
             AND OccurredAt >= DATEADD(day, -31, SYSUTCDATETIME());');
