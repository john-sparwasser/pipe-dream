#!/bin/sh
# Builds Quasar's shared library (libasar.dylib / libasar.so) for Pipe Dream's dev loop and tests.
# Quasar (github.com/john-sparwasser/quasar) is our fork of Asar, made faster; it keeps Asar's API
# and library name, so it drops in where Asar would. Pinned below; under Asar's LGPL it is a
# modified Asar and the fork is its source. QUASAR_SRC=<checkout> builds a working tree as it is,
# for working on the fork. Usage: tools/asar/build.sh <out-dir>   then
# export PIPEDREAM_ASAR=<out-dir>/libasar.*
set -eu
pin=71f92733fbd5b4677561848cd52985a5421b5371
out=$(cd "${1:?usage: build.sh <out-dir>}" && pwd)
src=${QUASAR_SRC:-"$out/quasar-src"}
if [ -z "${QUASAR_SRC:-}" ]; then
  [ -d "$src" ] || git clone -q https://github.com/john-sparwasser/quasar.git "$src"
  git -C "$src" fetch -q origin
  git -C "$src" checkout -q --force "$pin"
fi
cmake -S "$src/src" -B "$out/build" -DCMAKE_BUILD_TYPE=Release -DASAR_GEN_EXE=OFF -DASAR_GEN_DLL=ON \
      -DASAR_GEN_LIB=OFF -DASAR_GEN_EXE_TEST=OFF -DASAR_GEN_DLL_TEST=OFF >/dev/null
cmake --build "$out/build" -j8 >/dev/null
find "$out/build/asar/lib" -name "libasar*" ! -name "*.pc" -exec cp -P {} "$out"/ \;
ls "$out"/libasar*
