#!/bin/sh
# Builds Quasar's shared library (libasar.dylib / libasar.so / asar.dll) for Pipe Dream's dev loop,
# its tests and CI (.github/workflows/build.yml, which publishes it inside the app). Quasar
# (github.com/john-sparwasser/quasar) is our fork of Asar, made faster; it keeps Asar's API and
# library name, so it drops in where Asar would. Pinned below; under Asar's LGPL it is a modified
# Asar and the fork is its source. QUASAR_SRC=<checkout> builds a working tree as it is, for
# working on the fork. Usage: tools/asar/build.sh <out-dir>   — the last line printed is the
# library's path, for PIPEDREAM_ASAR.
set -eu
pin=5b38db8065a777c09468745110634923c9dc0981
out=$(cd "${1:?usage: build.sh <out-dir>}" && pwd)
# Git Bash on Windows: CMake and MSVC want C:/... rather than /c/...
if command -v cygpath >/dev/null 2>&1; then out=$(cygpath -m "$out"); fi
src=${QUASAR_SRC:-"$out/quasar-src"}
if [ -z "${QUASAR_SRC:-}" ]; then
  [ -d "$src" ] || git clone -q https://github.com/john-sparwasser/quasar.git "$src"
  git -C "$src" fetch -q origin
  git -C "$src" checkout -q --force "$pin"
fi
cmake -S "$src/src" -B "$out/build" -DCMAKE_BUILD_TYPE=Release -DASAR_GEN_EXE=OFF -DASAR_GEN_DLL=ON \
      -DASAR_GEN_LIB=OFF -DASAR_GEN_EXE_TEST=OFF -DASAR_GEN_DLL_TEST=OFF >/dev/null
cmake --build "$out/build" --config Release --parallel 8 >/dev/null     # --config: MSVC builds per config
# One file under the name .NET probes for ("asar" → asar.dll / libasar.so / libasar.dylib), the
# versioned symlinks resolved.
for name in asar.dll libasar.so libasar.dylib; do
  lib=$(find "$out/build" -name "$name" | head -n 1)
  if [ -n "$lib" ]; then cp -L "$lib" "$out/$name"; echo "$out/$name"; exit 0; fi
done
echo "no asar library under $out/build" >&2; exit 1
