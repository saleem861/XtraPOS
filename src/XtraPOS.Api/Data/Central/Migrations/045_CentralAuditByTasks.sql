-- «التدقيق» كما في النظام القديم (قرار المالك 25 نوفمبر 2026): تدقيق على معالجة النواقص.
--   * ShortageAuditRequests.ProcessedByName / ProcessedDate: الموظف الذي تمت مراجعة معالجته ويوم المعالجة.
--   * ShortageAuditRequestItems.TaskID / TaskType: المهمة المنجزة التي يُسأل عنها (شراء، مخزن، موازنة).
--   * ShortageAuditRequestItems.Received: رد المستلم «هل استلمت هذا الصنف؟» نعم/لا (بدل الكمية المستلمة).
-- Columns only. Safe to run again. Back up XtraPOS_Central before publishing.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.ShortageAuditRequests', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.ShortageAuditRequests', N'ProcessedByName') IS NULL
        ALTER TABLE dbo.ShortageAuditRequests ADD ProcessedByName nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.ShortageAuditRequests', N'ProcessedDate') IS NULL
        ALTER TABLE dbo.ShortageAuditRequests ADD ProcessedDate date NULL;
END;

IF OBJECT_ID(N'dbo.ShortageAuditRequestItems', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.ShortageAuditRequestItems', N'TaskID') IS NULL
        ALTER TABLE dbo.ShortageAuditRequestItems ADD TaskID uniqueidentifier NULL;
    IF COL_LENGTH(N'dbo.ShortageAuditRequestItems', N'TaskType') IS NULL
        ALTER TABLE dbo.ShortageAuditRequestItems ADD TaskType nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.ShortageAuditRequestItems', N'Received') IS NULL
        ALTER TABLE dbo.ShortageAuditRequestItems ADD Received bit NULL;
END;
