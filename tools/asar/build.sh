#!/bin/sh
# Builds Asar's shared library (libasar.dylib / libasar.so) for Pipe Dream's dev loop and tests.
# Asar is pinned at 5fd539c (v1.91+16, API 3.3) and carries one change, lazy-rom-crc.patch: the
# whole-ROM CRC Asar took after every patch is only read by symbols files, and was 77% of a
# patch's time. Under Asar's LGPL this build is a modified Asar; its source is upstream plus that
# patch. Usage: tools/asar/build.sh <out-dir>   then   export PIPEDREAM_ASAR=<out-dir>/libasar.*
set -eu
out=$(cd "${1:?usage: build.sh <out-dir>}" && pwd)
here=$(cd "$(dirname "$0")" && pwd)
src="$out/asar-src"
[ -d "$src" ] || git clone -q https://github.com/RPGHacker/asar.git "$src"
git -C "$src" checkout -q --force 5fd539c
git -C "$src" apply "$here/lazy-rom-crc.patch"
cmake -S "$src/src" -B "$out/build" -DCMAKE_BUILD_TYPE=Release -DASAR_GEN_EXE=OFF -DASAR_GEN_DLL=ON \
      -DASAR_GEN_LIB=OFF -DASAR_GEN_EXE_TEST=OFF -DASAR_GEN_DLL_TEST=OFF >/dev/null
cmake --build "$out/build" -j8 >/dev/null
find "$out/build/asar/lib" -name "libasar*" ! -name "*.pc" -exec cp -P {} "$out"/ \;
ls "$out"/libasar*
