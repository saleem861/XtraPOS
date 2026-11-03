-- Idempotent staging central database.
-- Run against master on the authorized SQL Server instance, as a database administrator.
-- Do not run this inside XtraPOS_Central. This script never alters that database.
--
-- sqlcmd -I -S <authorized-instance> -d master -v StagingCentralPassword="<chosen password>" -i deploy/staging-central-provision.sql
--
-- Passkey credentials are stored by the branch database, not this central database.
-- After this database exists and the staging connection points here, the staging
-- activation helper applies central migrations 001 through 016. It applies branch
-- migrations, including 031_CustomerPasskeys and 033_CustomerPasskeyDraft, only to
-- XtraPOS_Branch_Test_Staging.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF DB_NAME() <> N'master'
    THROW 51000, 'Run this script against master.', 1;
IF DB_NAME() = N'XtraPOS_Central'
    THROW 51001, 'Refusing to run inside XtraPOS_Central.', 1;
IF N'$(StagingCentralPassword)' = N'' OR CHARINDEX(N'''', N'$(StagingCentralPassword)') > 0
    THROW 51002, 'Pass -v StagingCentralPassword=... Use a password without a single quote, and do not store it in git.', 1;

IF DB_ID(N'XtraPOS_Central_Test_Staging') IS NULL
BEGIN
    CREATE DATABASE XtraPOS_Central_Test_Staging COLLATE Arabic_CI_AS;
END;

IF SUSER_ID(N'xtrapos_staging_central') IS NULL
BEGIN
    CREATE LOGIN xtrapos_staging_central
        WITH PASSWORD = N'$(StagingCentralPassword)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
END
ELSE
BEGIN
    ALTER LOGIN xtrapos_staging_central
        WITH PASSWORD = N'$(StagingCentralPassword)', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
END;

-- The login receives no server role. It can use only the staging central database.
USE XtraPOS_Central_Test_Staging;
IF DB_NAME() <> N'XtraPOS_Central_Test_Staging'
    THROW 51003, 'Refusing to continue outside XtraPOS_Central_Test_Staging.', 1;

IF DATABASE_PRINCIPAL_ID(N'xtrapos_staging_central') IS NULL
    CREATE USER xtrapos_staging_central FOR LOGIN xtrapos_staging_central;

IF NOT EXISTS (
    SELECT 1
    FROM sys.database_role_members AS members
    INNER JOIN sys.database_principals AS roles ON roles.principal_id = members.role_principal_id
    INNER JOIN sys.database_principals AS users ON users.principal_id = members.member_principal_id
    WHERE roles.name = N'db_owner' AND users.name = N'xtrapos_staging_central')
    ALTER ROLE db_owner ADD MEMBER xtrapos_staging_central;

-- Fictional rows only. This section does nothing until central migrations have created the tables.
IF OBJECT_ID(N'dbo.Businesses', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.Businesses)
BEGIN
    DECLARE @BusinessID uniqueidentifier = '11111111-1111-4111-8111-111111111111';
    INSERT INTO dbo.Businesses
        (BusinessID, BusinessName, BusinessDisplayName, Description, Phone, Address, IsActive, CreatedAt)
    VALUES
        (@BusinessID, N'Staging Test Business', N'Staging Test Business', N'Fictional staging business', NULL, NULL, 1, SYSUTCDATETIME());

    IF OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.Branches)
    BEGIN
        INSERT INTO dbo.Branches
            (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
        VALUES
            ('22222222-2222-4222-8222-222222222222', @BusinessID, N'ST', N'Staging Test Branch', N'Retail', 1, SYSUTCDATETIME());
    END;

    IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.Customers)
    BEGIN
        INSERT INTO dbo.Customers
            (CustomerID, BusinessID, CustomerNumber, Name, Phone, IsActive, CreatedAt, UpdatedAt)
        VALUES
            ('33333333-3333-4333-8333-333333333333', @BusinessID, N'ST-1', N'Staging Test Customer', N'0000000000', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
    END;
END;

PRINT 'STAGING_CENTRAL_DB=XtraPOS_Central_Test_Staging';
PRINT 'STAGING_CENTRAL_LOGIN=xtrapos_staging_central';
