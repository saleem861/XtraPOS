# Staging deployment

Manual workflow: `.github/workflows/deploy-staging.yml`. It is not run by this change.

The only allowed target is `administrator@69.197.178.239`, service `xtrapos-staging.service`, API port `5088`, and root `/opt/xtrapos-staging`. Host-key checking is `yes`. A wildcard known_hosts entry is rejected.

`deploy` downloads the CI artifact `xtrapos-release-<sha>`, scans it, copies it under `/opt/xtrapos-staging/releases`, keeps the existing `App_Data`, restarts `xtrapos-staging.service` only, and requires public HTTP 200, `/health` containing `Healthy`, and `/release.json` `sha` equal to that commit. A failed check restores the previous pointer and restarts the same service. `rollback` restores the newest directory in `/opt/xtrapos-staging/backups`.

The remote probe refuses port `5216`, `xtrapos-api.service`, paths outside `/opt/xtrapos-staging`, and the databases `XtraPOS_Central`, `XtraPOS_Branch`, `XtraPOS_Branch_01`, `XtraPOS_Branch_02`, `saleemPOSDB`, `Shop1`, `Shop2`, and `swstore`. No script runs `sqlcmd` or a migration command. nginx, DNS, and certificates are not edited.

The deploy and rollback jobs stay skipped until repository variable `STAGING_ENVIRONMENT_PROTECTION` is `verified`. Set that only after GitHub Environment `staging` has a required reviewer and allows `main` only. The jobs then need secrets `STAGING_SSH_KEY` and `STAGING_KNOWN_HOSTS`. Those secrets are not created here. No SSH key was available to verify the host fingerprint, so the server layout has not been inspected.
