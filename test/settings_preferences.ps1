param([string]$Root,[string]$Dll,[string]$JsonDll)
$ErrorActionPreference='Stop'
[Reflection.Assembly]::LoadFrom($JsonDll)|Out-Null
[Reflection.Assembly]::LoadFrom($Dll)|Out-Null
$checks=0
function Check($Condition,[string]$Message) {
    if(-not $Condition){throw $Message}
    $script:checks++
}
$dir=Join-Path $Root ('case-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force|Out-Null
$file=Join-Path $dir 'setting.ini'
$original="; keep this comment`r`n[AgentInspector]`r`nLanguage=cn ; language comment`r`nShotSaveFull=false`r`nGeometry=0.1,0.2,0.7,0.8`r`nFont=Microsoft YaHei`r`nFutureKey=untouched`r`n[MCP]`r`nCompactReplies=false # compact comment`r`nOtherKey=preserve`r`n[TrackMotion]`r`nSurfaceFormationNm=2`r`n"
[IO.File]::WriteAllText($file,$original,[Text.UTF8Encoding]::new($false))
$value=[SeaPowerAgentBridge.PlayerSettings]::Load($dir)
Check ($value.Language -eq 'zh') 'Chinese alias did not match existing setting'
$value.Language='en';$value.CompactReplies=$true;$value.ShotSaveFull=$true
[SeaPowerAgentBridge.PlayerSettings]::Save($dir,$value)
$saved=[IO.File]::ReadAllText($file)
foreach($line in @('; keep this comment','Geometry=0.1,0.2,0.7,0.8','Font=Microsoft YaHei','FutureKey=untouched','OtherKey=preserve','[TrackMotion]','SurfaceFormationNm=2','; language comment','# compact comment')) {
    Check ($saved.Contains($line)) ('Unrelated content lost: '+$line)
}
$bytes=[IO.File]::ReadAllBytes($file)
Check (-not($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) 'Saved a BOM'
Check ($saved.Contains("`r`n")) 'Lost CRLF convention'
$read=[SeaPowerAgentBridge.PlayerSettings]::Load($dir)
Check ($read.Language -eq 'en' -and $read.CompactReplies -and $read.ShotSaveFull) 'Saved values did not load'
Check (([SeaPowerAgentBridge.InspectorSettings]::Load($dir)).ShotSaveFull) 'Capture consumer does not see the preference'
$cfg=[SeaPowerAgentBridge.InspectorSettings]::Load($dir)
$cfg.SplitX=.33
$errorText=''
Check ([SeaPowerAgentBridge.InspectorSettings]::Save($dir,$cfg,[ref]$errorText)) 'Existing geometry save failed'
$read=[SeaPowerAgentBridge.PlayerSettings]::Load($dir)
Check ($read.Language -eq 'en' -and $read.CompactReplies -and $read.ShotSaveFull) 'Inspector save dropped player preferences'
# Simulate the existing geometry and a foreign section changing while the UI has a draft.
[IO.File]::AppendAllText($file,"`n[ForeignPlugin]`nKeep=external edit`n",[Text.UTF8Encoding]::new($false))
$value.Language='';$value.CompactReplies=$false
[SeaPowerAgentBridge.PlayerSettings]::Save($dir,$value)
$saved=[IO.File]::ReadAllText($file)
Check ($saved.Contains('SplitX=0.33') -and $saved.Contains('Keep=external edit')) 'Saved stale whole-file snapshot over concurrent unrelated edits'
Check (([SeaPowerAgentBridge.PlayerSettings]::Load($dir)).Language -eq '') 'Auto language did not persist'
$hash=(Get-FileHash -LiteralPath $file).Hash
$lock=[IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$failed=$false
try {[SeaPowerAgentBridge.PlayerSettings]::Save($dir,$value)}catch{$failed=$true}finally{$lock.Dispose()}
Check $failed 'Locked destination unexpectedly accepted a replacement'
Check ((Get-FileHash -LiteralPath $file).Hash -eq $hash) 'Failed save damaged the original'
Check (@(Get-ChildItem -LiteralPath $dir -Filter '*.tmp').Count -eq 0) 'Failed save leaked temporary file'
$value.Language='invalid'
$failed=$false
try {[SeaPowerAgentBridge.PlayerSettings]::Save($dir,$value)}catch{$failed=$true}
Check $failed 'Invalid language silently accepted'
Check ((Get-FileHash -LiteralPath $file).Hash -eq $hash) 'Invalid value modified the original'
$value.Language='zh';$value.CompactReplies=$true
$merged=[SeaPowerAgentBridge.PlayerSettings]::Merge("[MCP]`nCompactReplies=false`ncompactreplies=false`nUnrelated=yes`n",$value)
Check (($merged -split "`n"|Where-Object {$_ -match '^CompactReplies='}).Count -eq 1) 'Duplicate live managed key remains'
Check ($merged.Contains('Unrelated=yes')) 'Duplicate handling dropped foreign key'
Check (([SeaPowerAgentBridge.PlayerSettings]::Parse($merged)).CompactReplies) 'Duplicate key overrode new value'
$newDir=Join-Path $dir 'new';New-Item -ItemType Directory -Path $newDir|Out-Null
[SeaPowerAgentBridge.PlayerSettings]::Save($newDir,$value)
Check (([SeaPowerAgentBridge.PlayerSettings]::Load($newDir)).Language -eq 'zh') 'First-run save failed'
# Binding must persist in the exact legacy key read by the live console.
$value.MenuToggleKey='F9'
[SeaPowerAgentBridge.PlayerSettings]::Save($newDir,$value)
Check (([SeaPowerAgentBridge.PlayerSettings]::Load($newDir)).MenuToggleKey -eq 'F9') 'Menu key did not round-trip'
Check (([SeaPowerAgentBridge.InspectorSettings]::Load($newDir)).ToggleKey -eq 'F9') 'Legacy key consumer disagrees'
Check (([SeaPowerAgentBridge.PlayerSettings]::Parse('')).MenuToggleKey -eq 'N') 'Missing binding lost default'
foreach($case in @(@('q','Q'),@('0','Alpha0'),@('alpha7','Alpha7'),@('f12','F12'))) {
    $parsed=''
    Check ([SeaPowerAgentBridge.PlayerSettings]::TryMenuKey($case[0],[ref]$parsed) -and $parsed -eq $case[1]) ('Key normalization failed: '+$case[0])
}
foreach($bad in @('','Escape','Return','Mouse0','LeftControl','Ctrl+K','F13','F0')) {
    $parsed=''
    Check (-not [SeaPowerAgentBridge.PlayerSettings]::TryMenuKey($bad,[ref]$parsed)) ('Unsupported key accepted: '+$bad)
}
$beforeKey=(Get-FileHash -LiteralPath (Join-Path $newDir 'setting.ini')).Hash
$value.MenuToggleKey='Escape';$failed=$false
try {[SeaPowerAgentBridge.PlayerSettings]::Save($newDir,$value)}catch{$failed=$true}
Check $failed 'Unsupported key was saved'
Check ((Get-FileHash -LiteralPath (Join-Path $newDir 'setting.ini')).Hash -eq $beforeKey) 'Invalid binding modified file'
Check (-not ([SeaPowerAgentBridge.PlayerSettings]::Parse('')).MainAgentOnly) 'Main-only must default off'
Check (([SeaPowerAgentBridge.PlayerSettings]::Parse("[Tickets]`nMainAgentOnly=true ; note")).MainAgentOnly) 'Main-only inline comment not parsed'
$value.MenuToggleKey='N';$value.MainAgentOnly=$true
[SeaPowerAgentBridge.PlayerSettings]::Save($newDir,$value)
Check (([SeaPowerAgentBridge.PlayerSettings]::Load($newDir)).MainAgentOnly) 'Main-only save did not round-trip'
$cfg=[SeaPowerAgentBridge.InspectorSettings]::Load($newDir)
$errorText=''
Check ([SeaPowerAgentBridge.InspectorSettings]::Save($newDir,$cfg,[ref]$errorText)) 'Geometry consumer save failed'
Check (([SeaPowerAgentBridge.PlayerSettings]::Load($newDir)).MainAgentOnly) 'Geometry consumer dropped main-only preference'
$value.MainAgentOnly=$false
[SeaPowerAgentBridge.PlayerSettings]::Save($newDir,$value)
Check (-not ([SeaPowerAgentBridge.PlayerSettings]::Load($newDir)).MainAgentOnly) 'Main-only disable did not persist'
$report=[pscustomobject]@{checks=$checks;passed=$true;sample=$file;newSample=(Join-Path $newDir 'setting.ini')}
$report|ConvertTo-Json -Compress
