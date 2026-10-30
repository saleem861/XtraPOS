-- Allow Twilio Verify challenges without a locally generated OTP hash.
IF COL_LENGTH(N'dbo.CustomerPhoneVerificationChallenges', N'Provider') IS NULL
    ALTER TABLE dbo.CustomerPhoneVerificationChallenges ADD Provider nvarchar(40) NULL;
IF COL_LENGTH(N'dbo.CustomerPhoneVerificationChallenges', N'OtpHash') IS NOT NULL
    ALTER TABLE dbo.CustomerPhoneVerificationChallenges ALTER COLUMN OtpHash nvarchar(200) NULL;
