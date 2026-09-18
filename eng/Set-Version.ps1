param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$manifestPath = Join-Path $repoRoot "HaloPixelToolBox\HaloPixelToolBox\Package.appxmanifest"
$assemblyVersion = "$Version.0"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Set-SingleValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Replacement
    )

    $content = [System.IO.File]::ReadAllText($Path)
    $matches = [regex]::Matches($content, $Pattern)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one version field matching '$Pattern' in $Path, found $($matches.Count)."
    }
    $updated = [regex]::Replace($content, $Pattern, $Replacement)
    [System.IO.File]::WriteAllText($Path, $updated, $utf8NoBom)
}

Set-SingleValue -Path $propsPath -Pattern '<Version>[^<]+</Version>' -Replacement "<Version>$Version</Version>"
Set-SingleValue -Path $propsPath -Pattern '<AssemblyVersion>[^<]+</AssemblyVersion>' -Replacement "<AssemblyVersion>$assemblyVersion</AssemblyVersion>"
Set-SingleValue -Path $propsPath -Pattern '<FileVersion>[^<]+</FileVersion>' -Replacement "<FileVersion>$assemblyVersion</FileVersion>"
Set-SingleValue -Path $propsPath -Pattern '<InformationalVersion>[^<]+</InformationalVersion>' -Replacement "<InformationalVersion>$Version</InformationalVersion>"
Set-SingleValue -Path $propsPath -Pattern '<PackageVersion>[^<]+</PackageVersion>' -Replacement "<PackageVersion>$Version</PackageVersion>"
Set-SingleValue -Path $manifestPath `
    -Pattern '(<Identity\s+[^>]*\bVersion=")\d+\.\d+\.\d+\.\d+(")' `
    -Replacement ('${1}' + $assemblyVersion + '${2}')

Write-Host "HaloPixelToolBox product version is now $Version ($assemblyVersion for four-part fields)."
