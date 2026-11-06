param(
    [Parameter(Mandatory=$true)][string]$ModDir,
    [switch]$ArchiveFlatFiles,
    [string]$ArchiveRoot
)
$ErrorActionPreference='Stop'
$here=Split-Path -Parent $MyInvocation.MyCommand.Path
$moduleRoot=[IO.Path]::GetFullPath($ModDir).TrimEnd('\','/')
$sourceRoot=[IO.Path]::GetFullPath($here).TrimEnd('\','/')
function Assert-Within([string]$File,[string]$Root) {
    $absolute=[IO.Path]::GetFullPath($File)
    if(-not $absolute.StartsWith($Root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw "Path escapes expected directory: $absolute"}
    return $absolute
}
$map=Get-Content -LiteralPath (Join-Path $here 'deployment\modules.json') -Raw | ConvertFrom-Json
$destinations=@()
foreach($entry in $map.PSObject.Properties) {
    $source=Assert-Within (Join-Path $here $entry.Name) $sourceRoot
    $destination=Assert-Within (Join-Path $moduleRoot $entry.Value) $moduleRoot
    if(-not (Test-Path -LiteralPath $source -PathType Leaf)){throw "Missing module source: $source"}
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
    if((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash){throw "Module verification failed: $destination"}
    $destinations+=$entry.Value
}
if($ArchiveFlatFiles) {
    if(-not $ArchiveRoot){$ArchiveRoot=Join-Path $here '..\..\__scratch\deploy_backups'}
    $archiveBoundary=[IO.Path]::GetFullPath($ArchiveRoot).TrimEnd('\','/')
    $archive=Assert-Within (Join-Path $archiveBoundary ('dsh-spmcp-layout-'+(Get-Date -Format 'yyyyMMddHHmmssfff'))) $archiveBoundary
    foreach($name in $map.PSObject.Properties.Name) {
        $old=Assert-Within (Join-Path $moduleRoot $name) $moduleRoot
        if(-not (Test-Path -LiteralPath $old -PathType Leaf)){continue}
        if($name -in @('mcp_server.py','ticket_scheduler.py') -and (Get-Content -LiteralPath $old -TotalCount 1) -like '# Sea Power compatible module entry*'){continue}
        $target=Assert-Within (Join-Path $archive ('flat-python\'+$name)) $archiveBoundary
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Move-Item -LiteralPath $old -Destination $target
    }
    $cache=Assert-Within (Join-Path $moduleRoot '__pycache__') $moduleRoot
    if(Test-Path -LiteralPath $cache) {
        $target=Assert-Within (Join-Path $archive 'old-cache') $archiveBoundary
        New-Item -ItemType Directory -Path $archive -Force | Out-Null
        Move-Item -LiteralPath $cache -Destination $target
    }
    foreach($backup in Get-ChildItem -LiteralPath $moduleRoot -File -Filter '*.old') {
        $source=Assert-Within $backup.FullName $moduleRoot
        $target=Assert-Within (Join-Path $archive ('bridge-backups\'+$backup.Name)) $archiveBoundary
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Move-Item -LiteralPath $source -Destination $target
    }
}
foreach($entry in @(@('launch_mcp.py','mcp_server.py'),@('launch_tickets.py','ticket_scheduler.py'))) {
    Copy-Item -LiteralPath (Join-Path $here ('deployment\'+$entry[0])) -Destination (Join-Path $moduleRoot $entry[1]) -Force
}
[pscustomobject]@{moduleFiles=$destinations.Count;compatibilityEntries=2;archiveCreated=[bool]$archive}|ConvertTo-Json -Compress
