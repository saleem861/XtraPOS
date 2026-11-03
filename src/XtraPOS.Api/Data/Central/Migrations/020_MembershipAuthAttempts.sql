-- Short-lived OIDC attempts for membership linking. No customer or price changes.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.MembershipAuthAttempts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MembershipAuthAttempts
    (
        AttemptID uniqueidentifier NOT NULL CONSTRAINT PK_MembershipAuthAttempts PRIMARY KEY,
        BusinessID uniqueidentifier NULL,
        CustomerID uniqueidentifier NULL,
        Provider nvarchar(20) NOT NULL,
        StateHash binary(32) NOT NULL,
        Nonce nvarchar(80) NOT NULL,
        PkceVerifier nvarchar(128) NOT NULL,
        ProviderSubject nvarchar(256) NULL,
        Status nvarchar(20) NOT NULL,
        ExpiresAt datetime2(0) NOT NULL,
        CreatedAt datetime2(0) NOT NULL,
        CONSTRAINT UQ_MembershipAuthAttempts_State UNIQUE (StateHash),
        CONSTRAINT CK_MembershipAuthAttempts_Status CHECK (Status IN (N'Pending', N'Authenticated', N'Consumed'))
    );
END;
