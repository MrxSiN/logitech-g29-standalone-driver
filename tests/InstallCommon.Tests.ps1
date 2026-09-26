[CmdletBinding()]
param(
    # The g29ctl.exe whose install-plan output is checked.
    [Parameter(Mandatory = $true)]
    [string]$Executable
)

# Tests for tools\InstallCommon.ps1: the checks the elevated installer applies
# to the program's install plan, the service ownership test, and the install
# transaction against an in-memory machine with a failure injected at every
# step (the machine must end exactly as it began). No elevation, no service or
# registry changes.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\tools\InstallCommon.ps1')

$failures = 0
$count = 0

function Assert-True([bool]$Condition, [string]$Name) {
    $script:count++
    if (-not $Condition) {
        $script:failures++
        Write-Host "FAIL $Name"
    }
}

function Test-Rejects([string[]]$Lines, [string]$Name) {
    $rejected = $false
    try {
        $null = ConvertFrom-InstallPlan $Lines
    } catch {
        $rejected = $true
    }

    Assert-True $rejected $Name
}

# ---------------------------------------------------------------------------
# Plan checks
# ---------------------------------------------------------------------------

$lines = @(& $Executable install-plan)
Assert-True ($LASTEXITCODE -eq 0) 'install-plan succeeds'
$plan = ConvertFrom-InstallPlan $lines
Assert-True ($plan['service'] -ceq 'G29Standalone') 'the real plan names the stable service'
Assert-True ($plan['directory'] -ceq 'G29Standalone') 'the real plan names the stable directory'

$ranged = ConvertFrom-InstallPlan @(& $Executable install-plan --range 40 --autocenter 100)
Assert-True ($ranged['arguments'] -ceq 'service --range 40 --autocenter 100') 'options reach the service arguments'

function Set-Line([string[]]$Source, [string]$Key, [string]$Value) {
    return @($Source | ForEach-Object { if ($_ -like "$Key *") { "$Key $Value" } else { $_ } })
}

Test-Rejects (Set-Line $lines 'service' 'Spooler') 'an unrelated service (Spooler)'
Test-Rejects (Set-Line $lines 'service' 'G29StandaloneX') 'a service other than the stable one'
Test-Rejects (Set-Line $lines 'service' 'Spooler & calc') 'service name with shell characters'
Test-Rejects (Set-Line $lines 'directory' '..\Windows') 'directory traversal'
Test-Rejects (Set-Line $lines 'directory' 'C:\Temp') 'absolute directory'
Test-Rejects (Set-Line $lines 'directory' 'Other') 'a directory other than the stable one'
Test-Rejects (Set-Line $lines 'startup' 'Boot') 'unknown startup type'
Test-Rejects (Set-Line $lines 'startup' 'Disabled') 'disabled startup'
Test-Rejects (Set-Line $lines 'arguments' 'service" & calc "') 'arguments with quotes'
Test-Rejects (Set-Line $lines 'arguments' 'watch --range 900') 'arguments other than the service command'
Test-Rejects (Set-Line $lines 'arguments' 'service --range 900 --leds 3') 'unknown service options'
Test-Rejects (Set-Line $lines 'failure-actions' 'run/0') 'run recovery action'
Test-Rejects (Set-Line $lines 'failure-actions' 'restart/5000/run/0') 'trailing run recovery action'
Test-Rejects (Set-Line $lines 'failure-reset' '-1') 'negative failure reset'
Test-Rejects (Set-Line $lines 'trigger' 'stop/device/x') 'malformed trigger'
Test-Rejects (Set-Line $lines 'trigger' 'start/device/a5dcbf10-6530-11d2-901f-00c04fb951ed/USB\VID_045E&PID_028E') 'a trigger on another vendor''s device'
Test-Rejects (Set-Line $lines 'trigger' 'start/device/4d1e55b2-f16f-11cf-88cb-001111000030/USB\VID_046D&PID_C24F') 'a trigger on another device interface class'
Test-Rejects (Set-Line $lines 'trigger' 'start/device/a5dcbf10-6530-11d2-901f-00c04fb951ed/USB\VID_046D&PID_C24F\x') 'a trigger with a longer hardware ID'
Test-Rejects (Set-Line $lines 'start-after-install' 'maybe') 'unknown start-after-install'
Test-Rejects (Set-Line $lines 'display' "a`tb") 'display with control characters'
Test-Rejects (@($lines | Where-Object { $_ -notlike 'trigger *' })) 'missing key'
Test-Rejects ($lines + @('extra value')) 'unknown key'
Test-Rejects ($lines + @($lines[0])) 'repeated key'
Test-Rejects ($lines + @('nospace')) 'line without a value'

$exe = 'C:\Program Files\G29Standalone\g29ctl.exe'
Assert-True (Test-OwnedServicePath "`"$exe`" service --range 900 --autocenter 0" $exe) 'owned service path'
Assert-True (Test-OwnedServicePath "`"$($exe.ToUpperInvariant())`"" $exe) 'owned service path ignores case'
Assert-True (-not (Test-OwnedServicePath 'C:\Windows\System32\spoolsv.exe' $exe)) 'foreign service path'
Assert-True (-not (Test-OwnedServicePath "`"$exe.bak`" service" $exe)) 'path with a longer file name'
Assert-True (-not (Test-OwnedServicePath "$exe service" $exe)) 'unquoted path'
Assert-True (-not (Test-OwnedServicePath '' $exe)) 'empty path'
Assert-True ((@(Get-ServiceOptions "`"$exe`" service --range 540 --autocenter 20" $exe) -join ' ') -ceq '--range 540 --autocenter 20') 'previous service options are recovered'
Assert-True ($null -eq (Get-ServiceOptions "`"$exe`" watch" $exe)) 'a foreign command line has no options'

$good = ('A' * 64)
$hashText = "g29ctl.exe=$good;g29ffb64.dll=$good;g29ffb32.dll=$good"
Assert-True ((ConvertFrom-ArtifactHashes $hashText).Count -eq 3) 'artifact hashes parse'
foreach ($bad in @("g29ctl.exe=$good", "$hashText;evil.dll=$good", "g29ctl.exe=abc;g29ffb64.dll=$good;g29ffb32.dll=$good", "g29ctl.exe=$good;g29ffb64.dll=$good;g29ffb32.dll=$good & calc")) {
    $rejected = $false
    try { $null = ConvertFrom-ArtifactHashes $bad } catch { $rejected = $true }
    Assert-True $rejected "malformed artifact hashes: $bad"
}

# ---------------------------------------------------------------------------
# The install transaction on an in-memory machine
# ---------------------------------------------------------------------------

$directory = 'C:\PF\G29Standalone'
$installed = Join-Path $directory 'g29ctl.exe'
$source = 'C:\src'
$oldContent = @{ 'g29ctl.exe' = ('1' * 64); 'g29ffb64.dll' = ('2' * 64); 'g29ffb32.dll' = ('3' * 64); 'LICENSE' = ('4' * 64) }
$newContent = @{ 'g29ctl.exe' = ('A' * 64); 'g29ffb64.dll' = ('B' * 64); 'g29ffb32.dll' = ('C' * 64) }

# World: files (path -> content), services, the registration (the content of
# the g29ctl.exe that registered), a log, and the mutation that fails.
function New-World([switch]$Previous) {
    $world = @{
        Files       = @{}
        Directories = @{}
        Services    = @{ 'Spooler' = @{ PathName = 'C:\Windows\System32\spoolsv.exe'; Running = $true } }
        Registered  = $null
        Log         = New-Object System.Collections.Generic.List[string]
        Changes     = New-Object System.Collections.Generic.List[string]
        Mutations   = 0
        FailAt      = 0
        Plan        = $lines
        # the g29ctl.exe content whose plan is BadPlan / whose ffb-register fails
        BadPlanFor  = $null
        BadPlan     = $null
        FailRegister = $null
        # user hives: SID -> 'loaded' or 'profile' (an NTUSER.DAT not loaded);
        # Registry: key path under HKEY_USERS -> values; Mounted: name -> SID
        Hives       = @{ 'S-1-5-18' = 'loaded' }
        Registry    = @{}
        Mounted     = @{}
        LoadFails   = @()
    }

    foreach ($name in $newContent.Keys) {
        $world.Files[(Join-Path $source $name)] = $newContent[$name]
    }

    $world.Files['C:\repo\LICENSE'] = ('L' * 64)
    if ($Previous) {
        $world.Directories[$directory] = $true
        foreach ($name in $oldContent.Keys) {
            $world.Files[(Join-Path $directory $name)] = $oldContent[$name]
        }

        $world.Services['G29Standalone'] = @{ PathName = "`"$installed`" service --range 540 --autocenter 20"; Running = $true }
        $world.Registered = $oldContent['g29ctl.exe']
    }

    return $world
}

function Get-Snapshot($World) {
    $files = @($World.Files.Keys | Sort-Object | ForEach-Object { "$_=$($World.Files[$_])" })
    $services = @($World.Services.Keys | Sort-Object | ForEach-Object { "$_=$($World.Services[$_].PathName)|$($World.Services[$_].Running)" })
    $directories = @($World.Directories.Keys | Sort-Object)
    $registry = @($World.Registry.Keys | Sort-Object | ForEach-Object {
        $values = $World.Registry[$_]
        "$_ {" + (@($values.Keys | Sort-Object | ForEach-Object { "$_=$($values[$_])" }) -join ',') + '}'
    })
    return ($files + $services + $directories + $registry + @("registered=$($World.Registered)", "mounted=$($World.Mounted.Count)")) -join "`n"
}

function New-FakeMachine($World) {
    $mutate = {
        param([string]$Operation)
        $World.Log.Add($Operation)
        $World.Changes.Add($Operation)
        $World.Mutations++
        if ($World.Mutations -eq $World.FailAt) {
            throw "injected failure at $Operation"
        }
    }.GetNewClosure()

    # A registry path under HKEY_USERS with a mount name replaced by its SID;
    # a hive that is neither loaded nor mounted cannot be reached.
    $resolve = {
        param([string]$Path)
        $hive, $rest = $Path -split '\\', 2
        if ($World.Mounted.ContainsKey($hive)) {
            $hive = $World.Mounted[$hive]
        } elseif ($World.Hives[$hive] -ne 'loaded') {
            throw "HKEY_USERS\$hive is not loaded."
        }

        if ($null -eq $rest) { return $hive }
        return "$hive\$rest"
    }.GetNewClosure()

    return @{
        GetService        = {
            param([string]$Name)
            $World.Log.Add("query $Name")
            if (-not $World.Services.ContainsKey($Name)) { return $null }
            $service = $World.Services[$Name]
            return @{ PathName = $service.PathName; Running = $service.Running }
        }.GetNewClosure()
        StopService       = { param([string]$Name) & $mutate "stop $Name"; $World.Services[$Name].Running = $false }.GetNewClosure()
        DeleteService     = { param([string]$Name) & $mutate "delete $Name"; $World.Services.Remove($Name) }.GetNewClosure()
        CreateService     = {
            param([hashtable]$Plan, [string]$BinaryPath)
            & $mutate "create $($Plan['service'])"
            if ($World.Services.ContainsKey($Plan['service'])) { throw 'The service exists.' }
            $World.Services[$Plan['service']] = @{ PathName = $BinaryPath; Running = $false }
        }.GetNewClosure()
        StartService      = { param([string]$Name) & $mutate "start $Name"; $World.Services[$Name].Running = $true }.GetNewClosure()
        ListFiles         = {
            param([string]$Directory)
            return @($World.Files.Keys | Where-Object { [IO.Path]::GetDirectoryName($_) -eq $Directory } | ForEach-Object { [IO.Path]::GetFileName($_) })
        }.GetNewClosure()
        CreateDirectory   = {
            param([string]$Directory)
            & $mutate "mkdir $Directory"
            $created = -not $World.Directories.ContainsKey($Directory)
            $World.Directories[$Directory] = $true
            return $created
        }.GetNewClosure()
        RemoveDirectory   = {
            param([string]$Directory)
            & $mutate "rmdir $Directory"
            foreach ($path in @($World.Files.Keys | Where-Object { [IO.Path]::GetDirectoryName($_) -eq $Directory })) { $World.Files.Remove($path) }
            $World.Directories.Remove($Directory)
        }.GetNewClosure()
        MoveFile          = {
            param([string]$From, [string]$To)
            & $mutate "move $From"
            $World.Files[$To] = $World.Files[$From]
            $World.Files.Remove($From)
        }.GetNewClosure()
        CopyFile          = {
            param([string]$From, [string]$To)
            & $mutate "copy $To"
            $World.Files[$To] = $World.Files[$From]
        }.GetNewClosure()
        RemoveFile        = { param([string]$Path) & $mutate "remove $Path"; $World.Files.Remove($Path) }.GetNewClosure()
        FileHash          = { param([string]$Path) return $World.Files[$Path] }.GetNewClosure()
        InvokeG29         = {
            param([string]$Executable, [string[]]$Arguments)
            if (-not $World.Files.ContainsKey($Executable)) { return @{ ExitCode = 9009; Output = @() } }
            $content = $World.Files[$Executable]
            $World.Log.Add("run $content $($Arguments -join ' ')")
            switch ($Arguments[0]) {
                'install-plan' {
                    $arguments = 'service' + (@($Arguments | Select-Object -Skip 1 | ForEach-Object { " $_" }) -join '')
                    if ($arguments -eq 'service') { $arguments = 'service --range 900 --autocenter 0' }
                    $planLines = if ($content -eq $World.BadPlanFor) { $World.BadPlan } else { $World.Plan }
                    $output = @($planLines | ForEach-Object { if ($_ -like 'arguments *') { "arguments $arguments" } else { $_ } })
                    return @{ ExitCode = 0; Output = $output }
                }
                'ffb-register' {
                    & $mutate 'ffb-register'
                    if ($content -eq $World.FailRegister) { return @{ ExitCode = 1; Output = @() } }
                    $World.Registered = $content
                    return @{ ExitCode = 0; Output = @() }
                }
                'ffb-unregister' {
                    & $mutate 'ffb-unregister'
                    $World.Registered = $null
                    return @{ ExitCode = 0; Output = @() }
                }
            }

            return @{ ExitCode = 1; Output = @() }
        }.GetNewClosure()
        IsRegistered      = { return $null -ne $World.Registered }.GetNewClosure()
        RemoveEventSource = { param([string]$Name) & $mutate "eventsource $Name" }.GetNewClosure()
        MountUserHives    = {
            $hives = @()
            foreach ($sid in @($World.Hives.Keys | Sort-Object)) {
                if ($World.Hives[$sid] -eq 'loaded') {
                    $hives += @{ Sid = $sid; Name = $sid; Mounted = $false; Error = $null }
                } elseif ($World.Mounted.ContainsKey("mount-$sid")) {
                    # still loaded after a failed unload
                    $hives += @{ Sid = $sid; Name = "mount-$sid"; Mounted = $true; Error = $null }
                } elseif ($World.LoadFails -contains $sid) {
                    $hives += @{ Sid = $sid; Name = $null; Mounted = $false; Error = 'could not load it' }
                } else {
                    & $mutate "load $sid"
                    $World.Mounted["mount-$sid"] = $sid
                    $hives += @{ Sid = $sid; Name = "mount-$sid"; Mounted = $true; Error = $null }
                }
            }

            return $hives
        }.GetNewClosure()
        DismountUserHive  = { param([string]$Name) & $mutate "unload $Name"; $World.Mounted.Remove($Name) }.GetNewClosure()
        TestUserKey       = { param([string]$Path) return $World.Registry.ContainsKey((& $resolve $Path)) }.GetNewClosure()
        TestUserKeyEmpty  = {
            param([string]$Path)
            $key = & $resolve $Path
            if (-not $World.Registry.ContainsKey($key)) { return $false }
            return $World.Registry[$key].Count -eq 0 -and @($World.Registry.Keys | Where-Object { $_.StartsWith("$key\") }).Count -eq 0
        }.GetNewClosure()
        ReadUserValue     = {
            param([string]$Path, [string]$Name)
            $key = & $resolve $Path
            if (-not $World.Registry.ContainsKey($key)) { return $null }
            return $World.Registry[$key][$Name]
        }.GetNewClosure()
        RemoveUserKey     = {
            param([string]$Path)
            $key = & $resolve $Path
            & $mutate "regdelete $key"
            foreach ($path in @($World.Registry.Keys | Where-Object { $_ -eq $key -or $_.StartsWith("$key\") })) { $World.Registry.Remove($path) }
        }.GetNewClosure()
    }
}

function New-Context([switch]$SkipForceFeedback, [hashtable]$Hashes = $newContent) {
    return @{
        SourceDirectory   = $source
        Hashes            = $Hashes
        Options           = @()
        SkipForceFeedback = [bool]$SkipForceFeedback
        InstallDirectory  = $directory
        LicensePath       = 'C:\repo\LICENSE'
    }
}

function Invoke-Quietly([scriptblock]$Action) {
    try {
        & $Action 3>$null
        return $null
    } catch {
        return $_.Exception.Message
    }
}

# Fresh install and upgrade succeed and leave exactly the new installation.
foreach ($previous in @($false, $true)) {
    $world = New-World -Previous:$previous
    $error1 = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $world) }
    $label = if ($previous) { 'upgrade' } else { 'fresh install' }
    Assert-True ($null -eq $error1) "$label succeeds ($error1)"
    foreach ($name in $newContent.Keys) {
        Assert-True ($world.Files[(Join-Path $directory $name)] -eq $newContent[$name]) "$label installs $name"
    }

    Assert-True (@($world.Files.Keys | Where-Object { $_ -like "*.g29previous" }).Count -eq 0) "$label leaves no set-aside files"
    Assert-True ($world.Services['G29Standalone'].PathName -eq "`"$installed`" service --range 900 --autocenter 0") "$label creates the service with the plan's arguments"
    Assert-True ($world.Services['G29Standalone'].Running) "$label starts the service"
    Assert-True ($world.Registered -eq $newContent['g29ctl.exe']) "$label registers the new driver"
    Assert-True ($world.Services['Spooler'].Running -and @($world.Log | Where-Object { $_ -match 'Spooler' }).Count -eq 0) "$label never touches another service"
}

# A failure at every single change rolls the machine back to where it began.
foreach ($previous in @($false, $true)) {
    $reference = New-World -Previous:$previous
    $null = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $reference) }
    $total = $reference.Mutations
    Assert-True ($total -ge 8) "the transaction has steps to fail ($total)"
    for ($failAt = 1; $failAt -le $total; $failAt++) {
        $change = $reference.Changes[$failAt - 1]
        if ($change -like 'remove *.g29previous') {
            # after the commit: best effort by design (a game may hold the file)
            continue
        }

        $world = New-World -Previous:$previous
        $before = Get-Snapshot $world
        $world.FailAt = $failAt
        $message = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $world) }
        $label = if ($previous) { 'upgrade' } else { 'fresh install' }
        Assert-True ($null -ne $message -and $message -like 'injected failure*') "$label fails at change $failAt ($change)"
        Assert-True ((Get-Snapshot $world) -eq $before) "$label rolls back completely after a failure at change $failAt ($change)"
    }
}

# A failed DirectInput registration fails the installation (and rolls it back)
# unless force feedback was explicitly skipped.
foreach ($previous in @($false, $true)) {
    $world = New-World -Previous:$previous
    $world.FailRegister = $newContent['g29ctl.exe']
    $before = Get-Snapshot $world
    $message = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $world) }
    Assert-True ($message -like '*registration*') "a failed registration fails the installation (previous: $previous)"
    Assert-True ((Get-Snapshot $world) -eq $before) "a failed registration is rolled back (previous: $previous)"
}

$world = New-World
$message = Invoke-Quietly { $null = Invoke-G29Install (New-Context -SkipForceFeedback) (New-FakeMachine $world) }
Assert-True ($null -eq $message -and $null -eq $world.Registered -and $world.Services.ContainsKey('G29Standalone')) '-SkipForceFeedback installs the service only'

# Binaries changed after they were built are refused before they run.
$world = New-World -Previous
$before = Get-Snapshot $world
$world.Files[(Join-Path $source 'g29ctl.exe')] = ('F' * 64)
$before = Get-Snapshot $world
$message = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $world) }
Assert-True ($message -like '*changed after it was built*') 'a tampered binary is refused'
Assert-True ((Get-Snapshot $world) -eq $before) 'a tampered binary leaves the machine as it was'
Assert-True (@($world.Log | Where-Object { $_ -like "run $('F' * 64) *" }).Count -eq 0) 'a tampered g29ctl.exe never runs'
Assert-True (@($world.Log | Where-Object { $_ -like "run $('1' * 64) install-plan --range 540 --autocenter 20" }).Count -eq 1) 'the previous service is recreated with its own options'

# A plan naming another service or directory is refused; that service is never touched.
foreach ($key in @('service', 'directory')) {
    $world = New-World -Previous
    $world.BadPlanFor = $newContent['g29ctl.exe']
    $world.BadPlan = Set-Line $lines $key 'Spooler'
    $before = Get-Snapshot $world
    $message = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $world) }
    Assert-True ($message -like "*unexpected '$key'*") "a plan naming Spooler as the $key is refused"
    Assert-True ((Get-Snapshot $world) -eq $before) "a plan naming Spooler as the $key changes nothing"
    Assert-True (@($world.Log | Where-Object { $_ -match 'Spooler' }).Count -eq 0) "Spooler is never queried, stopped, deleted or created ($key)"
}

# A foreign service under the stable name stops everything before any change.
$world = New-World
$world.Services['G29Standalone'] = @{ PathName = 'C:\evil\other.exe'; Running = $true }
$message = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $world) }
Assert-True ($message -like '*refusing to modify it*' -and $world.Mutations -eq 0) 'a foreign G29Standalone service is refused before any change'
$message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
Assert-True ($message -like '*refusing to modify it*' -and $world.Mutations -eq 0) 'the uninstaller refuses a foreign G29Standalone service before any change'

# Uninstall removes exactly what was installed.
$world = New-World
$empty = Get-Snapshot $world
$null = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $world) }
$message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
Assert-True ($null -eq $message) "uninstall succeeds ($message)"
Assert-True ((Get-Snapshot $world) -eq $empty) 'uninstall leaves the machine as it was before the installation'
Assert-True ($world.Log -contains 'eventsource G29Standalone') 'uninstall removes the event source'
$message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
Assert-True ($null -eq $message) 'uninstalling twice is harmless'

# ---------------------------------------------------------------------------
# The per-user registration in other accounts' hives
# ---------------------------------------------------------------------------

# The key paths themselves are checked against what the program writes by
# tests\RegistrationTests.cs (InstallerKeysMatch).
Assert-True ($script:G29ClassKey -like "*\$script:G29Clsid\InprocServer32") 'the class key names the class ID'

foreach ($sid in @('S-1-5-18', 'S-1-5-21-1004336348-1177238915-682003330-1001', 'S-1-12-1-1-2-3-4')) {
    Assert-True ($sid -cmatch $script:G29UserSidPattern) "user hive $sid is swept"
}

foreach ($sid in @('S-1-5-19', 'S-1-5-20', '.DEFAULT', 'S-1-5-21-1004336348-1177238915-682003330-1001_Classes', 'S-1-5-21-1')) {
    Assert-True ($sid -cnotmatch $script:G29UserSidPattern) "hive $sid is not swept"
}

$oemPath = $script:G29OemKey
$userA = 'S-1-5-21-1-2-3-1001'
$userB = 'S-1-5-21-1-2-3-1002'

# What ffb-register leaves in one hive. -OwnOemKey: the OEM key did not exist
# before (otherwise Windows' own OEMName in it predates the registration).
function Add-Registration($World, [string]$Sid, [switch]$OwnOemKey, [string]$Clsid = $script:G29Clsid) {
    $marker = @{}
    if ($OwnOemKey) { $marker['CreatedOemKey'] = 1 }
    $marker['CreatedSteeringAxis'] = 1
    $World.Registry["$Sid\Software\G29Standalone"] = @{}
    $World.Registry["$Sid\$script:G29MarkerKey"] = $marker
    $World.Registry["$Sid\$oemPath\OEMForceFeedback"] = @{ 'CLSID' = $Clsid; 'Attributes' = '00000000E8030000E8030000' }
    $World.Registry["$Sid\$oemPath\OEMForceFeedback\Effects"] = @{}
    $World.Registry["$Sid\$oemPath\OEMForceFeedback\Effects\{13541C20-8E33-11D0-9AD0-00A0C9A06E35}"] = @{ '' = 'Constant' }
    $World.Registry["$Sid\$oemPath\Axes"] = @{}
    $World.Registry["$Sid\$oemPath\Axes\0"] = @{ '' = 'Wheel axis' }
    if ($OwnOemKey) { $World.Registry["$Sid\$oemPath"] = @{} }
}

function New-UserWorld {
    $world = New-World
    $world.Hives[$userA] = 'loaded'
    $world.Hives[$userB] = 'profile'
    # Windows' own keys, present before any installation
    $world.Registry["$userA\$oemPath"] = @{ 'OEMName' = 'Logitech G29 Driving Force Racing Wheel USB' }
    $world.Registry["$userA\Software"] = @{}
    $world.Registry["$userB\Software"] = @{}
    return $world
}

# An interactive installation (user A's HKCU, and user B's from an earlier
# install as B, whose OEM key was created by it), removed by an uninstall that
# runs as SYSTEM: both hives end as they were before.
$world = New-UserWorld
$clean = Get-Snapshot $world
$null = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $world) }
Add-Registration $world $userA
Add-Registration $world $userB -OwnOemKey
$message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
Assert-True ($null -eq $message) "an uninstall by another account succeeds ($message)"
Assert-True ((Get-Snapshot $world) -eq $clean) 'an uninstall by another account removes every user''s registration and nothing else'
Assert-True ($world.Registry["$userA\$oemPath"]['OEMName'] -ne $null) 'Windows'' own OEM values stay'
Assert-True ($world.Log -contains "load $userB" -and $world.Log -contains "unload mount-$userB") 'an unloaded profile is loaded and unloaded again'

# A SYSTEM installation (SYSTEM's own HKCU) removed by an interactive user.
$world = New-UserWorld
$clean = Get-Snapshot $world
Add-Registration $world 'S-1-5-18' -OwnOemKey
$message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
Assert-True ($null -eq $message -and (Get-Snapshot $world) -eq $clean) 'SYSTEM''s per-user registration is removed by another account'

# Without the hive's own marker nothing in it is ours, even our class ID (for
# example OEM keys DirectInput copied from HKLM).
$world = New-UserWorld
Add-Registration $world $userA
$world.Registry.Remove("$userA\$script:G29MarkerKey")
$world.Registry.Remove("$userA\Software\G29Standalone")
$before = Get-Snapshot $world
$message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
Assert-True ($null -eq $message -and (Get-Snapshot $world) -eq $before) 'a registration without its marker is left alone'

# A foreign driver registered over ours afterwards: its keys stay, the marker goes.
$world = New-UserWorld
$foreign = '{1ED6DDBB-0401-4498-A093-7D249203200C}'
Add-Registration $world $userA -Clsid $foreign
$message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
Assert-True ($null -eq $message) "a foreign registration does not fail the uninstall ($message)"
Assert-True ($world.Registry["$userA\$oemPath\OEMForceFeedback"]['CLSID'] -eq $foreign) 'a foreign OEMForceFeedback stays'
Assert-True ($world.Registry.ContainsKey("$userA\$oemPath\Axes\0")) 'the axis of a foreign registration stays'
Assert-True (-not $world.Registry.ContainsKey("$userA\$script:G29MarkerKey") -and -not $world.Registry.ContainsKey("$userA\Software\G29Standalone")) 'the marker of a replaced registration is removed'

# The class ID compares without regard to case; the product key stays while it holds anything else.
$world = New-UserWorld
Add-Registration $world $userA -Clsid $script:G29Clsid.ToLowerInvariant()
$world.Registry["$userA\Software\G29Standalone"]['Other'] = 'x'
$message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
Assert-True (-not $world.Registry.ContainsKey("$userA\$oemPath\OEMForceFeedback")) 'a lower-case class ID is recognized'
Assert-True ($world.Registry.ContainsKey("$userA\Software\G29Standalone")) 'a product key that holds other values stays'

# A hive that cannot be cleaned: the rest of the uninstall still happens, the
# failure is reported, and running it again finishes the job.
$reference = New-UserWorld
$null = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $reference) }
Add-Registration $reference $userA
Add-Registration $reference $userB
$start = $reference.Changes.Count
$null = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $reference) $directory }
$registryChanges = @(for ($index = $start; $index -lt $reference.Changes.Count; $index++) {
    if ($reference.Changes[$index] -match '^(regdelete|load|unload) ') { $index - $start + 1 }
})
Assert-True ($registryChanges.Count -ge 8) "the per-user sweep has changes to fail ($($registryChanges.Count))"
foreach ($failAt in $registryChanges) {
    $change = $reference.Changes[$start + $failAt - 1]
    $world = New-UserWorld
    $clean = Get-Snapshot $world
    $null = Invoke-Quietly { $null = Invoke-G29Install (New-Context) (New-FakeMachine $world) }
    Add-Registration $world $userA
    Add-Registration $world $userB
    $world.FailAt = $world.Mutations + $failAt
    $message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
    Assert-True ($message -like '*per-user DirectInput registration*run Uninstall-Driver.ps1 again*') "a failure at sweep change $failAt ($change) is reported"
    Assert-True (-not $world.Services.ContainsKey('G29Standalone') -and -not $world.Directories.ContainsKey($directory)) "a failure at sweep change $failAt ($change) still removes the service and files"
    Assert-True ($world.Mounted.Count -eq 0 -or $change -like 'unload *') "a failure at sweep change $failAt ($change) leaves no other hive loaded"
    $message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
    Assert-True ($null -eq $message -and (Get-Snapshot $world) -eq $clean) "running the uninstall again after a failure at sweep change $failAt ($change) finishes it"
}

$world = New-UserWorld
Add-Registration $world $userA
$world.LoadFails = @($userB)
$message = Invoke-Quietly { Remove-G29Installation (New-FakeMachine $world) $directory }
Assert-True ($message -like "*$userB*could not load it*") 'a profile that cannot be loaded is reported'
Assert-True (-not $world.Registry.ContainsKey("$userA\$script:G29MarkerKey")) 'the other hives are still cleaned when one cannot be loaded'

if ($failures -ne 0) {
    throw "$failures of $count install script checks failed."
}

Write-Host "Install script checks: $count passed."
