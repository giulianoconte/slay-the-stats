#!/bin/bash
set -e
SCRIPT_DIR=$( cd -- "$( dirname -- "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )
source "$SCRIPT_DIR/lock-helper.sh"

NO_WATCH=false
FORCE=false
RELEASE=false
for arg in "$@"; do
    case "$arg" in
        --no-watch) NO_WATCH=true ;;
        --force)    FORCE=true ;;
        --release)  RELEASE=true ;;
    esac
done

set -x
if $RELEASE; then
    DIST_DIR=$SCRIPT_DIR/SlayTheStats/dist
    dotnet publish $SCRIPT_DIR/SlayTheStats/SlayTheStats.csproj --nologo -v quiet -c Release /p:DeployToMods=true /p:ModsPath=$DIST_DIR/
    VERSION=$(python3 -c "import json; print(json.load(open('$SCRIPT_DIR/SlayTheStats/SlayTheStats.json'))['version'])")
    ARCHIVE=$SCRIPT_DIR/SlayTheStats-${VERSION}.zip
    rm -f "$ARCHIVE"
    (cd "$DIST_DIR" && zip -r "$ARCHIVE" SlayTheStats)
    echo "Release archive: $ARCHIVE"
else
    dotnet build $SCRIPT_DIR/SlayTheStats/SlayTheStats.csproj --nologo -v quiet /p:DeployToMods=true
fi
{ set +x; } 2>/dev/null

if $NO_WATCH; then
    exit 0
fi

mkdir -p "$SCRIPT_DIR/logs"
WAIT_LOG="$SCRIPT_DIR/logs/.watch.log"

force_arg=""
$FORCE && force_arg=force
acquire_lock "deploy" "$force_arg" || exit 1

START_TS=$(date +%s)
nohup "$SCRIPT_DIR/watch-log.sh" deploy "$START_TS" >>"$WAIT_LOG" 2>&1 &
WATCHER_PID=$!
write_lock "$WATCHER_PID" "deploy"
echo "log watcher pid=$WATCHER_PID mode=deploy (tail $WAIT_LOG)"
