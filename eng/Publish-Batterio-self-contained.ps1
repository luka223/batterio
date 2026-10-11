[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "publish-self-contained",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$ExecutableName = "Batterio.exe"
)

# Builds the verified multi-file bundle with Publish-Batterio.ps1, then appends it as a zip payload
# to the NativeAOT launcher. The resulting single executable unpacks the bundle to
# %LOCALAPPDATA%\Batterio\Runtime\<payload id> on first start and runs it from there.
# Trailer layout (read by src\Batterio.Startup\EmbeddedPayload.cs):
#   [launcher image][zip payload][payload SHA-256 (32 bytes)][payload length (int64 LE)]["BATTERIO"]

$ErrorActionPreference = "Stop"
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$output = [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must remain inside the repository."
}
if ($output -eq $repository) {
    throw "OutputDirectory must not be the repository root."
}
if ([IO.Path]::GetFileName($ExecutableName) -ne $ExecutableName -or
    -not $ExecutableName.EndsWith(".exe", [StringComparison]::OrdinalIgnoreCase)) {
    throw "ExecutableName must be a plain .exe file name."
}

New-Item -ItemType Directory -Path (Join-Path $repository "artifacts") -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
$stageName = "artifacts\batterio-single-file-stage-" + [guid]::NewGuid().ToString("N")
$stageRoot = Join-Path $repository $stageName
$folderStage = Join-Path $stageRoot "folder"
$payloadPath = Join-Path $stageRoot "payload.zip"
$bundleStage = Join-Path $stageRoot "bundle"
$bundlePath = Join-Path $bundleStage $ExecutableName

try {
    New-Item -ItemType Directory -Path $stageRoot, $bundleStage | Out-Null

    Push-Location $repository
    try {
        & (Join-Path $PSScriptRoot "Publish-Batterio.ps1") `
            -Configuration $Configuration `
            -OutputDirectory (Join-Path $stageName "folder") `
            -RuntimeIdentifier $RuntimeIdentifier
    }
    finally {
        Pop-Location
    }

    $launcherPath = Join-Path $folderStage "Batterio.App.exe"
    if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
        throw "The folder publish did not produce Batterio.App.exe."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $folderStage "Batterio.Runtime.exe") -PathType Leaf)) {
        throw "The folder publish did not produce Batterio.Runtime.exe."
    }

    # Symbols are not needed to run the app and would only grow the executable.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $compressionLevel = if ([Enum]::GetNames([IO.Compression.CompressionLevel]) -contains "SmallestSize") {
        [IO.Compression.CompressionLevel]"SmallestSize"
    } else {
        [IO.Compression.CompressionLevel]::Optimal
    }
    $folderPrefix = $folderStage.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $payloadFiles = Get-ChildItem -LiteralPath $folderStage -Recurse -File |
        Where-Object { $_.Extension -notin ".pdb", ".dbg" }
    $zipStream = [IO.File]::Create($payloadPath)
    try {
        $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $payloadFiles) {
                $entryName = $file.FullName.Substring($folderPrefix.Length).Replace([IO.Path]::DirectorySeparatorChar, "/")
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, $compressionLevel) | Out-Null
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $zipStream.Dispose()
    }

    $payloadLength = (Get-Item -LiteralPath $payloadPath).Length
    $sha256 = [Security.Cryptography.SHA256]::Create()
    $payloadRead = [IO.File]::OpenRead($payloadPath)
    try {
        [byte[]]$payloadHash = $sha256.ComputeHash($payloadRead)
    }
    finally {
        $payloadRead.Dispose()
        $sha256.Dispose()
    }

    $bundleStream = [IO.File]::Create($bundlePath)
    try {
        foreach ($part in @($launcherPath, $payloadPath)) {
            $partStream = [IO.File]::OpenRead($part)
            try {
                $partStream.CopyTo($bundleStream)
            }
            finally {
                $partStream.Dispose()
            }
        }
        $bundleStream.Write($payloadHash, 0, $payloadHash.Length)
        [byte[]]$lengthBytes = [BitConverter]::GetBytes([int64]$payloadLength)
        if (-not [BitConverter]::IsLittleEndian) {
            [Array]::Reverse($lengthBytes)
        }
        $bundleStream.Write($lengthBytes, 0, $lengthBytes.Length)
        [byte[]]$magic = [Text.Encoding]::ASCII.GetBytes("BATTERIO")
        $bundleStream.Write($magic, 0, $magic.Length)
    }
    finally {
        $bundleStream.Dispose()
    }

    $launcherInfo = Get-Item -LiteralPath $launcherPath
    $bundleInfo = Get-Item -LiteralPath $bundlePath
    if ($bundleInfo.Length -ne $launcherInfo.Length + $payloadLength + 48) {
        throw "The single-file executable has an unexpected size."
    }
    if ($bundleInfo.VersionInfo.ProductName -ne "Batterio") {
        throw "The single-file executable lost the launcher version metadata."
    }

    $backup = $null
    if (Test-Path -LiteralPath $output) {
        $outputItem = Get-Item -LiteralPath $output -Force
        if (-not $outputItem.PSIsContainer) {
            throw "OutputDirectory exists and is not a directory."
        }

        $backup = $output + ".previous-" + [guid]::NewGuid().ToString("N")
        Move-Item -LiteralPath $output -Destination $backup
    }
    try {
        Move-Item -LiteralPath $bundleStage -Destination $output
    }
    catch {
        if ($backup -and (Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $output)) {
            Move-Item -LiteralPath $backup -Destination $output
        }
        throw
    }
    if ($backup -and (Test-Path -LiteralPath $backup)) {
        Remove-Item -LiteralPath $backup -Recurse -Force
    }

    $finalPath = Join-Path $output $ExecutableName
    $payloadId = (($payloadHash[0..7] | ForEach-Object { $_.ToString("x2") }) -join "")
    Write-Host "Published single-file Batterio to $finalPath"
    Write-Host ("  bytes={0}; payloadFiles={1}; payloadBytes={2}; payloadId={3}" -f $bundleInfo.Length, @($payloadFiles).Count, $payloadLength, $payloadId)
    Write-Host ("  sha256={0}" -f (Get-FileHash -LiteralPath $finalPath -Algorithm SHA256).Hash)
    Write-Host "  First start unpacks the app to %LOCALAPPDATA%\Batterio\Runtime\$payloadId."
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
