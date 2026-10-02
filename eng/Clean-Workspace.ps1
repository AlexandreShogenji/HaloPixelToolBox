[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Resolve-Path (Join-Path $PSScriptRoot "..")).Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$RepoPrefix = $RepoRoot + [System.IO.Path]::DirectorySeparatorChar

function Resolve-SafeRepositoryPath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $RelativePath))
    if (-not $fullPath.StartsWith($RepoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean outside the repository: $fullPath"
    }

    return $fullPath
}

$cleanTargets = @(
    "obj",
    "src\HaloPixelToolBox\bin",
    "src\HaloPixelToolBox\obj",
    "src\HaloPixelToolBox.Core\bin",
    "src\HaloPixelToolBox.Core\obj",
    "packaging\HaloPixelToolBox.Installer\bin",
    "packaging\HaloPixelToolBox.Installer\obj",
    "packaging\HaloPixelToolBox.Installer\Resources\Resource\Source.zip",
    "packaging\HaloPixelToolBox.Installer.Package\bin",
    "packaging\HaloPixelToolBox.Installer.Package\obj",
    "packaging\HaloPixelToolBox.Installer.Package\Source.zip",
    "packaging\HaloPixelToolBox.Uninstaller\bin",
    "packaging\HaloPixelToolBox.Uninstaller\obj",
    "artifacts\cache"
)

$testsRoot = Join-Path $RepoRoot "tests"
if (Test-Path -LiteralPath $testsRoot -PathType Container) {
    foreach ($testProject in Get-ChildItem -LiteralPath $testsRoot -Filter "*.csproj" -File -Recurse) {
        $projectDirectory = $testProject.DirectoryName.Substring($RepoPrefix.Length)
        $cleanTargets += Join-Path $projectDirectory "bin"
        $cleanTargets += Join-Path $projectDirectory "obj"
    }
}

foreach ($relativePath in $cleanTargets) {
    $target = Resolve-SafeRepositoryPath -RelativePath $relativePath
    if ((Test-Path -LiteralPath $target) -and $PSCmdlet.ShouldProcess($target, "Remove generated content")) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "Removed $relativePath"
    }
}
