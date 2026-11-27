-- «إظهار الكل» في معالجة النواقص (قرار المالك 27 نوفمبر 2026):
--   الأصناف غير المطلوبة (بلا مبيعات أو رصيدها يغطي) تُحفظ مع المعالجة وتظهر فقط عند «إظهار الكل» بعلامة «غير مطلوب».
--   ShortageRunItems.IsRequired: 1 للناقص (كل الصفوف القديمة)، 0 لغير المطلوب. لا يدخل في «عولج N من M» ولا يمنع التثبيت.
-- Column only. Safe to run again. Back up XtraPOS_Central before publishing.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.ShortageRunItems', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.ShortageRunItems', N'IsRequired') IS NULL
BEGIN
    ALTER TABLE dbo.ShortageRunItems ADD IsRequired bit NOT NULL CONSTRAINT DF_ShortageRunItems_IsRequired DEFAULT (1);
END;
