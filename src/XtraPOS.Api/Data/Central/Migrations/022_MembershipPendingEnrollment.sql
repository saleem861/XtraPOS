-- Short-lived proof that a provider login owns the phone before a customer row exists.
-- Does not create customers or membership identities.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.MembershipAuthAttempts', N'OwnershipVerifiedAt') IS NULL
    ALTER TABLE dbo.MembershipAuthAttempts ADD OwnershipVerifiedAt datetime2(0) NULL;
