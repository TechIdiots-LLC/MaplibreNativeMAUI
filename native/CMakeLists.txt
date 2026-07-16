cmake_minimum_required(VERSION 3.15 FATAL_ERROR)

project(mln-cabi LANGUAGES CXX C)
set(CMAKE_CXX_STANDARD 20)

# ── maplibre-native dependency ────────────────────────────────────────────────
# maplibre-native is expected as a submodule at ../dependencies/maplibre-native
# or the path can be overridden via -DMAPLIBRE_NATIVE_DIR=...
if(NOT DEFINED MAPLIBRE_NATIVE_DIR)
    set(MAPLIBRE_NATIVE_DIR "${CMAKE_CURRENT_LIST_DIR}/../dependencies/maplibre-native"
        CACHE PATH "Path to maplibre-native source tree")
endif()

# Use the platform-specific toolchain if available (Windows vcpkg)
if(WIN32 AND EXISTS "${MAPLIBRE_NATIVE_DIR}/platform/windows/custom-toolchain.cmake")
    set(CMAKE_TOOLCHAIN_FILE "${MAPLIBRE_NATIVE_DIR}/platform/windows/custom-toolchain.cmake"
        CACHE STRING "CMake toolchain file")
endif()

# ── Shared (Windows/Android/Linux/macOS) or Static (iOS/tvOS/macCatalyst) ────
# Pass -DMBGL_STATIC=ON explicitly to force a static build (required for
# macCatalyst where the lib is statically linked via __Internal P/Invoke).
option(MBGL_STATIC "Build mln-cabi as a static library" OFF)
# Apple platforms (iOS, macCatalyst, macOS, tvOS) always use a static lib so that
# the MAUI app can link it via P/Invoke. Static libs don't run the linker at
# build time, which avoids cross-DSO typeinfo visibility issues with mbgl-core.
if(MBGL_STATIC OR APPLE)
    add_library(mln-cabi STATIC)
else()
    add_library(mln-cabi SHARED)
endif()

target_compile_definitions(mln-cabi PRIVATE
    MLN_CABI_EXPORT
    _USE_MATH_DEFINES
    NOMINMAX
)

target_include_directories(mln-cabi PRIVATE
    "${CMAKE_CURRENT_LIST_DIR}/include"
    "${CMAKE_CURRENT_LIST_DIR}/src"
)

target_sources(mln-cabi PRIVATE
    src/mln_cabi.cpp
    src/mln_cabi_offline.cpp
)

# ── Platform-specific frontend ────────────────────────────────────────────────
if(WIN32)
    target_sources(mln-cabi PRIVATE src/platform_frontend_windows.cpp)
    target_compile_options(mln-cabi PRIVATE /wd4267)
    target_include_directories(mln-cabi PRIVATE "${MAPLIBRE_NATIVE_DIR}/src")
    # Vulkan build: the offscreen frontend reuses mbgl::vulkan::HeadlessBackend,
    # whose header lives in platform/default/include (added to mbgl-core by
    # platform/windows/windows.cmake).
    if(MLN_WITH_VULKAN)
        target_include_directories(mln-cabi PRIVATE "${MAPLIBRE_NATIVE_DIR}/platform/default/include")
    endif()
elseif(ANDROID)
    target_sources(mln-cabi PRIVATE src/platform_frontend_android.cpp)
    target_link_libraries(mln-cabi PRIVATE EGL android log)
    target_include_directories(mln-cabi PRIVATE "${MAPLIBRE_NATIVE_DIR}/src")
    # Platform stub sources — symbols normally provided by MapLibreAndroid JNI SDK
    # that are absent in a standalone NDK build.
    target_sources(mln-cabi PRIVATE
        "${MAPLIBRE_NATIVE_DIR}/platform/default/src/mbgl/text/local_glyph_rasterizer.cpp"
        "${MAPLIBRE_NATIVE_DIR}/platform/default/src/mbgl/util/logging_stderr.cpp"
        "${MAPLIBRE_NATIVE_DIR}/platform/android/src/test/collator_test_stub.cpp"
        "${MAPLIBRE_NATIVE_DIR}/platform/android/src/test/number_format_test_stub.cpp"
        src/http_file_source_android.cpp
    )
    # MapLibre disables RTTI on Android/Linux. We must match that so polymorphic
    # classes in mln_cabi.cpp don't emit typeinfo that references non-existent
    # base class typeinfos in mbgl-core.a.
    target_compile_options(mln-cabi PRIVATE
        -fno-rtti
    )

    # LTO and size optimization matching what maplibre provides for mbgl-core
    set(_CABI_OPT "$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebInfo>>")
    target_compile_options(mln-cabi PRIVATE
        $<${_CABI_OPT}:-Oz>
        $<${_CABI_OPT}:-flto>
    )
    target_link_options(mln-cabi PRIVATE
        $<${_CABI_OPT}:-flto>
        $<${_CABI_OPT}:-Wl,--icf=all>
    )
elseif(APPLE)
    target_sources(mln-cabi PRIVATE src/platform_frontend_apple.mm)
    set_source_files_properties(src/platform_frontend_apple.mm PROPERTIES
        COMPILE_FLAGS "-x objective-c++")
    target_include_directories(mln-cabi PRIVATE "${MAPLIBRE_NATIVE_DIR}/src")

    if(MLN_WITH_VULKAN)
        # Vulkan via MoltenVK. mln-cabi is a static archive on Apple, so MoltenVK and
        # the Vulkan loader are linked into the final app (by the consuming project /
        # buildTransitive NativeReference); here we only need headers to compile.
        # QuartzCore provides CAMetalLayer; UIKit provides UIView (iOS + macCatalyst).
        target_include_directories(mln-cabi PRIVATE "${MAPLIBRE_NATIVE_DIR}/platform/default/include")
        find_library(QUARTZCORE_LIB QuartzCore REQUIRED)
        find_library(UIKIT_LIB UIKit)
        target_link_libraries(mln-cabi PRIVATE ${QUARTZCORE_LIB})
        if(UIKIT_LIB)
            target_link_libraries(mln-cabi PRIVATE ${UIKIT_LIB})
        endif()
    else()
        # Metal (default Apple renderer). metal-cpp headers are vendored in maplibre-native.
        target_include_directories(mln-cabi PRIVATE "${MAPLIBRE_NATIVE_DIR}/vendor/metal-cpp")
        find_library(METAL_LIB Metal REQUIRED)
        find_library(METALKIT_LIB MetalKit REQUIRED)
        find_library(QUARTZCORE_LIB QuartzCore REQUIRED)
        target_link_libraries(mln-cabi PRIVATE ${METAL_LIB} ${METALKIT_LIB} ${QUARTZCORE_LIB})
    endif()
    # mbgl-core is compiled with -fno-rtti on Apple (same as Android/Linux).
    # We must match that flag so MetalBackend's typeinfo does not emit a
    # reference to the non-existent base class typeinfo
    # (typeinfo for mbgl::mtl::RendererBackend) that mbgl-core never defines.
    target_compile_options(mln-cabi PRIVATE -fno-rtti)
else()
    message(FATAL_ERROR "Unsupported platform")
endif()

# ── Link maplibre-native ──────────────────────────────────────────────────────
# maplibre-native must be configured separately; we consume it via its cmake targets.
# Preferred: add_subdirectory(maplibre-native) before this or use find_package.
if(TARGET mbgl-core)
    target_link_libraries(mln-cabi PRIVATE mbgl-core)
else()
    message(WARNING "mbgl-core target not found — link maplibre-native before this project")
endif()

# ── Install / output ──────────────────────────────────────────────────────────
set_target_properties(mln-cabi PROPERTIES
    OUTPUT_NAME "mln-cabi"
    POSITION_INDEPENDENT_CODE ON
)

install(TARGETS mln-cabi DESTINATION lib)
install(FILES include/mln_cabi.h DESTINATION include)
