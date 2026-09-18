param(
    [string]$Version = "",
    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$VersionPropsPath = Join-Path $RepoRoot "Directory.Build.props"
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$versionProps = Get-Content -LiteralPath $VersionPropsPath
    $Version = [string]$versionProps.Project.PropertyGroup.Version
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Could not read the product version from $VersionPropsPath"
}
$ConfiguredVersion = [string]([xml](Get-Content -LiteralPath $VersionPropsPath)).Project.PropertyGroup.Version
$RequestedVersion = $Version.TrimStart("v")
if ($RequestedVersion -ne $ConfiguredVersion) {
    throw "Requested version $RequestedVersion does not match Directory.Build.props ($ConfiguredVersion). Run eng/Set-Version.ps1 first."
}
$repoFullPath = [System.IO.Path]::GetFullPath([string]$RepoRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$ReleaseRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $RepoRoot "artifacts\release"
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputRoot))
}
$repoPrefix = $repoFullPath + [System.IO.Path]::DirectorySeparatorChar
if (-not ([System.IO.Path]::GetFullPath($ReleaseRoot)).StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release output must remain inside the repository: $ReleaseRoot"
}
$VersionTag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
$VersionValue = $Version.TrimStart("v")
$VersionCore = ($VersionValue -split "-", 2)[0]
$ParsedVersion = [Version]$VersionCore
$PatchVersion = if ($ParsedVersion.Build -ge 0) { $ParsedVersion.Build } else { 0 }
$AssemblyVersion = "{0}.{1}.{2}.0" -f $ParsedVersion.Major, $ParsedVersion.Minor, $PatchVersion

$AppProject = Join-Path $RepoRoot "HaloPixelToolBox\HaloPixelToolBox\HaloPixelToolBox.csproj"
$UninstallerProject = Join-Path $RepoRoot "packaging\HaloPixelToolBox.Uninstaller\HaloPixelToolBox.Uninstaller.csproj"
$InstallerProject = Join-Path $RepoRoot "packaging\HaloPixelToolBox.Installer\HaloPixelToolBox.Installer.csproj"
$PackageProject = Join-Path $RepoRoot "packaging\HaloPixelToolBox.Installer.Package\HaloPixelToolBox.Installer.Package.csproj"
$LicenseFile = Join-Path $RepoRoot "LICENSE.txt"
$InstallerSourceZip = Join-Path $RepoRoot "packaging\HaloPixelToolBox.Installer\Resources\Resource\Source.zip"
$PackageSourceZip = Join-Path $RepoRoot "packaging\HaloPixelToolBox.Installer.Package\Source.zip"

$VersionReleaseRoot = Join-Path $ReleaseRoot $VersionTag
$AppPublishDir = Join-Path $VersionReleaseRoot "HaloPixelToolBox-$VersionTag-$Runtime"
$UninstallerPublishDir = Join-Path $VersionReleaseRoot "HaloPixelToolBox.Uninstaller-$VersionTag-$Runtime"
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
    $parentPrefix = $parentFullPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $childFullPath.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
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

function Remove-FileIfExists {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedParent
    )

    Assert-ChildPath -Parent $ExpectedParent -Child $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
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

function Test-ZipEntry {
    param(
        [Parameter(Mandatory = $true)][string]$ZipPath,
        [Parameter(Mandatory = $true)][string]$EntryPath
    )

    $normalizedEntryPath = $EntryPath.Replace("\", "/")
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        return $null -ne ($archive.Entries | Where-Object {
            $_.FullName.Replace("\", "/") -eq $normalizedEntryPath
        } | Select-Object -First 1)
    }
    finally {
        $archive.Dispose()
    }
}

$VersionProperties = @(
    "-p:Version=$VersionValue",
    "-p:AssemblyVersion=$AssemblyVersion",
    "-p:FileVersion=$AssemblyVersion",
    "-p:InformationalVersion=$VersionValue",
    "-p:PackageVersion=$VersionValue"
)

try {
    New-Item -ItemType Directory -Force -Path $VersionReleaseRoot | Out-Null
    Remove-DirectoryIfExists -Path $AppPublishDir -ExpectedParent $VersionReleaseRoot
    Remove-DirectoryIfExists -Path $UninstallerPublishDir -ExpectedParent $VersionReleaseRoot
    Remove-DirectoryIfExists -Path $InstallerPublishDir -ExpectedParent $VersionReleaseRoot
    Remove-DirectoryIfExists -Path $PackagePublishDir -ExpectedParent $VersionReleaseRoot

    Write-Host "Publishing HaloPixelToolBox $VersionTag for $Runtime..."
    Invoke-DotNet publish $AppProject "-c" $Configuration "-p:Platform=$Platform" "-p:PublishProfile=" "-r" $Runtime "--self-contained" "false" "-o" $AppPublishDir @VersionProperties
    Copy-Item -LiteralPath $LicenseFile -Destination (Join-Path $AppPublishDir "LICENSE.txt") -Force

    Write-Host "Creating portable application: $PortableZip"
    New-ZipFromDirectory -SourceDirectory $AppPublishDir -DestinationPath $PortableZip
    if (-not (Test-ZipEntry -ZipPath $PortableZip -EntryPath "HaloPixelToolBox.exe")) {
        throw "Portable archive does not contain HaloPixelToolBox.exe"
    }
    if (Test-ZipEntry -ZipPath $PortableZip -EntryPath "Uninstaller/Uninstall.exe") {
        throw "Portable archive must not contain the installed-app uninstaller"
    }

    Write-Host "Publishing uninstaller..."
    Invoke-DotNet publish $UninstallerProject "-c" $Configuration "-r" $Runtime "--self-contained" "false" "-p:PublishSingleFile=true" "-o" $UninstallerPublishDir @VersionProperties
    $uninstallerExe = Join-Path $UninstallerPublishDir "Uninstall.exe"
    if (-not (Test-Path -LiteralPath $uninstallerExe)) {
        throw "Could not find Uninstall.exe in $UninstallerPublishDir"
    }
    $installedUninstallerDirectory = Join-Path $AppPublishDir "Uninstaller"
    New-Item -ItemType Directory -Force -Path $installedUninstallerDirectory | Out-Null
    Copy-Item -Path (Join-Path $UninstallerPublishDir "*") -Destination $installedUninstallerDirectory -Recurse -Force

    Write-Host "Creating embedded app payload: $InstallerSourceZip"
    New-ZipFromDirectory -SourceDirectory $AppPublishDir -DestinationPath $InstallerSourceZip
    if (-not (Test-ZipEntry -ZipPath $InstallerSourceZip -EntryPath "HaloPixelToolBox.exe")) {
        throw "Installer payload does not contain HaloPixelToolBox.exe"
    }
    if (-not (Test-ZipEntry -ZipPath $InstallerSourceZip -EntryPath "Uninstaller/Uninstall.exe")) {
        throw "Installer payload does not contain Uninstaller/Uninstall.exe"
    }

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
}
finally {
    Remove-DirectoryIfExists -Path $AppPublishDir -ExpectedParent $VersionReleaseRoot
    Remove-DirectoryIfExists -Path $UninstallerPublishDir -ExpectedParent $VersionReleaseRoot
    Remove-DirectoryIfExists -Path $InstallerPublishDir -ExpectedParent $VersionReleaseRoot
    Remove-DirectoryIfExists -Path $PackagePublishDir -ExpectedParent $VersionReleaseRoot
    Remove-FileIfExists -Path $InstallerSourceZip -ExpectedParent $RepoRoot
    Remove-FileIfExists -Path $PackageSourceZip -ExpectedParent $RepoRoot
}
