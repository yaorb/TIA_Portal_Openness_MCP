#Requires -Version 5.1
<#
.SYNOPSIS
    Regenerate docs/tool-capability-matrix.md from the [McpServerTool] attributes in McpServer.cs.
.DESCRIPTION
    Static extraction, offline. Replaces the previous (broken) generator that emitted the literal
    PowerShell expression "$(@{name=...}.name)" into every Tool cell instead of the tool name.
    Handles both single-line and multi-line (string-concatenated) Description attributes.
    Run from any directory; defaults resolve against the delivery package root (parent of this script).
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Generate-ToolCapabilityMatrix.ps1
#>
param(
    [string]$SourceFile = "",
    [string]$OutFile = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
if (-not $OutFile)    { $OutFile    = Join-Path $root "docs\tool-capability-matrix.md" }

# [McpServerTool] attributes live across the McpServer partial files (McpServer.cs,
# McpServer.Runtime.cs, ...). Scan all of them so partial-file tools are not missed.
if (-not $SourceFile) {
    $dir = Join-Path $root "tools\tiaportal-mcp\src\TiaMcpServer\ModelContextProtocol"
    $files = Get-ChildItem -LiteralPath $dir -Filter "McpServer*.cs" | Sort-Object Name
    if (-not $files) { throw "No McpServer*.cs found in: $dir" }
    $text = ($files | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8) }) -join "`n"
} else {
    if (-not (Test-Path -LiteralPath $SourceFile)) { throw "Source not found: $SourceFile" }
    $text = [System.IO.File]::ReadAllText($SourceFile, [System.Text.Encoding]::UTF8)
}

# Match each tool's attributes. Both spellings occur in this repo:
#     [McpServerTool(Name = "X"), Description("...")]        (same line)
#     [McpServerTool(Name = "X")]                            (stacked, the current style)
#     [Description("...")]
# Only the first form used to be matched, so once the source was reflowed to stacked attributes
# this generator parsed ZERO tools and threw — i.e. the matrix silently stopped being regenerable
# (the doc still looked fine, which is exactly how it went unnoticed). Description bodies may span
# lines as "..." + "..." concatenation; Singleline lets . cross newlines.
$blockRx = [regex]::new(
    '(?m)^\s*\[McpServerTool\(Name\s*=\s*"(?<name>[^"]+)"\)\]\s*(?:,\s*Description\(|\r?\n\s*\[Description\()(?<body>.*?)\)\]',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
$segRx = [regex]'"(?<seg>[^"]*)"'

$tools = New-Object System.Collections.Generic.List[object]
foreach ($m in $blockRx.Matches($text)) {
    $name = $m.Groups['name'].Value
    # Rebuild the description by concatenating every quoted segment in the body.
    $desc = (($segRx.Matches($m.Groups['body'].Value) | ForEach-Object { $_.Groups['seg'].Value }) -join '').Trim()
    $layer = "L?"; $domain = "Misc"
    $tag = ([regex]'^\[(?<layer>L[0-9])\]\[(?:Category:)?(?<domain>[^\]]+)\]').Match($desc)
    if ($tag.Success) { $layer = $tag.Groups['layer'].Value; $domain = $tag.Groups['domain'].Value }
    $descCell = $desc -replace '\|', '\|'
    $tools.Add([pscustomobject]@{ Name = $name; Layer = $layer; Domain = $domain; Desc = $descCell; Order = $tools.Count })
}
if ($tools.Count -eq 0) {
    # 报「从哪个文件/目录里没解析出来」。原来只打 $SourceFile，走默认扫描时它是空的，
    # 于是这条错误消息本身什么也没说。
    $where = if ($SourceFile) { $SourceFile } else { "the McpServer*.cs files under $dir" }
    throw "No [McpServerTool] entries parsed from $where"
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# MCP 工具能力矩阵")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("本文件由源码中的 ``[McpServerTool]`` 静态抽取生成，运行时仍以 ``tools/list`` 为准。")
[void]$sb.AppendLine("")
[void]$sb.AppendLine(("- 生成时间：{0}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss")))
[void]$sb.AppendLine(("- 工具数量：{0}" -f $tools.Count))
[void]$sb.AppendLine("")

foreach ($layer in @("L0", "L1", "L2", "L?")) {
    $inLayer = @($tools | Where-Object { $_.Layer -eq $layer })
    if ($inLayer.Count -eq 0) { continue }
    [void]$sb.AppendLine("## $layer")
    [void]$sb.AppendLine("")
    foreach ($domain in ($inLayer | Select-Object -ExpandProperty Domain -Unique | Sort-Object)) {
        [void]$sb.AppendLine("### $domain")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("| Tool | Description |")
        [void]$sb.AppendLine("|---|---|")
        foreach ($t in ($inLayer | Where-Object { $_.Domain -eq $domain } | Sort-Object Order)) {
            [void]$sb.AppendLine(("| {0} | {1} |" -f $t.Name, $t.Desc))
        }
        [void]$sb.AppendLine("")
    }
}

[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("Wrote {0} tools to {1}" -f $tools.Count, $OutFile) -ForegroundColor Green
