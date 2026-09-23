<#
.SYNOPSIS
    按清单批量修改旧项目的 project.json（开始/完成/创建时间、状态、需求人、仿真类型、等待秒数）。

.DESCRIPTION
    这是给 SimFlow「设置 · 数据与备份 · 扫描恢复 → 采用项目档案」做前置准备的：
    脚本只改档案（project.json）里的值，应用扫描时再把档案同步进数据库。

    安全约定：
      * 默认只“干跑”，逐条打印“旧值 → 新值”；加 -Apply 才真正写入。
      * 写入前每个文件留一份同名 .bak 副本（-NoBackup 可跳过）。
      * 写回后立刻重新解析一次 JSON；解析失败就把 .bak 还原回去并报错。
      * 只做定点替换，保留原有键顺序、缩进与换行；不会重排/删键。
      * SimFlow 正在运行时拒绝写入（应用会用数据库覆盖档案），确需覆盖用 -Force。
      * 应用读得懂带不带 BOM 的 UTF-8；本脚本统一写成 UTF-8 无 BOM，和程序写出的格式一致。

.PARAMETER InputCsv
    修改清单（UTF-8 CSV，带表头）。必需列：ProjectCode。可选用列（大小写不敏感）：
      StartedAt / CompletedAt / CreatedAt / UpdatedAt         时间，缺时区按本机时区解释
      WorkflowStatus     NotStarted / Active / Waiting / Completed
      SimulationType     Dynamics / Electromagnetics / Structural / Fatigue / ThermalFluid / Multiphysics / Other（留空=未分类）
      Requester / Description / Notes                          文本
      AccumulatedWaitSeconds                                   非负整数
    示例：
      ProjectCode,StartedAt,CompletedAt,WorkflowStatus,UpdatedAt
      SIM_20260506_001,2026-05-06 09:00,2026-06-15 17:30,,2026-09-18 17:00

.PARAMETER Root
    要递归查找 project.json 的根目录，可多个。省略时读 %LOCALAPPDATA%\SimFlow\config.json
    里的本机 Work / 工作站 Work / 工作站 Archive 三个根。

.PARAMETER Apply
    真正写入。不加则只打印将要发生的修改。

.PARAMETER Force
    即使检测到 SimFlow 正在运行也写入。

.PARAMETER NoBackup
    不生成 .bak 副本（不推荐）。

.EXAMPLE
    # 1) 先干跑看差异
    .\Update-ProjectMetadata.ps1 -InputCsv .\times.csv
    # 2) 确认后写入（先退出 SimFlow）
    .\Update-ProjectMetadata.ps1 -InputCsv .\times.csv -Apply
    # 3) 指定根目录（跳过 config.json）
    .\Update-ProjectMetadata.ps1 -InputCsv .\times.csv -Root D:\Simulation -Apply
#>
[CmdletBinding()]
param(
    [string]$InputCsv,
    [string[]]$Root,
    [switch]$Apply,
    [switch]$Force,
    [switch]$NoBackup,
    [switch]$Menu
)

$ErrorActionPreference = 'Stop'

$WorkflowStatuses = @('NotStarted', 'Active', 'Waiting', 'Completed')
$SimulationTypes = @('Dynamics', 'Electromagnetics', 'Structural', 'Fatigue', 'ThermalFluid', 'Multiphysics', 'Other')

# CSV 列名（大小写不敏感）→ project.json 键名
$FieldMap = [ordered]@{
    createdat              = 'createdAt'
    startedat              = 'startedAt'
    completedat            = 'completedAt'
    updatedat              = 'updatedAt'
    workflowstatus         = 'workflowStatus'
    simulationtype         = 'simulationType'
    requester              = 'requester'
    description            = 'description'
    notes                  = 'notes'
    accumulatedwaitseconds = 'accumulatedWaitSeconds'
}
$TimeKeys = @('createdAt', 'startedAt', 'completedAt', 'updatedAt')

function Get-Roots {
    param([string[]]$Explicit)
    if ($Explicit) { return $Explicit }
    $configPath = Join-Path $env:LOCALAPPDATA 'SimFlow\config.json'
    if (-not (Test-Path $configPath)) {
        throw "未指定 -Root，也找不到配置文件 $configPath。请用 -Root 指定项目根目录。"
    }
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    $roots = @()
    foreach ($candidate in @($config.localWorkRoot, $config.workstationWorkRoot, $config.workstationArchiveRoot)) {
        if (-not $candidate) { continue }
        # UNC 共享离线或没有凭据时 Test-Path 会抛 PermissionDenied，这里当成“不可用”跳过，
        # 否则工具会在工作站共享不可达时直接崩掉。
        $accessible = $false
        try { $accessible = [bool](Test-Path -LiteralPath $candidate -ErrorAction SilentlyContinue) } catch { $accessible = $false }
        if ($accessible) { $roots += $candidate }
        else { Write-Host ("跳过不可用的根目录（离线或没有权限）：" + $candidate) -ForegroundColor DarkYellow }
    }
    if ($roots.Count -eq 0) { throw '配置里的三个根目录都不可用，请检查设置或用 -Root 指定。' }
    return $roots
}

function Get-ProjectIndex {
    param([string[]]$Roots)
    $index = @{}
    $duplicates = @{}
    foreach ($root in $Roots) {
        Write-Host ("扫描根目录：" + $root)
        $files = Get-ChildItem -Path $root -Filter 'project.json' -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'project.json' }
        foreach ($file in $files) {
            try { $document = Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json }
            catch { Write-Host ("  档案无法解析，已跳过：" + $file.FullName) -ForegroundColor DarkYellow; continue }
            $id = $document.id
            if (-not $id) { Write-Host ("  档案缺少 id，已跳过：" + $file.FullName) -ForegroundColor DarkYellow; continue }
            if ($index.ContainsKey($id)) { $duplicates[$id] = $true } else { $index[$id] = $file.FullName }
        }
    }
    foreach ($id in $duplicates.Keys) { $index.Remove($id) }
    if ($duplicates.Count -gt 0) {
        Write-Host ("以下编号在多个位置都存在，为避免改错已排除：" + ($duplicates.Keys -join ', ')) -ForegroundColor DarkYellow
    }
    return $index
}

# 只替换 "key": <scalar> 这一处，保留键顺序 / 缩进 / 换行。
function Set-JsonScalar {
    param([string]$Text, [string]$Key, [string]$ValueJson)
    $pattern = '(?m)^([ \t]*"' + [regex]::Escape($Key) + '"[ \t]*:[ \t]*)(?:"(?:[^"\\]|\\.)*"|null|-?\d+(?:\.\d+)?)([ \t]*,?[ \t]*)$'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -eq 1) {
        return [regex]::Replace($Text, $pattern, ('${1}' + $ValueJson + '${2}'))
    }
    if ($matches.Count -gt 1) { throw ("键 " + $Key + " 在同一文件里出现多次，已跳过") }

    # 键不存在（旧档案）：插到最后一个 } 之前，并按需补逗号。
    $end = $Text.LastIndexOf('}')
    if ($end -lt 0) { throw ("找不到 JSON 结束花括号，无法插入键 " + $Key) }
    $head = $Text.Substring(0, $end)
    $tail = $Text.Substring($end)
    $trimmedHead = $head.TrimEnd()
    $needsComma = -not $trimmedHead.EndsWith('{')
    $prefix = $head.Substring(0, $trimmedHead.Length)
    $separator = if ($needsComma) { ',' } else { '' }
    $newline = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $indent = '  '
    return ($prefix + $separator + $newline + $indent + '"' + $Key + '": ' + $ValueJson + $newline + $tail)
}

function Convert-TimeValue {
    param([string]$Raw, [string]$Key)
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($Raw, [ref]$parsed)) {
        throw ("时间格式无法识别：" + $Key + " = " + $Raw)
    }
    return '"' + $parsed.ToString('yyyy-MM-ddTHH:mm:sszzz') + '"'
}

$ScriptPath = $MyInvocation.MyCommand.Path
$ScriptDirectory = Split-Path -Parent $ScriptPath
if (-not $InputCsv) { $InputCsv = Join-Path $ScriptDirectory 'times.csv' }
elseif (-not [System.IO.Path]::IsPathRooted($InputCsv)) { $InputCsv = Join-Path (Get-Location).Path $InputCsv }

Write-Host ''
Write-Host '================ SimFlow 项目档案批量修改 ================' -ForegroundColor Cyan
Write-Host ('清单文件：' + $InputCsv)
Write-Host ('本次模式：' + $(if ($Apply) { '写入（每个文件先留 .bak 备份）' } else { '干跑（只显示会改什么，绝不写入）' })) -ForegroundColor Yellow

if (-not (Test-Path -LiteralPath $InputCsv)) {
    $template = @'
ProjectCode,StartedAt,CompletedAt,WorkflowStatus,SimulationType,UpdatedAt,Requester,AccumulatedWaitSeconds
SIM_20260506_001,2026-05-06 09:00,2026-06-15 17:30,,,,
SIM_20260512_002,2026-05-12 09:00,2026-06-20 18:00,,Structural,,
'@
    [System.IO.File]::WriteAllText($InputCsv, $template, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ''
    Write-Host '清单文件原本不存在，已生成模板（含 2 行示例）：' -ForegroundColor Green
    Write-Host ('  ' + $InputCsv)
    Write-Host '请这样改：'
    Write-Host '  1) 用记事本或 Excel 打开它；'
    Write-Host '  2) 把示例行换成你的项目：ProjectCode 填项目编号，其余列只填要改的，不填=不改；'
    Write-Host '  3) 时间写成 2026-06-15 17:30 这种样子就行（不写时区按本机时区）；'
    Write-Host '  4) 保存（UTF-8），再运行一次本工具。'
    Write-Host ''
    Write-Host '（提示：WorkflowStatus 只允许 NotStarted / Active / Waiting / Completed；'
    Write-Host '  SimulationType 只允许 Dynamics / Electromagnetics / Structural / Fatigue / ThermalFluid / Multiphysics / Other）'
    if (-not $Menu) { exit 0 }
    Write-Host ''
    Write-Host '正在用记事本打开模板，填好保存后关掉记事本即可回到菜单……' -ForegroundColor Yellow
    Start-Process notepad.exe -ArgumentList ('"' + $InputCsv + '"') -Wait
}

# 菜单模式：把交互全放在 PowerShell 里做（.cmd 只负责启动），避免 cmd.exe 解析中文出错。
if ($Menu) {
    while ($true) {
        Write-Host ''
        Write-Host '请选择（输入数字后回车）：' -ForegroundColor Cyan
        Write-Host '   1   干跑：只显示会改什么，不写入任何文件'
        Write-Host '   2   写入：按清单修改（每个文件先留 .bak 备份）'
        Write-Host '   3   用记事本打开清单文件'
        Write-Host '   4   查看详细帮助'
        Write-Host '   0   退出'
        $choice = $null
        try { $choice = Read-Host '你的选择' } catch { break }
        if ($null -eq $choice) { break }
        $choice = $choice.Trim()
        if ($choice -eq '0') { break }
        if ($choice -eq '1') {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath -InputCsv $InputCsv
            continue
        }
        if ($choice -eq '2') {
            Write-Host ''
            Write-Host '注意：请先退出 SimFlow，否则应用会把你的修改覆盖回去。' -ForegroundColor Yellow
            & powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath -InputCsv $InputCsv -Apply
            Write-Host ''
            Write-Host '改完请启动 SimFlow：设置 - 数据与备份 - 扫描恢复，' -ForegroundColor Cyan
            Write-Host '在第一个冲突框里勾选“对本次扫描剩余的 N 个冲突也采用项目档案”，再点“采用项目档案”。' -ForegroundColor Cyan
            continue
        }
        if ($choice -eq '3') {
            Start-Process notepad.exe -ArgumentList ('"' + $InputCsv + '"') -Wait
            continue
        }
        if ($choice -eq '4') {
            Get-Help $ScriptPath -Detailed | Out-String -Width 120 | Write-Host
            continue
        }
        Write-Host '请输入 0 到 4 之间的数字。' -ForegroundColor Yellow
    }
    exit 0
}

$running = @(Get-Process -Name SimFlow -ErrorAction SilentlyContinue)
if ($Apply -and $running.Count -gt 0 -and -not $Force) {
    throw 'SimFlow 正在运行：应用同步档案时会用数据库内容覆盖 project.json，请先退出应用（或加 -Force 强制写入）。'
}

$rows = @(Import-Csv -Path $InputCsv -Encoding UTF8)
if ($rows.Count -eq 0) { throw '清单里没有数据行。' }
$columns = @($rows[0].PSObject.Properties.Name)
if ($columns -notcontains 'ProjectCode') { throw '清单缺少必需列 ProjectCode。' }

$roots = Get-Roots -Explicit $Root
$index = Get-ProjectIndex -Roots $roots
Write-Host ("清单 " + $rows.Count + " 行；根目录下找到 " + $index.Count + " 个档案。") -ForegroundColor Cyan
if (-not $Apply) { Write-Host '当前是干跑模式（不会写入），确认后加 -Apply 执行。' -ForegroundColor Cyan }

$changedFiles = 0
$skipped = 0
$errors = 0
foreach ($row in $rows) {
    $code = $row.ProjectCode
    if ($code) { $code = $code.Trim() }
    if (-not $code) { Write-Host '清单中存在空 ProjectCode，已跳过该行。' -ForegroundColor DarkYellow; $skipped++; continue }
    if (-not $index.ContainsKey($code)) { Write-Host ("找不到项目档案：" + $code) -ForegroundColor DarkYellow; $skipped++; continue }

    $path = $index[$code]
    try {
        $text = [System.IO.File]::ReadAllText($path)
        $document = $text | ConvertFrom-Json
        $updates = [ordered]@{}

        foreach ($column in $columns) {
            $key = $FieldMap[$column.ToLowerInvariant()]
            if (-not $key) { continue }
            $raw = $row.$column
            if ($null -eq $raw) { continue }
            $value = ([string]$raw).Trim()
            if ($value.Length -eq 0) { continue }

            if ($TimeKeys -contains $key) { $updates[$key] = Convert-TimeValue -Raw $value -Key $key; continue }
            if ($key -eq 'workflowStatus') {
                if ($WorkflowStatuses -notcontains $value) { throw ("workflowStatus 只允许 " + ($WorkflowStatuses -join ' / ') + "，收到：" + $value) }
                $updates[$key] = '"' + $value + '"'; continue
            }
            if ($key -eq 'simulationType') {
                if ($SimulationTypes -notcontains $value) { throw ("simulationType 只允许 " + ($SimulationTypes -join ' / ') + "，收到：" + $value) }
                $updates[$key] = '"' + $value + '"'; continue
            }
            if ($key -eq 'accumulatedWaitSeconds') {
                $seconds = 0L
                if (-not [long]::TryParse($value, [ref]$seconds) -or $seconds -lt 0) { throw ("accumulatedWaitSeconds 需要非负整数，收到：" + $value) }
                $updates[$key] = $seconds.ToString(); continue
            }

            $updates[$key] = '"' + $value.Replace('\', '\\').Replace('"', '\"') + '"'
        }

        if ($updates.Count -eq 0) { Write-Host ("清单行没有可改字段，已跳过：" + $code) -ForegroundColor DarkYellow; $skipped++; continue }

        # 只写了完成时间没写状态时补一个 Completed，避免“统计算完成、列表还是进行中”。
        if ($updates.Contains('completedAt') -and -not $updates.Contains('workflowStatus') -and
            -not ($columns -contains 'WorkflowStatus') -and $document.workflowStatus -ne 'Completed') {
            $updates['workflowStatus'] = '"Completed"'
        }
        # 改了任何东西就给个新的 updatedAt（除非清单自己指定），扫描才会判定档案已变。
        if (-not $updates.Contains('updatedAt') -and -not ($columns -contains 'UpdatedAt')) {
            $updates['updatedAt'] = '"' + [DateTimeOffset]::Now.ToString('yyyy-MM-ddTHH:mm:sszzz') + '"'
        }

        $newText = $text
        $changes = @()
        foreach ($key in $updates.Keys) {
            $before = $document.$key
            if ($null -eq $before) { $before = '(空)' }
            $after = $updates[$key].Trim('"')
            if (([string]$before) -eq $after) { continue }
            $newText = Set-JsonScalar -Text $newText -Key $key -ValueJson $updates[$key]
            $changes += ("    " + $key + ": " + $before + " → " + $after)
        }

        if ($changes.Count -eq 0) { Write-Host ("无需修改：" + $code); continue }

        Write-Host ("[" + $code + "] " + $path + "  " + $changes.Count + " 处修改") -ForegroundColor Green
        $changes | ForEach-Object { Write-Host $_ }

        if ($Apply) {
            if (-not $NoBackup) {
                $backup = $path + '.' + (Get-Date -Format 'yyyyMMddHHmmss') + '.bak'
                Copy-Item -Path $path -Destination $backup -Force
            }
            [System.IO.File]::WriteAllText($path, $newText, (New-Object System.Text.UTF8Encoding($false)))
            try { $null = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json }
            catch {
                if (-not $NoBackup) {
                    $latest = Get-ChildItem ($path + '.*.bak') | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                    if ($latest) { Copy-Item -Path $latest.FullName -Destination $path -Force }
                }
                throw ("写回后 JSON 解析失败，已还原：" + $path)
            }
        }

        $changedFiles++
    }
    catch {
        Write-Host ("[" + $code + "] 失败：" + $_.Exception.Message) -ForegroundColor Red
        $errors++
    }
}

Write-Host ''
Write-Host ("完成：待修改/已修改 " + $changedFiles + " 个档案，跳过 " + $skipped + " 行，失败 " + $errors + " 行。") -ForegroundColor Cyan
if ($Apply) {
    Write-Host '下一步：启动 SimFlow → 设置 · 数据与备份 → 扫描恢复…，在冲突框里选「采用项目档案」；' -ForegroundColor Cyan
    Write-Host '冲突很多时勾选「对本次扫描剩余的 N 个冲突也采用项目档案」，一次点完。' -ForegroundColor Cyan
} else {
    Write-Host '（干跑结束，未写入任何文件。确认无误后执行下面这条：）' -ForegroundColor Cyan
    Write-Host ('powershell -NoProfile -ExecutionPolicy Bypass -File "' + $ScriptPath + '" -InputCsv "' + $InputCsv + '" -Apply') -ForegroundColor White
}
