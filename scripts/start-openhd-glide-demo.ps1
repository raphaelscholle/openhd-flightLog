param(
    [string]$Distro = "",
    [string]$GlideRoot = "C:\Users\Raphael\openhd-glide",
    [int]$MavlinkUdpPort = 14550,
    [int]$PreviewWidth = 1280,
    [int]$FlowHeight = 720,
    [int]$UiWidth = 760,
    [switch]$UseInstalledGlide,
    [switch]$SkipBuild,
    [switch]$SkipDatabase
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solution = Join-Path $repoRoot "openhd-flightLog.sln"
$project = Join-Path $repoRoot "OpenHdFlightLog\OpenHdFlightLog.csproj"

function Get-WslDistros {
    $raw = & wsl.exe -l -q
    if ($LASTEXITCODE -ne 0) {
        throw "wsl.exe -l -q failed."
    }

    $raw |
        ForEach-Object { $_ -replace "`0", "" } |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

function Resolve-WslDistro {
    if (-not [string]::IsNullOrWhiteSpace($Distro)) {
        return $Distro
    }

    $distros = @(Get-WslDistros)
    $preferred = @("Ubuntu-24.04", "Ubuntu-24.04.5", "Ubuntu-22.04")
    foreach ($candidate in $preferred) {
        if ($distros -contains $candidate) {
            return $candidate
        }
    }

    $ubuntu = $distros | Where-Object { $_ -like "Ubuntu*" } | Select-Object -First 1
    if ($ubuntu) {
        return $ubuntu
    }

    throw "No Ubuntu WSL distro found. Installed distros: $($distros -join ', ')"
}

function Invoke-WslDirect {
    param(
        [string]$ResolvedDistro,
        [string[]]$Arguments
    )

    & wsl.exe -d $ResolvedDistro -- @Arguments
}

function Convert-ToWslPath {
    param([string]$WindowsPath)

    $resolved = Resolve-Path $WindowsPath
    $drive = $resolved.Path.Substring(0, 1).ToLowerInvariant()
    $path = $resolved.Path.Substring(2).Replace("\", "/")
    return "/mnt/$drive$path"
}

if (-not $SkipDatabase) {
    & (Join-Path $PSScriptRoot "start-openhd-mariadb.ps1")
}

if (-not $SkipBuild) {
    dotnet build $solution
}

$resolvedDistro = Resolve-WslDistro
$wslIp = (Invoke-WslDirect -ResolvedDistro $resolvedDistro -Arguments @("hostname", "-I") |
    Select-Object -First 1).Trim().Split(" ")[0]

if ([string]::IsNullOrWhiteSpace($wslIp)) {
    $wslIp = "127.0.0.1"
}

if ($UseInstalledGlide) {
    $glideExecutable = "openhd-glide"
}
else {
    $glideExecutable = "$(Convert-ToWslPath (Join-Path $GlideRoot "build-wsl\openhd-glide"))"
    if (-not (Test-Path (Join-Path $GlideRoot "build-wsl\openhd-glide"))) {
        throw "OpenHD Glide binary not found: $(Join-Path $GlideRoot "build-wsl\openhd-glide")"
    }
}

$glideArgs = @(
    $glideExecutable,
    "--preview-stack",
    "--preview-width", $PreviewWidth.ToString(),
    "--flow-height", $FlowHeight.ToString(),
    "--ui-width", $UiWidth.ToString(),
    "--preview-x", "60",
    "--preview-y", "40",
    "--ui-opacity", "1.0",
    "--mavlink-udp-port", $MavlinkUdpPort.ToString()
)

Write-Host ""
Write-Host "OpenHD Glide demo"
Write-Host "  Distro:        $resolvedDistro"
Write-Host "  Glide:         $glideExecutable"
Write-Host "  WSL IP:        $wslIp"
Write-Host "  MAVLink UDP:   $MavlinkUdpPort"
Write-Host ""
Write-Host "FlightLog Studio will use:"
Write-Host "  UDP Target:    $wslIp"
Write-Host "  Port:          $MavlinkUdpPort"
Write-Host ""

Start-Process -FilePath "wsl.exe" -ArgumentList (@("-d", $resolvedDistro, "--") + $glideArgs)

$env:OPENHD_GLIDE_UDP_HOST = $wslIp
$env:OPENHD_GLIDE_UDP_PORT = $MavlinkUdpPort.ToString()
dotnet run --project $project
