<#
.SYNOPSIS
    将 tmp.json（缩写术语的原始收集格式）转换为 TermSearch 的 terms.json 格式。

.DESCRIPTION
    源文件里每个缩写对应一个对象，分两种情况：
      - 只有一个释义：对象直接包含 fullname / description 字段，例如 $APPEALS、1LM。
      - 有多个释义：对象包含一个 include 数组，数组每一项是 fullname / description，例如 ACS。

    输出格式（TermSearch 术语表 terms.json）：每个缩写对应一个数组，数组元素为
    { "FullName": ..., "Description": ... }（字段名必须是这个大小写，
    因为程序用 System.Text.Json 默认区分大小写反序列化）。

    注意：这里不用 ConvertFrom-Json，也不用 PowerShell 的 [ordered]@{} / @{}。
    这两者在 PowerShell 里默认都是大小写不敏感的字典，如果源文件里出现两个
    仅大小写不同的顶层缩写 key（例如 "COT" 和 "CoT"，是两个不同的缩写），
    要么直接解析报错 "contains the duplicated keys"，要么静默地让后写入的
    覆盖掉先写入的（数据被悄悄丢掉，且不会有任何报错提示）。
    改用 .NET 的 JavaScriptSerializer（解析）和 Dictionary[string,object]
    （拼装结果），两者默认都是大小写敏感的，能正确保留这类
    "仅大小写不同" 的缩写。

.PARAMETER InputPath
    源文件路径，默认为脚本所在目录的上一级下的 tmp.json（即项目根目录）。

.PARAMETER OutputPath
    输出文件路径。默认写到项目根目录的 terms.converted.json，不会覆盖
    src\TermSearch\terms.json，避免误覆盖程序正在使用的数据。
    如果确认要直接更新程序的术语表，显式传入
    -OutputPath ..\src\TermSearch\terms.json（会整体覆盖该文件，
    如需与已有词条合并，请自行先备份/人工合并）。

.EXAMPLE
    .\Convert-TmpToTerms.ps1
    使用默认路径转换，结果写到 terms.converted.json。

.EXAMPLE
    .\Convert-TmpToTerms.ps1 -OutputPath ..\src\TermSearch\terms.json
    直接覆盖程序使用的 terms.json。
#>
param(
    [string]$InputPath = (Join-Path $PSScriptRoot "..\tmp.json"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\terms.converted.json")
)

if (-not (Test-Path $InputPath)) {
    throw "找不到输入文件: $InputPath"
}

$raw = Get-Content -Path $InputPath -Raw -Encoding UTF8

$webExtensionsAsm = [System.Reflection.Assembly]::Load("System.Web.Extensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35")
$serializerType = $webExtensionsAsm.GetType("System.Web.Script.Serialization.JavaScriptSerializer")
$serializer = [Activator]::CreateInstance($serializerType)
$serializer.MaxJsonLength = [int]::MaxValue
$source = $serializer.DeserializeObject($raw)

function Get-Field {
    param($Dict, [string]$Name)
    if ($Dict.ContainsKey($Name)) { return [string]$Dict[$Name] }
    return ""
}

$result = New-Object 'System.Collections.Generic.Dictionary[string,object]'

foreach ($key in $source.Keys) {
    $value = $source[$key]
    $entries = @()

    if ($value.ContainsKey('include')) {
        foreach ($item in $value['include']) {
            $entries += [ordered]@{
                FullName    = Get-Field $item 'fullname'
                Description = Get-Field $item 'description'
            }
        }
    }
    else {
        $entries += [ordered]@{
            FullName    = Get-Field $value 'fullname'
            Description = Get-Field $value 'description'
        }
    }

    $result[$key] = $entries
}

$json = $result | ConvertTo-Json -Depth 10

[System.IO.File]::WriteAllText($OutputPath, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "已转换 $($result.Count) 个缩写词条，输出到：$OutputPath"
