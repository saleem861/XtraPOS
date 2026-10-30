IF OBJECT_ID(N'dbo.CustomerPasskeys', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerPasskeys
    (
        CredentialId VARBINARY(256) NOT NULL CONSTRAINT PK_CustomerPasskeys PRIMARY KEY,
        CustomerID UNIQUEIDENTIFIER NOT NULL,
        PublicKeyX VARBINARY(32) NOT NULL,
        PublicKeyY VARBINARY(32) NOT NULL,
        SignCount INT NOT NULL CONSTRAINT DF_CustomerPasskeys_SignCount DEFAULT (0),
        Status NVARCHAR(20) NOT NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_CustomerPasskeys_CreatedAt DEFAULT (SYSUTCDATETIME()),
        RevokedAt DATETIME2 NULL
    );
    CREATE INDEX IX_CustomerPasskeys_Customer ON dbo.CustomerPasskeys (CustomerID, Status);
END;

IF OBJECT_ID(N'dbo.CustomerPasskeySessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerPasskeySessions
    (
        SessionID UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_CustomerPasskeySessions PRIMARY KEY,
        TokenHash BINARY(32) NOT NULL CONSTRAINT UQ_CustomerPasskeySessions_Token UNIQUE,
        CustomerID UNIQUEIDENTIFIER NOT NULL,
        Purpose NVARCHAR(20) NOT NULL,
        EmployeeUserID UNIQUEIDENTIFIER NOT NULL,
        ExpiresAt DATETIME2 NOT NULL,
        PhoneVerifiedAt DATETIME2 NULL,
        ConsumedAt DATETIME2 NULL,
        ChallengeHash BINARY(32) NULL,
        ChallengeExpiresAt DATETIME2 NULL,
        Result NVARCHAR(20) NOT NULL CONSTRAINT DF_CustomerPasskeySessions_Result DEFAULT (N'Pending'),
        VerifiedCredentialId VARBINARY(256) NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_CustomerPasskeySessions_CreatedAt DEFAULT (SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.CustomerPasskeyAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerPasskeyAudit
    (
        AuditID UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_CustomerPasskeyAudit PRIMARY KEY,
        CustomerID UNIQUEIDENTIFIER NOT NULL,
        ActorUserID UNIQUEIDENTIFIER NULL,
        Action NVARCHAR(40) NOT NULL,
        OccurredAt DATETIME2 NOT NULL CONSTRAINT DF_CustomerPasskeyAudit_OccurredAt DEFAULT (SYSUTCDATETIME())
    );
END;
