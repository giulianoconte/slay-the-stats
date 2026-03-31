#!/bin/bash
set -ex
SCRIPT_DIR=$( cd -- "$( dirname -- "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )

RELEASE=false
for arg in "$@"; do
    [[ "$arg" == "--release" ]] && RELEASE=true
done

if $RELEASE; then
    DIST_DIR=$SCRIPT_DIR/SlayTheStats/dist
    dotnet build $SCRIPT_DIR/SlayTheStats/SlayTheStats.csproj --nologo -v quiet -c Release /p:ModsPath=$DIST_DIR/
    VERSION=$(python3 -c "import json; print(json.load(open('$SCRIPT_DIR/SlayTheStats/SlayTheStats.json'))['version'])")
    ARCHIVE=$SCRIPT_DIR/SlayTheStats-${VERSION}.zip
    rm -f "$ARCHIVE"
    (cd "$DIST_DIR" && zip -r "$ARCHIVE" SlayTheStats)
    echo "Release archive: $ARCHIVE"
else
    dotnet build $SCRIPT_DIR/SlayTheStats/SlayTheStats.csproj --nologo -v quiet
fi
