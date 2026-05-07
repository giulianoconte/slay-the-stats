#!/bin/bash
set -e
SCRIPT_DIR=$( cd -- "$( dirname -- "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )
source "$SCRIPT_DIR/lock-helper.sh"

FORCE=false
DEPLOY_ARGS=()
for arg in "$@"; do
    case "$arg" in
        --force) FORCE=true ;;
        *)       DEPLOY_ARGS+=("$arg") ;;
    esac
done

# Build via deploy.sh, but suppress its watcher; redeploy spawns its own
# (in redeploy mode, with tighter timeouts because Steam is being launched).
"$SCRIPT_DIR/deploy.sh" --no-watch ${DEPLOY_ARGS[@]+"${DEPLOY_ARGS[@]}"}

mkdir -p "$SCRIPT_DIR/logs"
WAIT_LOG="$SCRIPT_DIR/logs/.watch.log"

force_arg=""
$FORCE && force_arg=force
acquire_lock "redeploy" "$force_arg" || exit 1

START_TS=$(date +%s)
touch /media/sf_sts2-mods/.launch-sts2

nohup "$SCRIPT_DIR/watch-log.sh" redeploy "$START_TS" >>"$WAIT_LOG" 2>&1 &
WATCHER_PID=$!
write_lock "$WATCHER_PID" "redeploy"
echo "log watcher pid=$WATCHER_PID mode=redeploy (tail $WAIT_LOG)"
