-- WhatsApp verification and customer-item special prices.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.Customers', N'WhatsAppPhoneE164') IS NULL
    ALTER TABLE dbo.Customers ADD WhatsAppPhoneE164 nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.Customers', N'PhoneVerificationStatus') IS NULL
    ALTER TABLE dbo.Customers ADD PhoneVerificationStatus nvarchar(20) NOT NULL
        CONSTRAINT DF_Customers_PhoneVerificationStatus DEFAULT (N'Unverified');
IF COL_LENGTH(N'dbo.Customers', N'PhoneVerifiedAt') IS NULL
    ALTER TABLE dbo.Customers ADD PhoneVerifiedAt datetime2(0) NULL;
IF COL_LENGTH(N'dbo.Customers', N'PhoneVerifiedE164') IS NULL
    ALTER TABLE dbo.Customers ADD PhoneVerifiedE164 nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.Customers', N'WhatsAppOptInAt') IS NULL
    ALTER TABLE dbo.Customers ADD WhatsAppOptInAt datetime2(0) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Customers_PhoneVerificationStatus')
    EXEC(N'ALTER TABLE dbo.Customers ADD CONSTRAINT CK_Customers_PhoneVerificationStatus CHECK (PhoneVerificationStatus IN (N''Unverified'', N''Pending'', N''Verified''));');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Customers_WhatsAppPhone' AND object_id = OBJECT_ID(N'dbo.Customers'))
    EXEC(N'CREATE UNIQUE INDEX UX_Customers_WhatsAppPhone ON dbo.Customers (BusinessID, WhatsAppPhoneE164) WHERE WhatsAppPhoneE164 IS NOT NULL;');

IF OBJECT_ID(N'dbo.CustomerPhoneVerificationChallenges', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerPhoneVerificationChallenges
    (
        VerificationID uniqueidentifier NOT NULL CONSTRAINT PK_CustomerPhoneVerificationChallenges PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        DestinationE164 nvarchar(20) NOT NULL,
        Channel nvarchar(20) NOT NULL,
        OtpHash nvarchar(200) NOT NULL,
        Status nvarchar(20) NOT NULL,
        CreatedAt datetime2(0) NOT NULL,
        ExpiresAt datetime2(0) NOT NULL,
        LastSentAt datetime2(0) NOT NULL,
        ResendAvailableAt datetime2(0) NOT NULL,
        AttemptCount int NOT NULL,
        MaxAttempts int NOT NULL,
        ProviderMessageID nvarchar(120) NULL,
        VerifiedAt datetime2(0) NULL,
        CONSTRAINT FK_CustomerPhoneVerificationChallenges_Customer FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT CK_CustomerPhoneVerificationChallenges_Status CHECK (Status IN (N'Pending', N'Verified', N'Expired', N'Locked', N'Failed'))
    );
    CREATE INDEX IX_CustomerPhoneVerificationChallenges_Customer
        ON dbo.CustomerPhoneVerificationChallenges (CustomerID, CreatedAt DESC);
END;

IF OBJECT_ID(N'dbo.CustomerItemSpecialPrices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerItemSpecialPrices
    (
        CustomerItemSpecialPriceID uniqueidentifier NOT NULL CONSTRAINT PK_CustomerItemSpecialPrices PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        SpecialPrice decimal(19, 4) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_CustomerItemSpecialPrices_IsActive DEFAULT (1),
        ValidFrom datetime2(0) NULL,
        ValidTo datetime2(0) NULL,
        Notes nvarchar(1000) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_CustomerItemSpecialPrices_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_CustomerItemSpecialPrices_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedByUserID uniqueidentifier NULL,
        UpdatedByUserID uniqueidentifier NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_CustomerItemSpecialPrices_Customer FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT FK_CustomerItemSpecialPrices_Item FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT CK_CustomerItemSpecialPrices_SpecialPrice CHECK (SpecialPrice >= 0)
    );
    CREATE UNIQUE INDEX UX_CustomerItemSpecialPrices_CustomerItem
        ON dbo.CustomerItemSpecialPrices (BusinessID, CustomerID, ItemID);
    CREATE INDEX IX_CustomerItemSpecialPrices_Lookup
        ON dbo.CustomerItemSpecialPrices (CustomerID, ItemID, IsActive);
END;
