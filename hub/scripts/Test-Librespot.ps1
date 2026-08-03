<#
.SYNOPSIS
    Checks what a running librespot is actually offering: whether it is
    signed in, whether it is still discoverable by guests, or both.

.DESCRIPTION
    librespot has two ways of being usable, and they serve different
    people (docs/LIBRESPOT.md):

      Zeroconf   - unclaimed, announced on the local network. Anyone on
                   the Wi-Fi, with their own Spotify account, can take it
                   over. This is what makes it a speaker rather than a
                   personal device, and it is the guest story.

      Signed in  - librespot authenticates against Spotify and registers
                   as a device on one account. It appears wherever that
                   account is used, including away from home, and nobody
                   else can see it.

    Whether both can be true at once decides how cast points are set up,
    so this measures it rather than assuming.

    Three checks, none of which need a phone:

      1. Is the zeroconf HTTP server listening? That server is the guest
         path; without it no phone can ever claim the device.
      2. What does its own getInfo say? The activeUser field is empty
         when unclaimed and carries the account when signed in.
      3. Is it discoverable? A one-shot multicast DNS query for
         _spotify-connect._tcp.local, exactly the question a phone asks,
         with the unicast-response bit set (RFC 6762 section 5.4) so the
         answer comes straight back to us.

    Check 3 is the interesting one. Run it from this machine, and then
    from another machine on the same network — the difference between
    those two answers is precisely "is my network eating the
    announcement".

.EXAMPLE
    .\Test-Librespot.ps1 -LocalAddress 192.168.0.66

.EXAMPLE
    # From another PC on the same network, to test the network itself.
    .\Test-Librespot.ps1 -LocalAddress 192.168.0.40 -SkipLocalChecks
#>
[CmdletBinding()]
param(
    # This machine's address on the network the speakers are on. Not the
    # VPN's: an overlay interface with a better route metric is how these
    # announcements got lost in the first place.
    [Parameter(Mandatory = $true)]
    [string]$LocalAddress,

    [int]$ZeroconfPort = 41200,

    [string]$ExpectedName = 'Test speaker',

    [int]$DiscoverySeconds = 4,

    # For running on a machine that is not the one hosting librespot.
    [switch]$SkipLocalChecks
)

$ErrorActionPreference = 'Stop'

function Write-Result {
    param([string]$Label, [bool]$Ok, [string]$Detail)
    $mark = if ($Ok) { '  OK  ' } else { ' FAIL ' }
    $colour = if ($Ok) { 'Green' } else { 'Red' }
    Write-Host "[$mark] " -ForegroundColor $colour -NoNewline
    Write-Host "$Label" -NoNewline
    if ($Detail) { Write-Host " - $Detail" -ForegroundColor DarkGray } else { Write-Host '' }
}

Write-Host ''
Write-Host "librespot check - expecting '$ExpectedName' at $LocalAddress`:$ZeroconfPort" -ForegroundColor Cyan
Write-Host ''

$serverUp = $false
$signedIn = $false
$activeUser = ''

if (-not $SkipLocalChecks) {

    # --- 1. Is the process there at all? ------------------------------
    $process = Get-Process librespot -ErrorAction SilentlyContinue
    Write-Result 'librespot process running' ([bool]$process) `
        $(if ($process) { "pid $($process.Id -join ', ')" } else { 'start it first' })

    # --- 2. The zeroconf HTTP server, and what it says about itself ---
    # This is the endpoint a phone talks to once it has found the device,
    # so a failure here means the guest path is closed no matter how well
    # discovery works.
    try {
        $info = Invoke-RestMethod -Uri "http://localhost:$ZeroconfPort/?action=getInfo" -TimeoutSec 5
        $serverUp = $true
        $activeUser = [string]$info.activeUser
        $signedIn = -not [string]::IsNullOrWhiteSpace($activeUser)

        Write-Result 'zeroconf server answering' $true "name '$($info.remoteName)', type $($info.deviceType)"
        Write-Result 'signed in to an account' $signedIn `
            $(if ($signedIn) { "activeUser '$activeUser'" } else { 'unclaimed - a guest could take it' })
    }
    catch {
        Write-Result 'zeroconf server answering' $false $_.Exception.Message
    }
}

# --- 3. Is it discoverable? -------------------------------------------
# The same question a phone asks. Sent to the multicast group with the
# unicast-response bit set, so responders reply to our ephemeral port and
# we do not have to fight for UDP 5353, which on Windows is already shared
# between the built-in responder, Spotify, and anything else that wants it.
function New-MdnsPtrQuery {
    param([string]$ServiceName)

    $packet = [System.Collections.Generic.List[byte]]::new()
    # id 0, flags 0, one question, no answers/authority/additional
    $packet.AddRange([byte[]]@(0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0))
    foreach ($label in $ServiceName.Split('.')) {
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($label)
        $packet.Add([byte]$bytes.Length)
        $packet.AddRange($bytes)
    }
    $packet.Add(0)
    $packet.AddRange([byte[]]@(0, 12))       # QTYPE PTR
    $packet.AddRange([byte[]]@(0x80, 1))     # QCLASS IN, unicast response please
    return $packet.ToArray()
}

$service = '_spotify-connect._tcp.local'
$responders = [System.Collections.Generic.List[string]]::new()
$sawExpected = $false

$client = $null
try {
    $client = [System.Net.Sockets.UdpClient]::new()
    $bind = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($LocalAddress), 0)
    $client.Client.Bind($bind)

    # Explicit, because leaving the interface to the routing table is the
    # exact mistake that hid these announcements for an evening.
    $client.Client.SetSocketOption(
        [System.Net.Sockets.SocketOptionLevel]::IP,
        [System.Net.Sockets.SocketOptionName]::MulticastInterface,
        [System.Net.IPAddress]::Parse($LocalAddress).GetAddressBytes())

    $query = New-MdnsPtrQuery -ServiceName $service
    $group = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse('224.0.0.251'), 5353)
    [void]$client.Send($query, $query.Length, $group)

    $client.Client.ReceiveTimeout = 700
    $deadline = (Get-Date).AddSeconds($DiscoverySeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $from = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            $data = $client.Receive([ref]$from)
        }
        catch [System.Net.Sockets.SocketException] {
            continue   # nothing this round; keep listening until the deadline
        }

        # Not a full DNS parser: the names are stored as length-prefixed
        # ASCII, so readable runs are enough to see who answered and what
        # they called themselves.
        $text = [System.Text.Encoding]::ASCII.GetString($data)
        foreach ($match in [regex]::Matches($text, '[\x20-\x7E]{4,}')) {
            $value = $match.Value
            if ($value -like "*$ExpectedName*") { $sawExpected = $true }
            if ($value -like '*spotify-connect*' -or $value -like "*$ExpectedName*") {
                $entry = "$($from.Address) -> $value"
                if (-not $responders.Contains($entry)) { $responders.Add($entry) }
            }
        }
    }
}
finally {
    if ($client) { $client.Dispose() }
}

Write-Result "discoverable as '$ExpectedName'" $sawExpected `
    $(if ($sawExpected) { "$($responders.Count) matching record(s)" } else { "no answer in $DiscoverySeconds s" })

if ($responders.Count -gt 0) {
    Write-Host ''
    Write-Host '  Answers seen:' -ForegroundColor DarkGray
    $responders | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
}

# --- What it means ----------------------------------------------------
Write-Host ''
Write-Host 'Verdict' -ForegroundColor Cyan

if ($SkipLocalChecks) {
    if ($sawExpected) {
        Write-Host '  Discoverable from this machine, so the network carries the'
        Write-Host '  announcement. Any phone that still cannot see it is the phone.'
    }
    else {
        Write-Host '  Not discoverable from this machine. If the host itself can see it,'
        Write-Host '  the network is dropping multicast between clients - look for AP'
        Write-Host '  isolation, IGMP snooping, or a band the mesh does not bridge.'
    }
    return
}

if ($sawExpected -and $signedIn) {
    Write-Host '  Both modes at once: signed in AND announcing.' -ForegroundColor Green
    Write-Host "  Owner sees it through the account ($activeUser); guests can find it"
    Write-Host '  on the network. This is the arrangement cast points want.'
}
elseif ($sawExpected) {
    Write-Host '  Announcing but not signed in.' -ForegroundColor Yellow
    Write-Host '  Guests can claim it. It will not appear for an account that has'
    Write-Host '  not claimed it, and nothing shows away from home.'
}
elseif ($signedIn) {
    Write-Host '  Signed in but not announcing.' -ForegroundColor Yellow
    Write-Host "  It belongs to $activeUser and nobody else can use it. If guests"
    Write-Host '  matter, discovery still has to be solved.'
}
elseif ($serverUp) {
    Write-Host '  Running, reachable, and invisible.' -ForegroundColor Red
    Write-Host '  The server answers but nothing on the network can find it, and it'
    Write-Host '  has not signed in either, so no route to it exists yet.'
}
else {
    Write-Host '  Nothing to measure - librespot is not answering on this machine.' -ForegroundColor Red
}
Write-Host ''
