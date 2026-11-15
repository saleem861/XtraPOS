-- Idempotent staging branch database.
-- Run against master on the authorized SQL Server instance, as a database administrator.
-- Do not run this inside XtraPOS_Central. This script never alters that database.
-- It does not create or alter the xtrapos_staging login, and it grants no server role.
--
-- sqlcmd -I -S <authorized-instance> -d master -i deploy/staging-branch-provision.sql
--
-- After this database exists, staging activation applies the branch migrations.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF DB_NAME() <> N'master'
    THROW 51000, 'Run this script against master.', 1;
IF DB_NAME() = N'XtraPOS_Central'
    THROW 51001, 'Refusing to run inside XtraPOS_Central.', 1;
IF SUSER_ID(N'xtrapos_staging') IS NULL
    THROW 51004, 'The xtrapos_staging login must already exist. This script does not create it.', 1;

IF DB_ID(N'XtraPOS_Branch_Test_Staging') IS NULL
BEGIN
    CREATE DATABASE XtraPOS_Branch_Test_Staging COLLATE Arabic_CI_AS;
END;

USE XtraPOS_Branch_Test_Staging;
IF DB_NAME() <> N'XtraPOS_Branch_Test_Staging'
    THROW 51003, 'Refusing to continue outside XtraPOS_Branch_Test_Staging.', 1;

IF DATABASE_PRINCIPAL_ID(N'xtrapos_staging') IS NULL
    CREATE USER xtrapos_staging FOR LOGIN xtrapos_staging;

IF NOT EXISTS (
    SELECT 1
    FROM sys.database_role_members AS members
    INNER JOIN sys.database_principals AS roles ON roles.principal_id = members.role_principal_id
    INNER JOIN sys.database_principals AS users ON users.principal_id = members.member_principal_id
    WHERE roles.name = N'db_owner' AND users.name = N'xtrapos_staging')
    ALTER ROLE db_owner ADD MEMBER xtrapos_staging;

PRINT 'STAGING_BRANCH_DB=XtraPOS_Branch_Test_Staging';
PRINT 'STAGING_BRANCH_LOGIN=xtrapos_staging';
