<#
.SYNOPSIS
    把本机部署的 terms.json 和 git 仓库里的 terms.json 双向同步。

.DESCRIPTION
    用于多台设备共用同一个 TermSearch 术语表的场景：程序本身在每台设备上是
    独立的绿色版部署（跟 git 仓库是两个分开的文件夹），你在不同设备上用
    程序自带的"快捷补充"新增词条后，这些新增只落在本机部署文件夹里的
    terms.json，不会自动出现在别的设备或仓库里。这个脚本负责把两边的内容
    对齐、汇总到仓库，再发给其他设备。

    流程：
      1. 检查仓库工作目录是否干净（有未提交的改动就中止，不做任何覆盖）。
      2. git pull，拉取仓库里 terms.json 的最新版本（可能包含其他设备
         之前同步上去的新增）。
      3. 读取"仓库里的 terms.json"（作为基准）和"本机部署的 terms.json"，
         按跟 Merge-TermsFiles.ps1 一致的规则算出两者的并集：
           - 缩写不同 -> 都保留
           - 同一缩写全称不同 -> 都保留（多义词）
           - 缩写 + 全称相同、一边 Description 空一边有内容 -> 补全
           - 缩写 + 全称相同、两边都有内容但不一样 -> 以仓库版本为准，
             本机那条被跳过，打印到屏幕上（正常不会发生，因为程序本身
             没有编辑已有词条的功能）
      4. 把合并结果分别写回仓库那份和本机部署那份，写完两边就完全一致了。
      5. 只有仓库那份内容真的有变化时才 git add / commit / push；
         没有变化就不产生空提交。

    跟其他几个脚本一样，这里用大小写敏感的方式解析/合并 JSON（不是
    PowerShell 自带的 ConvertFrom-Json / [ordered]@{}），因为缩写本身可能
    存在只有大小写不同的两个不同词条（比如 "COT" 和 "CoT"）。

.PARAMETER DeployedTermsPath
    本机部署文件夹里、程序实际读取的那份 terms.json 路径（每台设备可能不
    一样，所以是必填参数）。

.PARAMETER RepoTermsPath
    仓库里的 terms.json 路径。默认是脚本所在目录（tools\）上一级的
    src\TermSearch\terms.json，一般不需要手动指定。

.EXAMPLE
    .\Sync-TermsWithRepo.ps1 -DeployedTermsPath "D:\Apps\TermSearch\terms.json"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$DeployedTermsPath,

    [string]$RepoTermsPath
)

$repoRoot = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($RepoTermsPath)) {
    $RepoTermsPath = Join-Path $repoRoot "src\TermSearch\terms.json"
}
if (-not (Test-Path $RepoTermsPath)) {
    throw "找不到仓库里的 terms.json: $RepoTermsPath"
}
if (-not (Test-Path $DeployedTermsPath)) {
    throw "找不到本机部署的 terms.json: $DeployedTermsPath"
}

# 1. 仓库工作目录必须干净，否则 git pull / commit 可能覆盖掉你还没提交的改动。
$status = git -C $repoRoot status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw "git status 失败，请确认 $repoRoot 是一个有效的 git 仓库。"
}
if (-not [string]::IsNullOrWhiteSpace($status)) {
    Write-Host "仓库里有未提交的改动，已中止，请先手动处理："
    Write-Host $status
    exit 1
}

# 2. 拉取仓库最新版本。
Write-Host "正在拉取仓库最新版本..."
git -C $repoRoot pull
if ($LASTEXITCODE -ne 0) {
    throw "git pull 失败，请检查网络/冲突后手动处理。"
}

# 3. 读取两份文件，用大小写敏感的方式解析。
$webExtensionsAsm = [System.Reflection.Assembly]::Load("System.Web.Extensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35")
$serializerType = $webExtensionsAsm.GetType("System.Web.Script.Serialization.JavaScriptSerializer")
$serializer = [Activator]::CreateInstance($serializerType)
$serializer.MaxJsonLength = [int]::MaxValue

$repoData = $serializer.DeserializeObject((Get-Content -Path $RepoTermsPath -Raw -Encoding UTF8))
$deployedData = $serializer.DeserializeObject((Get-Content -Path $DeployedTermsPath -Raw -Encoding UTF8))

$merged = New-Object 'System.Collections.Generic.Dictionary[string,object]'
foreach ($key in $repoData.Keys) {
    $merged[$key] = $repoData[$key]
}

$newKeyCount = 0
$appendedCount = 0
$filledCount = 0
$skipped = New-Object 'System.Collections.Generic.List[object]'

foreach ($key in $deployedData.Keys) {
    foreach ($entry in $deployedData[$key]) {
        $fullName = $entry['FullName']
        $description2 = $entry['Description']

        if (-not $merged.ContainsKey($key)) {
            $newList = New-Object 'System.Collections.Generic.List[object]'
            $newList.Add([ordered]@{ FullName = $fullName; Description = $description2 })
            $merged[$key] = $newList
            $newKeyCount++
            continue
        }

        $existingEntries = $merged[$key]
        $matchedEntry = $null
        foreach ($existing in $existingEntries) {
            if ([string]$existing['FullName'] -ceq $fullName) {
                $matchedEntry = $existing
                break
            }
        }

        if ($null -eq $matchedEntry) {
            # 同一缩写下没有全称匹配的条目：作为新的释义追加。
            # 已有的条目数组可能是反序列化出来的固定大小数组，不支持 Add，
            # 统一转成可变 List 再追加，然后整体替换回去。
            $mutableEntries = New-Object 'System.Collections.Generic.List[object]'
            foreach ($e in $existingEntries) { $mutableEntries.Add($e) }
            $mutableEntries.Add([ordered]@{ FullName = $fullName; Description = $description2 })
            $merged[$key] = $mutableEntries
            $appendedCount++
            continue
        }

        # 缩写 + 全称都匹配上了：仓库这条 Description 是空的、本机有内容，就补上；否则算重复跳过。
        if ([string]::IsNullOrWhiteSpace($matchedEntry['Description']) -and -not [string]::IsNullOrWhiteSpace($description2)) {
            $matchedEntry['Description'] = $description2
            $filledCount++
        }
        else {
            $skipped.Add([pscustomobject]@{ Key = $key; FullName = $fullName })
        }
    }
}

$hasChanges = ($newKeyCount + $appendedCount + $filledCount) -gt 0

# 4. 把合并结果分别写回仓库那份和本机部署那份。
$json = $merged | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($RepoTermsPath, $json, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($DeployedTermsPath, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "同步完成："
Write-Host "  新增缩写：$newKeyCount"
Write-Host "  同一缩写下追加释义（多义词）：$appendedCount"
Write-Host "  补上空缺的 Description：$filledCount"
Write-Host "  跳过（缩写+全称重复，以仓库版本为准）：$($skipped.Count)"

if ($skipped.Count -gt 0) {
    Write-Host ""
    Write-Host "以下条目因为全称重复被跳过（本机的内容未采纳）："
    foreach ($item in $skipped) {
        Write-Host "  [$($item.Key)] $($item.FullName)"
    }
}

# 5. 只有仓库那份真的有变化时才提交推送。
if (-not $hasChanges) {
    Write-Host ""
    Write-Host "仓库内容没有变化，跳过提交。"
    exit 0
}

Write-Host ""
Write-Host "正在提交并推送..."

$commitMessage = "Sync terms.json from $env:COMPUTERNAME (+$newKeyCount keys, +$appendedCount senses, +$filledCount descriptions)"

git -C $repoRoot add "src/TermSearch/terms.json"
git -C $repoRoot commit -m $commitMessage
if ($LASTEXITCODE -ne 0) {
    throw "git commit 失败。"
}

git -C $repoRoot push
if ($LASTEXITCODE -ne 0) {
    throw "git push 失败，改动已经提交在本地，请手动检查后重试 push。"
}

Write-Host "已推送到远程仓库。"
