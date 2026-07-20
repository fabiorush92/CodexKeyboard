[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$CliVersion = '0.35.2'
$CliArchiveSha256 = '831e71e91cda08071599a570fb40937c9cf0f0e8cf7711a7e24c7ee28b5406a7'
$CoreVersion = '0.0.20'
$BoardIndexUrl = 'https://raw.githubusercontent.com/DeqingSun/ch55xduino/ch55xduino/package_ch55xduino_mcs51_index.json'
$Fqbn = 'CH55xDuino:mcs51:ch552:usb_settings=user148,upload_method=usb,clock=16internal,bootloader_pin=p36'

$Root = $PSScriptRoot
$Tools = Join-Path $Root '.tools'
$CliDirectory = Join-Path $Tools "arduino-cli-$CliVersion"
$Cli = Join-Path $CliDirectory 'arduino-cli.exe'
$Config = Join-Path $Tools "arduino-cli-$CliVersion.yaml"
$Data = Join-Path $Tools "arduino-data-$CoreVersion"
$Downloads = Join-Path $Tools "arduino-downloads-$CoreVersion"
$UserDirectory = Join-Path $Tools 'arduino-user'
$BuildPath = Join-Path $Root '.build\firmware-intermediate'
$OutputDirectory = Join-Path $Root '.build\firmware'
$Sketch = Join-Path $Root 'firmware\CodexKeyboard'

function Invoke-ArduinoCli {
    & $Cli @args
    if ($LASTEXITCODE -ne 0) {
        throw "arduino-cli failed: $($args -join ' ')"
    }
}

New-Item -ItemType Directory -Force -Path $Tools | Out-Null

if (-not (Test-Path -LiteralPath $Cli)) {
    $Archive = Join-Path $Tools "arduino-cli_${CliVersion}_Windows_64bit.zip"
    $Uri = "https://github.com/arduino/arduino-cli/releases/download/v$CliVersion/arduino-cli_${CliVersion}_Windows_64bit.zip"

    Invoke-WebRequest -Uri $Uri -OutFile $Archive
    try {
        $ActualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Archive).Hash.ToLowerInvariant()
        if ($ActualHash -ne $CliArchiveSha256) {
            throw "Arduino CLI archive checksum mismatch: $ActualHash"
        }

        New-Item -ItemType Directory -Force -Path $CliDirectory | Out-Null
        Expand-Archive -LiteralPath $Archive -DestinationPath $CliDirectory -Force
    }
    finally {
        Remove-Item -LiteralPath $Archive -Force -ErrorAction SilentlyContinue
    }
}

Invoke-ArduinoCli config init --dest-file $Config --overwrite
Invoke-ArduinoCli config set directories.data $Data --config-file $Config
Invoke-ArduinoCli config set directories.downloads $Downloads --config-file $Config
Invoke-ArduinoCli config set directories.user $UserDirectory --config-file $Config

$CoreMarker = Join-Path $Data "packages\CH55xDuino\hardware\mcs51\$CoreVersion\platform.txt"
if (-not (Test-Path -LiteralPath $CoreMarker)) {
    Invoke-ArduinoCli core update-index --config-file $Config --additional-urls $BoardIndexUrl
    Invoke-ArduinoCli core install "CH55xDuino:mcs51@$CoreVersion" --config-file $Config --additional-urls $BoardIndexUrl
}

New-Item -ItemType Directory -Force -Path $BuildPath,$OutputDirectory | Out-Null
try {
    Invoke-ArduinoCli compile --clean --fqbn $Fqbn --config-file $Config --additional-urls $BoardIndexUrl --build-path $BuildPath --output-dir $OutputDirectory $Sketch
}
finally {
    Remove-Item -LiteralPath (Join-Path $Root 'nul.d') -Force -ErrorAction SilentlyContinue
}

$Artifact = Join-Path $OutputDirectory 'CodexKeyboard.ino.hex'
if (-not (Test-Path -LiteralPath $Artifact)) {
    throw "Expected firmware artifact was not produced: $Artifact"
}

$ArtifactHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Artifact).Hash.ToLowerInvariant()
Write-Host "Artifact: $Artifact"
Write-Host "SHA256:  $ArtifactHash"
