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
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$Arch -host_arch=amd64"
}
Write-Host "MSVC target: $env:VSCMD_ARG_TGT_ARCH (VS at $env:VSINSTALLDIR)"

# ── Configure ───────────────────────────────────────────────────────────────────────────────
$gl = if ($Renderer -eq 'opengl') { 'ON' } else { 'OFF' }
$vk = if ($Renderer -eq 'vulkan') { 'ON' } else { 'OFF' }

# vcpkg downloads its tools and sources during configure; a dropped download fails it, so
# retry from a clean build directory, as the GitHub workflows do.
for ($attempt = 1; $attempt -le 3; $attempt++) {
    Write-Host "CMake configure attempt $attempt of 3"
    cmake -B $BuildDir -G Ninja `
        -DCMAKE_BUILD_TYPE=Release `
        -DCMAKE_TOOLCHAIN_FILE=dependencies/maplibre-native/platform/windows/custom-toolchain.cmake `
        "-DMLN_WITH_OPENGL=$gl" `
        -DMLN_WITH_EGL=OFF `
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
