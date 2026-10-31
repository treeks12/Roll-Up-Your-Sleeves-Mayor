param(
    [ValidateSet('Inspect','Prepare','Install','Remove','AssertLaunch','SelfTest','IoSmokeTest','Library')]
    [string]$Mode = 'Inspect',
    [switch]$NoWorkshop
)
$ErrorActionPreference = 'Stop'
$Sc4EvolutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# No downloads, provider copies, preference edits, game launch, or process termination.
# Install/Remove are explicit offline actions. Default Inspect and AssertLaunch only read.
$script:HarmonyCleanup = $Mode -eq 'Remove'
$script:HarmonyLeaseOwner = 'SC4Evolution/Harmony-TestLease/v1'
$script:HarmonyManifestHash = 'd520a5b32ab16e6675cfdf9d5067d21328ab39eec71bab845f5dc3984f928f7b'
$script:HarmonyManifestPath = Join-Path $Sc4EvolutionRoot 'tools/CitiesHarmony/manifest.json'
$script:HarmonyProvider = [IO.Path]::GetFullPath((Join-Path $Sc4EvolutionRoot 'tools/CitiesHarmony/provider/CitiesHarmony'))
$script:HarmonyMods = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Colossal Order/Cities_Skylines/Addons/Mods'
$script:HarmonyLink = [IO.Path]::GetFullPath((Join-Path $script:HarmonyMods 'SC4EvolutionHarmonyTestLease'))
$script:HarmonyReceipt = Join-Path $Sc4EvolutionRoot '.cache/harmony-test-lease/receipt.json'

# Check every directory component from drive root down. No ancestor redirect is
# permitted; the one intentional reparse leaf is checked by Assert-HarmonyLink.
# Internal resolver injection is only for pure fixtures, never a CLI parameter.
function Assert-HarmonyNoReparseAncestors([string]$Directory,[bool]$AllowMissingTail=$false,[scriptblock]$Resolver=$null) {
    if(-not [IO.Path]::IsPathRooted($Directory)){throw 'A rooted directory is required.'}
    $components=[Collections.Generic.Stack[string]]::new()
    $current=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Directory))
    while($current){$components.Push($current);$parent=[IO.Directory]::GetParent($current);$current=if($null -eq $parent){$null}else{$parent.FullName}}
    $missing=$false
    while($components.Count -gt 0){
        $component=$components.Pop()
        if($null -ne $Resolver){$item=& $Resolver $component}
        else{try{$item=Get-Item -LiteralPath $component -Force -ErrorAction Stop}catch [System.Management.Automation.ItemNotFoundException]{$item=$null}}
        if($null -eq $item){if(-not $AllowMissingTail){throw "Required directory missing: $component"};$missing=$true;continue}
        if($missing -or -not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Unsafe non-directory/reparse ancestor: $component"}
    }
}
function Assert-HarmonyLocations {
    Assert-HarmonyNoReparseAncestors $Sc4EvolutionRoot
    Assert-HarmonyNoReparseAncestors $script:HarmonyProvider ([bool]$script:HarmonyCleanup)
    Assert-HarmonyNoReparseAncestors (Split-Path -Parent $script:HarmonyReceipt) $true
    Assert-HarmonyNoReparseAncestors $script:HarmonyMods ([bool]$script:HarmonyCleanup)
    # Launch uses Enter-Project and cs1-run. Validate its cache ancestors BEFORE
    # that script can create any directories or Steam App ID/launch files.
    if(-not $script:HarmonyCleanup){
    foreach($relative in @('.cache/cs1-run','.cache/dotnet-home','.cache/nuget/packages','.cache/nuget/http','.cache/nuget/plugins','.cache/dotnet-bundles','.cache/tmp')){
        Assert-HarmonyNoReparseAncestors (Join-Path $Sc4EvolutionRoot $relative) $true
    }
    foreach($leaf in @('steam_appid.txt','launch.json')){Assert-HarmonyRegularFileOrMissing (Join-Path $Sc4EvolutionRoot ('.cache/cs1-run/'+$leaf)) | Out-Null}
    }
}
function Assert-HarmonyRegularFileOrMissing([string]$Path,[scriptblock]$Resolver=$null) {
    if($null -ne $Resolver){$item=& $Resolver $Path}
    else{try{$item=Get-Item -LiteralPath $Path -Force -ErrorAction Stop}catch [System.Management.Automation.ItemNotFoundException]{$item=$null}}
    if($null -ne $item -and ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))){throw "Unsafe file leaf (including dangling links): $Path"}
    return $item
}
function Assert-HarmonyRemoveState($Metadata,$Receipt) {
    Assert-HarmonyReceipt $Receipt
    if($Metadata.Exists){Assert-HarmonyLink $Metadata $Receipt}
    # Intentionally no binary hash or other-provider test: reclaiming this owned
    # reparse entry is safe even after its provider files or neighbors changed.
}
function Assert-HarmonyOffline {
    if (@(Get-Process -Name 'Cities','Cities_Skylines' -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Cities is running; refuse lease installation/removal.' }
}
function Get-HarmonyHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-HarmonyPath([string]$Actual,[string]$Expected,[string]$Label) {
    if (-not [IO.Path]::IsPathRooted($Actual) -or -not [string]::Equals([IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Actual)),[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Expected)),[StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected $Label path." }
}
function Get-HarmonyProviderEvidence {
    Assert-HarmonyLocations
    if($null -eq (Assert-HarmonyRegularFileOrMissing $script:HarmonyManifestPath)){throw 'Pinned provider manifest is missing.'}
    if ((Get-HarmonyHash $script:HarmonyManifestPath) -ne $script:HarmonyManifestHash) { throw 'Pinned CitiesHarmony manifest changed; review required.' }
    $manifest = Get-Content -LiteralPath $script:HarmonyManifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or $manifest.providerRelease -ne '2.2.2-0' -or $manifest.providerFiles.Count -ne 6) { throw 'Unexpected provider manifest version/files.' }
    $actual = @(Get-ChildItem -LiteralPath $script:HarmonyProvider -Force)
    if ($actual.Count -ne $manifest.providerFiles.Count) { throw 'Provider contains extra/missing entries.' }
    foreach ($file in $manifest.providerFiles) {
        if ([IO.Path]::GetFileName([string]$file.path) -ne [string]$file.path) { throw 'Provider manifest entry must be a direct child.' }
        $path = Join-Path $script:HarmonyProvider ([string]$file.path)
        $item = Get-Item -LiteralPath $path -Force
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -ne $file.size -or (Get-HarmonyHash $path) -ne $file.sha256) { throw "Provider integrity failure: $($file.path)" }
    }
    $providerItem = Get-Item -LiteralPath $script:HarmonyProvider -Force
    if ($providerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Pinned provider source must be a real project directory.' }
    return $manifest
}
function Get-HarmonyLinkMetadata {
    Assert-HarmonyLocations
    $item = Get-Item -LiteralPath $script:HarmonyLink -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { return [pscustomobject]@{ Exists=$false; IsDirectory=$false; IsReparse=$false; LinkType=''; Target='' } }
    $target = @($item.Target)
    return [pscustomobject]@{ Exists=$true; IsDirectory=[bool]$item.PSIsContainer; IsReparse=[bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint); LinkType=[string]$item.LinkType; Target= $(if($target.Count -eq 1){[string]$target[0]}else{''}) }
}
function Get-HarmonyReceipt {
    Assert-HarmonyLocations
    $item = Assert-HarmonyRegularFileOrMissing $script:HarmonyReceipt
    if ($null -eq $item) { return $null }
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -gt 65536) { throw 'Invalid lease receipt file.' }
    Get-Content -LiteralPath $script:HarmonyReceipt -Raw | ConvertFrom-Json
}
function Assert-HarmonyReceipt($Receipt) {
    if ($null -eq $Receipt) { throw 'Lease receipt is missing; refuse adopting/deleting an unowned local provider.' }
    $leaseGuid = [guid]::Empty
    if ($Receipt.Schema -ne 1 -or $Receipt.Owner -ne $script:HarmonyLeaseOwner -or -not [guid]::TryParseExact([string]$Receipt.Id,'N',[ref]$leaseGuid) -or $Receipt.State -notin @('planned','installing','active','removed') -or $Receipt.ManifestSha256 -ne $script:HarmonyManifestHash) { throw 'Invalid lease receipt identity/state/hash.' }
    Assert-HarmonyPath ([string]$Receipt.ProjectRoot) $Sc4EvolutionRoot 'owner project'
    Assert-HarmonyPath ([string]$Receipt.LinkPath) $script:HarmonyLink 'lease link'
    Assert-HarmonyPath ([string]$Receipt.TargetPath) $script:HarmonyProvider 'provider target'
}
function Assert-HarmonyLink($Metadata,$Receipt) {
    Assert-HarmonyReceipt $Receipt
    if (-not $Metadata.Exists -or -not $Metadata.IsDirectory -or -not $Metadata.IsReparse -or $Metadata.LinkType -ne 'Junction') { throw 'Lease path is not the owned directory junction.' }
    Assert-HarmonyPath ([string]$Metadata.Target) $script:HarmonyProvider 'junction target'
    if ($Receipt.State -notin @('installing','active')) { throw 'Unexpected junction for a non-active receipt.' }
}
function Assert-HarmonyLaunchState($Metadata,$Receipt,[bool]$WithoutWorkshop,[int]$OtherProviders) {
    if ($OtherProviders -gt 0) { throw 'Unowned/duplicate local CitiesHarmony provider found; refusing launch.' }
    if ($Metadata.Exists) {
        Assert-HarmonyLink $Metadata $Receipt
        if (-not $WithoutWorkshop) { throw 'Harmony test lease requires -NoWorkshop; refusing duplicate Workshop provider exposure.' }
        if ($Receipt.State -ne 'active') { throw 'Lease installation is incomplete; inspect/remove before launch.' }
    }
    elseif ($null -ne $Receipt) {
        Assert-HarmonyReceipt $Receipt
        if ($Receipt.State -in @('installing','active')) { throw 'Stale lease receipt: junction is missing; remove/reconcile offline before launch.' }
    }
}
function Get-HarmonyOtherProviders {
    Assert-HarmonyLocations
    if (-not (Test-Path -LiteralPath $script:HarmonyMods -PathType Container)) { return @() }
    # Explicit stack follows local mod junctions (including SC4Evolution), rejects loops,
    # and never changes them. Lease subtree is excluded because its target is pinned separately.
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($script:HarmonyMods)
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $found = [Collections.Generic.List[string]]::new(); $count=0
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if ([string]::Equals([IO.Path]::GetFullPath($directory),$script:HarmonyLink,[StringComparison]::OrdinalIgnoreCase)) { continue }
        $info = Get-Item -LiteralPath $directory -Force
        $resolved = if ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) { $info.ResolveLinkTarget($true).FullName } else { $info.FullName }
        if (-not $visited.Add($resolved)) { throw 'Local mod directory alias/loop prevents complete provider inventory.' }
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            $count++; if($count -gt 20000){throw 'Local mod inventory exceeds safety bound.'}
            if($entry.PSIsContainer){$pending.Push($entry.FullName)}
            elseif ($entry.Name -ieq 'CitiesHarmony.dll') { $found.Add($entry.FullName) }
        }
    }
    return $found.ToArray()
}
function Save-HarmonyReceipt($Receipt,[switch]$New) {
    Assert-HarmonyLocations
    Assert-HarmonyReceipt $Receipt
    Assert-HarmonyRegularFileOrMissing $script:HarmonyReceipt | Out-Null
    $directory = Split-Path -Parent $script:HarmonyReceipt
    Assert-HarmonyNoReparseAncestors $directory $true
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Assert-HarmonyLocations
    $json = $Receipt | ConvertTo-Json -Depth 8
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $temp = $script:HarmonyReceipt+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        Assert-HarmonyLocations
        $stream=[IO.FileStream]::new($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
        Assert-HarmonyLocations
        Assert-HarmonyRegularFileOrMissing $script:HarmonyReceipt | Out-Null
        if($New){[IO.File]::Move($temp,$script:HarmonyReceipt)}else{[IO.File]::Replace($temp,$script:HarmonyReceipt,[System.Management.Automation.Language.NullString]::Value)}
    } finally { Assert-HarmonyLocations; if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force} }
}
function Test-HarmonyLeasePure {
    $receipt=[pscustomobject]@{Schema=1;Owner=$script:HarmonyLeaseOwner;Id=[guid]::NewGuid().ToString('N');State='active';ManifestSha256=$script:HarmonyManifestHash;ProjectRoot=$Sc4EvolutionRoot;LinkPath=$script:HarmonyLink;TargetPath=$script:HarmonyProvider}
    $link=[pscustomobject]@{Exists=$true;IsDirectory=$true;IsReparse=$true;LinkType='Junction';Target=$script:HarmonyProvider}
    $missing=[pscustomobject]@{Exists=$false}
    $rejects=0
    function Expect-Rejection([scriptblock]$Action) { $thrown=$false; try{& $Action}catch{$thrown=$true};if(-not $thrown){throw 'Expected lease guard rejection.'} }
    Assert-HarmonyLaunchState $link $receipt $true 0
    Expect-Rejection {Assert-HarmonyLaunchState $link $receipt $false 0};$rejects++
    Expect-Rejection {Assert-HarmonyLaunchState $link $receipt $true 1};$rejects++
    Expect-Rejection {Assert-HarmonyLaunchState $link $null $true 0};$rejects++
    Expect-Rejection {Assert-HarmonyLaunchState $missing $receipt $true 0};$rejects++
    $link.Target=Join-Path $Sc4EvolutionRoot 'unexpected';Expect-Rejection {Assert-HarmonyLink $link $receipt};$rejects++;$link.Target=$script:HarmonyProvider
    $link.IsReparse=$false;Expect-Rejection {Assert-HarmonyLink $link $receipt};$rejects++;$link.IsReparse=$true
    $receipt.ManifestSha256='wrong';Expect-Rejection {Assert-HarmonyReceipt $receipt};$rejects++;$receipt.ManifestSha256=$script:HarmonyManifestHash
    $receipt.Owner='other';Expect-Rejection {Assert-HarmonyReceipt $receipt};$rejects++;$receipt.Owner=$script:HarmonyLeaseOwner
    $receipt.State='installing';Expect-Rejection {Assert-HarmonyLaunchState $link $receipt $true 0};$rejects++
    # Removal policy accepts a missing owned stale link; no target deletion is attempted.
    Assert-HarmonyReceipt $receipt
    $receipt.State='removed';Assert-HarmonyLaunchState $missing $receipt $false 0
    Expect-Rejection {Assert-HarmonyLaunchState $link $receipt $true 0};$rejects++
    $writes=[Collections.Generic.List[string]]::new()
    $ancestryCases=@(
        @{Checked=$script:HarmonyProvider;Redirected=$Sc4EvolutionRoot},
        @{Checked=$script:HarmonyProvider;Redirected=(Join-Path $Sc4EvolutionRoot 'tools/CitiesHarmony/provider')},
        @{Checked=(Split-Path -Parent $script:HarmonyReceipt);Redirected=(Join-Path $Sc4EvolutionRoot '.cache')},
        @{Checked=$script:HarmonyMods;Redirected=(Split-Path -Parent $script:HarmonyMods)}
    )
    foreach($case in $ancestryCases){
        $redirected=$case.Redirected
        $fakeResolver={param($component) [pscustomobject]@{PSIsContainer=$true;Attributes=$(if([string]::Equals($component,$redirected,[StringComparison]::OrdinalIgnoreCase)){[IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint}else{[IO.FileAttributes]::Directory})}}
        Expect-Rejection {Assert-HarmonyNoReparseAncestors $case.Checked $false $fakeResolver; $writes.Add('unsafe write')};$rejects++
    }
    if($writes.Count -ne 0){throw 'Ancestor rejection allowed a write.'}
    $regularResolver={param($component) [pscustomobject]@{PSIsContainer=$true;Attributes=[IO.FileAttributes]::Directory}}
    Assert-HarmonyNoReparseAncestors $script:HarmonyProvider $false $regularResolver
    Write-Output 'Ancestor fixtures passed: project/provider/receipt-parent/Mods reparse rejected before mock write; regular ancestry accepted; no real fixture link created.'
    $danglingResolver={param($path) [pscustomobject]@{PSIsContainer=$false;Attributes=[IO.FileAttributes]::ReparsePoint}}
    foreach($leaf in @('steam_appid.txt','launch.json')){
        Expect-Rejection {Assert-HarmonyRegularFileOrMissing (Join-Path $Sc4EvolutionRoot ('.cache/cs1-run/'+$leaf)) $danglingResolver | Out-Null; $writes.Add('unsafe leaf write')};$rejects++
    }
    Assert-HarmonyRegularFileOrMissing (Join-Path $Sc4EvolutionRoot '.cache/cs1-run/launch.json') {param($path) $null} | Out-Null
    if($writes.Count -ne 0){throw 'Leaf rejection allowed a write.'}
    $receipt.State='active'
    $link | Add-Member -NotePropertyName ProviderIntegrityFailed -NotePropertyValue $true -Force
    $link | Add-Member -NotePropertyName OtherProviders -NotePropertyValue 2 -Force
    Assert-HarmonyRemoveState $link $receipt
    Assert-HarmonyRemoveState $missing $receipt
    Write-Output 'Recovery fixtures passed: owned junction removal policy accepts corrupted provider/other providers; dangling launch/appid file leaves rejected before mock writes.'
    $null=Get-HarmonyProviderEvidence
    Write-Output "Harmony lease pure tests passed: active -NoWorkshop accepted; $rejects guard rejections; missing owned stale cleanup allowed; removed receipt accepted only without link; all six pinned file hashes validated. No junction or real preference was created/changed."
}

function Test-HarmonyLeaseIO {
    # Real I/O regression for PowerShell's null-to-string overload binding.
    # Only this unique project scratch directory is touched; no lease operations.
    $scratch=Join-Path $Sc4EvolutionRoot ('.cache/harmony-lease-io-'+[guid]::NewGuid().ToString('N'))
    $first=Join-Path $scratch 'first.tmp';$second=Join-Path $scratch 'second.tmp';$destination=Join-Path $scratch 'record.json'
    $created=$false
    try{
        Assert-HarmonyNoReparseAncestors $scratch $true
        if($null -ne (Get-Item -LiteralPath $scratch -Force -ErrorAction SilentlyContinue)){throw 'Scratch already exists.'}
        New-Item -ItemType Directory -Path $scratch -ErrorAction Stop | Out-Null;$created=$true
        Assert-HarmonyNoReparseAncestors $scratch
        $stream=[IO.FileStream]::new($first,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write([Text.Encoding]::UTF8.GetBytes('planned'));$stream.Flush($true)}finally{$stream.Dispose()}
        $rejected=$false
        try{$unexpected=[IO.FileStream]::new($first,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$unexpected.Dispose()}catch [IO.IOException]{$rejected=$true}
        if(-not $rejected){throw 'CreateNew did not reject existing file.'}
        Assert-HarmonyNoReparseAncestors $scratch
        [IO.File]::Move($first,$destination)
        if([IO.File]::ReadAllText($destination) -ne 'planned' -or [IO.File]::Exists($first)){throw 'Move verification failed.'}
        $stream=[IO.FileStream]::new($second,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write([Text.Encoding]::UTF8.GetBytes('active'));$stream.Flush($true)}finally{$stream.Dispose()}
        Assert-HarmonyNoReparseAncestors $scratch
        Assert-HarmonyRegularFileOrMissing $destination | Out-Null
        [IO.File]::Replace($second,$destination,[System.Management.Automation.Language.NullString]::Value)
        if([IO.File]::ReadAllText($destination) -ne 'active' -or [IO.File]::Exists($second) -or @(Get-ChildItem -LiteralPath $scratch -Force).Count -ne 1){throw 'Replace verification failed.'}
    }finally{
        if($created){
            Assert-HarmonyNoReparseAncestors $scratch
            foreach($exact in @($first,$second,$destination)){
                $item=Assert-HarmonyRegularFileOrMissing $exact
                if($null -ne $item){[IO.File]::Delete($exact)}
            }
            Assert-HarmonyNoReparseAncestors $scratch
            [IO.Directory]::Delete($scratch)
        }
    }
    if($null -ne (Get-Item -LiteralPath $scratch -Force -ErrorAction SilentlyContinue)){throw 'Scratch cleanup failed.'}
    Write-Output 'Harmony real I/O smoke passed: exclusive CreateNew and collision rejection, Move, Replace with real null string backup, exact byte verification, exact-file/nonrecursive scratch cleanup. No preference/junction/lease/launch operation.'
}
if($Mode -eq 'Library'){return}
if($Mode -eq 'SelfTest'){Test-HarmonyLeasePure;return}
if($Mode -eq 'IoSmokeTest'){Test-HarmonyLeaseIO;return}
if($Mode -ne 'Remove'){$null=Get-HarmonyProviderEvidence}
$receipt=Get-HarmonyReceipt
$metadata=Get-HarmonyLinkMetadata
$others=if($Mode -eq 'Remove'){@()}else{@(Get-HarmonyOtherProviders)}
if($Mode -eq 'AssertLaunch') { Assert-HarmonyLaunchState $metadata $receipt ([bool]$NoWorkshop) $others.Count; return }
if($Mode -eq 'Inspect') {
    Assert-HarmonyLaunchState $metadata $receipt $true $others.Count
    Write-Output ([pscustomobject]@{Link=$script:HarmonyLink;Target=$script:HarmonyProvider;Exists=$metadata.Exists;ReceiptState=$(if($receipt){$receipt.State}else{'none'});ManifestSha256=$script:HarmonyManifestHash;OtherLocalProviders=$others.Count;CitiesPids=@(Get-Process Cities -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)})
    return
}
if($Mode -eq 'Prepare') {
    if($metadata.Exists -or $others.Count -gt 0){throw 'A lease/provider already exists; refusing preparation.'}
    if($null -ne $receipt){throw 'Receipt already exists; inspect it. A new preparation requires explicit archival of its completed receipt.'}
    $receipt=[ordered]@{Schema=1;Owner=$script:HarmonyLeaseOwner;Id=[guid]::NewGuid().ToString('N');State='planned';CreatedUtc=[datetime]::UtcNow.ToString('o');ProjectRoot=$Sc4EvolutionRoot;LinkPath=$script:HarmonyLink;TargetPath=$script:HarmonyProvider;ManifestSha256=$script:HarmonyManifestHash;ProviderRelease='2.2.2-0';RequiresNoWorkshop=$true}
    Save-HarmonyReceipt $receipt -New
    Write-Output "Prepared receipt only: $script:HarmonyReceipt. No local junction/preferences were changed."
    return
}
Assert-HarmonyOffline
Assert-HarmonyReceipt $receipt
if($Mode -ne 'Remove' -and $others.Count -gt 0){throw 'Unowned local CitiesHarmony provider exists; refusing lease mutation.'}
if($Mode -eq 'Install') {
    if($receipt.State -ne 'planned' -or $metadata.Exists){throw 'Install requires a planned receipt and absent link.'}
    if(-not (Test-Path -LiteralPath $script:HarmonyMods -PathType Container)){throw 'Existing local Mods directory required.'}
    Assert-HarmonyOffline
    $receipt.State='installing';Save-HarmonyReceipt $receipt
    # Never replace an existing entry. Junction creation is the only external filesystem write.
    Assert-HarmonyLocations
    New-Item -ItemType Junction -Path $script:HarmonyLink -Target $script:HarmonyProvider -ErrorAction Stop | Out-Null
    Assert-HarmonyLink (Get-HarmonyLinkMetadata) $receipt
    $receipt.State='active';Save-HarmonyReceipt $receipt
    Write-Output "Installed owned lease. Launch only with -NoWorkshop. Receipt: $script:HarmonyReceipt"
    return
}
if($Mode -eq 'Remove') {
    Assert-HarmonyRemoveState $metadata $receipt
    if($metadata.Exists){
        Assert-HarmonyLink $metadata $receipt
        Assert-HarmonyOffline
        # Both exact absolute link and target have been checked against this project's receipt.
        # Nonrecursive delete removes only the reparse entry, never its provider target.
        Assert-HarmonyLink (Get-HarmonyLinkMetadata) $receipt
        Assert-HarmonyLocations
        [IO.Directory]::Delete($script:HarmonyLink)
        if((Get-HarmonyLinkMetadata).Exists){throw 'Lease entry still exists after removal.'}
    }
    $receipt.State='removed';$receipt | Add-Member -NotePropertyName RemovedUtc -NotePropertyValue ([datetime]::UtcNow.ToString('o')) -Force
    Save-HarmonyReceipt $receipt
    Write-Output 'Lease removed/reconciled; provider source and all preferences retained.'
}
