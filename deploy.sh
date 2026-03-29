#!/bin/bash
set -ex
SCRIPT_DIR=$( cd -- "$( dirname -- "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )
dotnet build $SCRIPT_DIR/SlayTheStats/SlayTheStats.csproj --nologo -v quiet
