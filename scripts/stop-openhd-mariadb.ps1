param(
    [int]$Port = 13306,
    [string]$Password = "openhd"
)

$mysqlRoot = "C:\Program Files\MariaDB 12.3"
$adminExe = Join-Path $mysqlRoot "bin\mysqladmin.exe"

if (Test-Path $adminExe) {
    & $adminExe --host=127.0.0.1 --port=$Port --user=root --password=$Password shutdown
    exit $LASTEXITCODE
}

Get-Process mariadbd -ErrorAction SilentlyContinue | Stop-Process -Force
