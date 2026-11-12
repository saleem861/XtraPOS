-- Additive unified-schema objects copied from the approved isolated schema.
-- Existing tables, rows, and financial semantics are left in place.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
IF OBJECT_ID(N'dbo.BranchSqlEndpoints', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[BranchSqlEndpoints]
    (
        [BranchID] uniqueidentifier NOT NULL,
        [Host] nvarchar(200) NOT NULL,
        [Port] int NOT NULL,
        [InstanceName] nvarchar(128) NULL,
        [DatabaseName] nvarchar(128) NOT NULL,
        [AuthenticationMode] nvarchar(32) NOT NULL,
        [PrincipalName] nvarchar(128) NOT NULL,
        [TlsMode] nvarchar(32) NOT NULL,
        [Status] nvarchar(32) NOT NULL,
        [LastSuccessfulCheck] datetime2(0) NULL,
        [UpdatedAtUtc] datetime2(0) CONSTRAINT [DF_BranchSqlEndpoints_UpdatedAtUtc] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_BranchSqlEndpoints] PRIMARY KEY CLUSTERED ([BranchID])
    );
END;

IF OBJECT_ID(N'dbo.CentralFinancialEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CentralFinancialEvents]
    (
        [EventSequence] bigint IDENTITY(1,1) NOT NULL,
        [OperationID] nvarchar(80) NOT NULL,
        [EventType] nvarchar(40) NOT NULL,
        [BusinessID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [OriginBranchID] uniqueidentifier NOT NULL,
        [POSNumber] int NULL,
        [ActorUserID] uniqueidentifier NULL,
        [SourceSaleID] uniqueidentifier NULL,
        [SourceSaleNumber] bigint NULL,
        [Amount] decimal(19,4) NOT NULL,
        [SignedEffect] decimal(19,4) NOT NULL,
        [CurrencyCode] nvarchar(3) CONSTRAINT [DF_CentralFinancialEvents_Currency] DEFAULT (N'ILS') NOT NULL,
        [Reason] nvarchar(400) NULL,
        [ReversedOperationID] nvarchar(80) NULL,
        [Payload] nvarchar(max) CONSTRAINT [DF_CentralFinancialEvents_Payload] DEFAULT (N'{}') NOT NULL,
        [ConfirmedAt] datetime2(0) CONSTRAINT [DF_CentralFinancialEvents_ConfirmedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_CentralFinancialEvents] PRIMARY KEY CLUSTERED ([EventSequence])
    );
END;

IF OBJECT_ID(N'dbo.CentralPosRegistry', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CentralPosRegistry]
    (
        [PosNumber] int NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [DeviceID] uniqueidentifier NULL,
        [PosName] nvarchar(200) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [ReservedAt] datetime2(0) CONSTRAINT [DF_CentralPosRegistry_ReservedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        [PointType] nvarchar(40) CONSTRAINT [DF_CentralPosRegistry_PointType] DEFAULT (N'Sales') NOT NULL,
        [DatabaseName] nvarchar(128) NULL,
        CONSTRAINT [PK_CentralPosRegistry] PRIMARY KEY CLUSTERED ([PosNumber])
    );
END;

IF OBJECT_ID(N'dbo.CreditAllocationReversals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CreditAllocationReversals]
    (
        [ReversalAllocationID] uniqueidentifier NOT NULL,
        [ReversalOperationID] uniqueidentifier NOT NULL,
        [OriginalAllocationID] uniqueidentifier NOT NULL,
        [ReceivableID] uniqueidentifier NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        [LedgerEntryID] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_CreditAllocationReversals] PRIMARY KEY CLUSTERED ([ReversalAllocationID])
    );
END;

IF OBJECT_ID(N'dbo.CreditAuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CreditAuditLog]
    (
        [AuditID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [ActorUserID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NULL,
        [DeviceID] uniqueidentifier NULL,
        [Action] nvarchar(50) NOT NULL,
        [OldValue] nvarchar(500) NULL,
        [NewValue] nvarchar(500) NULL,
        [Reason] nvarchar(500) NULL,
        [Result] nvarchar(30) NOT NULL,
        [OccurredAt] datetime2(0) CONSTRAINT [DF_CreditAuditLog_OccurredAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_CreditAuditLog] PRIMARY KEY CLUSTERED ([AuditID])
    );
END;

IF OBJECT_ID(N'dbo.CreditAuthorizations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CreditAuthorizations]
    (
        [AuthorizationID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [RequestedAmount] decimal(19,4) NOT NULL,
        [CurrencyCode] nvarchar(3) CONSTRAINT [DF_CreditAuthorizations_CurrencyCode] DEFAULT (N'ILS') NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [IdempotencyKey] nvarchar(80) NOT NULL,
        [ExpiresAt] datetime2(0) NOT NULL,
        [SourceSaleID] uniqueidentifier NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_CreditAuthorizations_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_CreditAuthorizations] PRIMARY KEY CLUSTERED ([AuthorizationID])
    );
END;

IF OBJECT_ID(N'dbo.CreditCollectionIntents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CreditCollectionIntents]
    (
        [CollectionOperationID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        [CurrencyCode] nvarchar(3) CONSTRAINT [DF_CreditCollectionIntents_Currency] DEFAULT (N'ILS') NOT NULL,
        [PaymentMethodCode] nvarchar(20) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [IdempotencyKey] nvarchar(80) NOT NULL,
        [ExpiresAt] datetime2(0) NOT NULL,
        [CreatedBy] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_CreditCollectionIntents_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_CreditCollectionIntents] PRIMARY KEY CLUSTERED ([CollectionOperationID])
    );
END;

IF OBJECT_ID(N'dbo.CreditCollectionReservations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CreditCollectionReservations]
    (
        [ReservationID] uniqueidentifier NOT NULL,
        [CollectionOperationID] uniqueidentifier NOT NULL,
        [ReceivableID] uniqueidentifier NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        CONSTRAINT [PK_CreditCollectionReservations] PRIMARY KEY CLUSTERED ([ReservationID])
    );
END;

IF OBJECT_ID(N'dbo.CreditRepaymentReversals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CreditRepaymentReversals]
    (
        [ReversalOperationID] uniqueidentifier NOT NULL,
        [CollectionOperationID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        [ReasonCode] nvarchar(40) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [IdempotencyKey] nvarchar(80) NOT NULL,
        [LedgerEntryID] uniqueidentifier NULL,
        [CreatedBy] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_CreditRepaymentReversals_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [ExpiresAt] datetime2(0) NOT NULL,
        CONSTRAINT [PK_CreditRepaymentReversals] PRIMARY KEY CLUSTERED ([ReversalOperationID])
    );
END;

IF OBJECT_ID(N'dbo.CustomerMembershipIdentities', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CustomerMembershipIdentities]
    (
        [IdentityID] uniqueidentifier NOT NULL,
        [BusinessID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [Provider] nvarchar(20) NOT NULL,
        [ProviderSubject] nvarchar(256) NOT NULL,
        [LinkedAt] datetime2(0) NOT NULL,
        [LastAuthenticatedAt] datetime2(0) NULL,
        [Status] nvarchar(20) NOT NULL,
        [RevokedAt] datetime2(0) NULL,
        [CreatedAt] datetime2(0) NOT NULL,
        [UpdatedAt] datetime2(0) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        [ProviderAccountDisplay] nvarchar(256) NULL,
        [ProviderEmail] nvarchar(256) NULL,
        CONSTRAINT [PK_CustomerMembershipIdentities] PRIMARY KEY CLUSTERED ([IdentityID])
    );
END;

IF OBJECT_ID(N'dbo.CustomerMembershipSessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CustomerMembershipSessions]
    (
        [SessionID] uniqueidentifier NOT NULL,
        [BusinessID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [IdentityID] uniqueidentifier NOT NULL,
        [SessionTokenHash] binary(32) NOT NULL,
        [CreatedAt] datetime2(0) NOT NULL,
        [LastUsedAt] datetime2(0) NOT NULL,
        [IdleExpiresAt] datetime2(0) NOT NULL,
        [AbsoluteExpiresAt] datetime2(0) NOT NULL,
        [RevokedAt] datetime2(0) NULL,
        [RevocationReason] nvarchar(80) NULL,
        [DeviceDisplayName] nvarchar(80) NULL,
        [BrowserFamily] nvarchar(40) NULL,
        [Platform] nvarchar(40) NULL,
        [LastSeenAt] datetime2(0) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_CustomerMembershipSessions] PRIMARY KEY CLUSTERED ([SessionID])
    );
END;

IF OBJECT_ID(N'dbo.MembershipAuthAttempts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[MembershipAuthAttempts]
    (
        [AttemptID] uniqueidentifier NOT NULL,
        [BusinessID] uniqueidentifier NULL,
        [CustomerID] uniqueidentifier NULL,
        [Provider] nvarchar(20) NOT NULL,
        [StateHash] binary(32) NOT NULL,
        [Nonce] nvarchar(80) NOT NULL,
        [PkceVerifier] nvarchar(128) NOT NULL,
        [ProviderSubject] nvarchar(256) NULL,
        [Status] nvarchar(20) NOT NULL,
        [ExpiresAt] datetime2(0) NOT NULL,
        [CreatedAt] datetime2(0) NOT NULL,
        [OwnershipVerifiedAt] datetime2(0) NULL,
        CONSTRAINT [PK_MembershipAuthAttempts] PRIMARY KEY CLUSTERED ([AttemptID])
    );
END;

IF OBJECT_ID(N'dbo.MembershipCounterCodes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[MembershipCounterCodes]
    (
        [CounterID] uniqueidentifier NOT NULL,
        [BusinessID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [PublicToken] nvarchar(80) NOT NULL,
        [SaleContextID] uniqueidentifier NULL,
        [CustomerID] uniqueidentifier NULL,
        [Status] nvarchar(20) CONSTRAINT [DF_MembershipCounterCodes_Status] DEFAULT (N'Waiting') NOT NULL,
        [RecognizedCustomerID] uniqueidentifier NULL,
        [UpdatedAt] datetime2(0) NOT NULL,
        [TokenDay] date NULL,
        CONSTRAINT [PK_MembershipCounterCodes] PRIMARY KEY CLUSTERED ([CounterID])
    );
END;

IF OBJECT_ID(N'dbo.MembershipEnrollmentRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[MembershipEnrollmentRequests]
    (
        [EnrollmentID] uniqueidentifier NOT NULL,
        [BusinessID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NULL,
        [TokenHash] binary(32) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [ExpiresAt] datetime2(0) NOT NULL,
        [CreatedAt] datetime2(0) NOT NULL,
        [CashierUserID] uniqueidentifier NULL,
        [CounterID] uniqueidentifier NULL,
        [Provider] nvarchar(20) NULL,
        [ProviderSubject] nvarchar(256) NULL,
        [ProviderAccountDisplay] nvarchar(256) NULL,
        [ProviderEmail] nvarchar(256) NULL,
        CONSTRAINT [PK_MembershipEnrollmentRequests] PRIMARY KEY CLUSTERED ([EnrollmentID])
    );
END;

IF OBJECT_ID(N'dbo.MembershipVerificationAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[MembershipVerificationAudit]
    (
        [AuditID] uniqueidentifier NOT NULL,
        [BusinessID] uniqueidentifier NOT NULL,
        [EventCode] nvarchar(40) NOT NULL,
        [CustomerID] uniqueidentifier NULL,
        [VerificationRequestID] uniqueidentifier NULL,
        [CorrelationID] uniqueidentifier NULL,
        [CreatedAt] datetime2(0) NOT NULL,
        [ActorUserID] uniqueidentifier NULL,
        CONSTRAINT [PK_MembershipVerificationAudit] PRIMARY KEY CLUSTERED ([AuditID])
    );
END;

IF OBJECT_ID(N'dbo.MembershipVerificationRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[MembershipVerificationRequests]
    (
        [VerificationRequestID] uniqueidentifier NOT NULL,
        [BusinessID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [CounterID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NULL,
        [CashierUserID] uniqueidentifier NOT NULL,
        [SaleContextID] uniqueidentifier NOT NULL,
        [QRTokenHash] binary(32) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [CreatedAt] datetime2(0) NOT NULL,
        [ExpiresAt] datetime2(0) NOT NULL,
        [VerifiedAt] datetime2(0) NULL,
        [VerifiedByMembershipSessionID] uniqueidentifier NULL,
        [ConsumedAt] datetime2(0) NULL,
        [CancelledAt] datetime2(0) NULL,
        [CorrelationID] uniqueidentifier NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_MembershipVerificationRequests] PRIMARY KEY CLUSTERED ([VerificationRequestID])
    );
END;

IF OBJECT_ID(N'dbo.SyncChangeLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[SyncChangeLog]
    (
        [ChangeSequence] bigint IDENTITY(1,1) NOT NULL,
        [EntityType] nvarchar(64) NOT NULL,
        [EntityId] uniqueidentifier NOT NULL,
        [Operation] nvarchar(16) NOT NULL,
        [Payload] nvarchar(max) NULL,
        [CreatedAt] datetime2(3) CONSTRAINT [DF_SyncChangeLog_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [ChangedByUserID] uniqueidentifier NULL,
        CONSTRAINT [PK_SyncChangeLog] PRIMARY KEY CLUSTERED ([ChangeSequence])
    );
END;

IF OBJECT_ID(N'dbo.SyncEntityOwnership', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[SyncEntityOwnership]
    (
        [EntityType] nvarchar(64) NOT NULL,
        [OwnerRole] nvarchar(20) NOT NULL,
        [IsImplemented] bit NOT NULL,
        CONSTRAINT [PK_SyncEntityOwnership] PRIMARY KEY CLUSTERED ([EntityType])
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CentralFinancialEvents_BusinessCustomer' AND object_id = OBJECT_ID(N'dbo.CentralFinancialEvents'))
    CREATE  INDEX [IX_CentralFinancialEvents_BusinessCustomer] ON dbo.[CentralFinancialEvents] ([BusinessID], [CustomerID], [EventSequence]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CentralFinancialEvents_Operation' AND object_id = OBJECT_ID(N'dbo.CentralFinancialEvents'))
    CREATE UNIQUE  INDEX [UQ_CentralFinancialEvents_Operation] ON dbo.[CentralFinancialEvents] ([OperationID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CreditAllocationReversals_Original' AND object_id = OBJECT_ID(N'dbo.CreditAllocationReversals'))
    CREATE UNIQUE  INDEX [UQ_CreditAllocationReversals_Original] ON dbo.[CreditAllocationReversals] ([OriginalAllocationID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CreditAuthorizations_Active' AND object_id = OBJECT_ID(N'dbo.CreditAuthorizations'))
    CREATE  INDEX [IX_CreditAuthorizations_Active] ON dbo.[CreditAuthorizations] ([CustomerID], [Status], [ExpiresAt]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CreditAuthorizations_Idempotency' AND object_id = OBJECT_ID(N'dbo.CreditAuthorizations'))
    CREATE UNIQUE  INDEX [UQ_CreditAuthorizations_Idempotency] ON dbo.[CreditAuthorizations] ([IdempotencyKey]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CreditCollectionIntents_Customer' AND object_id = OBJECT_ID(N'dbo.CreditCollectionIntents'))
    CREATE  INDEX [IX_CreditCollectionIntents_Customer] ON dbo.[CreditCollectionIntents] ([CustomerID], [Status]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CreditCollectionIntents_Idempotency' AND object_id = OBJECT_ID(N'dbo.CreditCollectionIntents'))
    CREATE UNIQUE  INDEX [UQ_CreditCollectionIntents_Idempotency] ON dbo.[CreditCollectionIntents] ([IdempotencyKey]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CreditCollectionReservations_IntentReceivable' AND object_id = OBJECT_ID(N'dbo.CreditCollectionReservations'))
    CREATE UNIQUE  INDEX [UQ_CreditCollectionReservations_IntentReceivable] ON dbo.[CreditCollectionReservations] ([CollectionOperationID], [ReceivableID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CreditRepaymentReversals_Collection' AND object_id = OBJECT_ID(N'dbo.CreditRepaymentReversals'))
    CREATE UNIQUE  INDEX [UQ_CreditRepaymentReversals_Collection] ON dbo.[CreditRepaymentReversals] ([CollectionOperationID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CreditRepaymentReversals_Idempotency' AND object_id = OBJECT_ID(N'dbo.CreditRepaymentReversals'))
    CREATE UNIQUE  INDEX [UQ_CreditRepaymentReversals_Idempotency] ON dbo.[CreditRepaymentReversals] ([IdempotencyKey]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CustomerMembershipIdentities_ActiveSubject' AND object_id = OBJECT_ID(N'dbo.CustomerMembershipIdentities'))
    CREATE UNIQUE  INDEX [UQ_CustomerMembershipIdentities_ActiveSubject] ON dbo.[CustomerMembershipIdentities] ([BusinessID], [Provider], [ProviderSubject]) WHERE ([Status]=N'Active');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerMembershipSessions_Customer' AND object_id = OBJECT_ID(N'dbo.CustomerMembershipSessions'))
    CREATE  INDEX [IX_CustomerMembershipSessions_Customer] ON dbo.[CustomerMembershipSessions] ([BusinessID], [CustomerID], [RevokedAt], [IdleExpiresAt]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CustomerMembershipSessions_TokenHash' AND object_id = OBJECT_ID(N'dbo.CustomerMembershipSessions'))
    CREATE UNIQUE  INDEX [UQ_CustomerMembershipSessions_TokenHash] ON dbo.[CustomerMembershipSessions] ([SessionTokenHash]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_MembershipAuthAttempts_State' AND object_id = OBJECT_ID(N'dbo.MembershipAuthAttempts'))
    CREATE UNIQUE  INDEX [UQ_MembershipAuthAttempts_State] ON dbo.[MembershipAuthAttempts] ([StateHash]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_MembershipCounterCodes_PublicToken' AND object_id = OBJECT_ID(N'dbo.MembershipCounterCodes'))
    CREATE UNIQUE  INDEX [UQ_MembershipCounterCodes_PublicToken] ON dbo.[MembershipCounterCodes] ([PublicToken]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_MembershipEnrollmentRequests_TokenHash' AND object_id = OBJECT_ID(N'dbo.MembershipEnrollmentRequests'))
    CREATE UNIQUE  INDEX [UQ_MembershipEnrollmentRequests_TokenHash] ON dbo.[MembershipEnrollmentRequests] ([TokenHash]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MembershipVerificationRequests_Counter' AND object_id = OBJECT_ID(N'dbo.MembershipVerificationRequests'))
    CREATE  INDEX [IX_MembershipVerificationRequests_Counter] ON dbo.[MembershipVerificationRequests] ([BusinessID], [BranchID], [CounterID], [SaleContextID], [Status]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_MembershipVerificationRequests_TokenHash' AND object_id = OBJECT_ID(N'dbo.MembershipVerificationRequests'))
    CREATE UNIQUE  INDEX [UQ_MembershipVerificationRequests_TokenHash] ON dbo.[MembershipVerificationRequests] ([QRTokenHash]);

IF OBJECT_ID(N'dbo.CreditAllocationReversals', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.CreditPaymentAllocations', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CreditAllocationReversals_Original')
    ALTER TABLE dbo.[CreditAllocationReversals] ADD CONSTRAINT [FK_CreditAllocationReversals_Original]
        FOREIGN KEY ([OriginalAllocationID]) REFERENCES dbo.[CreditPaymentAllocations] ([AllocationID]);

IF OBJECT_ID(N'dbo.CreditAllocationReversals', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.CreditRepaymentReversals', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CreditAllocationReversals_Reversal')
    ALTER TABLE dbo.[CreditAllocationReversals] ADD CONSTRAINT [FK_CreditAllocationReversals_Reversal]
        FOREIGN KEY ([ReversalOperationID]) REFERENCES dbo.[CreditRepaymentReversals] ([ReversalOperationID]);

IF OBJECT_ID(N'dbo.CreditAuthorizations', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CreditAuthorizations_Customers')
    ALTER TABLE dbo.[CreditAuthorizations] ADD CONSTRAINT [FK_CreditAuthorizations_Customers]
        FOREIGN KEY ([CustomerID]) REFERENCES dbo.[Customers] ([CustomerID]);

IF OBJECT_ID(N'dbo.CreditCollectionIntents', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CreditCollectionIntents_Customers')
    ALTER TABLE dbo.[CreditCollectionIntents] ADD CONSTRAINT [FK_CreditCollectionIntents_Customers]
        FOREIGN KEY ([CustomerID]) REFERENCES dbo.[Customers] ([CustomerID]);

IF OBJECT_ID(N'dbo.CreditCollectionReservations', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.CreditCollectionIntents', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CreditCollectionReservations_Intent')
    ALTER TABLE dbo.[CreditCollectionReservations] ADD CONSTRAINT [FK_CreditCollectionReservations_Intent]
        FOREIGN KEY ([CollectionOperationID]) REFERENCES dbo.[CreditCollectionIntents] ([CollectionOperationID]);

IF OBJECT_ID(N'dbo.CreditCollectionReservations', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.CustomerReceivables', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CreditCollectionReservations_Receivable')
    ALTER TABLE dbo.[CreditCollectionReservations] ADD CONSTRAINT [FK_CreditCollectionReservations_Receivable]
        FOREIGN KEY ([ReceivableID]) REFERENCES dbo.[CustomerReceivables] ([ReceivableID]);

IF OBJECT_ID(N'dbo.CreditRepaymentReversals', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.CreditCollectionIntents', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CreditRepaymentReversals_Collection')
    ALTER TABLE dbo.[CreditRepaymentReversals] ADD CONSTRAINT [FK_CreditRepaymentReversals_Collection]
        FOREIGN KEY ([CollectionOperationID]) REFERENCES dbo.[CreditCollectionIntents] ([CollectionOperationID]);

IF OBJECT_ID(N'dbo.CustomerMembershipIdentities', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CustomerMembershipIdentities_Customers')
    ALTER TABLE dbo.[CustomerMembershipIdentities] ADD CONSTRAINT [FK_CustomerMembershipIdentities_Customers]
        FOREIGN KEY ([CustomerID]) REFERENCES dbo.[Customers] ([CustomerID]);

IF OBJECT_ID(N'dbo.CustomerMembershipSessions', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.CustomerMembershipIdentities', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CustomerMembershipSessions_Identities')
    ALTER TABLE dbo.[CustomerMembershipSessions] ADD CONSTRAINT [FK_CustomerMembershipSessions_Identities]
        FOREIGN KEY ([IdentityID]) REFERENCES dbo.[CustomerMembershipIdentities] ([IdentityID]);

IF COL_LENGTH(N'dbo.ItemUnits', N'RowVersion') IS NULL
    ALTER TABLE dbo.ItemUnits ADD RowVersion rowversion NOT NULL;

IF OBJECT_ID(N'dbo.TR_CustomerCreditLedger_AppendOnly', N'TR') IS NULL
   AND OBJECT_ID(N'dbo.CustomerCreditLedger', N'U') IS NOT NULL
EXEC(N'
CREATE TRIGGER dbo.TR_CustomerCreditLedger_AppendOnly
ON dbo.CustomerCreditLedger
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51011, N''Posted credit ledger rows cannot be changed or deleted.'', 1;
END');

IF OBJECT_ID(N'dbo.TR_CreditAuditLog_AppendOnly', N'TR') IS NULL
   AND OBJECT_ID(N'dbo.CreditAuditLog', N'U') IS NOT NULL
EXEC(N'
CREATE TRIGGER dbo.TR_CreditAuditLog_AppendOnly
ON dbo.CreditAuditLog
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51012, N''Credit audit rows cannot be changed or deleted.'', 1;
END');

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Customers_PhoneVerificationStatus')
   AND COL_LENGTH(N'dbo.Customers', N'PhoneVerificationStatus') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM dbo.Customers
        WHERE PhoneVerificationStatus NOT IN (N'Unverified', N'Pending', N'Verified'))
    ALTER TABLE dbo.Customers ADD CONSTRAINT CK_Customers_PhoneVerificationStatus
        CHECK ([PhoneVerificationStatus]=N'Verified' OR [PhoneVerificationStatus]=N'Pending' OR [PhoneVerificationStatus]=N'Unverified');

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CustomerPhoneVerificationChallenges_Status')
   AND COL_LENGTH(N'dbo.CustomerPhoneVerificationChallenges', N'Status') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM dbo.CustomerPhoneVerificationChallenges
        WHERE Status NOT IN (N'Pending', N'Verified', N'Expired', N'Locked', N'Failed'))
    ALTER TABLE dbo.CustomerPhoneVerificationChallenges ADD CONSTRAINT CK_CustomerPhoneVerificationChallenges_Status
        CHECK ([Status]=N'Failed' OR [Status]=N'Locked' OR [Status]=N'Expired' OR [Status]=N'Verified' OR [Status]=N'Pending');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerPhoneVerificationChallenges_CustomerCreated' AND object_id = OBJECT_ID(N'dbo.CustomerPhoneVerificationChallenges'))
    CREATE INDEX IX_CustomerPhoneVerificationChallenges_CustomerCreated
        ON dbo.CustomerPhoneVerificationChallenges (CustomerID, CreatedAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Customers_Business_WhatsApp' AND object_id = OBJECT_ID(N'dbo.Customers'))
   AND NOT EXISTS (
        SELECT 1
        FROM dbo.Customers
        WHERE WhatsAppPhoneE164 IS NOT NULL
        GROUP BY BusinessID, WhatsAppPhoneE164
        HAVING COUNT(*) > 1)
    CREATE UNIQUE INDEX UX_Customers_Business_WhatsApp
        ON dbo.Customers (BusinessID, WhatsAppPhoneE164)
        WHERE WhatsAppPhoneE164 IS NOT NULL;

IF OBJECT_ID(N'dbo.CustomerItemSpecialPrices', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CustomerItemSpecialPrices_Customer')
   AND NOT EXISTS (
        SELECT 1 FROM dbo.CustomerItemSpecialPrices AS price
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Customers AS customer WHERE customer.CustomerID = price.CustomerID))
    ALTER TABLE dbo.CustomerItemSpecialPrices ADD CONSTRAINT FK_CustomerItemSpecialPrices_Customer
        FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID);

IF OBJECT_ID(N'dbo.CustomerItemSpecialPrices', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CustomerItemSpecialPrices_Item')
   AND NOT EXISTS (
        SELECT 1 FROM dbo.CustomerItemSpecialPrices AS price
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Items AS item WHERE item.ItemID = price.ItemID))
    ALTER TABLE dbo.CustomerItemSpecialPrices ADD CONSTRAINT FK_CustomerItemSpecialPrices_Item
        FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID);


