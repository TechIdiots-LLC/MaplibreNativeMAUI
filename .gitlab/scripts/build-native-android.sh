#!/usr/bin/env bash
# Builds libmln-cabi.so for one Android ABI, as .github/workflows/native-android.yml and
# native-android-vulkan.yml do, and copies it to native-out/android-<abi>-<renderer>/.
#
#   build-native-android.sh <arm64-v8a|armeabi-v7a|x86_64> <opengl|vulkan>
#
# Needs cmake (3.25+), ninja, python3, curl and unzip. ANDROID_NDK_ROOT may point at an NDK
# 28.2.13676358 (r28c, maplibre-native's ndkVersion); without it the NDK zip is downloaded into
# .cache/ (which CI caches) and unpacked under /tmp. Run from the repository root, with the
# submodules checked out.
set -euo pipefail

ABI=${1:?ABI (arm64-v8a, armeabi-v7a or x86_64)}
RENDERER=${2:?renderer (opengl or vulkan)}
NDK_RELEASE=r28c
NDK_REVISION=28.2.13676358
BUILD_DIR=build/android-$ABI-$RENDERER
OUT=native-out/android-$ABI-$RENDERER

case "$RENDERER" in
  opengl) BACKEND=-DMLN_WITH_OPENGL=ON ;;
  vulkan) BACKEND=-DMLN_WITH_VULKAN=ON ;;
  *) echo "Unknown renderer: $RENDERER" >&2; exit 2 ;;
esac

# ── NDK ──────────────────────────────────────────────────────────────────────────────────────
if [ -z "${ANDROID_NDK_ROOT:-}" ]; then
  ZIP=.cache/android-ndk-$NDK_RELEASE-linux.zip
  mkdir -p .cache
  if [ ! -s "$ZIP" ]; then
    curl -fsSL --retry 3 -o "$ZIP.part" "https://dl.google.com/android/repository/android-ndk-$NDK_RELEASE-linux.zip"
    mv "$ZIP.part" "$ZIP"
  fi
  rm -rf "/tmp/android-ndk-$NDK_RELEASE"
  unzip -q "$ZIP" -d /tmp
  ANDROID_NDK_ROOT=/tmp/android-ndk-$NDK_RELEASE
fi
if ! grep -q "Pkg.Revision = $NDK_REVISION" "$ANDROID_NDK_ROOT/source.properties"; then
  echo "$ANDROID_NDK_ROOT is not NDK $NDK_REVISION:" >&2
  cat "$ANDROID_NDK_ROOT/source.properties" >&2
  exit 1
fi

# ── Configure and build ──────────────────────────────────────────────────────────────────────
cmake -B "$BUILD_DIR" -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_ROOT/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI="$ABI" \
  -DANDROID_PLATFORM=android-23 \
  -DCMAKE_BUILD_TYPE=Release \
  "$BACKEND"
cmake --build "$BUILD_DIR" --target mln-cabi --parallel "$(nproc)"

SO=$(find "$BUILD_DIR" -name libmln-cabi.so | head -1)
[ -n "$SO" ] || { echo "libmln-cabi.so not found under $BUILD_DIR" >&2; exit 1; }

# ── Strip ────────────────────────────────────────────────────────────────────────────────────
# Release does not imply a stripped .so, and the symbol table is a large share of the shipped
# size; the packages are close to nuget.org's 250 MB ceiling. Before the alignment check, so
# that check sees exactly what ships.
BEFORE=$(stat -c%s "$SO")
"$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip" --strip-unneeded "$SO"
AFTER=$(stat -c%s "$SO")
echo "$SO: $((BEFORE / 1048576)) MiB -> $((AFTER / 1048576)) MiB"

# ── 16 KB page alignment ─────────────────────────────────────────────────────────────────────
# Google Play rejects 64-bit .so files whose LOAD segments align below 16 KB.
python3 - "$SO" <<'EOF'
import struct, sys
path = sys.argv[1]
with open(path, 'rb') as f:
    head = f.read(64)
    if head[4] != 2:
        print(path, 'is 32-bit; 16 KB pages are a 64-bit requirement')
        raise SystemExit(0)
    phoff, = struct.unpack_from('<Q', head, 0x20)
    phentsize, phnum = struct.unpack_from('<HH', head, 0x36)
    f.seek(phoff)
    ph = f.read(phentsize * phnum)
aligns = [struct.unpack_from('<Q', ph, i * phentsize + 0x30)[0]
          for i in range(phnum)
          if struct.unpack_from('<I', ph, i * phentsize)[0] == 1]
print(path, 'LOAD aligns:', [hex(a) for a in aligns])
if not aligns or min(aligns) < 16384:
    sys.exit('%s is not 16 KB page aligned' % path)
EOF

mkdir -p "$OUT"
cp "$SO" "$OUT/"
ls -l "$OUT"
