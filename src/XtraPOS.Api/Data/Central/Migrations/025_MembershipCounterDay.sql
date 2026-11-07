-- The counter QR stays the same until the next Asia/Hebron day.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.MembershipCounterCodes', N'TokenDay') IS NULL
BEGIN
    ALTER TABLE dbo.MembershipCounterCodes ADD TokenDay date NULL;
END;
