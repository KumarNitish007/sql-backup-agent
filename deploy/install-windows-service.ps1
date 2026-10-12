# Installs SqlBackupAgent as a Windows service. Run from an elevated PowerShell in the folder containing SqlBackupAgent.exe.
# The service account needs: db_backupoperator (or sysadmin for restore drills) on the SQL Server, read/write on the
# staging folder, and write access to the destinations. SQL Server's own service account must be able to write to the staging folder.
param(
    [string]$ServiceName = "SqlBackupAgent",
    [string]$Account = "NT SERVICE\SqlBackupAgent"
)

$exe = Join-Path $PSScriptRoot "SqlBackupAgent.exe"
if (-not (Test-Path $exe)) { throw "SqlBackupAgent.exe not found next to this script." }

& $exe validate
if ($LASTEXITCODE -ne 0) { throw "Fix the configuration in appsettings.json before installing." }

New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "SQL Server Backup Agent" `
    -Description "Scheduled SQL Server backups, off-site copies, retention and restore drills." -StartupType Automatic
sc.exe config $ServiceName obj= $Account | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
Start-Service $ServiceName
Write-Host "Installed and started $ServiceName. Status page: http://localhost:5300"
