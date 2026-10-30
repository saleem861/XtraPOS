/*
Review only. Do not run against XtraPOS_Central or any existing database
until the owner explicitly authorizes it.

Effective permissions for [xtrapos_provisioner]:
- CONNECT SQL, which a new login receives by default.
- CREATE ANY DATABASE. The login becomes owner of databases it creates and can
  change or drop only those databases. It is not dbcreator and cannot alter,
  drop, or restore XtraPOS_Central or XtraPOS_Branch_Test_Staging.
- DENY VIEW ANY DATABASE, so the login cannot list databases it does not own.
- No user mapping in XtraPOS_Central except the one created below.
- EXECUTE only on dbo.RegisterProvisionedBranch. The procedure runs as dbo,
  so the login does not receive INSERT, UPDATE, or DELETE on any central table.
- No access to customer, ledger, or financial tables.

After the login exists, store Server, User, and Password only in the staging
server section SqlProvisioning. Do not commit that section and do not send it
to the browser.
*/

-- CREATE LOGIN [xtrapos_provisioner] WITH PASSWORD = N'<owner chooses>', CHECK_POLICY = ON;
-- GRANT CREATE ANY DATABASE TO [xtrapos_provisioner];
-- DENY VIEW ANY DATABASE TO [xtrapos_provisioner];

USE XtraPOS_Central;
GO

CREATE OR ALTER PROCEDURE dbo.RegisterProvisionedBranch
    @BranchId uniqueidentifier,
    @BusinessId uniqueidentifier,
    @BranchCode nvarchar(12),
    @BranchName nvarchar(150),
    @AdminUserId uniqueidentifier,
    @Result int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET @Result = 0;

    IF @BranchCode IS NULL OR LEN(@BranchCode) NOT BETWEEN 2 AND 12
        THROW 50002, N'Invalid branch code.', 1;
    IF @BranchName IS NULL OR LEN(@BranchName) NOT BETWEEN 2 AND 150
        THROW 50002, N'Invalid branch name.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Businesses WHERE BusinessID = @BusinessId AND IsActive = 1)
        THROW 50003, N'Business was not verified.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM dbo.Users AS u
        INNER JOIN dbo.UserRoles AS ur ON ur.UserID = u.UserID
        INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID AND r.RoleCode = N'Admin'
        WHERE u.UserID = @AdminUserId AND u.IsActive = 1)
        THROW 50004, N'Central administrator was not authorized.', 1;

    IF EXISTS (
        SELECT 1 FROM dbo.Branches
        WHERE BranchID = @BranchId
          AND (BranchCode <> @BranchCode OR BusinessID <> @BusinessId OR BranchName <> @BranchName))
        THROW 50005, N'Branch identity conflict.', 1;

    IF EXISTS (
        SELECT 1 FROM dbo.Branches
        WHERE BranchCode = @BranchCode AND BranchID <> @BranchId)
        THROW 50005, N'Branch code conflict.', 1;

    IF EXISTS (
        SELECT 1 FROM dbo.Branches
        WHERE BranchID = @BranchId
          AND BusinessID = @BusinessId
          AND BranchCode = @BranchCode
          AND BranchName = @BranchName)
    BEGIN
        IF NOT EXISTS (
            SELECT 1 FROM dbo.UserBranchAccess
            WHERE UserID = @AdminUserId AND BranchID = @BranchId AND IsActive = 1)
        BEGIN
            INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
            VALUES (@AdminUserId, @BranchId, 1, SYSUTCDATETIME());
            IF @@ROWCOUNT <> 1
                THROW 50005, N'Administrator branch access was not inserted.', 1;
        END

        SET @Result = 2;
        RETURN;
    END;

    INSERT INTO dbo.Branches
        (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
    VALUES
        (@BranchId, @BusinessId, @BranchCode, @BranchName, N'Retail', 1, SYSUTCDATETIME());

    IF @@ROWCOUNT <> 1
        THROW 50005, N'Branch was not inserted.', 1;

    INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
    VALUES (@AdminUserId, @BranchId, 1, SYSUTCDATETIME());

    IF @@ROWCOUNT <> 1
        THROW 50005, N'Administrator branch access was not inserted.', 1;

    SET @Result = 1;
END;
GO

-- CREATE USER [xtrapos_provisioner] FOR LOGIN [xtrapos_provisioner];
-- GRANT EXECUTE ON dbo.RegisterProvisionedBranch TO [xtrapos_provisioner];
