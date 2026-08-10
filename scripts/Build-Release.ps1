[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$CalibreRuntime,

    [string]$OutputRoot,

    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\release\$Version"
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) {
    throw "Output directory already exists: $OutputRoot"
}

$publishDirectory = Join-Path $OutputRoot "Kkindle-$Version-win-x64"
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

$projectPath = Join-Path $repositoryRoot 'src\Kkindle.App\Kkindle.App.csproj'
$publishArguments = @(
    'publish', $projectPath,
    '-c', 'Release',
    '-p:Platform=x64',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:WindowsAppSDKSelfContained=true',
    "-p:Version=$Version",
    '-o', $publishDirectory
)

if (-not [string]::IsNullOrWhiteSpace($CalibreRuntime)) {
    $CalibreRuntime = [System.IO.Path]::GetFullPath($CalibreRuntime)
    $converterPath = Join-Path $CalibreRuntime 'ebook-convert.exe'
    if (-not (Test-Path -LiteralPath $converterPath -PathType Leaf)) {
        throw "Invalid Calibre runtime; ebook-convert.exe was not found: $converterPath"
    }
    $publishArguments += "-p:KkindleCalibreRuntime=$CalibreRuntime"
}

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$applicationPath = Join-Path $publishDirectory 'Kkindle.exe'
$licensePath = Join-Path $publishDirectory 'LICENSE'
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw "Published output does not contain Kkindle.exe: $applicationPath"
}
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw "Published output does not contain LICENSE: $licensePath"
}

if (-not [string]::IsNullOrWhiteSpace($CalibreRuntime)) {
    $kfxPluginDirectory = Join-Path $publishDirectory 'CalibrePlugins'
    $kfxPluginPath = Join-Path $kfxPluginDirectory 'KFX Input.zip'
    $kfxPluginUrl = 'https://www.mobileread.com/forums/attachment.php?attachmentid=223438&d=1779301078'
    $kfxPluginSha256 = '6919e8cec65a92f922a14f616eedcb1b9dbb2a79dd4a261f9548e17ca208072f'
    New-Item -ItemType Directory -Path $kfxPluginDirectory -Force | Out-Null
    Invoke-WebRequest -Uri $kfxPluginUrl -OutFile $kfxPluginPath
    $actualKfxPluginHash = (Get-FileHash -LiteralPath $kfxPluginPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualKfxPluginHash -ne $kfxPluginSha256) {
        throw "KFX Input plugin checksum mismatch. Expected $kfxPluginSha256, got $actualKfxPluginHash"
    }
}

$portableArchive = Join-Path $OutputRoot "Kkindle-$Version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $compiler = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -eq $compiler) {
        $knownCompilerPaths = @(
            @(
                (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
                (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
                (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
            ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) }
        )
        if ($knownCompilerPaths.Count -eq 0) {
            throw 'ISCC.exe was not found. Install Inno Setup 6 or use -SkipInstaller.'
        }
        $compilerPath = $knownCompilerPaths[0]
    }
    else {
        $compilerPath = $compiler.Source
    }

    $installerScript = Join-Path $repositoryRoot 'installer\Kkindle.iss'
    $numericVersion = (($Version -split '-', 2)[0]) + '.0'
    & $compilerPath "/DMyAppVersion=$Version" "/DMyNumericVersion=$numericVersion" "/DSourceDir=$publishDirectory" "/DOutputDir=$OutputRoot" $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE"
    }
}

$releaseFiles = Get-ChildItem -LiteralPath $OutputRoot -File |
    Where-Object { $_.Extension -in '.exe', '.zip' } |
    Sort-Object Name

if (-not $SkipInstaller -and -not ($releaseFiles.Name -contains "Kkindle-$Version-win-x64-setup.exe")) {
    throw 'The installer was not generated.'
}

$checksumPath = Join-Path $OutputRoot 'SHA256SUMS.txt'
$checksums = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$($file.Name)"
}
[System.IO.File]::WriteAllLines($checksumPath, $checksums, [System.Text.UTF8Encoding]::new($false))

Write-Host "Release artifacts created at $OutputRoot"
Get-ChildItem -LiteralPath $OutputRoot -File | Select-Object Name, Length
