#!/usr/bin/env bash
# Builds the self-contained libmln-cabi.a (Metal) for one Apple target, as
# .github/workflows/native-apple.yml does, and copies it to native-out/<target>/.
#
#   build-native-apple.sh <ios|iossimulator|maccatalyst>
#
# ios and iossimulator are arm64 (MapLibre has no Intel simulator slice). maccatalyst is a
# universal x86_64 + arm64 archive. Needs Xcode, cmake and ninja. Run from the repository
# root, with the submodules checked out.
set -euo pipefail

TARGET=${1:?target (ios, iossimulator or maccatalyst)}
OUT=native-out/$TARGET

# Every .a the build made, merged into the mln-cabi archive. libmln-cabi.a is a thin C wrapper
# whose PRIVATE CMake link dependencies (mbgl-core, vendor libraries) the static archiver does
# not fold in, and the MAUI app links it alone, through an XCFramework slice or NativeReference.
merge_archives() {
  local dir=$1 cabi all
  cabi=$(find "$dir" -name libmln-cabi.a | head -1)
  [ -n "$cabi" ] || { echo "libmln-cabi.a not found under $dir" >&2; exit 1; }
  all=$(find "$dir" -name '*.a' -type f | sort)
  # shellcheck disable=SC2086
  libtool -static -o "$cabi.merged" $all
  mv "$cabi.merged" "$cabi"
  echo "$cabi: $(ar -t "$cabi" | wc -l | tr -d ' ') objects" >&2
  echo "$cabi"
}

case "$TARGET" in
  ios|iossimulator)
    if [ "$TARGET" = ios ]; then SYSROOT=iphoneos; else SYSROOT=iphonesimulator; fi
    BUILD=build/apple-$TARGET
    cmake -B "$BUILD" -G Xcode \
      -DCMAKE_SYSTEM_NAME=iOS \
      -DCMAKE_OSX_ARCHITECTURES=arm64 \
      -DCMAKE_OSX_SYSROOT=$SYSROOT \
      -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0 \
      -DMLN_WITH_METAL=ON
    cmake --build "$BUILD" --target mln-cabi --config Release
    LIB=$(merge_archives "$BUILD")
    ;;

  maccatalyst)
    # One build per architecture with the explicit macabi target triple, so every object is
    # tagged macCatalyst rather than plain macOS (else the app link fails with "built for
    # macOS"). CMAKE_<LANG>_COMPILER_TARGET goes first on every compile line, where subdirectory
    # flag changes cannot override it. The iOSSupport paths provide UIKit and friends;
    # -Wno-overriding-option silences the -mmacosx-version-min=14.3 that maplibre-native adds
    # on Darwin when no deployment target is set (#4661): the -target triple wins, but under
    # -Werror the warning fails the build. -Wno-overriding-t-option is its old name, for older
    # clang; -Wno-unknown-warning-option covers whichever name a clang does not know.
    SDK=$(xcrun --sdk macosx --show-sdk-path)
    FLAGS="-iframework $SDK/System/iOSSupport/System/Library/Frameworks -I$SDK/System/iOSSupport/usr/include -Wno-overriding-option -Wno-overriding-t-option"
    CLANG=$(xcrun -f clang)
    CLANGXX=$(xcrun -f clang++)
    SLICES=()
    for ARCH in x86_64 arm64; do
      TRIPLE=$ARCH-apple-ios15.0-macabi
      BUILD=build/apple-maccatalyst-$ARCH
      cmake -B "$BUILD" -G Ninja \
        -DCMAKE_SYSTEM_NAME=Darwin \
        -DCMAKE_OSX_SYSROOT=macosx \
        -DCMAKE_OSX_ARCHITECTURES="" \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_C_COMPILER="$CLANG" -DCMAKE_CXX_COMPILER="$CLANGXX" \
        -DCMAKE_OBJC_COMPILER="$CLANG" -DCMAKE_OBJCXX_COMPILER="$CLANGXX" \
        "-DCMAKE_C_COMPILER_TARGET=$TRIPLE" "-DCMAKE_CXX_COMPILER_TARGET=$TRIPLE" \
        "-DCMAKE_OBJC_COMPILER_TARGET=$TRIPLE" "-DCMAKE_OBJCXX_COMPILER_TARGET=$TRIPLE" \
        "-DCMAKE_C_FLAGS=$FLAGS" "-DCMAKE_CXX_FLAGS=$FLAGS" \
        "-DCMAKE_OBJC_FLAGS=$FLAGS" "-DCMAKE_OBJCXX_FLAGS=$FLAGS" \
        -DMLN_WITH_GLFW=OFF \
        -DMLN_WITH_METAL=ON
      cmake --build "$BUILD" --target mln-cabi --config Release --parallel
      SLICES+=("$(merge_archives "$BUILD")")
    done
    LIB=build/apple-maccatalyst/libmln-cabi.a
    mkdir -p "$(dirname "$LIB")"
    lipo -create "${SLICES[@]}" -output "$LIB"
    lipo -info "$LIB"
    ;;

  *) echo "Unknown target: $TARGET" >&2; exit 2 ;;
esac

# The Xcode generator's Release configuration still compiles with -g, so the amalgam carries
# DWARF for every object in it: dead weight in a distributed archive, and the packages are
# close to nuget.org's 250 MB ceiling.
BEFORE=$(stat -f%z "$LIB")
strip -S "$LIB"
ranlib "$LIB"
AFTER=$(stat -f%z "$LIB")
echo "$LIB: $((BEFORE / 1048576)) MiB -> $((AFTER / 1048576)) MiB"

mkdir -p "$OUT"
cp "$LIB" "$OUT/"
ls -l "$OUT"
