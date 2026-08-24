<#
.SYNOPSIS
    从 terms.json 格式的文件中挑出 Description 为空的词条，输出到一个独立的 JSON 文件。

.DESCRIPTION
    输入文件是 TermSearch 的 terms.json 格式：{ 缩写: [ { FullName, Description }, ... ] }。
    脚本遍历每个缩写下的每一条释义，把 Description 为空/空白的条目挑出来，
    按同样的格式（缩写 -> 释义数组）写到输出文件，只保留有空释义的缩写，
    每个缩写下也只保留那些确实为空的条目（同一缩写下已经写好的释义不会被带出来）。

    输出文件可以直接拿去人工/AI 补充 Description，再用 Merge-TermsFromText.ps1
    或类似方式合并回正式的 terms.json。

    跟 Convert-TmpToTerms.ps1 / Merge-TermsFromText.ps1 一样，这里不用
    ConvertFrom-Json 和 PowerShell 的 [ordered]@{} / @{}，因为它们默认大小写
    不敏感，遇到像 "COT" 和 "CoT" 这种仅大小写不同的缩写会出错或互相覆盖。
    改用 .NET 的 JavaScriptSerializer 和 Dictionary[string,object]，两者都
    大小写敏感。

.PARAMETER InputPath
    输入的 terms.json 格式文件路径。

.PARAMETER OutputPath
    输出文件路径，写入 Description 为空的词条。

.EXAMPLE
    .\Export-EmptyDescriptions.ps1 -InputPath ..\src\TermSearch\terms.json -OutputPath ..\terms_missing.json
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

$raw = Get-Content -Path $InputPath -Raw -Encoding UTF8
$data = $serializer.DeserializeObject($raw)

$result = New-Object 'System.Collections.Generic.Dictionary[string,object]'
$totalEntries = 0
$emptyCount = 0

foreach ($key in $data.Keys) {
    $emptyEntries = New-Object 'System.Collections.Generic.List[object]'

    foreach ($entry in $data[$key]) {
        $totalEntries++
        if ([string]::IsNullOrWhiteSpace($entry['Description'])) {
            $emptyEntries.Add([ordered]@{
                FullName    = $entry['FullName']
                Description = ""
            })
            $emptyCount++
        }
    }

    if ($emptyEntries.Count -gt 0) {
        $result[$key] = $emptyEntries
    }
}

$json = $result | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($OutputPath, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "共 $($data.Keys.Count) 个缩写、$totalEntries 条释义，其中 $emptyCount 条 Description 为空。"
Write-Host "已输出到：$OutputPath"
