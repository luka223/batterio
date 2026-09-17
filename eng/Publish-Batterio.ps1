[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "publish",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$output = [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must remain inside the repository."
}
New-Item -ItemType Directory -Path (Join-Path $repository "artifacts") -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
$stageRoot = Join-Path $repository ("artifacts\batterio-publish-stage-" + [guid]::NewGuid().ToString("N"))
$runtimeStage = Join-Path $stageRoot "runtime"
$launcherStage = Join-Path $stageRoot "launcher"
$bundleStage = Join-Path $stageRoot "bundle"

if ($output -eq $repository) {
    throw "OutputDirectory must not be the repository root."
}

$runtimeProjectPath = Join-Path $repository "src\Batterio.App\Batterio.App.csproj"
[xml]$runtimeProject = Get-Content -LiteralPath $runtimeProjectPath -Raw
$singleFileProperties = @(
    "PublishSingleFile",
    "IncludeAllContentForSelfExtract",
    "IncludeNativeLibrariesForSelfExtract",
    "EnableCompressionInSingleFile"
)
foreach ($propertyName in $singleFileProperties) {
    $configuredValues = $runtimeProject.SelectNodes("//*[local-name()='PropertyGroup']/*[local-name()='$propertyName']")
    foreach ($configuredValue in $configuredValues) {
        if ($configuredValue.InnerText.Trim().Equals("true", [StringComparison]::OrdinalIgnoreCase)) {
            throw "The runtime project still enables single-file publishing or extraction through '$propertyName'."
        }
    }
}

New-Item -ItemType Directory -Path $runtimeStage, $launcherStage, $bundleStage | Out-Null

try {
function Invoke-Publish {
    param(
        [string]$Project,
        [string]$Destination,
        [switch]$NativeAot
    )

    $arguments = @(
        "publish", $Project,
        "--configuration", $Configuration,
        "--runtime", $RuntimeIdentifier,
        "--self-contained", "true",
        "--no-restore",
        "--output", $Destination
    )
    if (-not $NativeAot) {
        $arguments += "-p:PublishSingleFile=false"
    } else {
        $arguments += "-p:PublishAot=true"
        $arguments += "-p:StripSymbols=false"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for '$Project' with exit code $LASTEXITCODE."
    }
}

& dotnet restore Batterio.sln --configfile NuGet.Config --runtime $RuntimeIdentifier
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

Invoke-Publish "src\Batterio.App\Batterio.App.csproj" $runtimeStage
Invoke-Publish "src\Batterio.Startup\Batterio.Startup.csproj" $launcherStage -NativeAot

Get-ChildItem -LiteralPath $runtimeStage -Force | Copy-Item -Destination $bundleStage -Recurse -Force

$launcherExecutable = Join-Path $launcherStage "Batterio.App.exe"
$runtimeExecutable = Join-Path $bundleStage "Batterio.Runtime.exe"
$runtimeAssembly = Join-Path $bundleStage "Batterio.Runtime.dll"
if (-not (Test-Path -LiteralPath $launcherExecutable -PathType Leaf)) {
    throw "The NativeAOT publish did not produce Batterio.App.exe."
}
if (-not (Test-Path -LiteralPath $runtimeExecutable -PathType Leaf)) {
    throw "The runtime publish did not produce Batterio.Runtime.exe."
}
if (-not (Test-Path -LiteralPath $runtimeAssembly -PathType Leaf)) {
    throw "The runtime publish did not produce Batterio.Runtime.dll."
}
foreach ($executable in @($launcherExecutable, $runtimeExecutable, $runtimeAssembly)) {
    if ((Get-Item -LiteralPath $executable).Length -le 0) {
        throw "The staged runtime component '$([IO.Path]::GetFileName($executable))' is empty."
    }
}

$launcherSymbols = Get-ChildItem -LiteralPath $launcherStage -File |
    Where-Object { $_.Extension -in ".pdb", ".dbg" }
Copy-Item -LiteralPath $launcherExecutable -Destination $bundleStage -Force
foreach ($symbol in $launcherSymbols) {
    Copy-Item -LiteralPath $symbol.FullName -Destination $bundleStage -Force
}

$launcherPath = Join-Path $bundleStage "Batterio.App.exe"
$iconPath = Join-Path $bundleStage "batterio.ico"
if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "The staged bundle is missing Batterio.App.exe."
}
if ([IO.Path]::GetFileName($launcherPath).Equals([IO.Path]::GetFileName($runtimeExecutable), [StringComparison]::OrdinalIgnoreCase)) {
    throw "The launcher and runtime executable names collide."
}
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "The staged bundle is missing batterio.ico."
}

$requiredWindowsAppSdkFiles = @(
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.WindowsAppRuntime.Bootstrap.dll",
    "Microsoft.ui.xaml.dll"
)
foreach ($requiredFile in $requiredWindowsAppSdkFiles) {
    $found = Get-ChildItem -LiteralPath $bundleStage -Recurse -File |
        Where-Object { $_.Name.Equals($requiredFile, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if (-not $found) {
        throw "The staged bundle is missing required Windows App SDK file '$requiredFile'."
    }
}

$unexpectedRuntimeConfig = Join-Path $bundleStage "Batterio.App.runtimeconfig.json"
if (Test-Path -LiteralPath $unexpectedRuntimeConfig -PathType Leaf) {
    throw "The staged bundle contains Batterio.App.runtimeconfig.json; the public launcher must remain NativeAOT."
}

function Read-UInt16 {
    param([byte[]]$Bytes, [int]$Offset)
    [BitConverter]::ToUInt16($Bytes, $Offset)
}

function Read-UInt32 {
    param([byte[]]$Bytes, [int]$Offset)
    [BitConverter]::ToUInt32($Bytes, $Offset)
}

function Convert-RvaToFileOffset {
    param([byte[]]$Bytes, [uint32]$Rva, [int]$SectionTable, [int]$SectionCount)
    for ($index = 0; $index -lt $SectionCount; $index++) {
        $section = $SectionTable + ($index * 40)
        $virtualSize = Read-UInt32 $Bytes ($section + 8)
        $virtualAddress = Read-UInt32 $Bytes ($section + 12)
        $rawSize = Read-UInt32 $Bytes ($section + 16)
        $rawPointer = Read-UInt32 $Bytes ($section + 20)
        $span = [Math]::Max($virtualSize, $rawSize)
        if ($Rva -ge $virtualAddress -and ($Rva - $virtualAddress) -lt $span) {
            $delta = [uint64]$Rva - [uint64]$virtualAddress
            if ($delta -ge $rawSize) {
                return -1
            }
            return [int]([uint64]$rawPointer + $delta)
        }
    }
    return -1
}

function Get-ExecutableImports {
    param([string]$Path)
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 256 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 2) -ne "MZ") {
        throw "'$([IO.Path]::GetFileName($Path))' is not a valid Windows executable."
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 24 -ge $bytes.Length -or
        [Text.Encoding]::ASCII.GetString($bytes, $peOffset, 4) -ne "PE`0`0") {
        throw "'$([IO.Path]::GetFileName($Path))' has an invalid PE header."
    }

    $coff = $peOffset + 4
    $sectionCount = Read-UInt16 $bytes ($coff + 2)
    $optionalSize = Read-UInt16 $bytes ($coff + 16)
    $optional = $coff + 20
    $magic = Read-UInt16 $bytes $optional
    $directoryOffset = if ($magic -eq 0x20b) { 112 } elseif ($magic -eq 0x10b) { 96 } else { throw "'$([IO.Path]::GetFileName($Path))' has an unsupported PE format." }
    if ($optional + $directoryOffset + 16 -gt $bytes.Length) {
        throw "'$([IO.Path]::GetFileName($Path))' has a truncated PE optional header."
    }

    $importRva = Read-UInt32 $bytes ($optional + $directoryOffset + 8)
    if ($importRva -eq 0) {
        return @()
    }

    $sectionTable = $optional + $optionalSize
    $importOffset = Convert-RvaToFileOffset $bytes $importRva $sectionTable $sectionCount
    if ($importOffset -lt 0) {
        throw "Could not locate the import table in '$([IO.Path]::GetFileName($Path))'."
    }

    $imports = [Collections.Generic.List[string]]::new()
    for ($descriptorIndex = 0; $descriptorIndex -lt 4096; $descriptorIndex++) {
        $descriptor = $importOffset + ($descriptorIndex * 20)
        if ($descriptor + 20 -gt $bytes.Length) {
            throw "The import table in '$([IO.Path]::GetFileName($Path))' is truncated."
        }
        $nameRva = Read-UInt32 $bytes ($descriptor + 12)
        if ($nameRva -eq 0) {
            break
        }
        $nameOffset = Convert-RvaToFileOffset $bytes $nameRva $sectionTable $sectionCount
        if ($nameOffset -lt 0) {
            throw "An import name in '$([IO.Path]::GetFileName($Path))' points outside the file."
        }
        $end = $nameOffset
        while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) { $end++ }
        if ($end -eq $bytes.Length) {
            throw "An import name in '$([IO.Path]::GetFileName($Path))' is not terminated."
        }
        $imports.Add([Text.Encoding]::ASCII.GetString($bytes, $nameOffset, $end - $nameOffset))
    }
    return $imports.ToArray()
}

$launcherImports = Get-ExecutableImports $launcherPath
$forbiddenLauncherImports = $launcherImports | Where-Object {
    $_ -match '^(hostfxr|hostpolicy|coreclr|Microsoft\.UI\.Xaml|Microsoft\.WindowsAppRuntime(\.Bootstrap)?|Microsoft\.Maui|Microsoft\.NETCore\.App).*\.dll$'
}
if ($forbiddenLauncherImports) {
    throw "The NativeAOT launcher imports runtime/UI dependencies: $($forbiddenLauncherImports -join ', ')."
}

$launcherInfo = Get-Item -LiteralPath $launcherPath
$runtimeInfo = Get-Item -LiteralPath $runtimeExecutable
$runtimeAssemblyInfo = Get-Item -LiteralPath $runtimeAssembly
$launcherVersionInfo = $launcherInfo.VersionInfo
if ($launcherVersionInfo.FileDescription -ne "Batterio" -or $launcherVersionInfo.ProductName -ne "Batterio") {
    throw "The launcher version metadata must identify the application as Batterio."
}
$launcherProductVersion = $launcherVersionInfo.ProductVersion
$runtimeProductVersion = $runtimeInfo.VersionInfo.ProductVersion
$runtimeAssemblyProductVersion = $runtimeAssemblyInfo.VersionInfo.ProductVersion
$launcherHash = (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash
$runtimeHash = (Get-FileHash -LiteralPath $runtimeExecutable -Algorithm SHA256).Hash
$runtimeAssemblyHash = (Get-FileHash -LiteralPath $runtimeAssembly -Algorithm SHA256).Hash

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
Remove-Item -LiteralPath $stageRoot -Recurse -Force

Write-Host "Published complete Batterio folder to $output"
Write-Host "Verification summary:"
Write-Host ("  launcher: productVersion={0}; bytes={1}; sha256={2}" -f $launcherProductVersion, $launcherInfo.Length, $launcherHash)
Write-Host ("  runtime:  productVersion={0}; bytes={1}; sha256={2}" -f $runtimeProductVersion, $runtimeInfo.Length, $runtimeHash)
Write-Host ("  runtime module: productVersion={0}; bytes={1}; sha256={2}" -f $runtimeAssemblyProductVersion, $runtimeAssemblyInfo.Length, $runtimeAssemblyHash)
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
