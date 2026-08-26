<#
.SYNOPSIS
    把本机部署的 terms.json 和 git 仓库里的 terms.json 双向同步。

.DESCRIPTION
    用于多台设备共用同一个 TermSearch 术语表的场景：程序本身在每台设备上是
    独立的绿色版部署（跟 git 仓库是两个分开的文件夹），你在不同设备上用
    程序自带的"快捷补充"/"编辑词条"功能改动 terms.json 后，这些改动只落在
    本机部署文件夹里，不会自动出现在别的设备或仓库里。这个脚本负责把两边的
    内容对齐、汇总到仓库，再发给其他设备。

    流程：
      1. 检查仓库工作目录是否干净（有未提交的改动就中止，不做任何覆盖）。
      2. 读取"上次在这台设备上同步到的 commit"（记在本地 git config 里，
         不会被提交/推送，纯粹是这台设备自己的书签），取出那个版本的
         terms.json 作为三方合并的基准（base）。第一次在这台设备上跑、
         或者找不到那个 commit 时，base 视为空，退化成普通的两边并集。
      3. git pull，拉取仓库里 terms.json 的最新版本（可能包含其他设备
         之前同步上去的改动）。
      4. 以 base 为基准，对本机（ours）和拉取后的仓库版本（theirs）做
         三方合并：
           - 缩写不同 -> 都保留
           - 同一缩写全称不同 -> 都保留（多义词）
           - 缩写 + 全称相同、一边 Description 空一边有内容 -> 补全
           - 缩写 + 全称相同、内容不一样：
             - 只有本机相对 base 变了（比如你在这台设备上编辑过这条）
               -> 采用本机的版本，同步给仓库和其他设备。
             - 只有仓库相对 base 变了（比如别的设备已经同步过这条编辑）
               -> 采用仓库的版本（本机这条其实是旧内容）。
             - 两边相对 base 都变了、还变得不一样 -> 真冲突，两边都不
               自动采用，原样打印到屏幕上，交给你自己确认，不阻塞其他
               条目的同步。
      5. 把合并结果分别写回仓库那份和本机部署那份（有冲突的条目除外，
         那几条会保持仓库原样，等你手动确认）。
      6. 只有仓库那份内容真的有变化时才 git add / commit / push；
         没有变化就不产生空提交。
      7. 把这次同步后仓库的最新 commit 记成"下次同步的基准"。

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

# git show 要用仓库内的相对路径（正斜杠），从 RepoTermsPath 相对 repoRoot 算出来。
$repoTermsRelative = $RepoTermsPath.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'

$webExtensionsAsm = [System.Reflection.Assembly]::Load("System.Web.Extensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35")
$serializerType = $webExtensionsAsm.GetType("System.Web.Script.Serialization.JavaScriptSerializer")
$serializer = [Activator]::CreateInstance($serializerType)
$serializer.MaxJsonLength = [int]::MaxValue

function Get-BaseDescription {
    param($BaseData, [string]$Key, [string]$FullName)
    if ($null -eq $BaseData -or -not $BaseData.ContainsKey($Key)) { return $null }
    foreach ($e in $BaseData[$Key]) {
        if ([string]$e['FullName'] -ceq $FullName) { return [string]$e['Description'] }
    }
    return $null
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

# 2. 取出上次在这台设备上同步到的版本，作为三方合并的基准。这个 commit 记在
#    本地 git config 里（config key: termsearch.lastsyncedcommit），只留在这台
#    设备的本地仓库配置里，不会被 commit/push，纯粹是"这台设备自己的书签"。
$lastSyncedCommit = git -C $repoRoot config --get termsearch.lastsyncedcommit 2>$null
$baseData = $null
if (-not [string]::IsNullOrWhiteSpace($lastSyncedCommit)) {
    $baseContent = git -C $repoRoot show "${lastSyncedCommit}:${repoTermsRelative}" 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($baseContent)) {
        try { $baseData = $serializer.DeserializeObject($baseContent) } catch { $baseData = $null }
    }
}
if ($null -eq $baseData) {
    Write-Host "没找到这台设备上次同步的基准版本，本次按普通的两边并集处理（不影响正确性，只是没法细分辨「只有一边改了」还是「两边都改了」）。"
}

# 3. 拉取仓库最新版本。
Write-Host "正在拉取仓库最新版本..."
git -C $repoRoot pull
if ($LASTEXITCODE -ne 0) {
    throw "git pull 失败，请检查网络/冲突后手动处理。"
}

# 4. 读取两份文件，用大小写敏感的方式解析。
$repoData = $serializer.DeserializeObject((Get-Content -Path $RepoTermsPath -Raw -Encoding UTF8))
$deployedData = $serializer.DeserializeObject((Get-Content -Path $DeployedTermsPath -Raw -Encoding UTF8))

$merged = New-Object 'System.Collections.Generic.Dictionary[string,object]'
foreach ($key in $repoData.Keys) {
    $merged[$key] = $repoData[$key]
}

$newKeyCount = 0
$appendedCount = 0
$filledCount = 0
$editPropagatedCount = 0
$conflicts = New-Object 'System.Collections.Generic.List[object]'

foreach ($key in $deployedData.Keys) {
    foreach ($entry in $deployedData[$key]) {
        $fullName = $entry['FullName']
        $oursDesc = [string]$entry['Description']

        if (-not $merged.ContainsKey($key)) {
            $newList = New-Object 'System.Collections.Generic.List[object]'
            $newList.Add([ordered]@{ FullName = $fullName; Description = $oursDesc })
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
            $mutableEntries.Add([ordered]@{ FullName = $fullName; Description = $oursDesc })
            $merged[$key] = $mutableEntries
            $appendedCount++
            continue
        }

        $theirsDesc = [string]$matchedEntry['Description']

        if ($theirsDesc -ceq $oursDesc) {
            continue # 内容完全一样，没什么好合并的。
        }

        if ([string]::IsNullOrWhiteSpace($theirsDesc) -and -not [string]::IsNullOrWhiteSpace($oursDesc)) {
            $matchedEntry['Description'] = $oursDesc
            $filledCount++
            continue
        }

        if ([string]::IsNullOrWhiteSpace($oursDesc)) {
            continue # 本机是空的，仓库已经有内容，没什么好合并的。
        }

        # 两边都有内容、而且不一样：跟 base 比一比，看看到底是谁改的。
        $baseDesc = Get-BaseDescription -BaseData $baseData -Key $key -FullName $fullName
        $theirsChanged = ($null -eq $baseDesc) -or ($theirsDesc -cne $baseDesc)
        $oursChanged = ($null -eq $baseDesc) -or ($oursDesc -cne $baseDesc)

        if ($theirsChanged -and -not $oursChanged) {
            # 只有仓库那边变了，本机这条其实是相对 base 没动过的旧内容，直接用仓库的（已经是 $merged 里的值了）。
            continue
        }
        elseif ($oursChanged -and -not $theirsChanged) {
            # 只有本机变了（这台设备上编辑过），仓库那边还是旧内容，把本机的编辑同步上去。
            $matchedEntry['Description'] = $oursDesc
            $editPropagatedCount++
        }
        else {
            # 两边相对 base 都变了、还变得不一样——真冲突，这次不自动合并，仓库版本原样保留。
            $conflicts.Add([pscustomobject]@{ Key = $key; FullName = $fullName; Ours = $oursDesc; Theirs = $theirsDesc })
        }
    }
}

$hasChanges = ($newKeyCount + $appendedCount + $filledCount + $editPropagatedCount) -gt 0

# 5. 把合并结果写回本机部署那份（冲突条目保持仓库原样，写完后这些条目两边也是一致的）。
#    仓库那份只在内容真的有变化时才重写——PowerShell 的 ConvertTo-Json 格式跟仓库里
#    已有文件的格式不一定一样，内容没变时也重写会把纯格式差异变成一次"改动"，导致
#    下次跑脚本时被"工作目录不干净"的检查挡住，所以内容没变就完全不碰这个文件。
$json = $merged | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($DeployedTermsPath, $json, [System.Text.UTF8Encoding]::new($false))
if ($hasChanges) {
    [System.IO.File]::WriteAllText($RepoTermsPath, $json, [System.Text.UTF8Encoding]::new($false))
}

Write-Host "同步完成："
Write-Host "  新增缩写：$newKeyCount"
Write-Host "  同一缩写下追加释义（多义词）：$appendedCount"
Write-Host "  补上空缺的 Description：$filledCount"
Write-Host "  同步本机的编辑到仓库：$editPropagatedCount"
Write-Host "  真冲突（需要你手动确认）：$($conflicts.Count)"

if ($conflicts.Count -gt 0) {
    Write-Host ""
    Write-Host "以下条目本机和仓库都改过、但改得不一样，这次没有自动合并（仓库版本原样保留，本机的编辑没有生效）："
    foreach ($c in $conflicts) {
        Write-Host "  [$($c.Key)] $($c.FullName)"
        Write-Host "    本机：$($c.Ours)"
        Write-Host "    仓库：$($c.Theirs)"
    }
    Write-Host "确认好要哪个版本后，可以用程序里的「编辑词条」功能改一下（默认 Ctrl+Shift+Enter），或者直接编辑 terms.json，改完后重新跑一次这个脚本同步。"
}

# 6. 只有仓库那份真的有变化时才提交推送。
if ($hasChanges) {
    Write-Host ""
    Write-Host "正在提交并推送..."

    $commitMessage = "Sync terms.json from $env:COMPUTERNAME (+$newKeyCount keys, +$appendedCount senses, +$filledCount descriptions, +$editPropagatedCount edits)"

    git -C $repoRoot add $repoTermsRelative
    git -C $repoRoot commit -m $commitMessage
    if ($LASTEXITCODE -ne 0) {
        throw "git commit 失败。"
    }

    git -C $repoRoot push
    if ($LASTEXITCODE -ne 0) {
        throw "git push 失败，改动已经提交在本地，请手动检查后重试 push。"
    }

    Write-Host "已推送到远程仓库。"
}
else {
    Write-Host ""
    Write-Host "仓库内容没有变化，跳过提交。"
}

# 7. 把这次同步后仓库的最新 commit 记成下次同步的基准。
$headCommit = git -C $repoRoot rev-parse HEAD
git -C $repoRoot config termsearch.lastsyncedcommit $headCommit
