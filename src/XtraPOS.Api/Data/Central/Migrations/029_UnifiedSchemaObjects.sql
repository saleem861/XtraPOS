-- Additive unified-schema objects copied from the approved isolated schema.
-- Existing tables, rows, and financial semantics are left in place.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
IF OBJECT_ID(N'dbo.BranchCollectionOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[BranchCollectionOutbox]
    (
        [OutboxEventID] uniqueidentifier NOT NULL,
        [CreditOperationID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [Payload] nvarchar(2000) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [AttemptCount] int CONSTRAINT [DF_BranchCollectionOutbox_AttemptCount] DEFAULT ((0)) NOT NULL,
        [LastAttemptAt] datetime2(0) NULL,
        [NextRetryAt] datetime2(0) CONSTRAINT [DF_BranchCollectionOutbox_NextRetryAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_BranchCollectionOutbox_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [CompletedAt] datetime2(0) NULL,
        [LastError] nvarchar(400) NULL,
        [LeaseOwner] nvarchar(80) NULL,
        [LeaseUntil] datetime2(0) NULL,
        CONSTRAINT [PK_BranchCollectionOutbox] PRIMARY KEY CLUSTERED ([OutboxEventID])
    );
END;

IF OBJECT_ID(N'dbo.BranchCollectionReversals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[BranchCollectionReversals]
    (
        [ReversalOperationID] uniqueidentifier NOT NULL,
        [CollectionOperationID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [CashSessionID] uniqueidentifier NOT NULL,
        [DeviceID] uniqueidentifier NOT NULL,
        [ActorUserID] uniqueidentifier NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        [ReasonCode] nvarchar(40) NOT NULL,
        [CashEffect] nvarchar(30) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_BranchCollectionReversals_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_BranchCollectionReversals] PRIMARY KEY CLUSTERED ([ReversalOperationID])
    );
END;

IF OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[BranchDebtCollections]
    (
        [CollectionOperationID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [CashSessionID] uniqueidentifier NULL,
        [DeviceID] uniqueidentifier NOT NULL,
        [CashierUserID] uniqueidentifier NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        [CurrencyCode] nvarchar(3) CONSTRAINT [DF_BranchDebtCollections_Currency] DEFAULT (N'ILS') NOT NULL,
        [PaymentMethodCode] nvarchar(20) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_BranchDebtCollections_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_BranchDebtCollections] PRIMARY KEY CLUSTERED ([CollectionOperationID])
    );
END;

IF OBJECT_ID(N'dbo.BranchFinancialInbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[BranchFinancialInbox]
    (
        [OperationID] nvarchar(80) NOT NULL,
        [EventSequence] bigint NOT NULL,
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
        [CurrencyCode] nvarchar(3) CONSTRAINT [DF_BranchFinancialInbox_Currency] DEFAULT (N'ILS') NOT NULL,
        [Reason] nvarchar(400) NULL,
        [ReversedOperationID] nvarchar(80) NULL,
        [Payload] nvarchar(max) CONSTRAINT [DF_BranchFinancialInbox_Payload] DEFAULT (N'{}') NOT NULL,
        [ConfirmedAt] datetime2(0) NOT NULL,
        [AppliedAt] datetime2(0) CONSTRAINT [DF_BranchFinancialInbox_AppliedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_BranchFinancialInbox] PRIMARY KEY CLUSTERED ([OperationID])
    );
END;

IF OBJECT_ID(N'dbo.BranchFinancialOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[BranchFinancialOutbox]
    (
        [OutboxEventID] uniqueidentifier NOT NULL,
        [CreditOperationID] uniqueidentifier NOT NULL,
        [SourceSaleID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [AuthorizationID] uniqueidentifier NULL,
        [EventType] nvarchar(40) NOT NULL,
        [Payload] nvarchar(2000) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [AttemptCount] int CONSTRAINT [DF_BranchFinancialOutbox_AttemptCount] DEFAULT ((0)) NOT NULL,
        [LastAttemptAt] datetime2(0) NULL,
        [NextRetryAt] datetime2(0) CONSTRAINT [DF_BranchFinancialOutbox_NextRetryAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_BranchFinancialOutbox_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [CompletedAt] datetime2(0) NULL,
        [LastError] nvarchar(400) NULL,
        [LeaseOwner] nvarchar(80) NULL,
        [LeaseUntil] datetime2(0) NULL,
        CONSTRAINT [PK_BranchFinancialOutbox] PRIMARY KEY CLUSTERED ([OutboxEventID])
    );
END;

IF OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[BranchOperationLog]
    (
        [Serial] int IDENTITY(1,1) NOT NULL,
        [OperationID] uniqueidentifier NOT NULL,
        [ReferenceNumber] nvarchar(80) CONSTRAINT [DF_BranchOperationLog_Reference] DEFAULT (N'') NOT NULL,
        [OriginBranchCode] nvarchar(50) NOT NULL,
        [OriginBranchID] uniqueidentifier NOT NULL,
        [POSNumber] int NOT NULL,
        [StationID] uniqueidentifier NULL,
        [ActorUserID] uniqueidentifier NOT NULL,
        [OperationType] nvarchar(40) NOT NULL,
        [PayloadVersion] int NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [Description] nvarchar(400) NOT NULL,
        [SourceEntityType] nvarchar(64) NOT NULL,
        [SourceEntityID] uniqueidentifier NOT NULL,
        [DestinationKind] nvarchar(20) NOT NULL,
        [DestinationBranchCode] nvarchar(50) NULL,
        [DestinationBranchID] uniqueidentifier NULL,
        [DestinationName] nvarchar(100) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [ResultMessage] nvarchar(max) NULL,
        [AttemptCount] int CONSTRAINT [DF_BranchOperationLog_AttemptCount] DEFAULT ((0)) NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_BranchOperationLog_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [StartedAt] datetime2(0) NULL,
        [CompletedAt] datetime2(0) NULL,
        [LastAttemptAt] datetime2(0) NULL,
        [LeaseOwner] nvarchar(80) NULL,
        [LeaseUntil] datetime2(0) NULL,
        CONSTRAINT [PK_BranchOperationLog] PRIMARY KEY CLUSTERED ([Serial])
    );
END;

IF OBJECT_ID(N'dbo.BranchReversalOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[BranchReversalOutbox]
    (
        [OutboxEventID] uniqueidentifier NOT NULL,
        [ReversalOperationID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [Payload] nvarchar(2000) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [AttemptCount] int CONSTRAINT [DF_BranchReversalOutbox_AttemptCount] DEFAULT ((0)) NOT NULL,
        [NextRetryAt] datetime2(0) CONSTRAINT [DF_BranchReversalOutbox_NextRetryAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [LastError] nvarchar(400) NULL,
        [LeaseOwner] nvarchar(80) NULL,
        [LeaseUntil] datetime2(0) NULL,
        CONSTRAINT [PK_BranchReversalOutbox] PRIMARY KEY CLUSTERED ([OutboxEventID])
    );
END;

IF OBJECT_ID(N'dbo.BranchStockBalances', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[BranchStockBalances]
    (
        [BranchStockBalanceID] uniqueidentifier CONSTRAINT [DF_BranchStockBalances_BranchStockBalanceID] DEFAULT (newsequentialid()) NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [ItemID] uniqueidentifier NOT NULL,
        [OnHandQuantity] decimal(18,4) NULL,
        [IsQuantityInitialized] bit CONSTRAINT [DF_BranchStockBalances_IsQuantityInitialized] DEFAULT ((0)) NOT NULL,
        [UnitCost] decimal(19,4) NULL,
        [LastRestockAt] datetime2(0) NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_BranchStockBalances_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [UpdatedAt] datetime2(0) CONSTRAINT [DF_BranchStockBalances_UpdatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_BranchStockBalances] PRIMARY KEY CLUSTERED ([BranchStockBalanceID]),
        CONSTRAINT [CK_BranchStockBalances_Initialization] CHECK ([IsQuantityInitialized]=(0) AND [OnHandQuantity] IS NULL OR [IsQuantityInitialized]=(1) AND [OnHandQuantity] IS NOT NULL)
    );
END;

IF OBJECT_ID(N'dbo.CentralPosReplica', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CentralPosReplica]
    (
        [PosNumber] int NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [PosName] nvarchar(200) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [SyncedAtUtc] datetime2(0) CONSTRAINT [DF_CentralPosReplica_SyncedAtUtc] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_CentralPosReplica] PRIMARY KEY CLUSTERED ([PosNumber])
    );
END;

IF OBJECT_ID(N'dbo.CustomerPasskeyAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CustomerPasskeyAudit]
    (
        [AuditID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [ActorUserID] uniqueidentifier NULL,
        [Action] nvarchar(40) NOT NULL,
        [OccurredAt] datetime2(7) CONSTRAINT [DF_CustomerPasskeyAudit_OccurredAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_CustomerPasskeyAudit] PRIMARY KEY CLUSTERED ([AuditID])
    );
END;

IF OBJECT_ID(N'dbo.CustomerPasskeys', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CustomerPasskeys]
    (
        [CredentialId] varbinary(256) NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [PublicKeyX] varbinary(32) NOT NULL,
        [PublicKeyY] varbinary(32) NOT NULL,
        [SignCount] int CONSTRAINT [DF_CustomerPasskeys_SignCount] DEFAULT ((0)) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [CreatedAt] datetime2(7) CONSTRAINT [DF_CustomerPasskeys_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [RevokedAt] datetime2(7) NULL,
        CONSTRAINT [PK_CustomerPasskeys] PRIMARY KEY CLUSTERED ([CredentialId])
    );
END;

IF OBJECT_ID(N'dbo.CustomerPasskeySessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[CustomerPasskeySessions]
    (
        [SessionID] uniqueidentifier NOT NULL,
        [TokenHash] binary(32) NOT NULL,
        [CustomerID] uniqueidentifier NOT NULL,
        [Purpose] nvarchar(20) NOT NULL,
        [EmployeeUserID] uniqueidentifier NOT NULL,
        [ExpiresAt] datetime2(7) NOT NULL,
        [PhoneVerifiedAt] datetime2(7) NULL,
        [ConsumedAt] datetime2(7) NULL,
        [ChallengeHash] binary(32) NULL,
        [ChallengeExpiresAt] datetime2(7) NULL,
        [Result] nvarchar(20) CONSTRAINT [DF_CustomerPasskeySessions_Result] DEFAULT (N'Pending') NOT NULL,
        [VerifiedCredentialId] varbinary(256) NULL,
        [CreatedAt] datetime2(7) CONSTRAINT [DF_CustomerPasskeySessions_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [DraftName] nvarchar(300) NULL,
        [DraftPhone] nvarchar(50) NULL,
        CONSTRAINT [PK_CustomerPasskeySessions] PRIMARY KEY CLUSTERED ([SessionID])
    );
END;

IF OBJECT_ID(N'dbo.Devices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[Devices]
    (
        [DeviceID] uniqueidentifier CONSTRAINT [DF_Devices_DeviceID] DEFAULT (newsequentialid()) NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [StationID] uniqueidentifier NULL,
        [DeviceName] nvarchar(150) NOT NULL,
        [DeviceType] nvarchar(30) NOT NULL,
        [DeviceTokenHash] varbinary(64) NULL,
        [IsActive] bit CONSTRAINT [DF_Devices_IsActive] DEFAULT ((1)) NOT NULL,
        [RegisteredAt] datetime2(0) NOT NULL,
        [LastSeenAt] datetime2(0) NULL,
        [RevokedAt] datetime2(0) NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_Devices_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [UpdatedAt] datetime2(0) NULL,
        CONSTRAINT [PK_Devices] PRIMARY KEY CLUSTERED ([DeviceID])
    );
END;

IF OBJECT_ID(N'dbo.FinancialSyncCheckpoints', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[FinancialSyncCheckpoints]
    (
        [StreamName] nvarchar(40) NOT NULL,
        [LastEventSequence] bigint NOT NULL,
        [UpdatedAt] datetime2(0) CONSTRAINT [DF_FinancialSyncCheckpoints_UpdatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_FinancialSyncCheckpoints] PRIMARY KEY CLUSTERED ([StreamName])
    );
END;

IF OBJECT_ID(N'dbo.ItemBranchInventory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[ItemBranchInventory]
    (
        [ItemBranchInventoryID] uniqueidentifier CONSTRAINT [DF_ItemBranchInventory_ItemBranchInventoryID] DEFAULT (newsequentialid()) NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [ItemID] uniqueidentifier NOT NULL,
        [UnitCost] decimal(19,4) NULL,
        [DailySalesRate] decimal(18,6) NULL,
        [AvailabilityDays] decimal(18,4) NULL,
        [LastSaleAt] datetime2(0) NULL,
        [LastRestockAt] datetime2(0) NULL,
        [IsActive] bit CONSTRAINT [DF_ItemBranchInventory_IsActive] DEFAULT ((1)) NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_ItemBranchInventory_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [UpdatedAt] datetime2(0) CONSTRAINT [DF_ItemBranchInventory_UpdatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        [IsAvailable] bit CONSTRAINT [DF_ItemBranchInventory_IsAvailable] DEFAULT ((1)) NOT NULL,
        CONSTRAINT [PK_ItemBranchInventory] PRIMARY KEY CLUSTERED ([ItemBranchInventoryID])
    );
END;

IF OBJECT_ID(N'dbo.ItemPriceHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[ItemPriceHistory]
    (
        [ItemPriceHistoryID] uniqueidentifier CONSTRAINT [DF_ItemPriceHistory_ItemPriceHistoryID] DEFAULT (newsequentialid()) NOT NULL,
        [ItemID] uniqueidentifier NOT NULL,
        [PriceTypeID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NULL,
        [OldPrice] decimal(19,4) NULL,
        [NewPrice] decimal(19,4) NOT NULL,
        [ChangedAt] datetime2(0) CONSTRAINT [DF_ItemPriceHistory_ChangedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [ChangedByUserID] uniqueidentifier NULL,
        [ChangeSource] nvarchar(50) NULL,
        [Reason] nvarchar(500) NULL,
        CONSTRAINT [PK_ItemPriceHistory] PRIMARY KEY CLUSTERED ([ItemPriceHistoryID])
    );
END;

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[POSCashSessions]
    (
        [CashSessionID] uniqueidentifier NOT NULL,
        [BusinessID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [POSStationID] uniqueidentifier NOT NULL,
        [DeviceID] uniqueidentifier NOT NULL,
        [OpenedByUserID] uniqueidentifier NOT NULL,
        [OpenedAt] datetime2(0) NOT NULL,
        [OpeningCashAmount] decimal(19,4) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [ClosedByUserID] uniqueidentifier NULL,
        [ClosedAt] datetime2(0) NULL,
        [ExpectedCashAmount] decimal(19,4) NULL,
        [CountedCashAmount] decimal(19,4) NULL,
        [CashDifference] decimal(19,4) NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_POSCashSessions_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        [CashboxID] uniqueidentifier NULL,
        CONSTRAINT [PK_POSCashSessions] PRIMARY KEY CLUSTERED ([CashSessionID]),
        CONSTRAINT [CK_POSCashSessions_Lifecycle] CHECK ([Status]=N'Open' AND [ClosedByUserID] IS NULL AND [ClosedAt] IS NULL AND [ExpectedCashAmount] IS NULL AND [CountedCashAmount] IS NULL AND [CashDifference] IS NULL OR [Status]=N'Closed' AND [ClosedByUserID] IS NOT NULL AND [ClosedAt] IS NOT NULL AND [ExpectedCashAmount] IS NOT NULL AND [CountedCashAmount] IS NOT NULL AND [CashDifference]=([CountedCashAmount]-[ExpectedCashAmount]))
    );
END;

IF OBJECT_ID(N'dbo.POSDeviceConfigurations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[POSDeviceConfigurations]
    (
        [DeviceID] uniqueidentifier NOT NULL,
        [POSNumber] int NULL,
        [BranchID] uniqueidentifier NULL,
        [OrganizationID] uniqueidentifier NULL,
        [DeviceName] nvarchar(200) NULL,
        [ConfigurationJson] nvarchar(max) NOT NULL,
        [SchemaVersion] int CONSTRAINT [DF_POSDeviceConfigurations_SchemaVersion] DEFAULT ((1)) NOT NULL,
        [ConfigVersion] int CONSTRAINT [DF_POSDeviceConfigurations_ConfigVersion] DEFAULT ((1)) NOT NULL,
        [IsSetupCompleted] bit CONSTRAINT [DF_POSDeviceConfigurations_IsSetupCompleted] DEFAULT ((0)) NOT NULL,
        [UpdatedAtUtc] datetime2(0) CONSTRAINT [DF_POSDeviceConfigurations_UpdatedAtUtc] DEFAULT (sysutcdatetime()) NOT NULL,
        [UpdatedBy] nvarchar(100) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_POSDeviceConfigurations] PRIMARY KEY CLUSTERED ([DeviceID])
    );
END;

IF OBJECT_ID(N'dbo.POSStations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[POSStations]
    (
        [StationID] uniqueidentifier CONSTRAINT [DF_POSStations_StationID] DEFAULT (newsequentialid()) NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [StationNumber] int NOT NULL,
        [StationName] nvarchar(100) NOT NULL,
        [IsActive] bit CONSTRAINT [DF_POSStations_IsActive] DEFAULT ((1)) NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_POSStations_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [UpdatedAt] datetime2(0) NULL,
        CONSTRAINT [PK_POSStations] PRIMARY KEY CLUSTERED ([StationID])
    );
END;

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[SaleItems]
    (
        [SaleItemID] uniqueidentifier NOT NULL,
        [SaleID] uniqueidentifier NOT NULL,
        [LineNumber] int NOT NULL,
        [PricingMode] nvarchar(32) NOT NULL,
        [ItemID] uniqueidentifier NOT NULL,
        [ItemNumberSnapshot] nvarchar(100) NOT NULL,
        [ItemNameSnapshot] nvarchar(500) NOT NULL,
        [Quantity] decimal(18,4) NOT NULL,
        [UnitPrice] decimal(19,4) NOT NULL,
        [LineTotal] decimal(19,4) NOT NULL,
        [CustomerID] uniqueidentifier NULL,
        [PriceTypeID] uniqueidentifier NULL,
        [PriceTypeCodeSnapshot] nvarchar(50) NULL,
        [PriceSource] nvarchar(50) NOT NULL,
        [PricingResolvedAt] datetime2(0) NOT NULL,
        [StockOwnerItemID] uniqueidentifier NOT NULL,
        [StockOwnerItemNumberSnapshot] nvarchar(100) NOT NULL,
        [StockOwnerConversionFactor] decimal(38,28) NOT NULL,
        [StockOwnerQuantity] decimal(38,28) NOT NULL,
        [ReferenceItemID] uniqueidentifier NULL,
        [ReferenceItemNumberSnapshot] nvarchar(100) NULL,
        [ReferenceItemNameSnapshot] nvarchar(500) NULL,
        [EnteredScaleValue] decimal(19,4) NULL,
        [ReferenceBasePrice] decimal(19,4) NULL,
        [ReferencePriceSource] nvarchar(50) NULL,
        [WeightKg] decimal(38,28) NULL,
        [WeightGrams] decimal(38,28) NULL,
        [VariableValueDiscountPercent] decimal(7,4) NULL,
        [RawDiscountedValue] decimal(19,4) NULL,
        [FinalSellingPrice] decimal(19,4) NULL,
        [RoundingPolicy] nvarchar(50) NULL,
        [ProjectedCostAtPricing] decimal(19,4) NULL,
        [UnitCostSnapshot] decimal(19,4) NULL,
        [LineCostSnapshot] decimal(19,4) NULL,
        [CostSource] nvarchar(50) NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_SaleItems_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        [ResolvedUnitPrice] decimal(19,4) NOT NULL,
        [ResolvedPriceSource] nvarchar(50) NOT NULL,
        [PriceOverrideMode] nvarchar(20) CONSTRAINT [DF_SaleItems_PriceOverrideMode] DEFAULT (N'None') NOT NULL,
        [AppliedSpecialPriceID] uniqueidentifier NULL,
        [AppliedMinimumQuantity] decimal(18,4) NULL,
        [AppliedBundlePrice] decimal(19,4) NULL,
        [AppliedTargetUnitPrice] decimal(19,4) NULL,
        CONSTRAINT [PK_SaleItems] PRIMARY KEY CLUSTERED ([SaleItemID])
    );
END;

IF OBJECT_ID(N'dbo.SalePayments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[SalePayments]
    (
        [SalePaymentID] uniqueidentifier NOT NULL,
        [SaleID] uniqueidentifier NOT NULL,
        [CashSessionID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [ClientPaymentKey] uniqueidentifier NOT NULL,
        [PaymentMethodCode] nvarchar(20) NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        [TenderedAmount] decimal(19,4) NULL,
        [ChangeAmount] decimal(19,4) CONSTRAINT [DF_SalePayments_ChangeAmount] DEFAULT ((0)) NOT NULL,
        [ExternalReference] nvarchar(100) NULL,
        [CreatedByUserID] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_SalePayments_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_SalePayments] PRIMARY KEY CLUSTERED ([SalePaymentID]),
        CONSTRAINT [CK_SalePayments_Card] CHECK ([PaymentMethodCode]<>N'Card' OR [TenderedAmount] IS NULL AND [ChangeAmount]=(0)),
        CONSTRAINT [CK_SalePayments_Cash] CHECK ([PaymentMethodCode]<>N'Cash' OR [TenderedAmount] IS NOT NULL AND [TenderedAmount]>=[Amount] AND [ChangeAmount]=([TenderedAmount]-[Amount]))
    );
END;

IF OBJECT_ID(N'dbo.Sales', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[Sales]
    (
        [SaleID] uniqueidentifier NOT NULL,
        [BusinessID] uniqueidentifier NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [POSStationID] uniqueidentifier NOT NULL,
        [DeviceID] uniqueidentifier NOT NULL,
        [CartID] uniqueidentifier NOT NULL,
        [CustomerID] uniqueidentifier NULL,
        [CashierUserID] uniqueidentifier NOT NULL,
        [SaleNumber] bigint NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [Subtotal] decimal(19,4) NOT NULL,
        [DiscountTotal] decimal(19,4) CONSTRAINT [DF_Sales_DiscountTotal] DEFAULT ((0)) NOT NULL,
        [Total] decimal(19,4) NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_Sales_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [CompletedAt] datetime2(0) NULL,
        [LegacySource] nvarchar(64) NULL,
        [LegacySaleKey] nvarchar(64) NULL,
        [RowVersion] rowversion NOT NULL,
        [CashSessionID] uniqueidentifier NULL,
        CONSTRAINT [PK_Sales] PRIMARY KEY CLUSTERED ([SaleID]),
        CONSTRAINT [CK_Sales_LegacyPair] CHECK ([LegacySource] IS NULL AND [LegacySaleKey] IS NULL OR [LegacySource] IS NOT NULL AND [LegacySaleKey] IS NOT NULL)
    );
END;

IF OBJECT_ID(N'dbo.StockMovements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[StockMovements]
    (
        [StockMovementID] uniqueidentifier CONSTRAINT [DF_StockMovements_StockMovementID] DEFAULT (newsequentialid()) NOT NULL,
        [BranchID] uniqueidentifier NOT NULL,
        [ItemID] uniqueidentifier NOT NULL,
        [MovementTypeID] uniqueidentifier NOT NULL,
        [QuantityDelta] decimal(18,4) NOT NULL,
        [UnitCost] decimal(19,4) NULL,
        [OccurredAt] datetime2(0) NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_StockMovements_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [CreatedByUserID] uniqueidentifier NULL,
        [ReferenceType] nvarchar(50) NULL,
        [ReferenceID] uniqueidentifier NULL,
        [MovementGroupID] uniqueidentifier NULL,
        [Notes] nvarchar(1000) NULL,
        [SourceType] nvarchar(40) NULL,
        [SourceDocumentID] uniqueidentifier NULL,
        [SourceLineID] uniqueidentifier NULL,
        [PostingMode] nvarchar(20) NULL,
        [PreviousQuantity] decimal(18,4) NULL,
        [ObservedQuantity] decimal(18,4) NULL,
        [ResultingQuantity] decimal(18,4) NULL,
        [ResultingUnitCost] decimal(19,4) NULL,
        CONSTRAINT [PK_StockMovements] PRIMARY KEY CLUSTERED ([StockMovementID])
    );
END;

IF OBJECT_ID(N'dbo.StockMovementTypes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[StockMovementTypes]
    (
        [MovementTypeID] uniqueidentifier CONSTRAINT [DF_StockMovementTypes_MovementTypeID] DEFAULT (newsequentialid()) NOT NULL,
        [MovementCode] nvarchar(50) NOT NULL,
        [MovementName] nvarchar(100) NOT NULL,
        [SortOrder] int NOT NULL,
        [IsActive] bit CONSTRAINT [DF_StockMovementTypes_IsActive] DEFAULT ((1)) NOT NULL,
        [CreatedAt] datetime2(0) CONSTRAINT [DF_StockMovementTypes_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_StockMovementTypes] PRIMARY KEY CLUSTERED ([MovementTypeID])
    );
END;

IF OBJECT_ID(N'dbo.SyncCheckpoints', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[SyncCheckpoints]
    (
        [EndpointID] uniqueidentifier NOT NULL,
        [StreamName] nvarchar(64) NOT NULL,
        [LastAppliedVersion] bigint NOT NULL,
        CONSTRAINT [PK_SyncCheckpoints] PRIMARY KEY CLUSTERED ([EndpointID], [StreamName])
    );
END;

IF OBJECT_ID(N'dbo.SyncEndpoints', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[SyncEndpoints]
    (
        [EndpointID] uniqueidentifier NOT NULL,
        [DatabaseRole] nvarchar(20) NOT NULL,
        [LocationKind] nvarchar(20) NOT NULL,
        [ServerName] nvarchar(200) NOT NULL,
        [InstanceName] nvarchar(200) NULL,
        [Port] int NULL,
        [DatabaseName] nvarchar(128) NOT NULL,
        [AuthenticationType] nvarchar(20) NOT NULL,
        [Username] nvarchar(128) NULL,
        [ProtectedPassword] nvarchar(max) NULL,
        [EncryptConnection] bit NOT NULL,
        [TrustServerCertificate] bit NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [LastSuccessfulSyncAt] datetime2(3) NULL,
        [LastError] nvarchar(400) NULL,
        [CreatedAt] datetime2(3) CONSTRAINT [DF_SyncEndpoints_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_SyncEndpoints] PRIMARY KEY CLUSTERED ([EndpointID])
    );
END;

IF OBJECT_ID(N'dbo.SyncInbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[SyncInbox]
    (
        [EndpointID] uniqueidentifier NOT NULL,
        [ChangeSequence] bigint NOT NULL,
        [EntityType] nvarchar(64) NOT NULL,
        [EntityId] uniqueidentifier NOT NULL,
        [AppliedAt] datetime2(3) CONSTRAINT [DF_SyncInbox_AppliedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_SyncInbox] PRIMARY KEY CLUSTERED ([EndpointID], [ChangeSequence])
    );
END;

IF OBJECT_ID(N'dbo.SyncOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[SyncOutbox]
    (
        [OutboxID] uniqueidentifier NOT NULL,
        [EndpointID] uniqueidentifier NOT NULL,
        [EntityType] nvarchar(64) NOT NULL,
        [EntityId] uniqueidentifier NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [CreatedAt] datetime2(3) CONSTRAINT [DF_SyncOutbox_CreatedAt] DEFAULT (sysutcdatetime()) NOT NULL,
        CONSTRAINT [PK_SyncOutbox] PRIMARY KEY CLUSTERED ([OutboxID])
    );
END;

IF OBJECT_ID(N'dbo.UserLoginAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[UserLoginAudit]
    (
        [LoginAuditID] bigint IDENTITY(1,1) NOT NULL,
        [UserID] uniqueidentifier NULL,
        [DeviceID] uniqueidentifier NULL,
        [UsernameAttempted] nvarchar(100) NOT NULL,
        [Succeeded] bit NOT NULL,
        [FailureReason] nvarchar(100) NULL,
        [OccurredAt] datetime2(0) CONSTRAINT [DF_UserLoginAudit_OccurredAt] DEFAULT (sysutcdatetime()) NOT NULL,
        [ClientInfo] nvarchar(300) NULL,
        CONSTRAINT [PK_UserLoginAudit] PRIMARY KEY CLUSTERED ([LoginAuditID])
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_BranchCollectionOutbox_Operation' AND object_id = OBJECT_ID(N'dbo.BranchCollectionOutbox'))
    CREATE UNIQUE  INDEX [UQ_BranchCollectionOutbox_Operation] ON dbo.[BranchCollectionOutbox] ([CreditOperationID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_BranchCollectionReversals_Collection' AND object_id = OBJECT_ID(N'dbo.BranchCollectionReversals'))
    CREATE UNIQUE  INDEX [UQ_BranchCollectionReversals_Collection] ON dbo.[BranchCollectionReversals] ([CollectionOperationID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BranchFinancialInbox_Customer' AND object_id = OBJECT_ID(N'dbo.BranchFinancialInbox'))
    CREATE  INDEX [IX_BranchFinancialInbox_Customer] ON dbo.[BranchFinancialInbox] ([BusinessID], [CustomerID], [ConfirmedAt]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_BranchFinancialInbox_Sequence' AND object_id = OBJECT_ID(N'dbo.BranchFinancialInbox'))
    CREATE UNIQUE  INDEX [UQ_BranchFinancialInbox_Sequence] ON dbo.[BranchFinancialInbox] ([EventSequence]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_BranchFinancialOutbox_Operation' AND object_id = OBJECT_ID(N'dbo.BranchFinancialOutbox'))
    CREATE UNIQUE  INDEX [UQ_BranchFinancialOutbox_Operation] ON dbo.[BranchFinancialOutbox] ([CreditOperationID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_BranchFinancialOutbox_Sale' AND object_id = OBJECT_ID(N'dbo.BranchFinancialOutbox'))
    CREATE UNIQUE  INDEX [UQ_BranchFinancialOutbox_Sale] ON dbo.[BranchFinancialOutbox] ([SourceSaleID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BranchOperationLog_Open' AND object_id = OBJECT_ID(N'dbo.BranchOperationLog'))
    CREATE  INDEX [IX_BranchOperationLog_Open] ON dbo.[BranchOperationLog] ([Serial]) WHERE ([Status] IN (N'Pending', N'Executing', N'Blocked'));

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_BranchOperationLog_Operation' AND object_id = OBJECT_ID(N'dbo.BranchOperationLog'))
    CREATE UNIQUE  INDEX [UQ_BranchOperationLog_Operation] ON dbo.[BranchOperationLog] ([OperationID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_BranchReversalOutbox_Operation' AND object_id = OBJECT_ID(N'dbo.BranchReversalOutbox'))
    CREATE UNIQUE  INDEX [UQ_BranchReversalOutbox_Operation] ON dbo.[BranchReversalOutbox] ([ReversalOperationID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BranchStockBalances_ItemID' AND object_id = OBJECT_ID(N'dbo.BranchStockBalances'))
    CREATE  INDEX [IX_BranchStockBalances_ItemID] ON dbo.[BranchStockBalances] ([ItemID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_BranchStockBalances_BranchID_ItemID' AND object_id = OBJECT_ID(N'dbo.BranchStockBalances'))
    CREATE UNIQUE  INDEX [UQ_BranchStockBalances_BranchID_ItemID] ON dbo.[BranchStockBalances] ([BranchID], [ItemID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerPasskeys_Customer' AND object_id = OBJECT_ID(N'dbo.CustomerPasskeys'))
    CREATE  INDEX [IX_CustomerPasskeys_Customer] ON dbo.[CustomerPasskeys] ([CustomerID], [Status]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CustomerPasskeySessions_Token' AND object_id = OBJECT_ID(N'dbo.CustomerPasskeySessions'))
    CREATE UNIQUE  INDEX [UQ_CustomerPasskeySessions_Token] ON dbo.[CustomerPasskeySessions] ([TokenHash]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemBranchInventory_BranchID_DailySalesRate' AND object_id = OBJECT_ID(N'dbo.ItemBranchInventory'))
    CREATE  INDEX [IX_ItemBranchInventory_BranchID_DailySalesRate] ON dbo.[ItemBranchInventory] ([BranchID], [DailySalesRate]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemBranchInventory_BranchID_LastSaleAt' AND object_id = OBJECT_ID(N'dbo.ItemBranchInventory'))
    CREATE  INDEX [IX_ItemBranchInventory_BranchID_LastSaleAt] ON dbo.[ItemBranchInventory] ([BranchID], [LastSaleAt]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_ItemBranchInventory_BranchID_ItemID' AND object_id = OBJECT_ID(N'dbo.ItemBranchInventory'))
    CREATE UNIQUE  INDEX [UQ_ItemBranchInventory_BranchID_ItemID] ON dbo.[ItemBranchInventory] ([BranchID], [ItemID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemPriceHistory_ItemID_ChangedAt' AND object_id = OBJECT_ID(N'dbo.ItemPriceHistory'))
    CREATE  INDEX [IX_ItemPriceHistory_ItemID_ChangedAt] ON dbo.[ItemPriceHistory] ([ItemID], [ChangedAt]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_POSCashSessions_BranchDevice_Open' AND object_id = OBJECT_ID(N'dbo.POSCashSessions'))
    CREATE UNIQUE  INDEX [UX_POSCashSessions_BranchDevice_Open] ON dbo.[POSCashSessions] ([BranchID], [DeviceID]) WHERE ([Status]=N'Open');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_POSCashSessions_CashSessionID_BranchID' AND object_id = OBJECT_ID(N'dbo.POSCashSessions'))
    CREATE UNIQUE  INDEX [UX_POSCashSessions_CashSessionID_BranchID] ON dbo.[POSCashSessions] ([CashSessionID], [BranchID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_POSStations_BranchID_StationNumber' AND object_id = OBJECT_ID(N'dbo.POSStations'))
    CREATE UNIQUE  INDEX [UQ_POSStations_BranchID_StationNumber] ON dbo.[POSStations] ([BranchID], [StationNumber]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SaleItems_SaleID_LineNumber' AND object_id = OBJECT_ID(N'dbo.SaleItems'))
    CREATE UNIQUE  INDEX [UQ_SaleItems_SaleID_LineNumber] ON dbo.[SaleItems] ([SaleID], [LineNumber]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalePayments_CashSessionID' AND object_id = OBJECT_ID(N'dbo.SalePayments'))
    CREATE  INDEX [IX_SalePayments_CashSessionID] ON dbo.[SalePayments] ([CashSessionID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SalePayments_Sale_ClientKey' AND object_id = OBJECT_ID(N'dbo.SalePayments'))
    CREATE UNIQUE  INDEX [UX_SalePayments_Sale_ClientKey] ON dbo.[SalePayments] ([SaleID], [ClientPaymentKey]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sales_BranchID_CreatedAt' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE  INDEX [IX_Sales_BranchID_CreatedAt] ON dbo.[Sales] ([BranchID], [CreatedAt]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sales_CashSessionID' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE  INDEX [IX_Sales_CashSessionID] ON dbo.[Sales] ([CashSessionID]) WHERE ([CashSessionID] IS NOT NULL);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sales_CustomerID_CreatedAt' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE  INDEX [IX_Sales_CustomerID_CreatedAt] ON dbo.[Sales] ([CustomerID], [CreatedAt]) WHERE ([CustomerID] IS NOT NULL);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Sales_BranchID_CartID' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE UNIQUE  INDEX [UX_Sales_BranchID_CartID] ON dbo.[Sales] ([BranchID], [CartID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Sales_BranchID_SaleNumber' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE UNIQUE  INDEX [UX_Sales_BranchID_SaleNumber] ON dbo.[Sales] ([BranchID], [SaleNumber]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Sales_LegacyIdentity' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE UNIQUE  INDEX [UX_Sales_LegacyIdentity] ON dbo.[Sales] ([BusinessID], [LegacySource], [LegacySaleKey]) WHERE ([LegacySaleKey] IS NOT NULL);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Sales_SaleID_BranchID' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE UNIQUE  INDEX [UX_Sales_SaleID_BranchID] ON dbo.[Sales] ([SaleID], [BranchID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockMovements_BranchID_ItemID_OccurredAt' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
    CREATE  INDEX [IX_StockMovements_BranchID_ItemID_OccurredAt] ON dbo.[StockMovements] ([BranchID], [ItemID], [OccurredAt]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockMovements_ItemID_OccurredAt' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
    CREATE  INDEX [IX_StockMovements_ItemID_OccurredAt] ON dbo.[StockMovements] ([ItemID], [OccurredAt]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockMovements_MovementGroupID' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
    CREATE  INDEX [IX_StockMovements_MovementGroupID] ON dbo.[StockMovements] ([MovementGroupID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockMovements_ReferenceType_ReferenceID' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
    CREATE  INDEX [IX_StockMovements_ReferenceType_ReferenceID] ON dbo.[StockMovements] ([ReferenceType], [ReferenceID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_StockMovements_Branch_SourceLine' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
    CREATE UNIQUE  INDEX [UX_StockMovements_Branch_SourceLine] ON dbo.[StockMovements] ([BranchID], [SourceType], [SourceLineID]) WHERE ([SourceLineID] IS NOT NULL);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_StockMovementTypes_MovementCode' AND object_id = OBJECT_ID(N'dbo.StockMovementTypes'))
    CREATE UNIQUE  INDEX [UQ_StockMovementTypes_MovementCode] ON dbo.[StockMovementTypes] ([MovementCode]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UserLoginAudit_DeviceID' AND object_id = OBJECT_ID(N'dbo.UserLoginAudit'))
    CREATE  INDEX [IX_UserLoginAudit_DeviceID] ON dbo.[UserLoginAudit] ([DeviceID]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UserLoginAudit_OccurredAt' AND object_id = OBJECT_ID(N'dbo.UserLoginAudit'))
    CREATE  INDEX [IX_UserLoginAudit_OccurredAt] ON dbo.[UserLoginAudit] ([OccurredAt]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UserLoginAudit_UserID' AND object_id = OBJECT_ID(N'dbo.UserLoginAudit'))
    CREATE  INDEX [IX_UserLoginAudit_UserID] ON dbo.[UserLoginAudit] ([UserID]);

IF OBJECT_ID(N'dbo.BranchCollectionOutbox', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BranchCollectionOutbox_Collection')
    ALTER TABLE dbo.[BranchCollectionOutbox] ADD CONSTRAINT [FK_BranchCollectionOutbox_Collection]
        FOREIGN KEY ([CreditOperationID]) REFERENCES dbo.[BranchDebtCollections] ([CollectionOperationID]);

IF OBJECT_ID(N'dbo.BranchCollectionReversals', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BranchCollectionReversals_Collection')
    ALTER TABLE dbo.[BranchCollectionReversals] ADD CONSTRAINT [FK_BranchCollectionReversals_Collection]
        FOREIGN KEY ([CollectionOperationID]) REFERENCES dbo.[BranchDebtCollections] ([CollectionOperationID]);

IF OBJECT_ID(N'dbo.BranchCollectionReversals', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BranchCollectionReversals_Session')
    ALTER TABLE dbo.[BranchCollectionReversals] ADD CONSTRAINT [FK_BranchCollectionReversals_Session]
        FOREIGN KEY ([CashSessionID], [BranchID]) REFERENCES dbo.[POSCashSessions] ([CashSessionID], [BranchID]);

IF OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BranchDebtCollections_Session')
    ALTER TABLE dbo.[BranchDebtCollections] ADD CONSTRAINT [FK_BranchDebtCollections_Session]
        FOREIGN KEY ([CashSessionID], [BranchID]) REFERENCES dbo.[POSCashSessions] ([CashSessionID], [BranchID]);

IF OBJECT_ID(N'dbo.BranchFinancialOutbox', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BranchFinancialOutbox_Sales')
    ALTER TABLE dbo.[BranchFinancialOutbox] ADD CONSTRAINT [FK_BranchFinancialOutbox_Sales]
        FOREIGN KEY ([SourceSaleID], [BranchID]) REFERENCES dbo.[Sales] ([SaleID], [BranchID]);

IF OBJECT_ID(N'dbo.BranchReversalOutbox', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.BranchCollectionReversals', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BranchReversalOutbox_Reversal')
    ALTER TABLE dbo.[BranchReversalOutbox] ADD CONSTRAINT [FK_BranchReversalOutbox_Reversal]
        FOREIGN KEY ([ReversalOperationID]) REFERENCES dbo.[BranchCollectionReversals] ([ReversalOperationID]);

IF OBJECT_ID(N'dbo.BranchStockBalances', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BranchStockBalances_Branch')
    ALTER TABLE dbo.[BranchStockBalances] ADD CONSTRAINT [FK_BranchStockBalances_Branch]
        FOREIGN KEY ([BranchID]) REFERENCES dbo.[Branches] ([BranchID]);

IF OBJECT_ID(N'dbo.BranchStockBalances', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Items', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BranchStockBalances_Item')
    ALTER TABLE dbo.[BranchStockBalances] ADD CONSTRAINT [FK_BranchStockBalances_Item]
        FOREIGN KEY ([ItemID]) REFERENCES dbo.[Items] ([ItemID]);

IF OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Devices_Branches')
    ALTER TABLE dbo.[Devices] ADD CONSTRAINT [FK_Devices_Branches]
        FOREIGN KEY ([BranchID]) REFERENCES dbo.[Branches] ([BranchID]);

IF OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.POSStations', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Devices_POSStations')
    ALTER TABLE dbo.[Devices] ADD CONSTRAINT [FK_Devices_POSStations]
        FOREIGN KEY ([StationID]) REFERENCES dbo.[POSStations] ([StationID]);

IF OBJECT_ID(N'dbo.ItemBranchInventory', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ItemBranchInventory_Branch')
    ALTER TABLE dbo.[ItemBranchInventory] ADD CONSTRAINT [FK_ItemBranchInventory_Branch]
        FOREIGN KEY ([BranchID]) REFERENCES dbo.[Branches] ([BranchID]);

IF OBJECT_ID(N'dbo.ItemBranchInventory', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Items', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ItemBranchInventory_Item')
    ALTER TABLE dbo.[ItemBranchInventory] ADD CONSTRAINT [FK_ItemBranchInventory_Item]
        FOREIGN KEY ([ItemID]) REFERENCES dbo.[Items] ([ItemID]);

IF OBJECT_ID(N'dbo.ItemPriceHistory', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ItemPriceHistory_Branch')
    ALTER TABLE dbo.[ItemPriceHistory] ADD CONSTRAINT [FK_ItemPriceHistory_Branch]
        FOREIGN KEY ([BranchID]) REFERENCES dbo.[Branches] ([BranchID]);

IF OBJECT_ID(N'dbo.ItemPriceHistory', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ItemPriceHistory_ChangedByUser')
    ALTER TABLE dbo.[ItemPriceHistory] ADD CONSTRAINT [FK_ItemPriceHistory_ChangedByUser]
        FOREIGN KEY ([ChangedByUserID]) REFERENCES dbo.[Users] ([UserID]);

IF OBJECT_ID(N'dbo.ItemPriceHistory', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Items', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ItemPriceHistory_Item')
    ALTER TABLE dbo.[ItemPriceHistory] ADD CONSTRAINT [FK_ItemPriceHistory_Item]
        FOREIGN KEY ([ItemID]) REFERENCES dbo.[Items] ([ItemID]);

IF OBJECT_ID(N'dbo.ItemPriceHistory', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.PriceTypes', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ItemPriceHistory_PriceType')
    ALTER TABLE dbo.[ItemPriceHistory] ADD CONSTRAINT [FK_ItemPriceHistory_PriceType]
        FOREIGN KEY ([PriceTypeID]) REFERENCES dbo.[PriceTypes] ([PriceTypeID]);

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_POSCashSessions_Branches')
    ALTER TABLE dbo.[POSCashSessions] ADD CONSTRAINT [FK_POSCashSessions_Branches]
        FOREIGN KEY ([BranchID]) REFERENCES dbo.[Branches] ([BranchID]);

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Businesses', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_POSCashSessions_Businesses')
    ALTER TABLE dbo.[POSCashSessions] ADD CONSTRAINT [FK_POSCashSessions_Businesses]
        FOREIGN KEY ([BusinessID]) REFERENCES dbo.[Businesses] ([BusinessID]);

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Cashboxes', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_POSCashSessions_Cashboxes')
    ALTER TABLE dbo.[POSCashSessions] ADD CONSTRAINT [FK_POSCashSessions_Cashboxes]
        FOREIGN KEY ([CashboxID]) REFERENCES dbo.[Cashboxes] ([CashboxID]);

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_POSCashSessions_ClosedBy')
    ALTER TABLE dbo.[POSCashSessions] ADD CONSTRAINT [FK_POSCashSessions_ClosedBy]
        FOREIGN KEY ([ClosedByUserID]) REFERENCES dbo.[Users] ([UserID]);

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_POSCashSessions_Devices')
    ALTER TABLE dbo.[POSCashSessions] ADD CONSTRAINT [FK_POSCashSessions_Devices]
        FOREIGN KEY ([DeviceID]) REFERENCES dbo.[Devices] ([DeviceID]);

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_POSCashSessions_OpenedBy')
    ALTER TABLE dbo.[POSCashSessions] ADD CONSTRAINT [FK_POSCashSessions_OpenedBy]
        FOREIGN KEY ([OpenedByUserID]) REFERENCES dbo.[Users] ([UserID]);

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.POSStations', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_POSCashSessions_POSStations')
    ALTER TABLE dbo.[POSCashSessions] ADD CONSTRAINT [FK_POSCashSessions_POSStations]
        FOREIGN KEY ([POSStationID]) REFERENCES dbo.[POSStations] ([StationID]);

IF OBJECT_ID(N'dbo.POSStations', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_POSStations_Branches')
    ALTER TABLE dbo.[POSStations] ADD CONSTRAINT [FK_POSStations_Branches]
        FOREIGN KEY ([BranchID]) REFERENCES dbo.[Branches] ([BranchID]);

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SaleItems_Customers')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [FK_SaleItems_Customers]
        FOREIGN KEY ([CustomerID]) REFERENCES dbo.[Customers] ([CustomerID]);

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Items', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SaleItems_Items')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [FK_SaleItems_Items]
        FOREIGN KEY ([ItemID]) REFERENCES dbo.[Items] ([ItemID]);

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.PriceTypes', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SaleItems_PriceTypes')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [FK_SaleItems_PriceTypes]
        FOREIGN KEY ([PriceTypeID]) REFERENCES dbo.[PriceTypes] ([PriceTypeID]);

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Items', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SaleItems_ReferenceItem')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [FK_SaleItems_ReferenceItem]
        FOREIGN KEY ([ReferenceItemID]) REFERENCES dbo.[Items] ([ItemID]);

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SaleItems_Sales')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [FK_SaleItems_Sales]
        FOREIGN KEY ([SaleID]) REFERENCES dbo.[Sales] ([SaleID]);

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Items', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SaleItems_StockOwner')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [FK_SaleItems_StockOwner]
        FOREIGN KEY ([StockOwnerItemID]) REFERENCES dbo.[Items] ([ItemID]);

IF OBJECT_ID(N'dbo.SalePayments', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SalePayments_SaleBranch')
    ALTER TABLE dbo.[SalePayments] ADD CONSTRAINT [FK_SalePayments_SaleBranch]
        FOREIGN KEY ([SaleID], [BranchID]) REFERENCES dbo.[Sales] ([SaleID], [BranchID]);

IF OBJECT_ID(N'dbo.SalePayments', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SalePayments_SessionBranch')
    ALTER TABLE dbo.[SalePayments] ADD CONSTRAINT [FK_SalePayments_SessionBranch]
        FOREIGN KEY ([CashSessionID], [BranchID]) REFERENCES dbo.[POSCashSessions] ([CashSessionID], [BranchID]);

IF OBJECT_ID(N'dbo.SalePayments', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SalePayments_Users')
    ALTER TABLE dbo.[SalePayments] ADD CONSTRAINT [FK_SalePayments_Users]
        FOREIGN KEY ([CreatedByUserID]) REFERENCES dbo.[Users] ([UserID]);

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Sales_Branches')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [FK_Sales_Branches]
        FOREIGN KEY ([BranchID]) REFERENCES dbo.[Branches] ([BranchID]);

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Businesses', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Sales_Businesses')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [FK_Sales_Businesses]
        FOREIGN KEY ([BusinessID]) REFERENCES dbo.[Businesses] ([BusinessID]);

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Sales_CashSessionBranch')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [FK_Sales_CashSessionBranch]
        FOREIGN KEY ([CashSessionID], [BranchID]) REFERENCES dbo.[POSCashSessions] ([CashSessionID], [BranchID]);

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Sales_Customers')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [FK_Sales_Customers]
        FOREIGN KEY ([CustomerID]) REFERENCES dbo.[Customers] ([CustomerID]);

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Sales_Devices')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [FK_Sales_Devices]
        FOREIGN KEY ([DeviceID]) REFERENCES dbo.[Devices] ([DeviceID]);

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.POSStations', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Sales_POSStations')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [FK_Sales_POSStations]
        FOREIGN KEY ([POSStationID]) REFERENCES dbo.[POSStations] ([StationID]);

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Sales_Users')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [FK_Sales_Users]
        FOREIGN KEY ([CashierUserID]) REFERENCES dbo.[Users] ([UserID]);

IF OBJECT_ID(N'dbo.StockMovements', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_StockMovements_Branch')
    ALTER TABLE dbo.[StockMovements] ADD CONSTRAINT [FK_StockMovements_Branch]
        FOREIGN KEY ([BranchID]) REFERENCES dbo.[Branches] ([BranchID]);

IF OBJECT_ID(N'dbo.StockMovements', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_StockMovements_CreatedByUser')
    ALTER TABLE dbo.[StockMovements] ADD CONSTRAINT [FK_StockMovements_CreatedByUser]
        FOREIGN KEY ([CreatedByUserID]) REFERENCES dbo.[Users] ([UserID]);

IF OBJECT_ID(N'dbo.StockMovements', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Items', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_StockMovements_Item')
    ALTER TABLE dbo.[StockMovements] ADD CONSTRAINT [FK_StockMovements_Item]
        FOREIGN KEY ([ItemID]) REFERENCES dbo.[Items] ([ItemID]);

IF OBJECT_ID(N'dbo.StockMovements', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.StockMovementTypes', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_StockMovements_MovementType')
    ALTER TABLE dbo.[StockMovements] ADD CONSTRAINT [FK_StockMovements_MovementType]
        FOREIGN KEY ([MovementTypeID]) REFERENCES dbo.[StockMovementTypes] ([MovementTypeID]);

IF OBJECT_ID(N'dbo.SyncCheckpoints', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.SyncEndpoints', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SyncCheckpoints_Endpoint')
    ALTER TABLE dbo.[SyncCheckpoints] ADD CONSTRAINT [FK_SyncCheckpoints_Endpoint]
        FOREIGN KEY ([EndpointID]) REFERENCES dbo.[SyncEndpoints] ([EndpointID]);

IF OBJECT_ID(N'dbo.SyncInbox', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.SyncEndpoints', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SyncInbox_Endpoint')
    ALTER TABLE dbo.[SyncInbox] ADD CONSTRAINT [FK_SyncInbox_Endpoint]
        FOREIGN KEY ([EndpointID]) REFERENCES dbo.[SyncEndpoints] ([EndpointID]);

IF OBJECT_ID(N'dbo.SyncOutbox', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.SyncEndpoints', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SyncOutbox_Endpoint')
    ALTER TABLE dbo.[SyncOutbox] ADD CONSTRAINT [FK_SyncOutbox_Endpoint]
        FOREIGN KEY ([EndpointID]) REFERENCES dbo.[SyncEndpoints] ([EndpointID]);

IF OBJECT_ID(N'dbo.UserLoginAudit', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_UserLoginAudit_Devices')
    ALTER TABLE dbo.[UserLoginAudit] ADD CONSTRAINT [FK_UserLoginAudit_Devices]
        FOREIGN KEY ([DeviceID]) REFERENCES dbo.[Devices] ([DeviceID]);

IF OBJECT_ID(N'dbo.UserLoginAudit', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_UserLoginAudit_Users')
    ALTER TABLE dbo.[UserLoginAudit] ADD CONSTRAINT [FK_UserLoginAudit_Users]
        FOREIGN KEY ([UserID]) REFERENCES dbo.[Users] ([UserID]);

IF COL_LENGTH(N'dbo.ItemPrices', N'BranchID') IS NULL
    ALTER TABLE dbo.ItemPrices ADD BranchID uniqueidentifier NULL;

IF COL_LENGTH(N'dbo.Users', N'FailedLoginCount') IS NULL
    ALTER TABLE dbo.Users ADD FailedLoginCount int NOT NULL
        CONSTRAINT DF_Users_FailedLoginCount DEFAULT (0);

IF COL_LENGTH(N'dbo.Users', N'LockoutEndAt') IS NULL
    ALTER TABLE dbo.Users ADD LockoutEndAt datetime2(0) NULL;

IF COL_LENGTH(N'dbo.Users', N'LastLoginAt') IS NULL
    ALTER TABLE dbo.Users ADD LastLoginAt datetime2(0) NULL;

EXEC(N'
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N''CK_Users_FailedLoginCount'')
   AND COL_LENGTH(N''dbo.Users'', N''FailedLoginCount'') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.Users WHERE FailedLoginCount < 0)
    ALTER TABLE dbo.Users ADD CONSTRAINT CK_Users_FailedLoginCount CHECK ([FailedLoginCount]>=(0));
');

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Branches_BranchType')
   AND NOT EXISTS (
        SELECT 1 FROM dbo.Branches
        WHERE BranchType NOT IN (N'Retail', N'Warehouse', N'RetailAndWarehouse'))
    ALTER TABLE dbo.Branches ADD CONSTRAINT CK_Branches_BranchType
        CHECK ([BranchType]=N'RetailAndWarehouse' OR [BranchType]=N'Warehouse' OR [BranchType]=N'Retail');

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_ItemUnits_UnitsPerPackage')
   AND NOT EXISTS (SELECT 1 FROM dbo.ItemUnits WHERE UnitsPerPackage <= 0)
    ALTER TABLE dbo.ItemUnits ADD CONSTRAINT CK_ItemUnits_UnitsPerPackage CHECK ([UnitsPerPackage]>(0));

EXEC(N'
IF OBJECT_ID(N''dbo.ItemPrices'', N''U'') IS NOT NULL
   AND COL_LENGTH(N''dbo.ItemPrices'', N''BranchID'') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N''FK_ItemPrices_Branch'')
   AND NOT EXISTS (
        SELECT 1 FROM dbo.ItemPrices
        WHERE BranchID IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM dbo.Branches WHERE Branches.BranchID = ItemPrices.BranchID))
    ALTER TABLE dbo.ItemPrices ADD CONSTRAINT FK_ItemPrices_Branch
        FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID);
');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Items_Classification' AND object_id = OBJECT_ID(N'dbo.Items'))
    CREATE INDEX IX_Items_Classification ON dbo.Items (Classification);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Items_ItemName' AND object_id = OBJECT_ID(N'dbo.Items'))
    CREATE INDEX IX_Items_ItemName ON dbo.Items (ItemName);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Items_ItemType' AND object_id = OBJECT_ID(N'dbo.Items'))
    CREATE INDEX IX_Items_ItemType ON dbo.Items (ItemType);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemUnits_ItemID' AND object_id = OBJECT_ID(N'dbo.ItemUnits'))
    CREATE INDEX IX_ItemUnits_ItemID ON dbo.ItemUnits (ItemID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemUnits_InternalUnitItemID' AND object_id = OBJECT_ID(N'dbo.ItemUnits'))
    CREATE INDEX IX_ItemUnits_InternalUnitItemID ON dbo.ItemUnits (InternalUnitItemID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemUnits_InternalUnitItemNumber' AND object_id = OBJECT_ID(N'dbo.ItemUnits'))
    CREATE INDEX IX_ItemUnits_InternalUnitItemNumber ON dbo.ItemUnits (InternalUnitItemNumber);


