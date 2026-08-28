#!/usr/bin/env bash
#
# deploy.sh — build and deploy the kgsm-reactor leaf. Fully headless: no sudo, no prompts, ever.
#
#   ./deploy/deploy.sh
#
# Assumes deploy/setup.sh has already provisioned this host (install prefix owned by you, units
# symlinked out of a directory you own, polkit grant in place). If it has not, this script says
# so and stops before touching anything — it never half-deploys and never blocks on a password.
#
# What it does:
#   1. builds as you (a failure here costs nothing — the running service is untouched),
#   2. refreshes the systemd unit if it changed (writing a file you own + daemon-reload),
#   3. swaps the binary tree in with the service briefly stopped,
#   4. verifies with a REAL health probe — success is a service that serves, never just a
#      process that launched.
#
# Knobs: RID, HEALTH_TRIES.
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

RID="${RID:-linux-x64}"

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap and may be down — bringing it back up ..."
        if systemctl start "$SERVICE"; then
            err "restarted ${SERVICE} (note: it is running the PREVIOUS build)."
        else
            err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
        fi
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

# ── Preflight ─────────────────────────────────────────────────────────────────
refuse_root
require_setup

# ── 1. Build (Native-AOT, unprivileged, before anything is disrupted) ─────────
# A single self-contained native binary — no .NET runtime is needed on the host.
log "publishing Native-AOT (${RID}) → ${PUBLISH_DIR}"
rm -rf "$PUBLISH_DIR"
dotnet publish "${REPO_DIR}/src/Reactor/Reactor.csproj" -c Release -r "$RID" -o "$PUBLISH_DIR"

# ── 2. Refresh the unit if it changed (we own the file; systemd reads it via the symlink) ──
install_units_unprivileged
if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    sysctl_do daemon-reload
fi

# ── 2b. Publish the leaf config descriptor (a no-op for a project that ships none) ──
# Before the swap, so the surface kgsm-api reads never lags the binary that implements it.
install_leaf_descriptor

# ── 3. The swap ───────────────────────────────────────────────────────────────
log "stopping ${SERVICE} (release the running binary)"
sysctl_do stop "$SERVICE"
STOPPED=1

log "syncing publish tree → ${PREFIX}"
rsync -a --delete --exclude='*.pdb' --exclude='*.xml' "$PUBLISH_DIR/" "$PREFIX/"

# ── 3b. The pristine sample rules ─────────────────────────────────────────────
# ⚠ Into the INSTALL PREFIX, never the state directory. These are code: refreshed on every deploy so
# they always match the binary, and read by nothing at runtime. The rules the host actually runs live
# in the state directory, are owned by whoever edited them, and no deploy may touch them — setup.sh
# seeds those once and only for a file that is not already there.
#
# Keeping the pristine copies here is what lets the panel offer "reset to the sample" without a deploy
# ever reaching a rule somebody is running.
log "refreshing sample rules → ${PREFIX}/rules.d"
mkdir -p "${PREFIX}/rules.d"
rsync -a --delete "${REPO_DIR}/deploy/rules.d/" "${PREFIX}/rules.d/"

# The health probe accepts only a `leaf.ready` written at or after this moment. Taken BEFORE the
# start, because the one written by the run being replaced would otherwise satisfy the check
# instantly and the gate would pass on a service that never came up.
READY_SINCE="$(date +%s)"
export READY_SINCE

log "starting ${SERVICE}"
sysctl_do start "$SERVICE"
STOPPED=0

# ── 4. Verify (the real pass/fail) ────────────────────────────────────────────
log "waiting for ${SERVICE} to report leaf.ready in its own journal ..."
if wait_health; then
    log "${PROJECT} is up and healthy ✓"
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
else
    err "service started but it never reported leaf.ready within ${HEALTH_TRIES}s."
    err "that means ingestion did not come up — check the journal directory and the ledger path."
    err "recent logs:"
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi
