#!/bin/zsh
# adhoc.sh FILE.mml [SECONDS]: our engine and AddmusicK's playing FILE, their differences and events per voice.
cd "${0:A:h}/../../src"
PIPEDREAM_MML="${1:A}" PIPEDREAM_SECONDS="${2:-8}" dotnet test tests/PipeDream.Tests.csproj --no-build --filter "FullyQualifiedName~MusicEngineTests.adhoc" --logger "console;verbosity=detailed" 2>&1 \
  | grep -E '^ +(v[0-9]|theirs|ours)' | cut -c1-${COLUMNS:-400}
