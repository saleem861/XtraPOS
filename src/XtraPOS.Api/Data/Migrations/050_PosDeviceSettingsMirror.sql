-- القيم الأولية لنقاط البيع: نسخة الفرع من جدول المركز CentralPosDeviceSettings.
-- سطر لكل نقطة في كل الفروع، لا لنقاط هذا الفرع فقط، فيتطابق الجدول في كل الفروع بعد السحب من المركز.
-- تعديلات الفرع تُكتب هنا وتدخل سجل الترحيل الموجود (BranchOperationLog، النوع DeviceSettings).
-- جدولا 049 (BranchDeviceSettingsOutbox و DeviceSettingsInbox) لم يعودا مستخدمين ويبقيان دون حذف.
-- Adds one table and copies existing per-device values. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.PosDeviceSettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PosDeviceSettings
    (
        DeviceID uniqueidentifier NOT NULL CONSTRAINT PK_PosDeviceSettings PRIMARY KEY,
        BranchID uniqueidentifier NOT NULL,
        BranchName nvarchar(200) NULL,
        PosNumber int NULL,
        DeviceName nvarchar(200) NULL,
        SettingsJson nvarchar(max) NOT NULL,
        CentralVersion bigint NOT NULL
            CONSTRAINT DF_PosDeviceSettings_CentralVersion DEFAULT (0),
        UpdatedAtUtc datetime2(3) NOT NULL
            CONSTRAINT DF_PosDeviceSettings_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedSource nvarchar(20) NOT NULL,
        SyncedAtUtc datetime2(0) NULL,
        CONSTRAINT CK_PosDeviceSettings_UpdatedSource CHECK (UpdatedSource IN (N'Branch', N'Central'))
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_PosDeviceSettings_Branch'
      AND object_id = OBJECT_ID(N'dbo.PosDeviceSettings'))
    CREATE INDEX IX_PosDeviceSettings_Branch
        ON dbo.PosDeviceSettings (BranchID, PosNumber);

-- قيم حُفظت بنسخة 049 داخل POSDeviceConfigurations.ConfigurationJson تحت "settings".
IF OBJECT_ID(N'dbo.POSDeviceConfigurations', N'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.PosDeviceSettings (DeviceID, BranchID, BranchName, PosNumber, DeviceName, SettingsJson, CentralVersion, UpdatedSource)
    SELECT d.DeviceID, d.BranchID, b.BranchName, st.StationNumber, d.DeviceName,
           JSON_QUERY(c.ConfigurationJson, N'$.settings'), 0, N'Branch'
    FROM dbo.POSDeviceConfigurations AS c
    INNER JOIN dbo.Devices AS d ON d.DeviceID = c.DeviceID
    LEFT JOIN dbo.Branches AS b ON b.BranchID = d.BranchID
    LEFT JOIN dbo.POSStations AS st ON st.StationID = d.StationID
    WHERE ISJSON(c.ConfigurationJson) = 1
      AND JSON_QUERY(c.ConfigurationJson, N'$.settings') IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM dbo.PosDeviceSettings AS existing WHERE existing.DeviceID = d.DeviceID);
END;
