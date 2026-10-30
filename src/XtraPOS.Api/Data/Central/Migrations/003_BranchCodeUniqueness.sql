IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_Branches_BusinessID_BranchCode'
      AND object_id = OBJECT_ID(N'dbo.Branches'))
BEGIN
    CREATE UNIQUE INDEX UX_Branches_BusinessID_BranchCode
        ON dbo.Branches (BusinessID, BranchCode)
        WHERE BranchCode IS NOT NULL;
END
