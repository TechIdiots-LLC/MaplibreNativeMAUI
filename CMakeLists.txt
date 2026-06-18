cmake_minimum_required(VERSION 3.15 FATAL_ERROR)

# On Apple platforms with the Ninja generator (used for macCatalyst builds)
# CMake must be told about ObjC/ObjCXX up front so it registers the compile
# rules (CMAKE_OBJCXX_COMPILE_OBJECT etc.) before any subdirectory is added.
if(APPLE)
    project(maplibre-maui LANGUAGES CXX C OBJC OBJCXX)
else()
    project(maplibre-maui LANGUAGES CXX C)
endif()

set(MAPLIBRE_NATIVE_DIR "${CMAKE_CURRENT_LIST_DIR}/dependencies/maplibre-native"
    CACHE PATH "Path to maplibre-native source tree")

# ── Renderer feature flags (platform defaults) ────────────────────────────────
# Pass -DCMAKE_TOOLCHAIN_FILE=... on the CMake command line for platform toolchains:
#   Windows : dependencies/maplibre-native/platform/windows/custom-toolchain.cmake
#   Android : $ANDROID_NDK/build/cmake/android.toolchain.cmake
#   iOS/mac : handled by -DCMAKE_SYSTEM_NAME=iOS / Xcode generator
if(WIN32)
    set(MLN_WITH_OPENGL ON  CACHE BOOL "Enable OpenGL renderer" )
    set(MLN_WITH_EGL    OFF CACHE BOOL "Enable EGL"             )
    set(MLN_WITH_VULKAN OFF CACHE BOOL "Enable Vulkan renderer" )
endif()

# ── maplibre-native (provides mbgl-core target) ───────────────────────────────
add_subdirectory("${MAPLIBRE_NATIVE_DIR}" "${CMAKE_BINARY_DIR}/maplibre-native")

# ── C ABI wrapper (shared on Windows/Android, static on iOS/macCatalyst) ──────
add_subdirectory(native)
