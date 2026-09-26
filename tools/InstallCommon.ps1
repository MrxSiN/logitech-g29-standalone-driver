# Shared by Install-Driver.ps1 and Uninstall-Driver.ps1 (dot-sourced).
#
# The installation policy (display text, service arguments, device trigger,
# recovery, start after install) is decided by the Brainfuck program:
# "g29ctl install-plan" prints one "key value" line per setting. These scripts
# run elevated, so the program only proposes: the identity of every Windows
# object an administrator changes is fixed here (the service name and the
# install directory), every other value must match a narrow grammar before it
# reaches the Service Control Manager, and an existing service is only ever
# stopped or deleted when it runs the executable this project installed.
#
# The installation itself is a transaction (Invoke-G29Install): each step has
# an undo, and a failure at any step undoes the completed steps in reverse, so
# the machine is left as it was, a previous installation included. The steps
# reach Windows only through a "machine" (New-WindowsMachine), which the tests
# replace with an in-memory one to inject failures.

# The stable identity (AGENT.md "Installation/service invariants").
$script:G29ServiceName = 'G29Standalone'
$script:G29DirectoryName = 'G29Standalone'
$script:G29ShippedFiles = @('g29ctl.exe', 'g29ffb64.dll', 'g29ffb32.dll')
$script:G29BackupSuffix = '.g29previous'
# The DirectInput class the program registers (HKLM, 64-bit view).
$script:G29ClassKey = 'HKLM:\SOFTWARE\Classes\CLSID\{D252A2D4-A917-47D3-BD1B-F5A0138CFE12}\InprocServer32'

# The per-user part of that registration (tests\RegistrationTests.cs checks that
# these are the keys the program writes). "g29ctl ffb-unregister" can only reach the HKCU of the account
# that runs it; Remove-G29UserRegistration removes the same keys from every
# other user's hive, and only where that hive's own marker proves this project
# created them.
$script:G29Clsid = '{D252A2D4-A917-47D3-BD1B-F5A0138CFE12}'
$script:G29OemKey = 'System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_046D&PID_C24F'
$script:G29MarkerKey = 'Software\G29Standalone\DirectInput'
$script:G29ProductKey = 'Software\G29Standalone'
# LocalSystem (a deployment tool installs into its own HKCU), local and domain
# accounts (S-1-5-21-...) and Microsoft Entra ID accounts (S-1-12-1-...); never
# LocalService/NetworkService or the _Classes hives.
$script:G29UserSidPattern = '^S-1-(5-18|5-21(-[0-9]+){4}|12-1(-[0-9]+){4})$'

$script:InstallPlanKeys = @('service', 'display', 'description', 'directory', 'startup', 'arguments', 'trigger', 'failure-reset', 'failure-actions', 'start-after-install')

# The G29 USB hardware IDs a trigger may name: the product IDs the program
# recognizes (native, compatibility modes, PS4 mode).
$script:TriggerHardwareId = 'USB\\VID_046D&PID_(C24F|C294|C298|C299|C29A|C29B|C260)'

# What each value must look like. The program picks the values; these bound
# what a wrong program could make an administrator do.
$script:InstallPlanShapes = @{
    'service'             = '^G29Standalone$'
    'display'             = '^[\x20-\x7E]{1,256}$'
    'description'         = '^[\x20-\x7E]{1,256}$'
    'directory'           = '^G29Standalone$'
    'startup'             = '^(Manual|Automatic)$'
    # the internal service command and its two documented options only
    'arguments'           = '^service( --range [0-9]{1,3})?( --autocenter [0-9]{1,3})?$'
    # start on arrival of the USB device interface (GUID_DEVINTERFACE_USB_DEVICE)
    # of a known G29 product ID
    'trigger'             = '^start/device/a5dcbf10-6530-11d2-901f-00c04fb951ed(/' + $script:TriggerHardwareId + '){1,7}$'
    'failure-reset'       = '^[0-9]{1,6}$'
    # restart actions only: a "run" action would execute a command as LocalSystem
    'failure-actions'     = '^restart/[0-9]{1,6}(/restart/[0-9]{1,6}){0,2}$'
    'start-after-install' = '^(yes|no)$'
}

# A console window opened only for this script (Explorer's "Run with
# PowerShell", the elevated step) closes when the script ends; wait for Enter
# there so the result stays readable. A shell the user typed into, or
# redirected input, returns at once.
function Wait-ConsoleClose {
    if ([Console]::IsInputRedirected) { return }
    if (-not ('G29.ConsoleWindow' -as [type])) {
        Add-Type -Namespace G29 -Name ConsoleWindow -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint GetConsoleProcessList(uint[] list, uint count);'
    }

    if ([G29.ConsoleWindow]::GetConsoleProcessList([uint32[]]::new(4), 4) -eq 1) {
        $null = Read-Host 'Press Enter to close this window'
    }
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Parses and validates install-plan output. Pure: no process, no machine state.
function ConvertFrom-InstallPlan([string[]]$Lines) {
    $plan = @{}
    foreach ($line in $Lines) {
        if ($line -cnotmatch '^[a-z-]+ ') {
            throw 'The installation plan has a malformed line.'
        }

        $key, $value = $line -split ' ', 2
        if ($plan.ContainsKey($key)) {
            throw "The installation plan repeats '$key'."
        }

        $plan[$key] = $value
    }

    foreach ($key in $plan.Keys) {
        if ($script:InstallPlanKeys -notcontains $key) {
            throw "The installation plan has an unknown key '$key'."
        }
    }

    foreach ($key in $script:InstallPlanKeys) {
        if (-not $plan.ContainsKey($key) -or [string]::IsNullOrEmpty($plan[$key])) {
            throw "The installation plan has no '$key'."
        }

        if ($plan[$key] -cnotmatch $script:InstallPlanShapes[$key]) {
            throw "The installation plan has an unexpected '$key'."
        }
    }

    return $plan
}

# The service options (--range/--autocenter) of a service command line such as
# '"C:\...\g29ctl.exe" service --range 900 --autocenter 0'; an empty list when
# there are none; $null when the line is not one this project writes.
function Get-ServiceOptions([string]$PathName, [string]$InstalledExecutable) {
    if (-not (Test-OwnedServicePath $PathName $InstalledExecutable)) {
        return $null
    }

    $arguments = $PathName.Substring($InstalledExecutable.Length + 2).Trim()
    if ($arguments -cnotmatch $script:InstallPlanShapes['arguments']) {
        return $null
    }

    return @($arguments -split ' ' | Select-Object -Skip 1)
}

# True when a service command line starts with the quoted installed executable.
function Test-OwnedServicePath([string]$PathName, [string]$InstalledExecutable) {
    if ([string]::IsNullOrEmpty($PathName)) {
        return $false
    }

    $quoted = "`"$InstalledExecutable`""
    return $PathName.Equals($quoted, [StringComparison]::OrdinalIgnoreCase) -or
        $PathName.StartsWith("$quoted ", [StringComparison]::OrdinalIgnoreCase)
}

# The canonical install directory under Program Files.
function Get-G29InstallDirectory([string]$ProgramFiles = $env:ProgramFiles) {
    return Join-Path ([IO.Path]::GetFullPath($ProgramFiles).TrimEnd('\')) $script:G29DirectoryName
}

# SHA-256 of each shipped file, as 'name=hex' pairs joined by ';' (the form the
# unprivileged phase hands to the elevated one on its command line).
function Get-ArtifactHashes([string]$Directory) {
    $pairs = foreach ($name in $script:G29ShippedFiles) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "$path is missing; run build.ps1 first."
        }

        "$name=$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)"
    }

    return $pairs -join ';'
}

function ConvertFrom-ArtifactHashes([string]$Text) {
    $hashes = @{}
    foreach ($pair in ($Text -split ';')) {
        if ($pair -cnotmatch '^([A-Za-z0-9.]+)=([0-9A-F]{64})$') {
            throw 'The artifact hashes are malformed.'
        }

        $hashes[$Matches[1]] = $Matches[2]
    }

    foreach ($name in $script:G29ShippedFiles) {
        if (-not $hashes.ContainsKey($name)) {
            throw "The artifact hashes do not name $name."
        }
    }

    if ($hashes.Count -ne $script:G29ShippedFiles.Count) {
        throw 'The artifact hashes name unexpected files.'
    }

    return $hashes
}

# A junction or symbolic link in place of the install directory could redirect
# a recursive delete elsewhere; the installer never follows one.
function Assert-NoReparsePoint([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Path is a link or junction; refusing to use it."
    }
}

# ---------------------------------------------------------------------------
# The machine: every Windows change the installer makes goes through one of
# these operations.
# ---------------------------------------------------------------------------

function New-WindowsMachine {
    return @{
        # $null, or @{ PathName; Running }
        GetService        = {
            param([string]$Name)
            $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'"
            if ($null -eq $service) { return $null }
            return @{ PathName = $service.PathName; Running = ($service.State -ne 'Stopped') }
        }
        StopService       = {
            param([string]$Name)
            $service = Get-Service -Name $Name
            if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                Stop-Service -Name $Name -Force
                $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(15))
            }
        }
        DeleteService     = {
            param([string]$Name)
            & sc.exe delete $Name | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not delete Windows service $Name." }
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            while ($null -ne (Get-Service -Name $Name -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 200
            }
        }
        CreateService     = {
            param([hashtable]$Plan, [string]$BinaryPath)
            New-Service -Name $Plan['service'] -BinaryPathName $BinaryPath -DisplayName $Plan['display'] -Description $Plan['description'] -StartupType $Plan['startup'] | Out-Null
            & sc.exe triggerinfo $Plan['service'] $Plan['trigger'] | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not configure the device trigger for Windows service $($Plan['service'])." }
            & sc.exe failure $Plan['service'] reset= $Plan['failure-reset'] actions= $Plan['failure-actions'] | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not configure recovery for Windows service $($Plan['service'])." }
        }
        StartService      = {
            param([string]$Name)
            Start-Service -Name $Name
        }
        ListFiles         = {
            param([string]$Directory)
            if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return @() }
            return @(Get-ChildItem -LiteralPath $Directory -File | ForEach-Object Name)
        }
        CreateDirectory   = {
            param([string]$Directory)
            Assert-NoReparsePoint $Directory
            $existed = Test-Path -LiteralPath $Directory -PathType Container
            New-Item -ItemType Directory -Path $Directory -Force | Out-Null
            return -not $existed
        }
        RemoveDirectory   = {
            param([string]$Directory)
            Assert-NoReparsePoint $Directory
            if (Test-Path -LiteralPath $Directory) { Remove-Item -LiteralPath $Directory -Recurse -Force }
        }
        MoveFile          = {
            param([string]$From, [string]$To)
            Move-Item -LiteralPath $From -Destination $To -Force
        }
        CopyFile          = {
            param([string]$From, [string]$To)
            Copy-Item -LiteralPath $From -Destination $To -Force
        }
        RemoveFile        = {
            param([string]$Path)
            if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
        }
        FileHash          = {
            param([string]$Path)
            return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        }
        # @{ ExitCode; Output }
        InvokeG29         = {
            param([string]$Executable, [string[]]$Arguments)
            $output = @(& $Executable @Arguments)
            return @{ ExitCode = $LASTEXITCODE; Output = $output }
        }
        IsRegistered      = {
            return Test-Path -LiteralPath $script:G29ClassKey
        }
        RemoveEventSource = {
            param([string]$Name)
            if ([System.Diagnostics.EventLog]::SourceExists($Name)) {
                [System.Diagnostics.EventLog]::DeleteEventSource($Name)
            }
        }
        # Every user hive, as @{ Sid; Name (under HKEY_USERS); Mounted; Error }:
        # the loaded ones, and each other profile's NTUSER.DAT loaded under a
        # temporary name (DismountUserHive unloads it again).
        MountUserHives    = {
            $loaded = @([Microsoft.Win32.Registry]::Users.GetSubKeyNames())
            $hives = @()
            foreach ($name in $loaded) {
                if ($name -cmatch $script:G29UserSidPattern) {
                    $hives += @{ Sid = $name; Name = $name; Mounted = $false; Error = $null }
                } elseif ($name.StartsWith('G29Standalone-') -and $name.Substring(14) -cmatch $script:G29UserSidPattern) {
                    # left loaded by an earlier uninstall that could not unload it
                    $hives += @{ Sid = $name.Substring(14); Name = $name; Mounted = $true; Error = $null }
                }
            }

            $profiles = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList')
            if ($null -eq $profiles) { return $hives }
            try {
                foreach ($sid in $profiles.GetSubKeyNames()) {
                    if ($sid -cnotmatch $script:G29UserSidPattern -or $loaded -contains $sid -or $loaded -contains "G29Standalone-$sid") { continue }
                    $key = $profiles.OpenSubKey($sid)
                    if ($null -eq $key) { continue }
                    try { $directory = [string]$key.GetValue('ProfileImagePath') } finally { $key.Dispose() }
                    if ([string]::IsNullOrEmpty($directory)) { continue }
                    $file = Join-Path ([Environment]::ExpandEnvironmentVariables($directory)) 'NTUSER.DAT'
                    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
                    $name = "G29Standalone-$sid"
                    & reg.exe load "HKU\$name" $file | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        $hives += @{ Sid = $sid; Name = $name; Mounted = $true; Error = $null }
                    } else {
                        $hives += @{ Sid = $sid; Name = $null; Mounted = $false; Error = "could not load $file (reg.exe exit code $LASTEXITCODE)" }
                    }
                }
            } finally {
                $profiles.Dispose()
            }

            return $hives
        }
        DismountUserHive  = {
            param([string]$Name)
            # a registry handle still open in this process would keep the hive loaded
            [GC]::Collect()
            [GC]::WaitForPendingFinalizers()
            & reg.exe unload "HKU\$Name" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not unload HKEY_USERS\$Name." }
        }
        # Paths below are relative to HKEY_USERS.
        TestUserKey       = {
            param([string]$Path)
            $key = [Microsoft.Win32.Registry]::Users.OpenSubKey($Path)
            if ($null -eq $key) { return $false }
            $key.Dispose()
            return $true
        }
        # True when the key exists and has neither subkeys nor values.
        TestUserKeyEmpty  = {
            param([string]$Path)
            $key = [Microsoft.Win32.Registry]::Users.OpenSubKey($Path)
            if ($null -eq $key) { return $false }
            try { return ($key.SubKeyCount -eq 0 -and $key.ValueCount -eq 0) } finally { $key.Dispose() }
        }
        # The value, or $null when the key or the value does not exist.
        ReadUserValue     = {
            param([string]$Path, [string]$Name)
            $key = [Microsoft.Win32.Registry]::Users.OpenSubKey($Path)
            if ($null -eq $key) { return $null }
            try { return $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } finally { $key.Dispose() }
        }
        RemoveUserKey     = {
            param([string]$Path)
            [Microsoft.Win32.Registry]::Users.DeleteSubKeyTree($Path, $false)
        }
    }
}

# ---------------------------------------------------------------------------
# Transaction
# ---------------------------------------------------------------------------

# Runs each step's Do; when one fails, runs the Undo of every completed step in
# reverse order (best effort, all of them), then rethrows the failure.
function Invoke-Transaction([object[]]$Steps, [hashtable]$Machine, [hashtable]$State) {
    $completed = New-Object System.Collections.Generic.List[object]
    try {
        foreach ($step in $Steps) {
            $completed.Add($step)
            & $step.Do $Machine $State
        }
    } catch {
        $failure = $_
        $problems = @()
        for ($index = $completed.Count - 1; $index -ge 0; $index--) {
            if ($null -ne $completed[$index].Undo) {
                try {
                    & $completed[$index].Undo $Machine $State
                } catch {
                    $problems += "$($completed[$index].Name): $($_.Exception.Message)"
                }
            }
        }

        if ($problems.Count -gt 0) {
            Write-Warning ("The rollback was incomplete; run Uninstall-Driver.ps1. " + ($problems -join ' '))
        }

        throw $failure
    }
}

function Assert-G29Exit($Result, [string]$What) {
    if ($Result.ExitCode -ne 0) {
        throw "$What failed with exit code $($Result.ExitCode)."
    }
}

# The service this project installed, or $null. Throws when a service of the
# canonical name exists but runs anything other than the installed executable.
function Get-G29OwnedService([hashtable]$Machine, [string]$InstalledExecutable) {
    $service = & $Machine.GetService $script:G29ServiceName
    if ($null -ne $service -and -not (Test-OwnedServicePath $service.PathName $InstalledExecutable)) {
        throw "A Windows service named $script:G29ServiceName exists but does not run $InstalledExecutable; refusing to modify it."
    }

    return $service
}

# Creates the service from a plan the installed executable produced.
function New-G29Service([hashtable]$Machine, [string]$Executable, [string[]]$Options) {
    $result = & $Machine.InvokeG29 $Executable (@('install-plan') + $Options)
    Assert-G29Exit $result 'g29ctl install-plan'
    $plan = ConvertFrom-InstallPlan @($result.Output)
    & $Machine.CreateService $plan "`"$Executable`" $($plan['arguments'])"
    return $plan
}

# The installation transaction. Context: SourceDirectory (built binaries),
# Hashes (ConvertFrom-ArtifactHashes), Options (install-plan options),
# SkipForceFeedback, InstallDirectory, LicensePath.
function Invoke-G29Install([hashtable]$Context, [hashtable]$Machine) {
    $directory = $Context.InstallDirectory
    $executable = Join-Path $directory 'g29ctl.exe'
    $shipped = @($script:G29ShippedFiles) + @('LICENSE')
    $state = @{
        Directory  = $directory
        Executable = $executable
        Shipped    = $shipped
        Context    = $Context
    }

    $steps = @(
        @{
            Name = 'inspect the existing installation'
            Do   = {
                param($machine, $state)
                $state.Previous = Get-G29OwnedService $machine $state.Executable
                $state.PreviousOptions = @()
                if ($null -ne $state.Previous) {
                    $state.PreviousOptions = Get-ServiceOptions $state.Previous.PathName $state.Executable
                    if ($null -eq $state.PreviousOptions) {
                        throw "The existing $script:G29ServiceName service has a command line this installer did not write; refusing to modify it."
                    }
                }

                $state.PreviousRegistered = [bool](& $machine.IsRegistered)
            }
            Undo = $null
        },
        @{
            Name = 'stop the existing service'
            Do   = {
                param($machine, $state)
                if ($null -ne $state.Previous -and $state.Previous.Running) {
                    & $machine.StopService $script:G29ServiceName
                    $state.PreviousStopped = $true
                }
            }
            Undo = {
                param($machine, $state)
                # runs last: the previous service exists again by now
                if ($state.PreviousStopped) {
                    $service = & $machine.GetService $script:G29ServiceName
                    if ($null -ne $service -and -not $service.Running) {
                        & $machine.StartService $script:G29ServiceName
                    }
                }
            }
        },
        @{
            Name = 'remove the existing service'
            Do   = {
                param($machine, $state)
                if ($null -ne $state.Previous) {
                    & $machine.DeleteService $script:G29ServiceName
                    $state.PreviousDeleted = $true
                }
            }
            Undo = {
                param($machine, $state)
                # runs after the previous files were restored
                if ($state.PreviousDeleted) {
                    $null = New-G29Service $machine $state.Executable $state.PreviousOptions
                    $state.PreviousRecreated = $true
                }

                if ($state.PreviousRegistered -and $state.SetAside -contains 'g29ctl.exe') {
                    Assert-G29Exit (& $machine.InvokeG29 $state.Executable @('ffb-register')) 'Restoring the previous DirectInput registration'
                }
            }
        },
        @{
            Name = 'set the existing files aside'
            Do   = {
                param($machine, $state)
                $state.CreatedDirectory = [bool](& $machine.CreateDirectory $state.Directory)
                $state.SetAside = @()
                foreach ($name in @(& $machine.ListFiles $state.Directory)) {
                    if ($name.EndsWith($script:G29BackupSuffix, [StringComparison]::OrdinalIgnoreCase)) {
                        # left behind by an earlier installation that could not delete it
                        & $machine.RemoveFile (Join-Path $state.Directory $name)
                        continue
                    }

                    & $machine.MoveFile (Join-Path $state.Directory $name) (Join-Path $state.Directory ($name + $script:G29BackupSuffix))
                    $state.SetAside += $name
                }
            }
            Undo = {
                param($machine, $state)
                # runs after the copy step's undo removed the new files
                foreach ($name in $state.SetAside) {
                    & $machine.MoveFile (Join-Path $state.Directory ($name + $script:G29BackupSuffix)) (Join-Path $state.Directory $name)
                }

                if ($state.CreatedDirectory) {
                    & $machine.RemoveDirectory $state.Directory
                }
            }
        },
        @{
            Name = 'copy and verify the new files'
            Do   = {
                param($machine, $state)
                $context = $state.Context
                $state.Copied = @()
                foreach ($name in $script:G29ShippedFiles) {
                    $target = Join-Path $state.Directory $name
                    & $machine.CopyFile (Join-Path $context.SourceDirectory $name) $target
                    $state.Copied += $target
                    # checked where only administrators can write: what runs
                    # elevated is exactly what the unprivileged phase built
                    $hash = & $machine.FileHash $target
                    if ($hash -ne $context.Hashes[$name]) {
                        throw "$name changed after it was built (SHA-256 $hash); refusing to install it."
                    }
                }

                $license = Join-Path $state.Directory 'LICENSE'
                & $machine.CopyFile $context.LicensePath $license
                $state.Copied += $license
            }
            Undo = {
                param($machine, $state)
                foreach ($path in $state.Copied) {
                    & $machine.RemoveFile $path
                }
            }
        },
        @{
            Name = 'create the service'
            Do   = {
                param($machine, $state)
                $state.Created = $true
                $state.Plan = New-G29Service $machine $state.Executable $state.Context.Options
            }
            Undo = {
                param($machine, $state)
                $service = & $machine.GetService $script:G29ServiceName
                if ($null -ne $service -and (Test-OwnedServicePath $service.PathName $state.Executable)) {
                    if ($service.Running) {
                        & $machine.StopService $script:G29ServiceName
                    }

                    & $machine.DeleteService $script:G29ServiceName
                }
            }
        },
        @{
            Name = 'register the DirectInput force feedback driver'
            Do   = {
                param($machine, $state)
                if (-not $state.Context.SkipForceFeedback) {
                    $state.RegisterAttempted = $true
                    Assert-G29Exit (& $machine.InvokeG29 $state.Executable @('ffb-register')) 'DirectInput force feedback registration (g29ctl doctor has details; -SkipForceFeedback installs without it)'
                }
            }
            Undo = {
                param($machine, $state)
                if ($state.RegisterAttempted) {
                    # removes exactly what the new version registered; a previous
                    # registration is restored by the service step's undo
                    Assert-G29Exit (& $machine.InvokeG29 $state.Executable @('ffb-unregister')) 'Removing the new DirectInput registration'
                }
            }
        },
        @{
            Name = 'start the service'
            Do   = {
                param($machine, $state)
                if ($state.Plan['start-after-install'] -eq 'yes') {
                    & $machine.StartService $script:G29ServiceName
                }
            }
            Undo = $null # the create step's undo stops it
        },
        @{
            Name = 'check the installation'
            Do   = {
                param($machine, $state)
                $service = Get-G29OwnedService $machine $state.Executable
                if ($null -eq $service) {
                    throw "The $script:G29ServiceName service is missing after installation."
                }

                foreach ($name in $state.Shipped) {
                    if (@(& $machine.ListFiles $state.Directory) -notcontains $name) {
                        throw "$name is missing after installation."
                    }
                }

                if (-not $state.Context.SkipForceFeedback -and -not (& $machine.IsRegistered)) {
                    throw 'The DirectInput registration is missing after installation.'
                }
            }
            Undo = $null
        }
    )

    Invoke-Transaction $steps $Machine $state

    # Committed. Old files a game still has loaded stay until the next install.
    foreach ($name in @(& $Machine.ListFiles $directory)) {
        if ($name.EndsWith($script:G29BackupSuffix, [StringComparison]::OrdinalIgnoreCase)) {
            try {
                & $Machine.RemoveFile (Join-Path $directory $name)
            } catch {
                Write-Warning "Could not remove the old file $name (a game may still have it loaded): $($_.Exception.Message)"
            }
        }
    }

    return $state.Plan
}

# Removes this project's DirectInput registration from one user hive (Hive is
# its name under HKEY_USERS): the keys `g29ctl ffb-unregister` removes from
# the HKCU of the account running it, under the same conditions. Nothing unless the hive's own marker exists; the whole OEM key when the
# marker says it was created; otherwise, only while OEMForceFeedback's CLSID
# is ours, the steering axis when the marker says it was created and then
# OEMForceFeedback. The proof goes last (our CLSID, then the marker), so an
# uninstall that fails part-way can be run again. An empty product key is
# removed in any case. Returns whether the hive had a marker.
function Remove-G29UserRegistration([hashtable]$Machine, [string]$Hive) {
    $marker = "$Hive\$script:G29MarkerKey"
    $owned = [bool](& $Machine.TestUserKey $marker)
    if ($owned) {
        $oem = "$Hive\$script:G29OemKey"
        if ($null -ne (& $Machine.ReadUserValue $marker 'CreatedOemKey')) {
            # a key we created: everything else in it is DirectInput's own cache
            & $Machine.RemoveUserKey $oem
        } else {
            $clsid = & $Machine.ReadUserValue "$oem\OEMForceFeedback" 'CLSID'
            if ($clsid -is [string] -and $clsid.Equals($script:G29Clsid, [StringComparison]::OrdinalIgnoreCase)) {
                if ($null -ne (& $Machine.ReadUserValue $marker 'CreatedSteeringAxis')) {
                    & $Machine.RemoveUserKey "$oem\Axes\0"
                    if (& $Machine.TestUserKeyEmpty "$oem\Axes") {
                        & $Machine.RemoveUserKey "$oem\Axes"
                    }
                }

                & $Machine.RemoveUserKey "$oem\OEMForceFeedback"
            }
        }

        & $Machine.RemoveUserKey $marker
    }

    if (& $Machine.TestUserKeyEmpty "$Hive\$script:G29ProductKey") {
        & $Machine.RemoveUserKey "$Hive\$script:G29ProductKey"
    }

    return $owned
}

# Runs Remove-G29UserRegistration on every user hive, loaded or not. Returns
# one message per hive that could not be cleaned; an empty list on success.
function Remove-G29UserRegistrations([hashtable]$Machine) {
    $problems = @()
    try {
        $hives = @(& $Machine.MountUserHives)
    } catch {
        return @("the user hives could not be listed: $($_.Exception.Message)")
    }

    try {
        foreach ($hive in $hives) {
            if ($null -ne $hive.Error) {
                $problems += "$($hive.Sid): $($hive.Error)"
                continue
            }

            try {
                $null = Remove-G29UserRegistration $Machine $hive.Name
            } catch {
                $problems += "$($hive.Sid): $($_.Exception.Message)"
            }
        }
    } finally {
        foreach ($hive in $hives) {
            if ($hive.Mounted) {
                try {
                    & $Machine.DismountUserHive $hive.Name
                } catch {
                    $problems += "$($hive.Sid): $($_.Exception.Message)"
                }
            }
        }
    }

    return $problems
}

# Removes everything the installer creates: the DirectInput registration (in
# HKLM and in every user's hive), the service, the install directory and the
# event log source.
function Remove-G29Installation([hashtable]$Machine, [string]$InstallDirectory) {
    $executable = Join-Path $InstallDirectory 'g29ctl.exe'

    # checked before anything is removed
    $service = Get-G29OwnedService $Machine $executable

    if (@(& $Machine.ListFiles $InstallDirectory) -contains 'g29ctl.exe') {
        Assert-G29Exit (& $Machine.InvokeG29 $executable @('ffb-unregister')) 'Removing the DirectInput force feedback registration'
    }

    # the installing account may not be the one uninstalling (for example an
    # interactive install removed by a deployment tool running as SYSTEM)
    $problems = @(Remove-G29UserRegistrations $Machine)

    if ($null -ne $service) {
        if ($service.Running) {
            & $Machine.StopService $script:G29ServiceName
        }

        & $Machine.DeleteService $script:G29ServiceName
    }

    & $Machine.RemoveDirectory $InstallDirectory
    & $Machine.RemoveEventSource $script:G29ServiceName

    if ($problems.Count -gt 0) {
        throw ("The per-user DirectInput registration could not be removed from every account; run Uninstall-Driver.ps1 again. " + ($problems -join ' '))
    }
}

# Runs this script again elevated and exits with its exit code.
function Invoke-Elevated([string]$ScriptPath, [string]$Arguments) {
    # The elevated process runs under the execution policy the caller chose for
    # this process, and never a weaker one.
    $policy = Get-ExecutionPolicy -Scope Process
    $policyArgument = if ($policy -ne 'Undefined') { "-ExecutionPolicy $policy " } else { '' }
    # The script keeps its own window open (Wait-ConsoleClose); this wrapper
    # only turns a thrown error into a failing exit code.
    $quotedPath = $ScriptPath.Replace("'", "''")
    $command = "try { & '$quotedPath' $Arguments; exit `$LASTEXITCODE } catch { exit 1 }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $commandLine = "-NoProfile $policyArgument-EncodedCommand $encoded"
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $commandLine -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        Write-Error "The elevated step failed (exit code $($process.ExitCode)); its window showed the error." -ErrorAction Continue
    } else {
        Write-Host 'The elevated step completed.'
    }

    Wait-ConsoleClose
    exit $process.ExitCode
}
