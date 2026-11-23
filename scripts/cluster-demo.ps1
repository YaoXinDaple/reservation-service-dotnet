# Three-node hinted handoff demo. Uses compose.cluster.yaml (no host ports, no volumes).
#   ./scripts/cluster-demo.ps1          run the demo, leave the cluster running
#   ./scripts/cluster-demo.ps1 -Down    run the demo, then remove the cluster
param([switch]$Down)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$composeArgs = @('compose', '-f', (Join-Path $projectRoot 'compose.cluster.yaml'))
$nodes = @('cass1', 'cass2', 'cass3')

function Invoke-Compose { docker @composeArgs @args; if ($LASTEXITCODE -ne 0) { throw "docker compose $args failed." } }
function Invoke-Node([string]$Node, [string[]]$Command) { docker @composeArgs exec -T $Node @Command 2>&1 }
function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }

# cqlsh prints errors instead of failing loudly; treat any error text as a failure.
function Invoke-Cql([string]$Node, [string]$Cql, [switch]$AllowError) {
    $output = (Invoke-Node $Node @('cqlsh', '-e', $Cql)) | Out-String
    if (-not $AllowError -and $output -match 'Error|Unavailable|NoHostAvailable|Exception') {
        throw "CQL failed on ${Node}:`n$output"
    }
    $output
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for $What." }
        Start-Sleep -Seconds 3
    }
}

# Ring lines (UN/DN/UJ/...) as seen from one node; empty if the node is not reachable.
function Get-Ring([string]$From = 'cass1') {
    # A node that is still starting makes nodetool fail; treat that as "not ready yet".
    try { @(Invoke-Node $From @('nodetool', 'status') | ForEach-Object { "$_" } | Where-Object { $_ -match '^[UD][NLJM]\s' }) }
    catch { @() }
}
function Get-UpNormalCount { @(Get-Ring | Where-Object { $_ -match '^UN\s' }).Count }

Write-Step '1. 启动三节点集群（节点必须逐个加入，首次约 4～6 分钟）'
$running = @(docker @composeArgs ps --status running --services 2>$null)
if ($running.Count -eq 3 -and (Get-UpNormalCount) -eq 3) {
    Write-Host '复用已经在运行的完整集群。'
} else {
    if ($running.Count -gt 0) {
        Write-Host '发现不完整的旧集群（可能是上次中断留下的），先删除再重建。' -ForegroundColor Yellow
    }
    Invoke-Compose down
    $expected = 0
    foreach ($node in $nodes) {
        $expected++
        Write-Host "启动 $node，等待它以 UN（Up/Normal）状态加入环……"
        # --no-deps: start exactly this node; the loop itself enforces the one-at-a-time order.
        Invoke-Compose up -d --no-deps --wait --wait-timeout 600 $node
        Wait-Until { (Get-UpNormalCount) -ge $expected } 300 "$node to join the ring"
    }
}
$ring = Get-Ring
$ring | ForEach-Object { Write-Host $_ }
if (@($ring | Where-Object { $_ -match '^UN\s' }).Count -ne 3) { throw 'The ring must contain three UN nodes before the demo can run.' }

Write-Step '2. 创建 RF=3 的 keyspace'
Invoke-Cql cass1 @"
CREATE KEYSPACE IF NOT EXISTS demo WITH replication = {'class': 'NetworkTopologyStrategy', 'datacenter1': 3};
CREATE TABLE IF NOT EXISTS demo.kv (id int PRIMARY KEY, value text);
"@ | Out-Null
$value = "written while cass3 was down at $(Get-Date -Format 'HH:mm:ss')"
$cass3Id = ((Invoke-Node cass3 @('nodetool', 'info')) | Select-String '^ID\s*:\s*(\S+)').Matches[0].Groups[1].Value
Write-Host "cass3 的 Host ID：$cass3Id"

Write-Step '3. 停止 cass3，模拟一个副本掉线'
Invoke-Compose stop cass3
Wait-Until { @(Get-Ring | Where-Object { $_ -match '^DN\s' }).Count -eq 1 } 60 'cass1 to see cass3 as down'

Write-Step '4. 在 cass1 上写入：QUORUM 需要 2 个副本，ALL 需要 3 个'
Invoke-Cql cass1 "CONSISTENCY QUORUM; INSERT INTO demo.kv (id, value) VALUES (1, '$value');" | Out-Null
Write-Host "QUORUM 写入成功：$value" -ForegroundColor Green
$all = Invoke-Cql cass1 "CONSISTENCY ALL; INSERT INTO demo.kv (id, value) VALUES (2, 'needs all replicas');" -AllowError
if ($all -notmatch 'Cannot achieve consistency level ALL') { throw "ALL write should fail while cass3 is down:`n$all" }
Write-Host 'ALL 写入失败：Cannot achieve consistency level ALL（存活副本 2 个，需要 3 个）' -ForegroundColor Yellow

Write-Step '5. 协调节点 cass1 为 cass3 保存了提示（文件名以 cass3 的 Host ID 开头）'
Wait-Until { (Invoke-Node cass1 @('sh', '-c', 'ls /var/lib/cassandra/hints/')) -match $cass3Id } 30 'the hint file'
Invoke-Node cass1 @('sh', '-c', 'ls -l /var/lib/cassandra/hints/') | Select-Object -Skip 1

Write-Step '6. 启动 cass3，等待提示投递完成'
Invoke-Compose start cass3
Wait-Until { (Get-UpNormalCount) -eq 3 } 300 'cass3 to rejoin the ring'
Wait-Until { -not ((Invoke-Node cass1 @('sh', '-c', 'ls /var/lib/cassandra/hints/')) -match $cass3Id) } 120 'hint delivery'
Write-Host '提示文件已经消失：cass1 已把错过的写入补发给 cass3。' -ForegroundColor Green

Write-Step '7. 停止 cass1 和 cass2，只用 cass3 以 ONE 读取'
Invoke-Compose stop cass1 cass2
$read = Invoke-Cql cass3 'CONSISTENCY ONE; SELECT value FROM demo.kv WHERE id = 1;'
Write-Host $read
if ($read -notmatch [regex]::Escape($value)) { throw 'cass3 should hold the write it missed while it was down.' }
Write-Host 'cass3 独自返回了它掉线期间的那次写入：提示移交生效。' -ForegroundColor Green

Write-Step '8. 恢复集群'
# These nodes already own their tokens, so restarting them together is safe (no bootstrap).
Invoke-Compose start cass1 cass2
Wait-Until { (Get-UpNormalCount) -eq 3 } 300 'all three nodes to be up'
Get-Ring | ForEach-Object { Write-Host $_ }
if ($Down) {
    Invoke-Compose down
    Write-Host '集群已删除。'
} else {
    Write-Host '集群保持运行。删除：docker compose -f compose.cluster.yaml down'
}
