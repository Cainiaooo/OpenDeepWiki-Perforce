<#
.SYNOPSIS
    采集 Perforce 工作区两个 changelist 之间的变更文件，并注入 OpenDeepWiki 触发增量更新。

.DESCRIPTION
    对应「Perforce 增量改造方案.md」§3。与 OpenDeepWiki 解耦，放在 CI 或定时任务里运行。
    流程：读取上次处理的 changelist -> p4 sync -> 取当前 changelist -> 若有变化则解析
    变更文件(depot 路径 -> 工作区相对路径) -> POST /incremental-update/external -> 推进本地状态。

    这是模板脚本：请按部署实际情况替换 depot 路径、客户端映射、仓库/分支 ID、过滤规则与鉴权。

.NOTES
    触发方式二选一：定时轮询(推荐起步)或 p4 服务端 change-commit trigger。
#>

param(
    # OpenDeepWiki API 根地址
    [string]$ApiBaseUrl = "http://localhost:5085",
    # 仓库所有者(或管理员)的 JWT，用于外部注入端点鉴权(Authorization: Bearer)
    [Parameter(Mandatory = $true)][string]$ApiToken,
    # 仓库 ID 与分支 ID（提交 Perforce 源后从 OpenDeepWiki 获取）
    [Parameter(Mandatory = $true)][string]$RepositoryId,
    [Parameter(Mandatory = $true)][string]$BranchId,
    # 关注的 depot 路径（只投喂源码目录，排除二进制/生成目录）
    [string]$DepotPath = "//depot/SampleProject/...",
    # 本地状态文件：记录上次处理到的 changelist
    [string]$StateFile = ".last-processed-cl",
    # 单次注入的变更文件数阈值：超过则建议改为全量重建而非增量
    [int]$MaxChangedFiles = 500,
    # 轮询增量任务完成的最长等待秒数与间隔
    [int]$PollTimeoutSeconds = 1800,
    [int]$PollIntervalSeconds = 15,
    # 只保留这些前缀下的文件（工作区相对路径），空数组表示不过滤
    [string[]]$IncludePrefixes = @("Source/", "Script/"),
    # 排除这些前缀下的文件
    [string[]]$ExcludePrefixes = @("Content/", "Intermediate/", "Binaries/", "Saved/", "DerivedDataCache/")
)

$ErrorActionPreference = "Stop"

function Get-RelativePath([string]$depotFile) {
    # depot 路径 -> 工作区相对路径。优先用 `p4 where`；此处给出简化实现，按需替换。
    $whereLine = (& p4 where $depotFile) 2>$null | Select-Object -First 1
    if (-not $whereLine) { return $null }
    # `p4 where` 输出：<depotPath> <clientPath> <localPath>
    $localPath = ($whereLine -split '\s+')[-1]
    if (-not $localPath) { return $null }
    $clientRoot = (& p4 -F '%clientRoot%' -ztag info) 2>$null
    if (-not $clientRoot) { return $localPath }
    return ($localPath.Substring($clientRoot.Length)).TrimStart('\', '/') -replace '\\', '/'
}

function Test-PathAllowed([string]$relPath) {
    foreach ($ex in $ExcludePrefixes) { if ($relPath.StartsWith($ex)) { return $false } }
    if ($IncludePrefixes.Count -eq 0) { return $true }
    foreach ($inc in $IncludePrefixes) { if ($relPath.StartsWith($inc)) { return $true } }
    return $false
}

# 1. 读取上次处理到的 changelist
$last = if (Test-Path $StateFile) { (Get-Content $StateFile -Raw).Trim() } else { "0" }

# 2. 同步工作区并取当前 changelist
& p4 sync $DepotPath | Out-Null
$currentLine = (& p4 changes -m1 "$DepotPath#have") | Select-Object -First 1
if (-not $currentLine) { Write-Host "无法获取当前 changelist"; exit 1 }
$current = ($currentLine -replace 'Change (\d+).*', '$1')

if ($current -eq $last) {
    Write-Host "无新增 changelist（当前 = $current），跳过。"
    exit 0
}

# 3. 取两个 changelist 之间的变更文件，区分修改与删除
$from = [int]$last + 1
$rawFiles = & p4 files "$DepotPath@$from,@$current"

$changed = New-Object System.Collections.Generic.List[string]
$deleted = New-Object System.Collections.Generic.List[string]

foreach ($line in $rawFiles) {
    if ($line -notmatch '^(?<depot>//.+?)#\d+ - (?<action>\w+)') { continue }
    $rel = Get-RelativePath $Matches.depot
    if (-not $rel -or -not (Test-PathAllowed $rel)) { continue }

    if ($Matches.action -in @('delete', 'move/delete')) { $deleted.Add($rel) }
    else { $changed.Add($rel) }
}

# Select-Object -Unique 在单元素时会把集合收成标量；始终用 @() 包回数组。
$changed = @($changed | Select-Object -Unique)
$deleted = @($deleted | Select-Object -Unique)

if ($changed.Count -eq 0 -and $deleted.Count -eq 0) {
    Write-Host "changelist 推进到 $current，但无关注目录下的变更。仅推进本地状态。"
    Set-Content -Path $StateFile -Value $current
    exit 0
}

if ($changed.Count -gt $MaxChangedFiles) {
    Write-Warning "变更文件数 $($changed.Count) 超过阈值 $MaxChangedFiles，建议触发全量重建而非增量。"
    exit 2
}

# 4. 注入 OpenDeepWiki
# Windows PowerShell 5.x 的 ConvertTo-Json 会把单元素数组展成 JSON 标量，导致
# ASP.NET 对 List<string> 绑定失败。这里手工构造数组 JSON，兼容 PS 5.1 / 7+。
function ConvertTo-JsonStringArray([string[]]$Items) {
    if ($null -eq $Items -or $Items.Count -eq 0) { return '[]' }
    $parts = foreach ($item in $Items) {
        $escaped = [string]$item
        $escaped = $escaped.Replace('\', '\\').Replace('"', '\"')
        '"' + $escaped + '"'
    }
    return '[' + ($parts -join ',') + ']'
}

$headers = @{ Authorization = "Bearer $ApiToken" }
$body = @"
{
  "targetRevision": "$current",
  "changedFiles": $(ConvertTo-JsonStringArray -Items $changed),
  "deletedFiles": $(ConvertTo-JsonStringArray -Items $deleted)
}
"@

$uri = "$ApiBaseUrl/api/v1/repositories/$RepositoryId/branches/$BranchId/incremental-update/external"
Write-Host "注入 changelist $current：$($changed.Count) 变更 / $($deleted.Count) 删除 -> $uri"

$response = Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($body))

if (-not $response.success) {
    Write-Error "注入失败：$($response | ConvertTo-Json -Depth 4)"
    exit 1
}

$taskId = $response.taskId
Write-Host "任务已创建/复用：TaskId=$taskId, Status=$($response.status)"

# 5. 轮询任务直到完成——只有真正 Completed 才推进本地状态。
#    若入队后任务失败/卡住/进程崩溃就推进状态，会导致该 changelist 的变更永久漏投。
$statusUri = "$ApiBaseUrl/api/v1/incremental-updates/$taskId"
$deadline = (Get-Date).AddSeconds($PollTimeoutSeconds)

while ($true) {
    Start-Sleep -Seconds $PollIntervalSeconds
    try {
        $status = (Invoke-RestMethod -Method Get -Uri $statusUri -Headers $headers).status
    } catch {
        Write-Warning "查询任务状态失败，稍后重试：$($_.Exception.Message)"
        $status = "Unknown"
    }

    switch ($status) {
        "Completed" {
            Write-Host "任务完成，推进本地状态到 changelist $current。"
            Set-Content -Path $StateFile -Value $current
            exit 0
        }
        { $_ -in @("Failed", "Cancelled") } {
            Write-Error "任务未成功(Status=$status)，保留原基线，下次将重投 changelist $current。"
            exit 1
        }
    }

    if ((Get-Date) -gt $deadline) {
        Write-Error "等待任务完成超时(${PollTimeoutSeconds}s)，保留原基线，下次将重投 changelist $current。"
        exit 1
    }
}
