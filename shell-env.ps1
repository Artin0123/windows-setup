# shell-env.ps1 - 寫入 Bash (~/.bashrc) 與 PowerShell ($PROFILE) 的基本環境設定
# 以標記區塊管理，可重複執行（已存在就原地更新，不會重複寫入）；
# 也會把舊版「沒有標記」的相同內容認出來並收進區塊，避免重複。

$utf8Bom   = New-Object System.Text.UTF8Encoding($true)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-Utf8Text {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return "" }
    # 明確以 UTF-8 讀取（5.1 的 Get-Content 會把無 BOM 的 UTF-8 當 ANSI，中文變亂碼）
    return ([System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) -replace "`r`n", "`n" -replace "`r", "`n")
}

# $Text 與 $Inner 皆為 LF 換行。回傳更新後的文字。
function Set-ManagedBlock {
    param(
        [string]$Text,
        [string]$StartMarker,
        [string]$EndMarker,
        [string]$Inner,
        [string[]]$LegacyPatterns = @()    # regex；沒有標記的舊版內容，會被移除並以區塊取代
    )
    $block = $StartMarker + "`n" + $Inner.Trim("`n") + "`n" + $EndMarker

    # 1. 已有標記區塊：第一個原地更新，其餘重複的移除（不用 [regex]::Replace，避免 $_ 之類被展開）
    $pattern = '(?s)' + [regex]::Escape($StartMarker) + '.*?' + [regex]::Escape($EndMarker)
    $found = [regex]::Matches($Text, $pattern)
    if ($found.Count -gt 0) {
        $sb = New-Object System.Text.StringBuilder
        $pos = 0
        $first = $true
        foreach ($m in $found) {
            [void]$sb.Append($Text.Substring($pos, $m.Index - $pos))
            if ($first) { [void]$sb.Append($block); $first = $false }
            $pos = $m.Index + $m.Length
        }
        [void]$sb.Append($Text.Substring($pos))
        return $sb.ToString()
    }

    # 2. 沒有標記：移除舊版相同內容，並把區塊放在第一個舊內容的位置
    $insertAt = -1
    foreach ($pat in $LegacyPatterns) {
        while ($true) {
            $m = [regex]::Match($Text, $pat)
            if (-not $m.Success) { break }
            if ($insertAt -lt 0 -or $m.Index -lt $insertAt) { $insertAt = $m.Index }
            $Text = $Text.Remove($m.Index, $m.Length)
        }
    }
    if ($insertAt -ge 0) {
        return $Text.Substring(0, $insertAt) + $block + "`n" + $Text.Substring($insertAt)
    }

    # 3. 全新：放在檔案最前面
    if ([string]::IsNullOrWhiteSpace($Text)) { return $block + "`n" }
    return $block + "`n`n" + $Text.TrimStart("`n")
}

# ------------------------------------------------------------------------------
# 1. Bash (~/.bashrc)
# ------------------------------------------------------------------------------
$bashrcPath = Join-Path $HOME ".bashrc"
$bashInner = @'
export TERM=xterm-256color
export EDITOR=notepad.exe
export MSYS_NO_PATHCONV=1
'@ -replace "`r`n", "`n"

$bashLegacy = @(
    '(?m)^export TERM=xterm-256color[ \t]*(\n|\z)',
    '(?m)^export EDITOR=notepad\.exe[ \t]*(\n|\z)',
    '(?m)^export MSYS_NO_PATHCONV=1[ \t]*(\n|\z)'
)
$bashOld = Read-Utf8Text $bashrcPath
$bashNew = Set-ManagedBlock -Text $bashOld -StartMarker '# === SHELL_ENV_START ===' -EndMarker '# === SHELL_ENV_END ===' -Inner $bashInner -LegacyPatterns $bashLegacy
if ($bashNew -ne $bashOld) {
    [System.IO.File]::WriteAllText($bashrcPath, $bashNew, $utf8NoBom)    # bashrc：UTF-8 無 BOM + LF
    Write-Host "[Bash] 已更新 $bashrcPath 的環境變數區塊。" -ForegroundColor Green
} else {
    Write-Host "[Bash] 環境變數區塊已是最新，跳過。" -ForegroundColor Yellow
}

# ------------------------------------------------------------------------------
# 2. PowerShell ($PROFILE)
# ------------------------------------------------------------------------------
$psInner = @'
$GitBashBin = Get-ChildItem (Join-Path $HOME 'scoop\apps\git') -Directory |
    Where-Object { $_.Name -match '^\d+(\.\d+)+' } |
    Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending |
    ForEach-Object { Join-Path $_.FullName 'bin' } |
    Where-Object { Test-Path (Join-Path $_ 'bash.exe') } |
    Select-Object -First 1

if ($GitBashBin -and (($env:Path -split ';') -notcontains $GitBashBin)) {
    $env:Path = "$GitBashBin;$env:Path"
}

# 設定 PowerShell 預設讀寫檔案的編碼為 UTF-8
$PSDefaultParameterValues['*:Encoding'] = 'utf8'

# 設定 Console 輸出與輸入的編碼為 UTF-8
[console]::InputEncoding = [console]::OutputEncoding = [System.Text.Encoding]::UTF8
'@ -replace "`r`n", "`n"

$psProfilePath = $PROFILE
$psDir = Split-Path $psProfilePath
if (-not (Test-Path $psDir)) { New-Item -ItemType Directory -Force -Path $psDir | Out-Null }

$psLegacy = @('(?m)' + [regex]::Escape($psInner.Trim("`n")) + '[ \t]*(\n|\z)')
$psOld = Read-Utf8Text $psProfilePath
$psNew = Set-ManagedBlock -Text $psOld -StartMarker '# === SHELL_ENV_START ===' -EndMarker '# === SHELL_ENV_END ===' -Inner $psInner -LegacyPatterns $psLegacy
if ($psNew -ne $psOld) {
    # $PROFILE：UTF-8 含 BOM + CRLF
    [System.IO.File]::WriteAllText($psProfilePath, ($psNew -replace "`n", "`r`n"), $utf8Bom)
    Write-Host "[PowerShell] 已更新 `$PROFILE 的環境設定區塊。" -ForegroundColor Green
} else {
    Write-Host "[PowerShell] 環境設定區塊已是最新，跳過。" -ForegroundColor Yellow
}
