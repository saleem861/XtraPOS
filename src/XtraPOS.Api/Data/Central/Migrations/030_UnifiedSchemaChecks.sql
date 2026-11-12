-- Add check constraints and the non-unique price index copied from the branch schema.
-- The unique ItemPrices indexes are intentionally not copied: central keeps one price per item and price type, while a branch may keep a default price and a branch price.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.BranchCollectionOutbox', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchCollectionOutbox_Attempts')
    ALTER TABLE dbo.[BranchCollectionOutbox] ADD CONSTRAINT [CK_BranchCollectionOutbox_Attempts] CHECK ([AttemptCount]>=(0));

IF OBJECT_ID(N'dbo.BranchCollectionOutbox', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchCollectionOutbox_Status')
    ALTER TABLE dbo.[BranchCollectionOutbox] ADD CONSTRAINT [CK_BranchCollectionOutbox_Status] CHECK ([Status]=N'ReconciliationRequired' OR [Status]=N'Completed' OR [Status]=N'Pending');

IF OBJECT_ID(N'dbo.BranchCollectionReversals', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchCollectionReversals_Amount')
    ALTER TABLE dbo.[BranchCollectionReversals] ADD CONSTRAINT [CK_BranchCollectionReversals_Amount] CHECK ([Amount]>(0));

IF OBJECT_ID(N'dbo.BranchCollectionReversals', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchCollectionReversals_Effect')
    ALTER TABLE dbo.[BranchCollectionReversals] ADD CONSTRAINT [CK_BranchCollectionReversals_Effect] CHECK ([CashEffect]=N'RecordingCorrection' OR [CashEffect]=N'RefundOutflow');

IF OBJECT_ID(N'dbo.BranchCollectionReversals', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchCollectionReversals_Reason')
    ALTER TABLE dbo.[BranchCollectionReversals] ADD CONSTRAINT [CK_BranchCollectionReversals_Reason] CHECK ([ReasonCode]=N'IncorrectRecording' OR [ReasonCode]=N'RefundToCustomer');

IF OBJECT_ID(N'dbo.BranchCollectionReversals', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchCollectionReversals_Status')
    ALTER TABLE dbo.[BranchCollectionReversals] ADD CONSTRAINT [CK_BranchCollectionReversals_Status] CHECK ([Status]=N'ReconciliationRequired' OR [Status]=N'Rejected' OR [Status]=N'Completed' OR [Status]=N'Pending');

IF OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchDebtCollections_Amount')
    ALTER TABLE dbo.[BranchDebtCollections] ADD CONSTRAINT [CK_BranchDebtCollections_Amount] CHECK ([Amount]>(0));

IF OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchDebtCollections_Currency')
    ALTER TABLE dbo.[BranchDebtCollections] ADD CONSTRAINT [CK_BranchDebtCollections_Currency] CHECK ([CurrencyCode]=N'ILS');

IF OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchDebtCollections_Method')
    ALTER TABLE dbo.[BranchDebtCollections] ADD CONSTRAINT [CK_BranchDebtCollections_Method] CHECK ([PaymentMethodCode]=N'Cash');

IF OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchDebtCollections_Status')
    ALTER TABLE dbo.[BranchDebtCollections] ADD CONSTRAINT [CK_BranchDebtCollections_Status] CHECK ([Status]=N'ReconciliationRequired' OR [Status]=N'Completed' OR [Status]=N'Pending');

IF OBJECT_ID(N'dbo.BranchFinancialInbox', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchFinancialInbox_Amount')
    ALTER TABLE dbo.[BranchFinancialInbox] ADD CONSTRAINT [CK_BranchFinancialInbox_Amount] CHECK ([Amount]>(0));

IF OBJECT_ID(N'dbo.BranchFinancialInbox', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchFinancialInbox_Type')
    ALTER TABLE dbo.[BranchFinancialInbox] ADD CONSTRAINT [CK_BranchFinancialInbox_Type] CHECK ([EventType]=N'Adjustment' OR [EventType]=N'RepaymentReversal' OR [EventType]=N'DebtReversal' OR [EventType]=N'Repayment' OR [EventType]=N'CreditSale');

IF OBJECT_ID(N'dbo.BranchFinancialOutbox', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchFinancialOutbox_Attempts')
    ALTER TABLE dbo.[BranchFinancialOutbox] ADD CONSTRAINT [CK_BranchFinancialOutbox_Attempts] CHECK ([AttemptCount]>=(0));

IF OBJECT_ID(N'dbo.BranchFinancialOutbox', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchFinancialOutbox_Status')
    ALTER TABLE dbo.[BranchFinancialOutbox] ADD CONSTRAINT [CK_BranchFinancialOutbox_Status] CHECK ([Status]=N'ReconciliationRequired' OR [Status]=N'Completed' OR [Status]=N'Pending');

IF OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchOperationLog_Attempts')
    ALTER TABLE dbo.[BranchOperationLog] ADD CONSTRAINT [CK_BranchOperationLog_Attempts] CHECK ([AttemptCount]>=(0));

IF OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchOperationLog_Destination')
    ALTER TABLE dbo.[BranchOperationLog] ADD CONSTRAINT [CK_BranchOperationLog_Destination] CHECK ([DestinationKind]=N'Branch' OR [DestinationKind]=N'Central');

IF OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchOperationLog_Status')
    ALTER TABLE dbo.[BranchOperationLog] ADD CONSTRAINT [CK_BranchOperationLog_Status] CHECK ([Status]=N'Cancelled' OR [Status]=N'Blocked' OR [Status]=N'Completed' OR [Status]=N'Executing' OR [Status]=N'Pending');

IF OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchOperationLog_Version')
    ALTER TABLE dbo.[BranchOperationLog] ADD CONSTRAINT [CK_BranchOperationLog_Version] CHECK ([PayloadVersion]>=(1));

IF OBJECT_ID(N'dbo.BranchReversalOutbox', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_BranchReversalOutbox_Status')
    ALTER TABLE dbo.[BranchReversalOutbox] ADD CONSTRAINT [CK_BranchReversalOutbox_Status] CHECK ([Status]=N'ReconciliationRequired' OR [Status]=N'Completed' OR [Status]=N'Pending');

IF OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Devices_DeviceType')
    ALTER TABLE dbo.[Devices] ADD CONSTRAINT [CK_Devices_DeviceType] CHECK ([DeviceType]=N'Warehouse' OR [DeviceType]=N'CustomerDisplay' OR [DeviceType]=N'Management' OR [DeviceType]=N'POS');

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_POSCashSessions_Counted')
    ALTER TABLE dbo.[POSCashSessions] ADD CONSTRAINT [CK_POSCashSessions_Counted] CHECK ([CountedCashAmount] IS NULL OR [CountedCashAmount]>=(0));

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_POSCashSessions_Opening')
    ALTER TABLE dbo.[POSCashSessions] ADD CONSTRAINT [CK_POSCashSessions_Opening] CHECK ([OpeningCashAmount]>=(0));

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_POSCashSessions_Status')
    ALTER TABLE dbo.[POSCashSessions] ADD CONSTRAINT [CK_POSCashSessions_Status] CHECK ([Status]=N'Closed' OR [Status]=N'Open');

IF OBJECT_ID(N'dbo.POSStations', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_POSStations_StationNumber')
    ALTER TABLE dbo.[POSStations] ADD CONSTRAINT [CK_POSStations_StationNumber] CHECK ([StationNumber]>(0));

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaleItems_LineNumber')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [CK_SaleItems_LineNumber] CHECK ([LineNumber]>(0));

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaleItems_LineTotal')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [CK_SaleItems_LineTotal] CHECK ([LineTotal]>=(0));

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaleItems_PriceOverrideMode')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [CK_SaleItems_PriceOverrideMode] CHECK ([PriceOverrideMode]=N'LineTotal' OR [PriceOverrideMode]=N'UnitPrice' OR [PriceOverrideMode]=N'None');

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaleItems_PricingMode')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [CK_SaleItems_PricingMode] CHECK ([PricingMode]=N'VariableValueByWeight' OR [PricingMode]=N'Fixed');

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaleItems_Quantity')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [CK_SaleItems_Quantity] CHECK ([Quantity]>(0));

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaleItems_ResolvedUnitPrice')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [CK_SaleItems_ResolvedUnitPrice] CHECK ([ResolvedUnitPrice]>=(0));

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaleItems_StockOwnerFactor')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [CK_SaleItems_StockOwnerFactor] CHECK ([StockOwnerConversionFactor]>(0));

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaleItems_StockOwnerQuantity')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [CK_SaleItems_StockOwnerQuantity] CHECK ([StockOwnerQuantity]>(0));

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaleItems_UnitPrice')
    ALTER TABLE dbo.[SaleItems] ADD CONSTRAINT [CK_SaleItems_UnitPrice] CHECK ([UnitPrice]>=(0));

IF OBJECT_ID(N'dbo.SalePayments', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SalePayments_Amount')
    ALTER TABLE dbo.[SalePayments] ADD CONSTRAINT [CK_SalePayments_Amount] CHECK ([Amount]>(0));

IF OBJECT_ID(N'dbo.SalePayments', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SalePayments_Change')
    ALTER TABLE dbo.[SalePayments] ADD CONSTRAINT [CK_SalePayments_Change] CHECK ([ChangeAmount]>=(0));

IF OBJECT_ID(N'dbo.SalePayments', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SalePayments_Method')
    ALTER TABLE dbo.[SalePayments] ADD CONSTRAINT [CK_SalePayments_Method] CHECK ([PaymentMethodCode]=N'Card' OR [PaymentMethodCode]=N'Cash');

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Sales_DiscountTotal')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [CK_Sales_DiscountTotal] CHECK ([DiscountTotal]>=(0));

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Sales_Status')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [CK_Sales_Status] CHECK ([Status]=N'CreditPending' OR [Status]=N'Cancelled' OR [Status]=N'Completed' OR [Status]=N'Draft');

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Sales_Subtotal')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [CK_Sales_Subtotal] CHECK ([Subtotal]>=(0));

IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Sales_Total')
    ALTER TABLE dbo.[Sales] ADD CONSTRAINT [CK_Sales_Total] CHECK ([Total]>=(0));

IF OBJECT_ID(N'dbo.StockMovements', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_StockMovements_PostingMode')
    ALTER TABLE dbo.[StockMovements] ADD CONSTRAINT [CK_StockMovements_PostingMode] CHECK ([PostingMode] IS NULL OR ([PostingMode]=N'Absolute' OR [PostingMode]=N'Delta'));

IF OBJECT_ID(N'dbo.StockMovements', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_StockMovements_SourceType')
    ALTER TABLE dbo.[StockMovements] ADD CONSTRAINT [CK_StockMovements_SourceType] CHECK ([SourceType] IS NULL OR ([SourceType]=N'CostAdjustment' OR [SourceType]=N'Damage' OR [SourceType]=N'InventoryReconciliation' OR [SourceType]=N'OpeningBalance' OR [SourceType]=N'InventoryCount' OR [SourceType]=N'TransferRejectReturn' OR [SourceType]=N'TransferReversal' OR [SourceType]=N'TransferIn' OR [SourceType]=N'TransferOut' OR [SourceType]=N'PurchaseReturn' OR [SourceType]=N'SaleReturn' OR [SourceType]=N'Purchase' OR [SourceType]=N'Sale'));

IF OBJECT_ID(N'dbo.SyncEndpoints', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SyncEndpoints_DatabaseRole')
    ALTER TABLE dbo.[SyncEndpoints] ADD CONSTRAINT [CK_SyncEndpoints_DatabaseRole] CHECK ([DatabaseRole]=N'BranchRemote' OR [DatabaseRole]=N'Central');

IF OBJECT_ID(N'dbo.SyncEndpoints', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SyncEndpoints_LocationKind')
    ALTER TABLE dbo.[SyncEndpoints] ADD CONSTRAINT [CK_SyncEndpoints_LocationKind] CHECK ([LocationKind]=N'Remote' OR [LocationKind]=N'Local');


IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemPrices_BranchID' AND object_id = OBJECT_ID(N'dbo.ItemPrices'))
    CREATE INDEX IX_ItemPrices_BranchID ON dbo.ItemPrices (BranchID);

IF OBJECT_ID(N'dbo.ItemUnits', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.ItemUnits', N'InternalUnitItemID') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ItemUnits_InternalUnitItem')
   AND NOT EXISTS (
        SELECT 1 FROM dbo.ItemUnits
        WHERE InternalUnitItemID IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM dbo.Items WHERE Items.ItemID = ItemUnits.InternalUnitItemID))
    ALTER TABLE dbo.ItemUnits ADD CONSTRAINT FK_ItemUnits_InternalUnitItem
        FOREIGN KEY (InternalUnitItemID) REFERENCES dbo.Items (ItemID);
