#Requires -Version 5.1
<#
.SYNOPSIS
    Offline guard: forbid silent catch blocks (no reason, no outlet).

.DESCRIPTION
    纯源码静态检查，不需要 TIA Portal / 不需要编译。跑在 CI 与本地均可。

    R1（强制）空 catch 必须写明为什么可以吞：
       catch 体内只有注释、没有任何代码时，注释不能是 `// ignored` 这类没有信息量的占位，
       也不能没有注释。写法要求：说清「守的是什么、失败后走哪条路」。
       例：// teardown：释放失败无处可报，也不影响结果
           // 反射探测：读不到就当没有，调用方按返回值分支处理

    R2（-Strict，默认关）捕获了异常变量却从不使用它，且不记日志、不重抛。
       默认关闭的原因：这类里多数是 `catch (Exception ex) { return false; }` ——
       失败的「事实」已经由返回值上报，只是没带原因。误报率高，所以只在 -Strict 下报。

    为什么只强制 R1：本仓 P0–P2 修掉的 98 处静默 catch，全部都能被 R1 命中
    （7 处 P0 + 46 处无注释 + 48 处机械 // ignored 都是空体）。R1 是经实测验证过的那条线。

    基线（ratchet）：存量已清零，所以当前**没有**基线文件 —— 守卫按零容忍跑。
    将来若确实要临时放行一批存量，用 -UpdateBaseline 生成 scripts/try-catch-baseline.txt
    （格式 <仓库相对路径>=<允许的违规数>），CI 就只拦新增；清完再删掉该文件。
    要清理存量时用 -NoBaseline，它会把基线内的违规也逐条列出来。

.EXAMPLE
    pwsh -File .\scripts\Audit-TryCatch.ps1
.EXAMPLE
    pwsh -File .\scripts\Audit-TryCatch.ps1 -Strict          # 连 R2 一起报（不设基线）
.EXAMPLE
    pwsh -File .\scripts\Audit-TryCatch.ps1 -NoBaseline      # 把基线内存量也逐条列出来，用于清理基线
.EXAMPLE
    pwsh -File .\scripts\Audit-TryCatch.ps1 -UpdateBaseline  # 把当前存量写成基线
#>
param(
    [Parameter(Mandatory = $false)][string]$SourceRoot = "",
    [Parameter(Mandatory = $false)][string]$Baseline = "",
    [switch]$Strict,
    [switch]$UpdateBaseline,
    [switch]$NoBaseline
)

$ErrorActionPreference = "Stop"

# ── 工具 ────────────────────────────────────────────────────────────────────

# 把注释与字符串字面量的内容替换成同长度的空格，保留行号。
# 为什么必须做：注释里出现 "catch" 会让朴素正则把它当成代码 —— 本仓就有一句
# 「// 别 catch 吞成 null」的中文注释，第一版审计脚本因此在 904 个之外多算了 16 个。
function Get-MaskedSource {
    param([string]$Text)

    $sb = New-Object System.Text.StringBuilder $Text.Length
    $i = 0
    $n = $Text.Length
    while ($i -lt $n) {
        $c = $Text[$i]
        # 行注释
        if ($c -eq '/' -and ($i + 1) -lt $n -and $Text[$i + 1] -eq '/') {
            while ($i -lt $n -and $Text[$i] -ne "`n") { [void]$sb.Append(' '); $i++ }
            continue
        }
        # 块注释
        if ($c -eq '/' -and ($i + 1) -lt $n -and $Text[$i + 1] -eq '*') {
            [void]$sb.Append('  '); $i += 2
            while ($i -lt $n -and -not ($Text[$i] -eq '*' -and ($i + 1) -lt $n -and $Text[$i + 1] -eq '/')) {
                [void]$sb.Append($(if ($Text[$i] -eq "`n") { "`n" } else { ' ' })); $i++
            }
            if ($i -lt $n) { [void]$sb.Append('  '); $i += 2 }
            continue
        }
        # 逐字字符串 @"..."（"" 表示一个引号）
        if ($c -eq '@' -and ($i + 1) -lt $n -and $Text[$i + 1] -eq '"') {
            [void]$sb.Append('  '); $i += 2
            while ($i -lt $n) {
                if ($Text[$i] -eq '"') {
                    if (($i + 1) -lt $n -and $Text[$i + 1] -eq '"') { [void]$sb.Append('  '); $i += 2; continue }
                    [void]$sb.Append(' '); $i++; break
                }
                [void]$sb.Append($(if ($Text[$i] -eq "`n") { "`n" } else { ' ' })); $i++
            }
            continue
        }
        # 原始字符串 """..."""
        if ($c -eq '"' -and ($i + 2) -lt $n -and $Text[$i + 1] -eq '"' -and $Text[$i + 2] -eq '"') {
            [void]$sb.Append('   '); $i += 3
            while ($i -lt $n) {
                if ($Text[$i] -eq '"' -and ($i + 2) -lt $n -and $Text[$i + 1] -eq '"' -and $Text[$i + 2] -eq '"') {
                    [void]$sb.Append('   '); $i += 3; break
                }
                [void]$sb.Append($(if ($Text[$i] -eq "`n") { "`n" } else { ' ' })); $i++
            }
            continue
        }
        # 普通/插值字符串（插值里的花括号一并掩掉，这样括号配对不会被它干扰）
        if (($c -eq '"') -or ($c -eq '$' -and ($i + 1) -lt $n -and $Text[$i + 1] -eq '"')) {
            $skip = if ($c -eq '$') { 2 } else { 1 }
            for ($k = 0; $k -lt $skip; $k++) { [void]$sb.Append(' ') }
            $i += $skip
            $depth = 0
            while ($i -lt $n) {
                $d = $Text[$i]
                if ($d -eq '\') { [void]$sb.Append('  '); $i += 2; continue }
                if ($d -eq '{') { $depth++ }
                elseif ($d -eq '}') { if ($depth -gt 0) { $depth-- } }
                elseif ($d -eq '"' -and $depth -le 0) { [void]$sb.Append(' '); $i++; break }
                [void]$sb.Append($(if ($d -eq "`n") { "`n" } else { ' ' })); $i++
            }
            continue
        }
        # 字符字面量
        if ($c -eq "'") {
            [void]$sb.Append(' '); $i++
            while ($i -lt $n) {
                if ($Text[$i] -eq '\') { [void]$sb.Append('  '); $i += 2; continue }
                if ($Text[$i] -eq "'") { [void]$sb.Append(' '); $i++; break }
                [void]$sb.Append(' '); $i++
            }
            continue
        }
        [void]$sb.Append($c)
        $i++
    }
    return $sb.ToString()
}

function Get-LineNumbers {
    param([string]$Text)
    $lines = New-Object 'System.Collections.Generic.List[int]'
    $ln = 1
    for ($i = 0; $i -lt $Text.Length; $i++) {
        $lines.Add($ln)
        if ($Text[$i] -eq "`n") { $ln++ }
    }
    $lines.Add($ln)
    return $lines
}

# 空体判定：去掉注释与空白后没有代码
function Test-BodyIsEmpty {
    param([string]$Body)
    foreach ($line in ($Body -split "`r?`n")) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('//') -or $t.StartsWith('/*') -or $t.StartsWith('*')) { continue }
        return $false
    }
    return $true
}

function Get-BodyComment {
    param([string]$Body)
    $parts = @()
    foreach ($line in ($Body -split "`r?`n")) {
        $t = $line.Trim()
        if ($t.StartsWith('//')) { $parts += ($t -replace '^//\s*', '') }
    }
    # 块注释也是理由：本仓有几处写的是 /* best-effort cleanup */、/* malformed line — skip */，
    # 只认 // 会把它们误判成「没有理由」。
    foreach ($m in [regex]::Matches($Body, '(?s)/\*(.*?)\*/')) {
        $parts += ([regex]::Replace($m.Groups[1].Value, '\s+', ' ')).Trim()
    }
    return ($parts -join ' ').Trim()
}

function Get-EnclosingMethod {
    param([string[]]$Lines, [int]$CatchLine)
    $from = [Math]::Max(0, $CatchLine - 200)
    for ($k = $CatchLine - 2; $k -ge $from; $k--) {
        $t = $Lines[$k]
        if ($t -match '^\s{2,6}(public|private|internal|protected|static).*\b\w+\s*\(' -and $t -notmatch '=>' -and $t -notmatch '=' -and -not $t.TrimStart().StartsWith('//')) {
            return $t.Trim()
        }
    }
    return '(未识别)'
}

# ── 规则引擎 ────────────────────────────────────────────────────────────────
# 返回违规列表；每条含 File/Line/Method/Rule/Detail
function Invoke-SilentCatchScan {
    param([string]$Text, [string]$DisplayPath, [bool]$StrictMode)

    $violations = New-Object System.Collections.Generic.List[object]
    $masked = Get-MaskedSource -Text $Text
    $lineAt = Get-LineNumbers -Text $Text
    $lines = $Text -split "`r?`n"

    $ignoredOnly = '^(ignored|ignore|ignoredexception|no-?op|swallow)\.?$'

    foreach ($m in [regex]::Matches($masked, '\bcatch\b')) {
        $i = $m.Index
        $j = $i
        while ($j -lt $masked.Length -and $masked[$j] -ne '{') { $j++ }
        if ($j -ge $masked.Length) { continue }

        $clause = ([regex]::Replace($masked.Substring($i, $j - $i), '\s+', ' ')).Trim()
        $depth = 0; $k = $j; $end = -1
        while ($k -lt $masked.Length) {
            if ($masked[$k] -eq '{') { $depth++ }
            elseif ($masked[$k] -eq '}') { $depth--; if ($depth -eq 0) { $end = $k; break } }
            $k++
        }
        if ($end -lt 0) { continue }

        $body = $Text.Substring($j + 1, $end - $j - 1)
        $bodyMasked = $masked.Substring($j + 1, $end - $j - 1)
        $line = $lineAt[$i]
        $method = Get-EnclosingMethod -Lines $lines -CatchLine $line

        if (Test-BodyIsEmpty -Body $body) {
            # R1：空 catch 必须写明理由
            $comment = Get-BodyComment -Body $body
            $normalized = ($comment -replace '[\.。;；]', '').Trim()
            if ($comment -eq '') {
                $violations.Add([pscustomobject]@{
                    File = $DisplayPath; Line = $line; Method = $method; Rule = 'R1'
                    Detail = '空 catch 且没有任何注释'
                })
            }
            elseif ($normalized -match $ignoredOnly) {
                $violations.Add([pscustomobject]@{
                    File = $DisplayPath; Line = $line; Method = $method; Rule = 'R1'
                    Detail = "空 catch 的注释没有信息量（`"$comment`"）"
                })
            }
            continue
        }

        # R2（-Strict）：捕获了变量却从不使用，且不记日志、不重抛
        if ($StrictMode) {
            $varName = ''
            $vm = [regex]::Match($clause, 'catch\s*\(\s*[\w\.<>\[\],\s]+\s+(\w+)\s*(\)|when)')
            if ($vm.Success) { $varName = $vm.Groups[1].Value }
            if ($varName -ne '') {
                $usesVar = [regex]::IsMatch($bodyMasked, "\b$varName\b")
                $hasThrow = [regex]::IsMatch($bodyMasked, '\bthrow\b')
                $hasLog = [regex]::IsMatch($bodyMasked, '\b(logger|_logger|Logger|LogInformation|LogWarning|LogError|LogDebug|LogTrace)\b')
                if (-not $usesVar -and -not $hasThrow -and -not $hasLog) {
                    $violations.Add([pscustomobject]@{
                        File = $DisplayPath; Line = $line; Method = $method; Rule = 'R2'
                        Detail = "捕获了 $varName 却从不使用它，也不记日志、不重抛"
                    })
                }
            }
        }
    }
    return $violations
}

# ── 必错哨兵 ────────────────────────────────────────────────────────────────
# 闸门自己坏了必须是 FAIL，不能是假 PASS（本仓 Check-DeadToolReferences.py 同款约定）
function Test-GuardSentinel {
    $bad = @'
public class T
{
  public void A()
  {
    try { X(); }
    catch
    {
      // ignored
    }
  }
  public void B()
  {
    try { X(); }
    catch { }
  }
  public void C()
  {
    try { X(); }
    catch
    {
      // 注释里有个 catch 字样，不该被当成代码
    }
  }
}
'@
    $found = @(Invoke-SilentCatchScan -Text $bad -DisplayPath '(sentinel)' -StrictMode $false)
    if ($found.Count -ne 2) {
        Write-Host "FAIL  哨兵未按预期命中：期望 2 处违规（A 与 B），实际 $($found.Count) 处。闸门本身坏了，结果不可信。" -ForegroundColor Red
        return $false
    }

    $good = @'
public class T
{
  public void A()
  {
    try { X(); }
    catch (Exception ex)
    {
      // 读不到就当没有，调用方按返回值分支处理
      logger?.LogWarning(ex, "A failed");
    }
  }
}
'@
    $ok = @(Invoke-SilentCatchScan -Text $good -DisplayPath '(sentinel)' -StrictMode $false)
    if ($ok.Count -ne 0) {
        Write-Host "FAIL  哨兵误报：合规代码被判为 $($ok.Count) 处违规。闸门本身坏了。" -ForegroundColor Red
        return $false
    }
    return $true
}

# ── 主流程 ──────────────────────────────────────────────────────────────────

function Resolve-RepoRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

$repoRoot = Resolve-RepoRoot
if (-not $SourceRoot) { $SourceRoot = Join-Path $repoRoot "tools/tiaportal-mcp/src/TiaMcpServer" }
if (-not $Baseline) { $Baseline = Join-Path $PSScriptRoot "try-catch-baseline.txt" }

if (-not (Test-Path -LiteralPath $SourceRoot)) {
    Write-Host "FAIL  源码目录不存在：$SourceRoot" -ForegroundColor Red
    exit 2
}
if ($Strict -and $UpdateBaseline) {
    Write-Host "FAIL  -Strict 与 -UpdateBaseline 不能同时用（-Strict 不设基线）。" -ForegroundColor Red
    exit 2
}

Write-Host "== 必错哨兵 =="
if (-not (Test-GuardSentinel)) { exit 2 }
Write-Host "  通过：闸门能命中空 catch，也不会误报合规写法"
Write-Host ""

$files = Get-ChildItem -LiteralPath $SourceRoot -Recurse -Include *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin|obj-v20|bin-v20)\\' }

$all = New-Object System.Collections.Generic.List[object]
foreach ($f in $files) {
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $rel = $f.FullName.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
    foreach ($v in (Invoke-SilentCatchScan -Text $text -DisplayPath $rel -StrictMode ([bool]$Strict))) {
        $all.Add($v)
    }
}

$byFile = @{}
foreach ($v in $all) {
    if (-not $byFile.ContainsKey($v.File)) { $byFile[$v.File] = 0 }
    $byFile[$v.File] = $byFile[$v.File] + 1
}

if ($UpdateBaseline) {
    $out = New-Object System.Collections.Generic.List[string]
    $out.Add('# 静默 catch 守卫（scripts/Audit-TryCatch.ps1）的存量基线。')
    $out.Add('# 格式： <仓库相对路径>=<允许的违规数>。CI 只拦**新增**；清理一处就把数字改小，')
    $out.Add('# 归零时删掉该行。重新生成： pwsh -File scripts/Audit-TryCatch.ps1 -UpdateBaseline')
    foreach ($k in ($byFile.Keys | Sort-Object)) { $out.Add("$k=$($byFile[$k])") }
    [System.IO.File]::WriteAllLines($Baseline, $out, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "已写入基线：$Baseline（$($byFile.Count) 个文件、$($all.Count) 处存量）"
    exit 0
}

# 与基线对账（-NoBaseline：把存量也当违规列出来，用于逐条清理基线）
$baselineMap = @{}
if ((Test-Path -LiteralPath $Baseline) -and -not $Strict -and -not $NoBaseline) {
    foreach ($line in (Get-Content -LiteralPath $Baseline)) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $p = $t.Split('=')
        if ($p.Count -eq 2) { $baselineMap[$p[0].Trim()] = [int]$p[1].Trim() }
    }
}

$new = New-Object System.Collections.Generic.List[object]
foreach ($v in $all) {
    $allowed = 0
    if ($baselineMap.ContainsKey($v.File)) { $allowed = $baselineMap[$v.File] }
    # 同一文件内超出的部分算新增：按出现顺序扣减额度
    if ($allowed -gt 0) { $baselineMap[$v.File] = $allowed - 1; continue }
    $new.Add($v)
}

Write-Host "== 静默 catch 检查 =="
Write-Host ("  扫描 {0} 个 .cs 文件；命中 {1} 处{2}" -f $files.Count, $all.Count, $(if ($Strict) { '（-Strict：R1 + R2，不与基线对账）' } elseif ($NoBaseline) { '（-NoBaseline：存量也逐条列出）' } else { "，其中基线内 $($all.Count - $new.Count) 处" }))

if ($new.Count -gt 0) {
    Write-Host ""
    foreach ($v in $new) {
        Write-Host ("  FAIL  [{0}] {1}:{2}" -f $v.Rule, $v.File, $v.Line) -ForegroundColor Red
        Write-Host ("        {0}" -f $v.Detail)
        Write-Host ("        所在方法：{0}" -f $v.Method)
    }
    Write-Host ""
    Write-Host "  空 catch 必须写明「守的是什么、失败后走哪条路」，例如：" -ForegroundColor Yellow
    Write-Host "    // teardown：释放失败无处可报，也不影响结果"
    Write-Host "    // 反射探测：读不到就当没有，调用方按返回值分支处理"
    Write-Host "    // 探测结果会影响分支，故记日志：logger?.LogWarning(ex, `"...`");"
    Write-Host ""
    Write-Host "  若确实要保留存量写法，请说明理由；若这是新引入的，请补上理由或改成记日志。" -ForegroundColor Yellow
    exit 1
}

Write-Host "  通过：没有新增的静默 catch" -ForegroundColor Green
exit 0
