<#
.SYNOPSIS
    Checks that post-build.bat produced a mod folder worth shipping.

.DESCRIPTION
    Shared by the CI and Release workflows so the two can't drift apart - the
    list of what belongs in the mod folder lives here and nowhere else.

    post-build.bat stages with a flat *.dll glob over the build output, which
    both misses things silently and picks up whatever happens to be lying in
    bin\. So this checks in both directions: everything expected is present,
    and nothing the loader owns has been bundled alongside it.

.PARAMETER StageDir
    The staged mod folder, e.g. bin\MioAP.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $StageDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $StageDir)) {
    throw "Nothing staged at $StageDir - did post-build.bat run?"
}

Get-ChildItem -Recurse -File $StageDir |
    Select-Object @{n='File';e={ $_.FullName.Replace((Get-Location).Path + '\', '') }}, Length |
    Format-Table -AutoSize

# The .json files come from Mod\, the DLLs from the build output.
$required = @(
    'Archipelago.MultiClient.Net.dll'
    'client.json'
    'MioAP.dll'
    'mod.json'
    'nacre_carcass.json'
    'Newtonsoft.Json.dll'
)

# PolyHook belongs to mio-mod-loader, which loads its own copies before we are
# initialized. A second set risks a managed binding that doesn't match the
# native it ends up bound to, so treat these as a packaging regression rather
# than harmless weight. post-build.bat already drops them; this is the backstop.
$forbidden = @(
    'asmjit.dll'
    'asmtk.dll'
    'cpolyhook2.dll'
    'PolyHook_2.dll'
    'PolyHook2.NET.dll'
    'Zydis.dll'
)

# Collect everything before failing - one problem per run is a slow way to find
# out three things are wrong.
$missing  = $required  | Where-Object { -not (Test-Path (Join-Path $StageDir $_)) }
$bundled  = $forbidden | Where-Object {      (Test-Path (Join-Path $StageDir $_)) }

$problems = @()
if ($missing) { $problems += "missing: $($missing -join ', ')" }
if ($bundled) { $problems += "should not be bundled: $($bundled -join ', ')" }

if ($problems) { throw ($problems -join ' / ') }

$count = (Get-ChildItem -Recurse -File $StageDir).Count
Write-Host "Staged mod folder looks shippable ($count files)."