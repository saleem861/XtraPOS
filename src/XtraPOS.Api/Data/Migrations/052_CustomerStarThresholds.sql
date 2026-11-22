-- Customer star thresholds: the minimum purchases inside a rolling window of days for one to five stars.
-- Append-only: every change is a new row and the newest row is in force. The same table exists on XtraPOS_Central (037).
-- Adds one table only. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CustomerStarThresholds', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerStarThresholds
    (
        ThresholdID uniqueidentifier NOT NULL CONSTRAINT PK_CustomerStarThresholds PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        WindowDays int NOT NULL,
        OneStar decimal(19, 4) NOT NULL,
        TwoStars decimal(19, 4) NOT NULL,
        ThreeStars decimal(19, 4) NOT NULL,
        FourStars decimal(19, 4) NOT NULL,
        FiveStars decimal(19, 4) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_CustomerStarThresholds_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedByUserID uniqueidentifier NULL,
        CreatedByName nvarchar(200) NULL,
        Source nvarchar(20) NOT NULL CONSTRAINT DF_CustomerStarThresholds_Source DEFAULT (N'Branch'),
        RelayedAt datetime2(0) NULL,
        CONSTRAINT CK_CustomerStarThresholds_Window CHECK (WindowDays BETWEEN 7 AND 730),
        CONSTRAINT CK_CustomerStarThresholds_Order CHECK (OneStar > 0 AND TwoStars > OneStar AND ThreeStars > TwoStars AND FourStars > ThreeStars AND FiveStars > FourStars),
        CONSTRAINT CK_CustomerStarThresholds_Source CHECK (Source IN (N'Branch', N'Central'))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerStarThresholds_Business' AND object_id = OBJECT_ID(N'dbo.CustomerStarThresholds'))
BEGIN
    CREATE INDEX IX_CustomerStarThresholds_Business ON dbo.CustomerStarThresholds (BusinessID, CreatedAt DESC);
END;
