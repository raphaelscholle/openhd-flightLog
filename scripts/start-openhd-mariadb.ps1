param(
    [int]$Port = 13306,
    [string]$Password = "openhd",
    [string]$Database = "openhd_flightlog"
)

$ErrorActionPreference = "Stop"

$mysqlRoot = "C:\Program Files\MariaDB 12.3"
$serverExe = Join-Path $mysqlRoot "bin\mariadbd.exe"
$clientExe = Join-Path $mysqlRoot "bin\mysql.exe"
$dataDir = Join-Path $mysqlRoot "data"
$logDir = Join-Path $PSScriptRoot "..\logs"

if (-not (Test-Path $serverExe)) {
    throw "MariaDB server not found at $serverExe. Install it with: winget install --id MariaDB.Server"
}

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$probe = Test-NetConnection 127.0.0.1 -Port $Port -WarningAction SilentlyContinue
if (-not $probe.TcpTestSucceeded) {
    $outLog = Join-Path $logDir "mariadb-$Port.out.log"
    $errLog = Join-Path $logDir "mariadb-$Port.err.log"
    $arguments = "--console --datadir=`"$dataDir`" --port=$Port --bind-address=127.0.0.1"
    Start-Process -FilePath $serverExe `
        -ArgumentList $arguments `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errLog `
        -WindowStyle Hidden | Out-Null

    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $probe = Test-NetConnection 127.0.0.1 -Port $Port -WarningAction SilentlyContinue
    } while (-not $probe.TcpTestSucceeded -and (Get-Date) -lt $deadline)
}

if (-not $probe.TcpTestSucceeded) {
    throw "MariaDB did not start on 127.0.0.1:$Port. See logs\mariadb-$Port.err.log."
}

& $clientExe --host=127.0.0.1 --port=$Port --user=root --password=$Password --execute="CREATE DATABASE IF NOT EXISTS $Database CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;" 2>$null
if ($LASTEXITCODE -ne 0) {
    & $clientExe --host=127.0.0.1 --port=$Port --user=root --execute="ALTER USER 'root'@'localhost' IDENTIFIED BY '$Password'; CREATE DATABASE IF NOT EXISTS $Database CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; FLUSH PRIVILEGES;"
}

Write-Host "MariaDB ready on 127.0.0.1:$Port, database $Database."
