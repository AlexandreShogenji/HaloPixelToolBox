param(
    [string]$Version = "2.1.0",
    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$ReleaseRoot = Join-Path $RepoRoot "release"
$VersionTag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
$VersionValue = $Version.TrimStart("v")
$VersionCore = ($VersionValue -split "-", 2)[0]
$ParsedVersion = [Version]$VersionCore
$PatchVersion = if ($ParsedVersion.Build -ge 0) { $ParsedVersion.Build } else { 0 }
$AssemblyVersion = "{0}.{1}.{2}.0" -f $ParsedVersion.Major, $ParsedVersion.Minor, $PatchVersion

$AppProject = Join-Path $RepoRoot "HaloPixelToolBox\HaloPixelToolBox\HaloPixelToolBox.csproj"
$InstallerProject = Join-Path $RepoRoot "HaloPixelToolBox.Installer\HaloPixelToolBox.Installer.csproj"
$PackageProject = Join-Path $RepoRoot "HaloPixelToolBox.Installer.Package\HaloPixelToolBox.Installer.Package.csproj"
$InstallerSourceZip = Join-Path $RepoRoot "HaloPixelToolBox.Installer\Resources\Resource\Source.zip"
$PackageSourceZip = Join-Path $RepoRoot "HaloPixelToolBox.Installer.Package\Source.zip"

$VersionReleaseRoot = Join-Path $ReleaseRoot $VersionTag
$AppPublishDir = Join-Path $VersionReleaseRoot "HaloPixelToolBox-$VersionTag-$Runtime"
$InstallerPublishDir = Join-Path $VersionReleaseRoot "HaloPixelToolBox.Installer-$VersionTag-$Runtime"
$PackagePublishDir = Join-Path $VersionReleaseRoot "HaloPixelToolBox.Installer.Package-$VersionTag-$Runtime"
$PortableZip = Join-Path $VersionReleaseRoot "HaloPixelToolBox-$VersionTag-$Runtime.zip"
$FinalInstallerExe = Join-Path $VersionReleaseRoot "HaloPixelToolBox-$VersionTag-installer-$Runtime.exe"
$ChecksumFile = Join-Path $VersionReleaseRoot "SHA256SUMS.txt"

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFullPath = [System.IO.Path]::GetFullPath($Parent)
    $childFullPath = [System.IO.Path]::GetFullPath($Child)
    if (-not $childFullPath.StartsWith($parentFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside expected directory: $childFullPath"
    }
}

function Remove-DirectoryIfExists {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedParent
    )

    Assert-ChildPath -Parent $ExpectedParent -Child $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Invoke-DotNet {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function New-ZipFromDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory)) {
        throw "Zip source directory does not exist: $SourceDirectory"
    }

    $items = @(Get-ChildItem -LiteralPath $SourceDirectory -Force)
    if ($items.Count -eq 0) {
        throw "Zip source directory is empty: $SourceDirectory"
    }

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    Compress-Archive -Path (Join-Path $SourceDirectory "*") -DestinationPath $DestinationPath -CompressionLevel Optimal
}

$VersionProperties = @(
    "-p:Version=$VersionValue",
    "-p:AssemblyVersion=$AssemblyVersion",
    "-p:FileVersion=$AssemblyVersion",
    "-p:InformationalVersion=$VersionValue",
    "-p:PackageVersion=$VersionValue"
)

New-Item -ItemType Directory -Force -Path $VersionReleaseRoot | Out-Null
Remove-DirectoryIfExists -Path $AppPublishDir -ExpectedParent $VersionReleaseRoot
Remove-DirectoryIfExists -Path $InstallerPublishDir -ExpectedParent $VersionReleaseRoot
Remove-DirectoryIfExists -Path $PackagePublishDir -ExpectedParent $VersionReleaseRoot

Write-Host "Publishing HaloPixelToolBox $VersionTag for $Runtime..."
Invoke-DotNet publish $AppProject "-c" $Configuration "-p:Platform=$Platform" "-p:PublishProfile=" "-r" $Runtime "--self-contained" "false" "-o" $AppPublishDir @VersionProperties

Write-Host "Creating embedded app payload: $InstallerSourceZip"
New-ZipFromDirectory -SourceDirectory $AppPublishDir -DestinationPath $InstallerSourceZip
Copy-Item -LiteralPath $InstallerSourceZip -Destination $PortableZip -Force

Write-Host "Publishing installer..."
Invoke-DotNet publish $InstallerProject "-c" $Configuration "-r" $Runtime "--self-contained" "true" "-o" $InstallerPublishDir @VersionProperties

Write-Host "Creating embedded installer payload: $PackageSourceZip"
New-ZipFromDirectory -SourceDirectory $InstallerPublishDir -DestinationPath $PackageSourceZip

Write-Host "Publishing installer package..."
Invoke-DotNet publish $PackageProject "-c" $Configuration "-r" $Runtime "--self-contained" "true" "-p:PublishAot=false" "-p:PublishSingleFile=true" "-p:EnableCompressionInSingleFile=true" "-o" $PackagePublishDir @VersionProperties

$packageExe = Join-Path $PackagePublishDir "HaloPixelToolBox.Installer.Package.exe"
if (-not (Test-Path -LiteralPath $packageExe)) {
    $packageExe = (Get-ChildItem -LiteralPath $PackagePublishDir -Filter "*.exe" | Select-Object -First 1).FullName
}
if (-not $packageExe) {
    throw "Could not find installer package executable in $PackagePublishDir"
}

Copy-Item -LiteralPath $packageExe -Destination $FinalInstallerExe -Force

$artifacts = @($PortableZip, $FinalInstallerExe) | Where-Object { Test-Path -LiteralPath $_ }
$hashLines = foreach ($artifact in $artifacts) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $artifact
    "$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $artifact)"
}
$hashLines | Set-Content -LiteralPath $ChecksumFile -Encoding UTF8

Write-Host "Release artifacts:"
Get-Item -LiteralPath $artifacts | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
Write-Host "Checksums written to $ChecksumFile"
