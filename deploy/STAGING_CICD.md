# Staging deployment

Workflow: `.github/workflows/deploy-staging.yml`. Approval comes from the GitHub Environment `staging`. This change does not dispatch it.

Allowed target: `xtrapos-deploy@69.197.178.239`. Service: `xtrapos-staging.service`. API: `127.0.0.1:5088`. Root: `/opt/xtrapos-staging`. Working directory: `/opt/xtrapos-staging/current/api`. Production `xtrapos-api.service` on port `5216` is never restarted.

Inspection reads the root and upstream from the one HTTPS server block for `test-pos.sharbelfashion.com`. An HTTP server block for that name is accepted only when it redirects to HTTPS and does not proxy. Nginx is not modified.

`current` and `current.txt` must name the same release. Activation updates both. It copies the previous release's `App_Data` into `/opt/xtrapos-staging/shared/app-data` and points only the new release at that shared directory. The previous release keeps its own `App_Data` directory, so a rollback still has it. Data-protection keys at `/usr/share/XtraPOS/data-protection-keys` are not deleted. After activation, a failed public `/health` check, homepage check, or release SHA check restores the previous pointer and restarts only `xtrapos-staging.service`.

Secrets used by the Environment, and not stored in git: `STAGING_SSH_HOST`, `STAGING_SSH_USER`, `STAGING_SSH_KEY`, `STAGING_KNOWN_HOSTS`. The host secret must be `69.197.178.239` and the user secret must be `xtrapos-deploy`. Host-key checking stays enabled.

If `xtrapos-deploy` cannot read the unit or restart staging, the only extra privilege required is:

```
xtrapos-deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart xtrapos-staging.service, /usr/bin/systemctl is-active xtrapos-staging.service, /usr/bin/systemctl show xtrapos-staging.service, /usr/bin/systemctl show xtrapos-api.service, /usr/sbin/nginx -T
```

If that account cannot replace `/opt/xtrapos-staging/current`, give it write access to the staging root only:

```
sudo chgrp xtrapos-deploy /opt/xtrapos-staging
sudo chmod 2775 /opt/xtrapos-staging
```

Do not grant sudo for `xtrapos-api.service`, port `5216`, nginx site edits, or SQL.
