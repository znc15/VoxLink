# Generates GitHub Release notes from conventional commits since the previous tag.
# Prints Markdown to stdout. Usage: ./scripts/release-notes.ps1
# CI: called from ci.yml when a v* tag is pushed; the current tag comes from GITHUB_REF_NAME.
# NOTE: keep this file UTF-8 WITH BOM so Windows PowerShell 5.1 parses the Chinese titles.
$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 decodes native command output with the OEM codepage;
# force UTF-8 so Chinese commit messages survive. (pwsh defaults to UTF-8 already.)
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

# 1. Resolve the commit range: previous tag..HEAD
$current = $env:GITHUB_REF_NAME
if ([string]::IsNullOrEmpty($current)) {
    $current = git tag --sort=-version:refname 2>$null | Select-Object -First 1
}
$prev = git tag --sort=-version:refname 2>$null |
    Where-Object { $_ -ne $current } |
    Select-Object -First 1

$logLines = @()
if (-not [string]::IsNullOrEmpty($prev)) {
    $logLines = @(git log --pretty=format:"%h%x09%s" "$prev..HEAD" 2>$null)
}
if ($logLines.Count -eq 0) {
    # First release, or the previous tag is not an ancestor: list everything.
    $logLines = @(git log --pretty=format:"%h%x09%s" 2>$null)
    $prev = $null
}

# 2. Parse conventional commits (type(scope): description) and group by type.
$sectionOrder = @("feat", "fix", "perf", "docs", "refactor", "test", "chore")
$sectionTitle = @{
    "feat"     = "✨ 新功能"
    "fix"      = "🐛 修复"
    "perf"     = "⚡ 性能优化"
    "docs"     = "📝 文档"
    "refactor" = "♻️ 重构"
    "test"     = "✅ 测试"
    "chore"    = "🔧 维护"
}
$groups = @{}
$all = New-Object System.Collections.Generic.List[string]
foreach ($line in $logLines) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $hash, $subject = $line -split "`t", 2
    $m = [regex]::Match($subject, '^([a-z]+)(?:\(([^)]+)\))?:\s*(.+)$')
    $item = $null
    if ($m.Success) {
        $type = $m.Groups[1].Value
        $scope = $m.Groups[2].Value
        $desc = $m.Groups[3].Value
        if (-not $groups.ContainsKey($type)) { $groups[$type] = New-Object System.Collections.Generic.List[string] }
        if ($scope) { $item = "- $desc（$scope）" } else { $item = "- $desc" }
        $groups[$type].Add($item)
    } else {
        if (-not $groups.ContainsKey("other")) { $groups["other"] = New-Object System.Collections.Generic.List[string] }
        $item = "- $subject"
        $groups["other"].Add($item)
    }
    $all.Add("- ``$hash`` $subject")
}

# 3. Render Markdown.
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("## 更新说明")
[void]$sb.AppendLine("")
if ($prev) {
    [void]$sb.AppendLine("> 自 $prev 以来的 $($logLines.Count) 条提交自动生成。")
} else {
    [void]$sb.AppendLine("> 首次发布,共 $($logLines.Count) 条提交。")
}
foreach ($type in $sectionOrder) {
    if ($groups.ContainsKey($type) -and $groups[$type].Count -gt 0) {
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("### " + $sectionTitle[$type])
        [void]$sb.AppendLine("")
        foreach ($i in $groups[$type]) { [void]$sb.AppendLine($i) }
    }
}
if ($groups.ContainsKey("other") -and $groups["other"].Count -gt 0) {
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("### 其他")
    [void]$sb.AppendLine("")
    foreach ($i in $groups["other"]) { [void]$sb.AppendLine($i) }
}
if ($all.Count -gt 0) {
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("<details>")
    [void]$sb.AppendLine("<summary>完整提交列表($($all.Count))</summary>")
    [void]$sb.AppendLine("")
    foreach ($i in $all) { [void]$sb.AppendLine($i) }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("</details>")
}
Write-Output $sb.ToString()
