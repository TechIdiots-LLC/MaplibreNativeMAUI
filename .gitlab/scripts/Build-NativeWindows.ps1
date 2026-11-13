<#
.SYNOPSIS
    Builds mln-cabi.dll for Windows, as .github/workflows/native-windows.yml and
    native-windows-vulkan.yml do, and copies it to native-out/win-<Arch>-<Renderer>/.

.DESCRIPTION
    Enters a Visual Studio developer shell for the target architecture (arm64 is cross-built
    on an x64 host), configures with maplibre-native's vcpkg toolchain and builds the mln-cabi
    target with Ninja. vcpkg's packages come from its default binary cache, which on the
    GitLab shell runner persists in the runner account's profile between jobs.

    Run from the repository root, with the submodules checked out.

.EXAMPLE
    .\.gitlab\scripts\Build-NativeWindows.ps1 -Arch x64 -Renderer opengl
#>
param(
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [ValidateSet('opengl', 'vulkan')][string]$Renderer = 'opengl',
    [string]$BuildDir = "build/win-$Arch-$Renderer"
)

$ErrorActionPreference = 'Stop'

# ── MSVC developer environment ──────────────────────────────────────────────────────────────
# maplibre-native's custom-toolchain.cmake picks the vcpkg triplet from VSCMD_ARG_TGT_ARCH and
# only loads vcpkg when VCINSTALLDIR is set, so both must come from a real developer shell.
if ($env:VSCMD_ARG_TGT_ARCH -ne $Arch) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $component = if ($Arch -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' }
                 else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    $vs = & $vswhere -latest -products * -requires $component -property installationPath
    if (-not $vs) { throw "No Visual Studio install with $component found" }
    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    # Enter-VsDevShell reads back the environment VsDevCmd leaves, one NAME=value per line, and
    # prints every line it cannot parse. GitLab's multi-line variables (CI_COMMIT_MESSAGE,
    # CI_COMMIT_DESCRIPTION, ...) are such lines, so set them aside while it runs.
    $multiLine = @(Get-ChildItem env: | Where-Object { $_.Value -match "[`r`n]" })
    foreach ($v in $multiLine) { Remove-Item "env:$($v.Name)" }
    try {
        Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$Arch -host_arch=amd64"
    } finally {
        foreach ($v in $multiLine) { Set-Item "env:$($v.Name)" $v.Value }
    }
}
Write-Host "MSVC target: $env:VSCMD_ARG_TGT_ARCH (VS at $env:VSINSTALLDIR)"

# ── Ninja ───────────────────────────────────────────────────────────────────────────────────
# Not on PATH everywhere: the Build Tools developer shell only adds the one bundled with VS's
# CMake component. Use PATH, then that one, then the Android SDK's CMake, then a download kept
# in the runner account's profile.
function Find-Ninja {
    $cmd = Get-Command ninja -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @()
    if ($env:VSINSTALLDIR) {
        $candidates += Join-Path $env:VSINSTALLDIR 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
    }
    foreach ($sdk in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA 'Android\Sdk'))) {
        if ($sdk -and (Test-Path (Join-Path $sdk 'cmake'))) {
            # Any version will do; Ninja's file format is stable
            $candidates += Get-ChildItem (Join-Path $sdk 'cmake') -Directory |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'bin\ninja.exe' }
        }
    }
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) { return $found }

    $dir = Join-Path $env:LOCALAPPDATA 'mln-ci\ninja-1.13.1'
    $exe = Join-Path $dir 'ninja.exe'
    if (-not (Test-Path $exe)) {
        New-Item -ItemType Directory -Force $dir | Out-Null
        $zip = Join-Path $dir 'ninja-win.zip'
        Invoke-WebRequest 'https://github.com/ninja-build/ninja/releases/download/v1.13.1/ninja-win.zip' -OutFile $zip
        Expand-Archive $zip -DestinationPath $dir -Force
        Remove-Item $zip
    }
    return $exe
}
$ninja = Find-Ninja
Write-Host "Ninja: $ninja ($(& $ninja --version))"
Write-Host "CMake: $((cmake --version | Select-Object -First 1))"

# ── Configure ───────────────────────────────────────────────────────────────────────────────
$gl = if ($Renderer -eq 'opengl') { 'ON' } else { 'OFF' }
$vk = if ($Renderer -eq 'vulkan') { 'ON' } else { 'OFF' }

# CMake 4 refuses projects that declare a minimum below 3.5, as some vendored libraries do;
# this lets them configure with the 3.5 policies. Projects asking for 3.5 or newer are unaffected.
$env:CMAKE_POLICY_VERSION_MINIMUM = '3.5'

# Every -D argument is quoted: PowerShell splits an unquoted argument that starts with '-' at
# its first '.', which turned the toolchain path into ".../custom-toolchain" and ".cmake".
# vcpkg downloads its tools and sources during configure; a dropped download fails it, so
# retry from a clean build directory, as the GitHub workflows do.
for ($attempt = 1; $attempt -le 3; $attempt++) {
    Write-Host "CMake configure attempt $attempt of 3"
    cmake -B $BuildDir -G Ninja `
        "-DCMAKE_MAKE_PROGRAM=$ninja" `
        "-DCMAKE_BUILD_TYPE=Release" `
        "-DCMAKE_TOOLCHAIN_FILE=dependencies/maplibre-native/platform/windows/custom-toolchain.cmake" `
        "-DMLN_WITH_OPENGL=$gl" `
        "-DMLN_WITH_EGL=OFF" `
        "-DMLN_WITH_VULKAN=$vk" `
        "-DCMAKE_CXX_FLAGS=/wd4267"
    if ($LASTEXITCODE -eq 0) { break }
    if ($attempt -eq 3) { exit $LASTEXITCODE }
    Write-Host "Configure failed (exit $LASTEXITCODE); cleaning and retrying"
    Remove-Item -Recurse -Force $BuildDir -ErrorAction SilentlyContinue
}

# ── Build ───────────────────────────────────────────────────────────────────────────────────
cmake --build $BuildDir --target mln-cabi --config Release --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# ── Collect ─────────────────────────────────────────────────────────────────────────────────
$out = "native-out/win-$Arch-$Renderer"
New-Item -ItemType Directory -Force $out | Out-Null
$dll = Get-ChildItem $BuildDir -Recurse -Filter mln-cabi.dll | Select-Object -First 1
if (-not $dll) { throw "mln-cabi.dll not found under $BuildDir" }
Copy-Item $dll.FullName $out
# The .pdb, where the build makes one, lets a crash dump from a sample be symbolicated.
Get-ChildItem $BuildDir -Recurse -Filter mln-cabi.pdb | Select-Object -First 1 | Copy-Item -Destination $out
Get-ChildItem $out | ForEach-Object { '{0}  {1:N1} MiB' -f $_.FullName, ($_.Length / 1MB) }
