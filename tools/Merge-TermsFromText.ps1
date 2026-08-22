<#
.SYNOPSIS
    将"缩略语~全称~描述"格式的文本文件解析后，增量合并进 terms.json。

.DESCRIPTION
    输入文件每行一条记录，用 ~ 分隔三段：缩略语~全称~描述。

    输出文件是 TermSearch 的 terms.json 格式。合并规则（按缩写做增量更新，
    不会删除或覆盖已有数据）：
      - 输出文件不存在：新建，写入所有解析出的词条。
      - 该缩写在输出文件里不存在：新增一条。
      - 该缩写存在，且已有一条记录的全称与本次解析出的全称完全一致：跳过（视为重复）。
      - 该缩写存在，但全称不同：在该缩写下追加一条新的全称/描述（多义词，
        与 ACS/PM 这类词条的处理方式一致）。

    缩写、全称的匹配都是大小写敏感的精确匹配（跟程序本身查询时的大小写不敏感
    前缀匹配是两回事——这里只是决定“是不是同一条记录”，用精确匹配更保守，
    不会把两个大小写不同的缩写误判成同一个）。

.PARAMETER InputPath
    输入文本文件路径，每行“缩略语~全称~描述”。

.PARAMETER OutputPath
    输出 terms.json 路径。若文件已存在，会先读取现有内容再合并写回；
    若不存在会新建。

.EXAMPLE
    .\Merge-TermsFromText.ps1 -InputPath .\test.txt -OutputPath ..\src\TermSearch\terms.json
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

if (-not (Test-Path $InputPath)) {
    throw "找不到输入文件: $InputPath"
}

$webExtensionsAsm = [System.Reflection.Assembly]::Load("System.Web.Extensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35")
$serializerType = $webExtensionsAsm.GetType("System.Web.Script.Serialization.JavaScriptSerializer")
$serializer = [Activator]::CreateInstance($serializerType)
$serializer.MaxJsonLength = [int]::MaxValue

# 加载已有的 terms.json（大小写敏感字典，理由同 Convert-TmpToTerms.ps1：
# PowerShell 自带的 ConvertFrom-Json / [ordered]@{} 默认大小写不敏感，
# 会导致例如 "COT" 和 "CoT" 这类仅大小写不同的缩写被静默合并/覆盖）。
$terms = New-Object 'System.Collections.Generic.Dictionary[string,object]'
if (Test-Path $OutputPath) {
    $existingRaw = Get-Content -Path $OutputPath -Raw -Encoding UTF8
    if (-not [string]::IsNullOrWhiteSpace($existingRaw)) {
        $loaded = $serializer.DeserializeObject($existingRaw)
        foreach ($k in $loaded.Keys) {
            $terms[$k] = $loaded[$k]
        }
    }
}

$addedCount = 0
$appendedCount = 0
$skippedDuplicateCount = 0
$skippedMalformedCount = 0
$lineNumber = 0

foreach ($line in Get-Content -Path $InputPath -Encoding UTF8) {
    $lineNumber++

    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    # 用 -split 限制最多 3 段，这样即使"描述"里本身含有 ~，也不会被切碎。
    $parts = $line -split '~', 3
    $key = $parts[0].Trim()

    if ([string]::IsNullOrWhiteSpace($key) -or $parts.Count -lt 2) {
        Write-Warning "第 $lineNumber 行格式不对（应为 缩略语~全称~描述），已跳过：$line"
        $skippedMalformedCount++
        continue
    }

    $fullName = $parts[1].Trim()
    $description = if ($parts.Count -ge 3) { $parts[2].Trim() } else { "" }

    if (-not $terms.ContainsKey($key)) {
        $entries = New-Object 'System.Collections.Generic.List[object]'
        $entries.Add([ordered]@{ FullName = $fullName; Description = $description })
        $terms[$key] = $entries
        $addedCount++
        continue
    }

    $existingEntries = $terms[$key]
    $duplicate = $false
    foreach ($entry in $existingEntries) {
        if ([string]$entry['FullName'] -ceq $fullName) {
            $duplicate = $true
            break
        }
    }

    if ($duplicate) {
        $skippedDuplicateCount++
    }
    else {
        # 从现有 terms.json 反序列化出来的数组是固定大小的，不支持 Add，
        # 这里统一转成可变的 List 再追加，然后整体替换回去。
        $mutableEntries = New-Object 'System.Collections.Generic.List[object]'
        foreach ($entry in $existingEntries) { $mutableEntries.Add($entry) }
        $mutableEntries.Add([ordered]@{ FullName = $fullName; Description = $description })
        $terms[$key] = $mutableEntries
        $appendedCount++
    }
}

$json = $terms | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($OutputPath, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "完成合并，写入：$OutputPath"
Write-Host "  新增缩写：$addedCount"
Write-Host "  同一缩写下追加释义（多义词）：$appendedCount"
Write-Host "  跳过（全称已存在，视为重复）：$skippedDuplicateCount"
Write-Host "  跳过（行格式不对）：$skippedMalformedCount"
