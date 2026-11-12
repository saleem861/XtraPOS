-- Add check constraints that already exist on the other database type.
-- Names are created only when absent. No tables or rows are dropped.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CentralFinancialEvents', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CentralFinancialEvents_Amount')
    ALTER TABLE dbo.[CentralFinancialEvents] ADD CONSTRAINT [CK_CentralFinancialEvents_Amount] CHECK ([Amount]>(0));

IF OBJECT_ID(N'dbo.CentralFinancialEvents', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CentralFinancialEvents_Currency')
    ALTER TABLE dbo.[CentralFinancialEvents] ADD CONSTRAINT [CK_CentralFinancialEvents_Currency] CHECK ([CurrencyCode]=N'ILS');

IF OBJECT_ID(N'dbo.CentralFinancialEvents', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CentralFinancialEvents_Type')
    ALTER TABLE dbo.[CentralFinancialEvents] ADD CONSTRAINT [CK_CentralFinancialEvents_Type] CHECK ([EventType]=N'Adjustment' OR [EventType]=N'RepaymentReversal' OR [EventType]=N'DebtReversal' OR [EventType]=N'Repayment' OR [EventType]=N'CreditSale');

IF OBJECT_ID(N'dbo.CentralPosRegistry', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CentralPosRegistry_PosNumber')
    ALTER TABLE dbo.[CentralPosRegistry] ADD CONSTRAINT [CK_CentralPosRegistry_PosNumber] CHECK ([PosNumber]>(0));

IF OBJECT_ID(N'dbo.CentralPosRegistry', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CentralPosRegistry_Status')
    ALTER TABLE dbo.[CentralPosRegistry] ADD CONSTRAINT [CK_CentralPosRegistry_Status] CHECK ([Status]=N'Revoked' OR [Status]=N'Active');

IF OBJECT_ID(N'dbo.CreditAllocationReversals', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditAllocationReversals_Amount')
    ALTER TABLE dbo.[CreditAllocationReversals] ADD CONSTRAINT [CK_CreditAllocationReversals_Amount] CHECK ([Amount]>(0));

IF OBJECT_ID(N'dbo.CreditAuthorizations', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditAuthorizations_Amount')
    ALTER TABLE dbo.[CreditAuthorizations] ADD CONSTRAINT [CK_CreditAuthorizations_Amount] CHECK ([RequestedAmount]>(0));

IF OBJECT_ID(N'dbo.CreditAuthorizations', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditAuthorizations_Status')
    ALTER TABLE dbo.[CreditAuthorizations] ADD CONSTRAINT [CK_CreditAuthorizations_Status] CHECK ([Status]=N'Expired' OR [Status]=N'Reversed' OR [Status]=N'ReconciliationRequired' OR [Status]=N'Rejected' OR [Status]=N'Completed' OR [Status]=N'Posting' OR [Status]=N'Authorized' OR [Status]=N'Pending');

IF OBJECT_ID(N'dbo.CreditCollectionIntents', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditCollectionIntents_Amount')
    ALTER TABLE dbo.[CreditCollectionIntents] ADD CONSTRAINT [CK_CreditCollectionIntents_Amount] CHECK ([Amount]>(0));

IF OBJECT_ID(N'dbo.CreditCollectionIntents', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditCollectionIntents_Currency')
    ALTER TABLE dbo.[CreditCollectionIntents] ADD CONSTRAINT [CK_CreditCollectionIntents_Currency] CHECK ([CurrencyCode]=N'ILS');

IF OBJECT_ID(N'dbo.CreditCollectionIntents', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditCollectionIntents_Method')
    ALTER TABLE dbo.[CreditCollectionIntents] ADD CONSTRAINT [CK_CreditCollectionIntents_Method] CHECK ([PaymentMethodCode]=N'Cash');

IF OBJECT_ID(N'dbo.CreditCollectionIntents', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditCollectionIntents_Status')
    ALTER TABLE dbo.[CreditCollectionIntents] ADD CONSTRAINT [CK_CreditCollectionIntents_Status] CHECK ([Status]=N'ReconciliationRequired' OR [Status]=N'Released' OR [Status]=N'Completed' OR [Status]=N'Posting' OR [Status]=N'Authorized');

IF OBJECT_ID(N'dbo.CreditCollectionReservations', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditCollectionReservations_Amount')
    ALTER TABLE dbo.[CreditCollectionReservations] ADD CONSTRAINT [CK_CreditCollectionReservations_Amount] CHECK ([Amount]>(0));

IF OBJECT_ID(N'dbo.CreditRepaymentReversals', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditRepaymentReversals_Amount')
    ALTER TABLE dbo.[CreditRepaymentReversals] ADD CONSTRAINT [CK_CreditRepaymentReversals_Amount] CHECK ([Amount]>(0));

IF OBJECT_ID(N'dbo.CreditRepaymentReversals', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditRepaymentReversals_Reason')
    ALTER TABLE dbo.[CreditRepaymentReversals] ADD CONSTRAINT [CK_CreditRepaymentReversals_Reason] CHECK ([ReasonCode]=N'IncorrectRecording' OR [ReasonCode]=N'RefundToCustomer');

IF OBJECT_ID(N'dbo.CreditRepaymentReversals', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CreditRepaymentReversals_Status')
    ALTER TABLE dbo.[CreditRepaymentReversals] ADD CONSTRAINT [CK_CreditRepaymentReversals_Status] CHECK ([Status]=N'ReconciliationRequired' OR [Status]=N'Released' OR [Status]=N'Completed' OR [Status]=N'Posting' OR [Status]=N'Authorized');

IF OBJECT_ID(N'dbo.CustomerMembershipIdentities', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CustomerMembershipIdentities_Provider')
    ALTER TABLE dbo.[CustomerMembershipIdentities] ADD CONSTRAINT [CK_CustomerMembershipIdentities_Provider] CHECK ([Provider]=N'Apple' OR [Provider]=N'Google');

IF OBJECT_ID(N'dbo.CustomerMembershipIdentities', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_CustomerMembershipIdentities_Status')
    ALTER TABLE dbo.[CustomerMembershipIdentities] ADD CONSTRAINT [CK_CustomerMembershipIdentities_Status] CHECK ([Status]=N'Revoked' OR [Status]=N'Active');

IF OBJECT_ID(N'dbo.MembershipAuthAttempts', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_MembershipAuthAttempts_Status')
    ALTER TABLE dbo.[MembershipAuthAttempts] ADD CONSTRAINT [CK_MembershipAuthAttempts_Status] CHECK ([Status]=N'Consumed' OR [Status]=N'Authenticated' OR [Status]=N'Pending');

IF OBJECT_ID(N'dbo.MembershipCounterCodes', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_MembershipCounterCodes_Status')
    ALTER TABLE dbo.[MembershipCounterCodes] ADD CONSTRAINT [CK_MembershipCounterCodes_Status] CHECK ([Status]=N'Rejected' OR [Status]=N'Unlinked' OR [Status]=N'Verified' OR [Status]=N'Waiting');

IF OBJECT_ID(N'dbo.MembershipEnrollmentRequests', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_MembershipEnrollmentRequests_Status')
    ALTER TABLE dbo.[MembershipEnrollmentRequests] ADD CONSTRAINT [CK_MembershipEnrollmentRequests_Status] CHECK ([Status]=N'Expired' OR [Status]=N'Completed' OR [Status]=N'Pending');

IF OBJECT_ID(N'dbo.MembershipVerificationRequests', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_MembershipVerificationRequests_Status')
    ALTER TABLE dbo.[MembershipVerificationRequests] ADD CONSTRAINT [CK_MembershipVerificationRequests_Status] CHECK ([Status]=N'Cancelled' OR [Status]=N'Expired' OR [Status]=N'Rejected' OR [Status]=N'Verified' OR [Status]=N'Pending');


