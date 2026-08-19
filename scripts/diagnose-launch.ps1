<#
.SYNOPSIS
    Works out why Windows will not start 1remote.exe.

.DESCRIPTION
    The agent is an unsigned executable in a user-writable folder, which is a shape
    several Windows protections dislike. When one of them refuses, the message is
    almost always the same three words -- "Access is denied" -- regardless of which
    one it was, and each has a different remedy: one clears itself in seconds, one
    needs an administrator, one cannot be fixed on that machine at all.

    This asks each of them in turn and prints what it finds, so the answer comes from
    the machine rather than from guessing. It reads logs and policy; it changes
    nothing.

    Elevation is not required, but a few checks say "needs elevation" without it.

.PARAMETER Path
    The executable to ask about. Defaults to where install.ps1 puts it.

.PARAMETER Minutes
    How far back to read the event logs. Defaults to 60.

.EXAMPLE
    irm https://raw.githubusercontent.com/eranyariv/1RemoteCLI/main/scripts/diagnose-launch.ps1 | iex

.EXAMPLE
    .\scripts\diagnose-launch.ps1 -Minutes 240
#>
[CmdletBinding()]
param(
    [string] $Path = (Join-Path $env:LOCALAPPDATA 'Programs\1RemoteCLI\1remote.exe'),
    [int] $Minutes = 60
)

$ErrorActionPreference = 'Continue'

$findings = New-Object System.Collections.ArrayList
$startedNow = $false

function Write-Section($text) {
    Write-Host ''
    Write-Host "== $text" -ForegroundColor Cyan
}

function Write-Fact($label, $value) {
    Write-Host ("   {0,-14} {1}" -f $label, $value)
}

function Add-Finding($severity, $text) {
    [void]$findings.Add([pscustomobject]@{ Severity = $severity; Text = $text })
}

# Reading another provider's log is not always permitted, and the failure is not
# interesting enough to interrupt the report.
function Get-Events($logName, $ids, $since) {
    try {
        @(Get-WinEvent -FilterHashtable @{ LogName = $logName; Id = $ids; StartTime = $since } -ErrorAction Stop)
    }
    catch {
        @()
    }
}

function Get-EventFields($event) {
    $fields = @{}

    try {
        ([xml]$event.ToXml()).Event.EventData.Data | ForEach-Object {
            if ($_.Name) { $fields[$_.Name] = $_.'#text' }
        }
    }
    catch {
    }

    $fields
}

$since = (Get-Date).AddMinutes(-$Minutes)
$leaf = Split-Path $Path -Leaf

Write-Host ''
Write-Host "1RemoteCLI launch diagnosis" -ForegroundColor White
Write-Host "$Path"
Write-Host ("Looking back {0} minutes, to {1:HH:mm:ss}." -f $Minutes, $since)

# ---------------------------------------------------------------- the file itself

Write-Section 'The file'

$file = Get-Item $Path -ErrorAction SilentlyContinue

if (-not $file) {
    Write-Fact 'present' 'NO -- nothing at that path'
    Add-Finding 'blocking' "There is no file at $Path. The install did not get as far as copying it; re-run install.ps1 and read what it says."
}
else {
    Write-Fact 'size' ("{0:N0} bytes" -f $file.Length)
    Write-Fact 'written' $file.LastWriteTime
    Write-Fact 'sha256' (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()

    # The hash is worth printing even without the release to compare against: it is
    # the first thing to check by hand, and a truncated download is a real failure
    # mode that looks like everything else here.
    $streams = @(Get-Item $Path -Stream * -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Stream)
    Write-Fact 'streams' ($streams -join ', ')

    if ($streams -contains 'Zone.Identifier') {
        Add-Finding 'suspect' "The file still carries the mark of the web, so SmartScreen will prompt. Clear it with: Unblock-File '$Path'"
    }

    $acl = Get-Acl $Path -ErrorAction SilentlyContinue

    if ($acl) {
        Write-Fact 'owner' $acl.Owner

        $denied = @($acl.Access | Where-Object { $_.AccessControlType -eq 'Deny' })

        foreach ($ace in $denied) {
            Write-Fact 'DENY' ("{0}: {1}" -f $ace.IdentityReference, $ace.FileSystemRights)
            Add-Finding 'blocking' "There is a Deny entry on the file for $($ace.IdentityReference). That is an access control problem, not a security policy one."
        }

        if (-not $denied) { Write-Fact 'deny aces' 'none' }
    }
}

# ------------------------------------------------------------------- can it start

Write-Section 'Starting it'

if ($file) {
    try {
        $output = (& $Path --version 2>&1 | Out-String).Trim()
        Write-Fact 'result' "started, exit $LASTEXITCODE"
        Write-Fact 'printed' $output
        $startedNow = $true
    }
    catch {
        Write-Fact 'result' 'REFUSED'
        Write-Fact 'error' $_.Exception.Message
        Add-Finding 'blocking' "Windows refused to start it just now: $($_.Exception.Message)"
        $startedNow = $false
    }
}

# ----------------------------------------------------------- attack surface reduction

Write-Section 'Attack surface reduction'

# Rules delivered by Intune do not appear in Get-MpPreference at all, which makes
# an empty list there look like an answer when it is not. The event log is the
# thing that actually knows.
$asr = @(Get-Events 'Microsoft-Windows-Windows Defender/Operational' @(1121, 1122) $since)
$mine = @()

foreach ($event in $asr) {
    $fields = Get-EventFields $event
    if ($fields['Path'] -and ($fields['Path'] -like "*$leaf*" -or $fields['Path'] -like '*1REMOT~1*')) {
        $mine += [pscustomobject]@{
            When    = $event.TimeCreated
            Blocked = ($event.Id -eq 1121)
            Rule    = $fields['ID']
            By      = $fields['Process Name']
        }
    }
}

if (-not $asr) {
    Write-Fact 'events' 'none readable (either none happened, or this needs elevation)'
}
elseif (-not $mine) {
    Write-Fact 'events' "$($asr.Count) in the window, none about this file"
}
else {
    foreach ($m in $mine) {
        $verb = 'audited'
        if ($m.Blocked) { $verb = 'BLOCKED' }
        Write-Fact $verb ("{0:HH:mm:ss}  rule {1}  launched by {2}" -f $m.When, $m.Rule, (Split-Path $m.By -Leaf))
    }

    $blocked = @($mine | Where-Object { $_.Blocked })

    if ($blocked) {
        $ransomware = @($blocked | Where-Object { $_.Rule -eq 'C1DB55AB-C21A-4637-BB3F-A12568109D35' })

        # Whether the block will ever lift is the only question worth answering here,
        # and Defender does not report it. Cloud lookups looked like the signal -- one
        # machine paired them with every block and allowed the file twenty minutes
        # later -- but another asks occasionally and has still refused the same file
        # for the best part of an hour. So use the thing actually being asked about:
        # how long this machine has been saying no. Past the point where waiting works
        # elsewhere, waiting has been tried here, and it has failed.
        $lookups = Get-Events 'Microsoft-Windows-Windows Defender/Operational' 2010 $since
        $asked = @($blocked | Where-Object {
                $block = $_.When
                $lookups | Where-Object { [math]::Abs(($_.TimeCreated - $block).TotalSeconds) -le 90 }
            })

        Write-Fact 'asked cloud' "$($asked.Count) of $($blocked.Count) blocks"

        $first = ($blocked | Sort-Object When | Select-Object -First 1).When
        $spanMinutes = [int]((Get-Date) - $first).TotalMinutes
        $span = "$spanMinutes minutes"

        if ($spanMinutes -ge ($Minutes - 1)) {
            $span = "at least $spanMinutes minutes, which is as far back as this report looks"
        }

        Write-Fact 'refusing for' $span

        if ($ransomware) {
            $rule = "An attack surface reduction rule blocked the launch: 'Use advanced protection against ransomware'."

            # From the file's metadata rather than by running it: the whole context
            # here is that running it is what Windows just refused, and each refused
            # launch adds an event to the log this report is reading.
            $fileVersion = try { [version] $file.VersionInfo.FileVersion } catch { $null }
            $old = $fileVersion -and $fileVersion -lt [version] '0.9.0.0'

            if ($old) {
                $rule += " This build is $($file.VersionInfo.ProductVersion), and releases up to 0.08 were compressed single-file bundles -- a self-extracting high-entropy blob, which is the shape of a packer and is what this rule objects to. That is almost certainly what is happening here. Upgrading to 0.09 or later fixes it: those are published uncompressed, and both machines that used to refuse then accepted them."
            }
            else {
                $rule += ' Compression was the usual cause of this and was removed in 0.09, so on this build something else is refusing it.'
            }

            if ($startedNow) {
                Add-Finding 'blocking' "$rule It has stopped: the file ran when this report tried it."
            }
            elseif ($old) {
                Add-Finding 'blocking' $rule
            }
            elseif ($spanMinutes -ge 30) {
                Add-Finding 'blocking' "$rule Where it lifts on its own it takes about twenty minutes, and this machine has been refusing the same file for $span. Waiting has already been tried here and has not worked, so rescanning, reinstalling or downloading it again will not help either. It needs an administrator to allow it, or a signed build."
            }
            else {
                Add-Finding 'blocking' "$rule It can still come back clean and be allowed, but not quickly: twenty minutes has been measured, so a few seconds of retrying proves nothing. Wait half an hour, then run this report again -- if it is still refusing by then, waiting is not going to fix it."
            }
        }
        else {
            Add-Finding 'blocking' "An attack surface reduction rule blocked the launch: $($blocked[0].Rule). Look it up in Microsoft's ASR rules reference; if it is enforced by policy, an administrator has to allow it."
        }
    }
}

$preference = Get-MpPreference -ErrorAction SilentlyContinue

if ($preference) {
    Write-Fact 'local rules' "$($preference.AttackSurfaceReductionRules_Ids.Count) (Intune-managed rules do not appear here)"
}

$managed = 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\Defender'

if (Test-Path $managed) {
    $winning = (Get-ItemProperty $managed -ErrorAction SilentlyContinue).AttackSurfaceReductionRules_WinningProvider

    if ($winning) {
        Write-Fact 'managed by' "policy (provider $winning)"
        Add-Finding 'context' 'Attack surface reduction is managed centrally on this machine, so its rules are your organisation choosing them, not local settings you can change.'
    }
}

# ------------------------------------------------------ cloud protection

Write-Section 'Cloud protection'

# This decides whether an attack surface reduction block is a delay or a wall. The
# ransomware rule lets a file through once the cloud has found it unharmful -- but
# that verdict can only exist if the machine is allowed to ask for it, and allowed
# to send the file. Where sample submission is off, an unsigned build nobody has
# seen before stays unknown forever, and "wait a minute and try again" is wrong
# advice that never comes true.
if ($preference) {
    $maps = switch ($preference.MAPSReporting) {
        0 { 'off' }
        1 { 'basic' }
        2 { 'advanced' }
        default { "unknown ($($preference.MAPSReporting))" }
    }

    $consent = switch ($preference.SubmitSamplesConsent) {
        0 { 'always prompt' }
        1 { 'send safe samples' }
        2 { 'never send' }
        3 { 'send all samples' }
        4 { 'never send' }
        default { "unknown ($($preference.SubmitSamplesConsent))" }
    }

    $level = switch ($preference.CloudBlockLevel) {
        0 { 'default' }
        1 { 'moderate' }
        2 { 'high' }
        4 { 'high plus' }
        6 { 'zero tolerance' }
        default { "unknown ($($preference.CloudBlockLevel))" }
    }

    Write-Fact 'cloud lookup' $maps
    Write-Fact 'sample sending' $consent
    Write-Fact 'block level' $level

    if ($preference.MAPSReporting -eq 0) {
        Add-Finding 'blocking' 'Cloud-delivered protection is off, so this machine never asks Microsoft whether a file is safe. An unsigned build it has not seen before cannot become known-good here, which means the block will not lift on its own no matter how long you wait.'
    }
    elseif ($preference.SubmitSamplesConsent -in @(2, 4)) {
        Add-Finding 'blocking' 'Sample submission is off, so this machine will not send the file for analysis. It can ask whether the file is already known, but it cannot make it known -- so unless somebody else submits this exact build, the block will not lift on its own.'
    }

    if ($preference.CloudBlockLevel -ge 4) {
        Add-Finding 'suspect' "The cloud block level is set to '$level', which is well above default. At that setting Windows blocks anything it is not confident about, and an unsigned build with no reputation is exactly that."
    }
}
else {
    Write-Fact 'settings' 'could not read Get-MpPreference'
}

# ------------------------------------------------------ controlled folder access

Write-Section 'Controlled folder access'

$cfa = @(Get-Events 'Microsoft-Windows-Windows Defender/Operational' @(1123, 1124) $since)
$cfaMine = @($cfa | Where-Object { (Get-EventFields $_)['Path'] -like "*$leaf*" })

if ($cfaMine) {
    Write-Fact 'events' "$($cfaMine.Count) about this file"
    Add-Finding 'blocking' 'Controlled folder access interfered. Allow the app in Windows Security > Virus & threat protection > Ransomware protection.'
}
else {
    Write-Fact 'events' 'none about this file'
}

# --------------------------------------------------------- applocker and code integrity

Write-Section 'AppLocker and code integrity'

$appLocker = @(Get-Events 'Microsoft-Windows-AppLocker/EXE and DLL' @(8003, 8004) $since)
$appLockerMine = @($appLocker | Where-Object { $_.Message -like "*$leaf*" })

if ($appLockerMine) {
    foreach ($event in $appLockerMine | Select-Object -First 3) {
        Write-Fact 'applocker' ("{0:HH:mm:ss}  id {1}  {2}" -f $event.TimeCreated, $event.Id, ($event.Message -replace '\s+', ' '))
    }

    if ($appLockerMine | Where-Object { $_.Id -eq 8004 }) {
        Add-Finding 'blocking' 'AppLocker blocked it. The usual cause is a policy that only allows executables under Program Files and Windows -- and this one installs under your profile. An administrator has to allow the path or the publisher; nothing on the machine can be changed to work around it.'
    }
    else {
        Add-Finding 'suspect' 'AppLocker audited this executable. It is not blocking yet, but the policy is watching it.'
    }
}
else {
    Write-Fact 'applocker' 'no events about this file'
}

$ci = @(Get-Events 'Microsoft-Windows-CodeIntegrity/Operational' @(3076, 3077) $since)
$ciMine = @($ci | Where-Object { $_.Message -like "*$leaf*" })

if ($ciMine) {
    Write-Fact 'code integrity' "$($ciMine.Count) events about this file"
    Add-Finding 'blocking' 'A code integrity (WDAC) policy blocked it. That policy only trusts signed code, so an unsigned build cannot run on this machine at all until it is signed or explicitly allowed.'
}
else {
    Write-Fact 'code integrity' 'no events about this file'
}

$sacKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy'
$sac = (Get-ItemProperty $sacKey -Name VerifiedAndReputablePolicyState -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState

$sacText = switch ($sac) {
    0 { 'off' }
    1 { 'ON -- enforcing' }
    2 { 'evaluation' }
    default { 'not reported' }
}

Write-Fact 'smart app ctrl' $sacText

if ($sac -eq 1) {
    Add-Finding 'blocking' 'Smart App Control is on and enforcing. It only runs signed or well-known software, it cannot be re-enabled once turned off, and it will block every unsigned build. On a machine with it on, this tool needs a signed release.'
}

# ------------------------------------------------------------------- antivirus

Write-Section 'Antivirus'

try {
    $products = @(Get-CimInstance -Namespace 'root\SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction Stop)

    foreach ($product in $products) {
        Write-Fact 'installed' $product.displayName
    }

    $others = @($products | Where-Object { $_.displayName -notlike '*Defender*' })

    if ($others) {
        Add-Finding 'suspect' "There is third-party antivirus here ($($others[0].displayName)). If nothing above explains the refusal, check its quarantine and its logs -- it will not write to the Defender log this script reads."
    }
}
catch {
    Write-Fact 'installed' 'could not enumerate'
}

$detections = @(Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.Resources -like "*$leaf*" })

if ($detections) {
    Write-Fact 'detections' "$($detections.Count) naming this file"
    Add-Finding 'blocking' 'Defender has a threat detection naming this file. Check Windows Security > Protection history; it may have been quarantined outright.'
}
else {
    Write-Fact 'detections' 'none naming this file'
}

# --------------------------------------------------------------------- verdict

Write-Host ''
Write-Host '== What this means' -ForegroundColor Cyan

if (-not $findings.Count) {
    Write-Host ''
    Write-Host '   Nothing here refused it, and it started when this script tried.' -ForegroundColor Green
    Write-Host '   If the failure was earlier today, that is the answer: the block was' -ForegroundColor Green
    Write-Host '   temporary and has already lifted. Run the install again.' -ForegroundColor Green
}
else {
    # The common case after a temporary block is a report full of past refusals and
    # an executable that runs perfectly well now. Say so before the explanations,
    # because it is the whole answer and it is otherwise buried under them.
    if ($startedNow) {
        Write-Host ''
        Write-Host '   It started when this script tried, so whatever refused it has stopped.' -ForegroundColor Green
        Write-Host '   Finish the install by running:' -ForegroundColor Green
        Write-Host "     & '$Path' install" -ForegroundColor Green
        Write-Host ''
        Write-Host '   What refused it, for the record:' -ForegroundColor Gray
    }

    foreach ($finding in ($findings | Sort-Object { switch ($_.Severity) { 'blocking' { 0 } 'suspect' { 1 } default { 2 } } })) {
        $colour = switch ($finding.Severity) {
            'blocking' { 'Red' }
            'suspect' { 'Yellow' }
            default { 'Gray' }
        }

        Write-Host ''
        Write-Host "   $($finding.Text)" -ForegroundColor $colour
    }
}

Write-Host ''
Write-Host '   Windows Security > Protection history shows the same events with a button to' -ForegroundColor Gray
Write-Host '   allow them, where allowing is permitted.' -ForegroundColor Gray
Write-Host ''
