IF OBJECT_ID(N'dbo.CentralPosRegistry', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CentralPosRegistry', N'PointType') IS NULL
BEGIN
    ALTER TABLE dbo.CentralPosRegistry
        ADD PointType nvarchar(40) NOT NULL
            CONSTRAINT DF_CentralPosRegistry_PointType DEFAULT (N'Sales');
END;

IF OBJECT_ID(N'dbo.CentralPosRegistry', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CentralPosRegistry', N'DatabaseName') IS NULL
BEGIN
    ALTER TABLE dbo.CentralPosRegistry
        ADD DatabaseName nvarchar(128) NULL;
END;
