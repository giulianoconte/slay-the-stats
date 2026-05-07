#!/bin/bash
# Background watcher spawned by deploy.sh / redeploy.sh. Captures the next
# godot session log to <tree>/logs/.
#
# Args: <mode> <start_ts>
#   mode     = "deploy" or "redeploy"
#              redeploy: tight timeouts on trigger consumption + new log
#              deploy:   no trigger wait, generous timeout on new log
#   start_ts = unix epoch seconds before the trigger touch (or build); only
#              godot logs with mtime >= start_ts are considered "ours"
set -u
SCRIPT_DIR=$( cd -- "$( dirname -- "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )

MODE="${1:-deploy}"
START_TS="${2:-0}"

LOG_DIR="/media/sf_sts2-appdata/logs"
TRIGGER="/media/sf_sts2-mods/.launch-sts2"
LOCK_FILE=/tmp/sts2-redeploy.lock
DEST="$SCRIPT_DIR/logs"
mkdir -p "$DEST"

log() { echo "[$(date '+%H:%M:%S')] $*"; }

# Release the lock only if it still names us. Avoids removing a lock a newer
# deploy/redeploy has since claimed.
cleanup() {
    if [[ -f "$LOCK_FILE" ]] && grep -qx "pid=$$" "$LOCK_FILE"; then
        rm -f "$LOCK_FILE"
    fi
}
trap cleanup EXIT INT TERM

log "watcher pid=$$ mode=$MODE start_ts=$START_TS dest=$DEST"

# 1. (redeploy only) Wait for host to consume the trigger (= Steam invoked).
if [[ "$MODE" == "redeploy" ]]; then
    deadline=$((SECONDS + 60))
    while [[ -e "$TRIGGER" ]]; do
        if (( SECONDS > deadline )); then
            log "ERROR: launch trigger never consumed in 60s; bailing."
            exit 1
        fi
        sleep 1
    done
    log "launch trigger consumed."
    NEW_LOG_TIMEOUT=180
else
    log "deploy mode: skipping trigger wait."
    NEW_LOG_TIMEOUT=$((8 * 3600))
fi

# 2. Wait for a new godot<timestamp>.log (mtime >= START_TS) to appear.
deadline=$((SECONDS + NEW_LOG_TIMEOUT))
new_log=""
while [[ -z "$new_log" ]]; do
    if (( SECONDS > deadline )); then
        log "ERROR: no new godot log appeared in ${NEW_LOG_TIMEOUT}s; bailing."
        exit 1
    fi
    for f in "$LOG_DIR"/godot*.log; do
        [[ -e "$f" ]] || continue
        [[ "$(basename "$f")" == "godot.log" ]] && continue
        mtime=$(stat -c %Y "$f" 2>/dev/null || echo 0)
        if (( mtime >= START_TS )); then
            new_log="$f"
            break
        fi
    done
    [[ -z "$new_log" ]] && sleep 2
done
log "new log: $new_log"

# 3. Wait for game exit.
#    Primary signal: Godot's shutdown markers grep'd from log content
#    ("leaked at exit" / "still in use at exit" — emitted on every clean exit).
#    Fallback: log mtime stable for 60s (catches crashes that don't write the
#    shutdown trace, and any future Godot version that drops it).
SHUTDOWN_RE='leaked at exit|still in use at exit'
last_mtime=0
stable_since=$SECONDS
exit_reason=""
while true; do
    if grep -qE "$SHUTDOWN_RE" "$new_log" 2>/dev/null; then
        exit_reason="shutdown marker found (clean exit)"
        break
    fi
    mtime=$(stat -c %Y "$new_log" 2>/dev/null || echo 0)
    if (( mtime != last_mtime )); then
        last_mtime=$mtime
        stable_since=$SECONDS
    elif (( SECONDS - stable_since >= 60 )); then
        exit_reason="no shutdown marker, log idle 60s (crash or abort)"
        break
    fi
    sleep 2
done
log "game exited: $exit_reason"

# 4. Copy.
cp "$new_log" "$DEST/"
log "copied -> $DEST/$(basename "$new_log")"
