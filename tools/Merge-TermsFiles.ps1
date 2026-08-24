<#
.SYNOPSIS
    把两个 terms.json 格式的文件合并成一个：将 f2.json 的内容合并进 f1.json。

.DESCRIPTION
    f1.json 和 f2.json 都是 TermSearch 的 terms.json 格式：
    { 缩写: [ { FullName, Description }, ... ] }。

    合并规则（以 f1 为基准，把 f2 的内容并进去）：
      - f2 里的缩写，f1 没有：整条新增到 f1。
      - f2 里的缩写，f1 已有，但全称（FullName）不一样：在该缩写下追加一条
        （多义词，跟 ACS/PM 这类词条的处理方式一致）。
      - f2 里的缩写和全称，f1 都已经一模一样：判定为重复，跳过、不合并，
        并把跳过的条目打印到屏幕上，方便你知道漏了什么/为什么没合并。

    默认直接把合并结果写回 f1.json（原地更新）；如果不想动 f1.json，
    可以用 -OutputPath 指定写到别的文件。

    跟其他几个脚本一样，这里用大小写敏感的方式解析/合并 JSON（不是
    PowerShell 自带的 ConvertFrom-Json / [ordered]@{}），因为缩写本身可能
    存在只有大小写不同的两个不同词条（比如 "COT" 和 "CoT"）。

.PARAMETER File1Path
    第一个文件（合并的目标/基准），比如 f1.json。

.PARAMETER File2Path
    第二个文件（要并入第一个文件的内容），比如 f2.json。

.PARAMETER OutputPath
    合并结果的输出路径。默认等于 File1Path，即原地更新 f1.json。

.EXAMPLE
    .\Merge-TermsFiles.ps1 -File1Path f1.json -File2Path f2.json
    把 f2.json 合并进 f1.json，直接覆盖 f1.json。

.EXAMPLE
    .\Merge-TermsFiles.ps1 -File1Path f1.json -File2Path f2.json -OutputPath merged.json
    合并结果另存为 merged.json，不改动 f1.json。
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$File1Path,

    [Parameter(Mandatory = $true)]
    [string]$File2Path,

    [string]$OutputPath
)

if (-not (Test-Path $File1Path)) {
    throw "找不到文件: $File1Path"
}
if (-not (Test-Path $File2Path)) {
    throw "找不到文件: $File2Path"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = $File1Path
}

$webExtensionsAsm = [System.Reflection.Assembly]::Load("System.Web.Extensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35")
$serializerType = $webExtensionsAsm.GetType("System.Web.Script.Serialization.JavaScriptSerializer")
$serializer = [Activator]::CreateInstance($serializerType)
$serializer.MaxJsonLength = [int]::MaxValue

$data1 = $serializer.DeserializeObject((Get-Content -Path $File1Path -Raw -Encoding UTF8))
$data2 = $serializer.DeserializeObject((Get-Content -Path $File2Path -Raw -Encoding UTF8))

$merged = New-Object 'System.Collections.Generic.Dictionary[string,object]'
foreach ($key in $data1.Keys) {
    $merged[$key] = $data1[$key]
}

$newKeyCount = 0
$appendedCount = 0
$skipped = New-Object 'System.Collections.Generic.List[object]'

foreach ($key in $data2.Keys) {
    foreach ($entry in $data2[$key]) {
        $fullName = $entry['FullName']

        if (-not $merged.ContainsKey($key)) {
            $newList = New-Object 'System.Collections.Generic.List[object]'
            $newList.Add([ordered]@{ FullName = $fullName; Description = $entry['Description'] })
            $merged[$key] = $newList
            $newKeyCount++
            continue
        }

        $existingEntries = $merged[$key]
        $duplicate = $false
        foreach ($existing in $existingEntries) {
            if ([string]$existing['FullName'] -ceq $fullName) {
                $duplicate = $true
                break
            }
        }

        if ($duplicate) {
            $skipped.Add([pscustomobject]@{ Key = $key; FullName = $fullName })
            continue
        }

        # 已有的条目数组可能是反序列化出来的固定大小数组，不支持 Add，
        # 统一转成可变 List 再追加，然后整体替换回去。
        $mutableEntries = New-Object 'System.Collections.Generic.List[object]'
        foreach ($e in $existingEntries) { $mutableEntries.Add($e) }
        $mutableEntries.Add([ordered]@{ FullName = $fullName; Description = $entry['Description'] })
        $merged[$key] = $mutableEntries
        $appendedCount++
    }
}

$json = $merged | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($OutputPath, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "合并完成，写入：$OutputPath"
Write-Host "  新增缩写：$newKeyCount"
Write-Host "  同一缩写下追加释义（多义词）：$appendedCount"
Write-Host "  跳过（缩写+全称已在 f1 中重复）：$($skipped.Count)"

if ($skipped.Count -gt 0) {
    Write-Host ""
    Write-Host "以下条目因为全称重复被跳过，未合并进 f1："
    foreach ($item in $skipped) {
        Write-Host "  [$($item.Key)] $($item.FullName)"
    }
}
