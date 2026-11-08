# Server script fixes waiting for the owner's yes

Found in bug round 44 (2026-11-08) by reading the live scripts on 170.9.249.94. Nothing on the server was changed.
Each fix below is small and only touches the deploy scripts. Apply them before the next deploy, or deploy as usual and apply
them after; none of them changes the website or the server code.

What already works: both deploys build first and touch nothing if the build fails; each deploy restarts the standby copy,
checks it, then the primary, so one copy always answers; every container has `--restart always` and the user's
`podman-restart.service` is enabled with linger on, so all seven containers come back after a reboot; certbot renewal ran
10 hours ago; the postgres backup and the hourly follow-up timers run; the backend keeps a `coopilotx-backend:previous` image
for rollback.

## 1. A failed git pull still deploys old code and says it worked (backend and website)

**Backend** (`~/coopilotx/deploy_backend.sh`): `git pull --ff-only || log "(git pull skipped/failed - building working tree as-is)"`.
If the pull fails (a local edit on the server, GitHub down, a force push), the script builds the old code, restarts both
copies on it and prints "deploy complete". The only hint is one line in the middle of the log.

**Website** (`~/verchor-deploy.sh`): `git fetch -q origin main && git reset -q --hard origin/main`. If the fetch fails, the
reset is skipped, the old code is built and the script still prints `RESULT_DONE`, which is the line checked after a deploy.

Fix: stop the deploy when the source can't be updated, and print the commit that was built.

```diff
--- ~/coopilotx/deploy_backend.sh
-git pull --ff-only || log "  (git pull skipped/failed - building working tree as-is)"
+if ! git pull --ff-only; then
+  log "RESULT_PULL_FAILED - source not updated, nothing was built or touched"
+  exit 1
+fi
+log "  building $(git rev-parse --short HEAD)"
```

```diff
--- ~/verchor-deploy.sh
-git fetch -q origin main && git reset -q --hard origin/main
+if ! git fetch -q origin main; then
+  echo "RESULT_FETCH_FAILED - source not updated, nothing was built or touched"
+  exit 1
+fi
+git reset -q --hard origin/main
```

## 2. The website has no rollback image

The backend tags the running image as `coopilotx-backend:previous` before building. The website does not: after a deploy the
old `verchor-website` image is untagged and `docker image prune -f` at the end of the script deletes it. If a deploy passes
the health check (the home page returns 200) but something else is broken, the only way back is a rebuild of an old commit,
which takes minutes.

Fix: keep the running image as `verchor-website:previous`, like the backend.

```diff
--- ~/verchor-deploy.sh
 echo "BUILDING $(date)  $(git rev-parse --short HEAD)"
+PREV=$(docker inspect verchor --format '{{.Image}}' 2>/dev/null || echo "")
+[ -n "$PREV" ] && docker tag "$PREV" verchor-website:previous 2>/dev/null || true
```

Rollback is then: `docker tag verchor-website:previous verchor-website:latest` and run the script's `run_site` steps
(or rerun the script after resetting the repo to the previous commit).

## 3. An aborted deploy leaves the standby on the broken build

When the new standby fails its health check, both scripts stop and leave the standby container on the broken image. The
primary keeps serving, but there is no standby behind it until someone deploys again, so a primary restart or crash in that
time takes the site or the answers down.

Fix: put the standby back on the previous image before stopping.

```diff
--- ~/coopilotx/deploy_backend.sh  (inside: if ! wait_healthy 8092 standby; then)
   log "ABORTED - standby did not start. Primary untouched and still serving."
+  podman rm -f coopilotx-backend-standby >/dev/null 2>&1 || true
+  podman run -d --name coopilotx-backend-standby --network coopilotx-net \
+    --env-file "$ENVFILE" -e PDF_SERVICE_URL=http://coopilotx-pdf-service:3001 \
+    -v "$FIREBASE":/app/firebase-service-account.json:ro,z \
+    -p 8092:8082 --restart always coopilotx-backend:previous >/dev/null \
+    && log "  standby put back on the previous image"
```

The same for the website once fix 2 exists (`run_site` with image `verchor-website:previous`).

## 4. No free disk check before a build

The disk is 71% full (8.7 GB free of 30 GB). A website build pulls node images and writes a large build cache, and a build
that runs out of disk fails half way, which the scripts handle, but podman can also fail to start the new container after a
successful build. A check before building turns that into a clean stop.

```diff
+FREE_KB=$(df --output=avail / | tail -1)
+if [ "$FREE_KB" -lt 3000000 ]; then
+  echo "RESULT_LOW_DISK - under 3 GB free, nothing was built or touched"
+  exit 1
+fi
```

Add it before the build in both scripts. Space that can be freed safely (owner's call): the 438 MB
`old-server-backup-20260917.tar.gz` and old images such as `varoxel-website:new2`.

## Not a problem

- `update_backend.sh` only calls `deploy_backend.sh`.
- The old `container-coopilotx-frontend.service` unit is disabled and points at a container that no longer exists; harmless.
- The postgres backup refuses a dump under 200 bytes; the current dumps are about 10.7 KB and hold the resume rows.
