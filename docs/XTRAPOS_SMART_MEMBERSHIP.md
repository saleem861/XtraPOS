# XtraPOS Smart Membership

Membership recognition is separate from cashier login, Passkey diagnostics, credit authorization, and stock.

## Architecture

Central stores provider identities, hashed membership sessions, one-use verification requests, and audit events. The branch API reaches those tables only through the linked central connection. The POS creates a request for the signed-in counter. The phone page at `/membership/verify/{token}` loads status with GET, then POSTs only when a membership cookie and a matching CSRF cookie exist. GET never verifies.

## Enrollment and providers

Google and Apple start routes stay disabled until `Membership:GoogleLoginEnabled` or `Membership:AppleLoginEnabled` is set and a client id is present. Even then the callback returns `BLOCKED_EXTERNAL_CONFIG` until a verified redirect URI and signing keys are supplied outside source control. Email is not an identity key. Provider subject checks reject a bad issuer, audience, expiry, nonce, or signature before any link is stored.

## Sessions and QR

Session secrets are hashed with SHA-256. QR tokens are 32 random bytes, stored only as a hash, and expire after `Membership:QRExpirySeconds` (default 60). A second pending request for the same counter cancels the previous one. Verification is a single conditional update from Pending to Verified. Pricing stays on the existing customer price type. `Membership:RequireVerificationForSpecialPrices` defaults to false, so current price behavior is unchanged. Credit, balances, and stock are not written by this feature.

## Flags

`Membership:Enabled` defaults to false. Cash sales do not depend on it. The POS shows the membership QR only when the flag is on.

## Tests

`dotnet test tests/XtraPOS.Api.Tests/XtraPOS.Api.Tests.csproj --filter FullyQualifiedName~MembershipRulesTests`

`npm test -- src/components/MembershipPhonePage.test.tsx` from `src/XtraPOS.Web`.

## Staging and rollback

Do not deploy this with unrelated dirty backend changes. Production is out of scope. Disabling `Membership:Enabled` leaves sales available. The migration `019_SmartMembership` is additive.

## Provider configuration

Set these only in the host environment, not in source:

- `Membership__Enabled`
- `Membership__GoogleLoginEnabled`
- `Membership__GoogleClientId`
- `Membership__GoogleClientSecret`
- `Membership__AppleLoginEnabled`
- `Membership__AppleClientId`
- `Membership__AppleClientSecret`
- `Membership__PublicOrigin`

Google redirect: `https://test-pos.sharbelfashion.com/api/membership/auth/google/callback`

Apple return URL: `https://test-pos.sharbelfashion.com/api/membership/auth/apple/callback`

Apple also needs a Services ID, Team ID, Key ID, and private key registered on the verified domain. Until those values exist, start and callback return `BLOCKED_EXTERNAL_CONFIG`. First linking calls the existing phone OTP check. A provider subject that is already linked creates a session without another OTP. OTP delivery still depends on the existing phone-verification provider.

## Not verified

Real Google login, real Apple login, Android Chrome scan, and iPhone Safari scan are `BLOCKED_EXTERNAL_CONFIG` or `PENDING_MANUAL`.
