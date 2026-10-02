# RadCord Network Scanner
# Listens on UDP port 8766 for RadCord "hello" broadcasts and lists all discovered servers.
# Usage:  .\scan_network.ps1
#         .\scan_network.ps1 -Duration 8

param([int]$Duration = 5)

$PORT       = 8766
$discovered = @{}

Write-Host ""
Write-Host "=== RadCord Network Scanner ===" -ForegroundColor Cyan
Write-Host "Scanning UDP port $PORT for $Duration seconds ..." -ForegroundColor Yellow
Write-Host ""

try {
    $ep  = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $PORT)
    $udp = [System.Net.Sockets.UdpClient]::new()
    $udp.Client.SetSocketOption(
        [System.Net.Sockets.SocketOptionLevel]::Socket,
        [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true)
    $udp.Client.Bind($ep)
    $udp.EnableBroadcast        = $true
    $udp.Client.ReceiveTimeout  = 500

    $deadline = [DateTime]::Now.AddSeconds($Duration)
    while ([DateTime]::Now -lt $deadline) {
        try {
            $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            $bytes  = $udp.Receive([ref]$remote)
            $json   = [System.Text.Encoding]::UTF8.GetString($bytes)
            $msg    = $json | ConvertFrom-Json -ErrorAction SilentlyContinue
            if ($null -eq $msg -or $msg.type -ne "hello") { continue }

            $nid  = $msg.network_id
            $name = $msg.network_name
            $nick = $msg.nickname
            $ip   = $remote.Address.ToString()
            $ver  = $msg.version

            if (-not $discovered.ContainsKey($nid)) {
                $discovered[$nid] = @{
                    Name    = $name
                    Users   = [System.Collections.Generic.List[string]]::new()
                    IPs     = [System.Collections.Generic.List[string]]::new()
                    Version = $ver
                }
                Write-Host "  [FOUND] Server: '$name'   ID: $nid" -ForegroundColor Green
            }

            $e = $discovered[$nid]
            if ($nick -notin $e.Users) {
                $e.Users.Add($nick)
                Write-Host "          Member: $nick  @ $ip" -ForegroundColor White
            }
            if ($ip -notin $e.IPs) { $e.IPs.Add($ip) }
        }
        catch [System.Net.Sockets.SocketException] { }
        catch { }
    }
    $udp.Close()
}
catch {
    Write-Host "Cannot bind port ${PORT}: $_" -ForegroundColor Red
    Write-Host "If RadCord is already running it occupies the port - that is fine," -ForegroundColor DarkYellow
    Write-Host "RadCord discovers peers automatically via its own listener." -ForegroundColor DarkYellow
}

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
if ($discovered.Count -eq 0) {
    Write-Host "No servers found. Make sure RadCord is running on at least one PC in the network." -ForegroundColor Yellow
} else {
    foreach ($kv in $discovered.GetEnumerator()) {
        $e = $kv.Value
        Write-Host "Server  : $($e.Name)" -ForegroundColor Green
        Write-Host "  ID      : $($kv.Key)"
        Write-Host "  Version : $($e.Version)"
        Write-Host "  IPs     : $($e.IPs -join ', ')"
        Write-Host "  Members ($($e.Users.Count)) : $($e.Users -join ', ')"
        Write-Host ""
    }
}
