# ==============================================================================
# PowerShell 雙 Shell 自動化安裝腳本
# 一次性設定：PowerShell ($PROFILE) + Bash (~/.bashrc) + BASH_ENV 環境變數
#
# ※※※ 本檔必須以 UTF-8 with BOM 儲存 ※※※
# PS 5.1 對無 BOM 的 .ps1 以 ANSI 解析，中文註解亂碼會導致解析直接失敗。
# ==============================================================================

# CRLF/CR -> LF（bash 設定檔用）；對空字串安全
function ConvertTo-UnixLineEnding {
    param([string]$Text)
    return ($Text -replace "`r`n", "`n" -replace "`r", "`n")
}

function Update-MarkedBlock {
    param(
        [string]$Content,
        [string]$StartMarker,
        [string]$EndMarker,
        [string]$NewBlock
    )
    # 空檔 -> 直接回傳新區塊
    if ([string]::IsNullOrEmpty($Content)) {
        return $NewBlock.TrimStart("`r", "`n")
    }
    $pattern = '(?s)' + [regex]::Escape($StartMarker) + '.*?' + [regex]::Escape($EndMarker)
    $foundBlocks = [regex]::Matches($Content, $pattern)
    if ($foundBlocks.Count -gt 0) {
        # 不能用 [regex]::Replace：取代字串中的 $_ / $1 / $& 等會被 .NET 展開，
        # 區塊裡的 PowerShell 程式碼（如 Where-Object { $_ ... }）會把整份檔案再塞進去一次。
        # 改用 Substring 手動拼接；若已有多個重複區塊，只保留第一個位置並移除其餘。
        $block = $NewBlock.TrimEnd("`r", "`n")
        $sb = [System.Text.StringBuilder]::new()
        $pos = 0
        $first = $true
        foreach ($m in $foundBlocks) {
            [void]$sb.Append($Content.Substring($pos, $m.Index - $pos))
            if ($first) { [void]$sb.Append($block); $first = $false }
            $pos = $m.Index + $m.Length
        }
        [void]$sb.Append($Content.Substring($pos))
        return $sb.ToString()
    }
    # 有 START 卻配不到完整區塊：多半是 END 被手動編輯弄壞（截斷或刪除）。
    # 本腳本的區塊一律寫在檔尾，故捨棄第一個 START 起到檔尾的殘缺內容再重寫，
    # 避免殘缺程式碼與新區塊重複執行。
    $idx = $Content.IndexOf($StartMarker)
    if ($idx -ge 0) {
        Write-Warning "偵測到殘缺的標記區塊（缺少結束標記），已移除並重寫。"
        $healthy = $Content.Substring(0, $idx).TrimEnd("`r", "`n")
        if ([string]::IsNullOrEmpty($healthy)) {
            return $NewBlock.TrimStart("`r", "`n")
        }
        return $healthy + "`r`n`r`n" + $NewBlock.TrimStart("`r", "`n")
    }
    return $Content.TrimEnd("`r", "`n") + "`r`n`r`n" + $NewBlock.TrimStart("`r", "`n")
}

# ------------------------------------------------------------------------------
# 1. 寫入 PowerShell 設定檔 ($PROFILE)
# ------------------------------------------------------------------------------
$psProfilePath = $PROFILE
$psMarker = "# === NATIVE_VENV_AUTO_ACTIVATE_START ==="
$psEndMarker = "# === NATIVE_VENV_AUTO_ACTIVATE_END ==="

if (-not (Test-Path (Split-Path $psProfilePath))) { New-Item -ItemType Directory -Force -Path (Split-Path $psProfilePath) | Out-Null }
if (-not (Test-Path $psProfilePath)) { New-Item -ItemType File -Force -Path $psProfilePath | Out-Null }

# 一律明確以 UTF-8 讀寫：Windows PowerShell 5.1 的 Get-Content 會把無 BOM 的 UTF-8 當 ANSI 讀，中文會變亂碼
$psContent = [System.IO.File]::ReadAllText($psProfilePath, [System.Text.Encoding]::UTF8)
$psExisted = $psContent -and $psContent -match [regex]::Escape($psMarker)

$psCode = @"
$psMarker
# 1. 歷史紀錄最佳化 (忽略重複與空白)；無 PSReadLine 的環境直接略過
if (Get-Module -ListAvailable PSReadLine) {
    Set-PSReadLineOption -HistoryNoDuplicates
    Set-PSReadLineOption -AddToHistoryHandler {
        param([string]`$line)
        if ([string]::IsNullOrWhiteSpace(`$line)) { return `$false }
        if (`$line.StartsWith(" ")) { return `$false }
        return `$true
    }
}

# 2. 原生極速虛擬環境切換引擎
# 回傳 `$true 表示目前 PATH 確實指向指定 venv（避免只有 VIRTUAL_ENV、PATH 未切的半啟動）。
# 刻意只認 PATH 第一段：排在更前面的 python 才會真正接管命令列。
# 已知取捨：其他工具 hook 暫時把別的目錄插到最前時，下一個 prompt 會「拔舊掛新」
# 把 venv bin 推回最前（接受此行為以維持 python 解析正確）。
function global:_Venv-Is-Live {
    param([string]`$venvPath, [string]`$binDir)
    if ([string]::IsNullOrEmpty(`$venvPath)) { return `$false }
    if (`$env:VIRTUAL_ENV -ne `$venvPath) { return `$false }
    `$newBin = Join-Path `$venvPath `$binDir
    `$pathSep = [System.IO.Path]::PathSeparator
    `$first = (`$env:PATH -split [regex]::Escape(`$pathSep) | Select-Object -First 1)
    if (`$first -eq `$newBin) { return `$true }
    try {
        return ([IO.Path]::GetFullPath(`$first) -eq [IO.Path]::GetFullPath(`$newBin))
    } catch {
        return `$false
    }
}

# 函式刻意掛 global；命名雖非核准動詞，但以 Auto- / _Venv- 前綴避開與使用者自訂函式衝突
function global:Auto-Activate-Venv {
    `$current = `$PWD.Path
    `$venvPath = `$null

    `$isWin = [Environment]::OSVersion.Platform -eq 'Win32NT'
    `$binDir = if (`$isWin) { "Scripts" } else { "bin" }
    `$exeName = if (`$isWin) { "python.exe" } else { "python" }
    `$pathSep = [System.IO.Path]::PathSeparator

    # 快速路徑：同一 venv 仍為真啟用狀態，且 cwd 未離開該 venv 子樹 -> 直接返回，零磁碟存取
    # （向上走訪能找到的 venv 必為 cwd 的祖先；cwd 在 `$env:VIRTUAL_ENV 之下時結果必相同）
    if (_Venv-Is-Live -venvPath `$env:VIRTUAL_ENV -binDir `$binDir) {
        if (`$PWD.Path -eq `$env:VIRTUAL_ENV -or
            `$PWD.Path.StartsWith("`$env:VIRTUAL_ENV`$pathSep", [StringComparison]::OrdinalIgnoreCase)) { return }
    }

    # 向上遞迴尋找虛擬環境
    while (![string]::IsNullOrEmpty(`$current)) {
        # 正斜線在 Windows 與 Linux 皆可用；寫死反斜線會讓非 Windows 分支失效
        if (Test-Path (Join-Path `$current ".venv/`$binDir/`$exeName")) {
            `$venvPath = Join-Path `$current ".venv"; break
        }
        if (Test-Path (Join-Path `$current "venv/`$binDir/`$exeName")) {
            `$venvPath = Join-Path `$current "venv"; break
        }
        `$parent = Split-Path `$current -Parent
        if (`$parent -eq `$current) { break }
        `$current = `$parent
    }

    # 已真正啟用同一環境 -> 跳過
    if (_Venv-Is-Live -venvPath `$venvPath -binDir `$binDir) { return }

    # 退出舊環境：從 PATH 中拔除舊的 bin/Scripts 路徑
    if (`$env:VIRTUAL_ENV) {
        `$oldBin = Join-Path `$env:VIRTUAL_ENV `$binDir
        # 大小寫與結尾反斜線差異的殘留也一併清掉
        `$paths = `$env:PATH -split [regex]::Escape(`$pathSep) |
            Where-Object { `$_.TrimEnd('\') -ine `$oldBin.TrimEnd('\') }
        `$env:PATH = `$paths -join `$pathSep
        `$env:VIRTUAL_ENV = `$null
    }

    # 進入新環境：把新的 bin/Scripts 路徑塞入 PATH 最前方
    if (`$venvPath) {
        `$env:VIRTUAL_ENV = `$venvPath
        `$newBin = Join-Path `$venvPath `$binDir
        `$env:PATH = "`$newBin`$pathSep`$env:PATH"
    }
}

# 3. 畫面渲染攔截 (內建 Oh-My-Posh 快取；每次 prompt 都檢查半啟動)
# 注意：本區塊請保持在 `$PROFILE 尾端——若其後才執行 oh-my-posh init，
# prompt 會被重設，本攔截即失效
`$global:_venv_has_omp = [bool](Get-Command oh-my-posh -ErrorAction SilentlyContinue)

if (-not `$global:_venv_original_prompt) {
    if (Test-Path "Function:\prompt") { `$global:_venv_original_prompt = `$function:prompt }
    else { `$global:_venv_original_prompt = { "PS `$(`$executionContext.SessionState.Path.CurrentLocation)> " } }
}

function global:prompt {
    Auto-Activate-Venv

    if (-not `$global:_venv_has_omp) {
        if (`$env:VIRTUAL_ENV) {
            `$venvName = Split-Path `$env:VIRTUAL_ENV -Leaf
            Write-Host -NoNewline -ForegroundColor Green "(`$venvName) "
        }
    }

    & `$global:_venv_original_prompt
}

# Profile 載入時直接執行一次（非互動式 / Agent 終端不會畫 prompt）
Auto-Activate-Venv
$psEndMarker
"@

$psUpdated = Update-MarkedBlock -Content $psContent -StartMarker $psMarker -EndMarker $psEndMarker -NewBlock $psCode
[System.IO.File]::WriteAllText($psProfilePath, $psUpdated.TrimEnd("`r", "`n") + "`r`n", [System.Text.UTF8Encoding]::new($true))
if ($psExisted) {
    Write-Host "[PowerShell] 已更新 `$PROFILE 內的 venv 自動啟動區塊。" -ForegroundColor Green
} else {
    Write-Host "[PowerShell] 已成功寫入 `$PROFILE！" -ForegroundColor Green
}

# ------------------------------------------------------------------------------
# 2. 寫入 Bash 設定檔 (~/.bashrc)
# ------------------------------------------------------------------------------
$bashrcPath = Join-Path $HOME ".bashrc"
$bashMarker = "# === BASH_VENV_AUTO_ACTIVATE_START ==="
$bashEndMarker = "# === BASH_VENV_AUTO_ACTIVATE_END ==="

if (-not (Test-Path $bashrcPath)) { New-Item -ItemType File -Force -Path $bashrcPath | Out-Null }

$bashContent = [System.IO.File]::ReadAllText($bashrcPath, [System.Text.Encoding]::UTF8)
$bashExisted = $bashContent -and $bashContent -match [regex]::Escape($bashMarker)

$bashCode = @'
# === BASH_VENV_AUTO_ACTIVATE_START ===
# 自動偵測並啟動/退出虛擬環境
# 回傳 0 表示目前 python 確實來自指定 venv（避免只有 VIRTUAL_ENV、PATH 未切的半啟動）
_venv_is_live() {
    local venv="$1"
    local py
    [ -n "$venv" ] || return 1
    [ "$VIRTUAL_ENV" = "$venv" ] || return 1
    py="$(command -v python 2>/dev/null || command -v python3 2>/dev/null)" || return 1
    case "$py" in
        "$venv"/*) return 0 ;;
        *) return 1 ;;
    esac
}
auto_activate_venv() {
    local target_dir="$PWD"
    local venv_path=""
    local activate_script=""
    # 向上遞迴尋找虛擬環境 (.venv 或 venv)
    # 防護：POSIX 走訪會終止於 "/"，但 Windows 磁碟根（C:/ → C: → .）dirname 不再縮短，
    # 缺防護會變成無限迴圈（PWD 為 Windows 形式時觸發，bash 啟動即卡死）
    while [ -n "$target_dir" ] && [ "$target_dir" != "/" ] && [ "$target_dir" != "." ]; do
        if [ -f "$target_dir/.venv/Scripts/activate" ]; then
            venv_path="$target_dir/.venv"
            activate_script="$target_dir/.venv/Scripts/activate"
            break
        elif [ -f "$target_dir/.venv/bin/activate" ]; then
            venv_path="$target_dir/.venv"
            activate_script="$target_dir/.venv/bin/activate"
            break
        elif [ -f "$target_dir/venv/Scripts/activate" ]; then
            venv_path="$target_dir/venv"
            activate_script="$target_dir/venv/Scripts/activate"
            break
        elif [ -f "$target_dir/venv/bin/activate" ]; then
            venv_path="$target_dir/venv"
            activate_script="$target_dir/venv/bin/activate"
            break
        fi
        local prev="$target_dir"
        target_dir="$(dirname "$target_dir")"
        # dirname 不再縮短（磁碟根/相對路徑）即到底，防止無限迴圈
        [ "$target_dir" = "$prev" ] && break
    done
    # 情況 A：離開專案或切換到其他環境 -> 自動 deactivate
    if [ -n "$VIRTUAL_ENV" ] && [ "$VIRTUAL_ENV" != "$venv_path" ]; then
        if declare -f deactivate >/dev/null; then
            deactivate
        else
            unset VIRTUAL_ENV
        fi
    fi
    # 情況 B：發現 venv，且尚未真正啟用（含 VIRTUAL_ENV 有設但 PATH 未切）-> source activate
    if [ -n "$venv_path" ] && ! _venv_is_live "$venv_path"; then
        # 半啟動時 deactivate 可能不存在，先清掉以免 activate 誤判
        if [ -n "$VIRTUAL_ENV" ] && ! declare -f deactivate >/dev/null; then
            unset VIRTUAL_ENV
        fi
        source "$activate_script"
    fi
}
# Prompt 鉤子與 History 即時同步（每次 prompt 都檢查，修復 PATH 被重置的半啟動）
_venv_prompt_command() {
    history -a
    history -n
    auto_activate_venv
}
# 註冊至 PROMPT_COMMAND（bash 5.1+ 的 PROMPT_COMMAND 可為陣列；此處僅偵測字串形式，
# 陣列環境最壞情況是重複註冊，行為仍正確、僅每個 prompt 多跑一次）
if [[ ";;$PROMPT_COMMAND;" != *";_venv_prompt_command;"* ]]; then
    PROMPT_COMMAND="_venv_prompt_command${PROMPT_COMMAND:+;$PROMPT_COMMAND}"
fi
# 非互動式 shell（如 AI agent 的 bash 工具）不會跑 PROMPT_COMMAND，
# 且可能透過 BASH_ENV 直接載入本檔，故在載入時主動觸發一次 venv 自動啟動
auto_activate_venv
# === BASH_VENV_AUTO_ACTIVATE_END ===
'@

# 將換行轉為 Unix (LF)，避免 Git Bash 報 `\r` 錯誤
$bashCodeUnix = ConvertTo-UnixLineEnding $bashCode
# bashrc 以 LF 為主；讀取後若有 CRLF，先正規化再替換
$bashContentNorm = ConvertTo-UnixLineEnding $bashContent
$bashUpdated = Update-MarkedBlock -Content $bashContentNorm -StartMarker $bashMarker -EndMarker $bashEndMarker -NewBlock $bashCodeUnix.TrimStart("`n")
$bashUpdatedUnix = ConvertTo-UnixLineEnding $bashUpdated
[System.IO.File]::WriteAllText($bashrcPath, $bashUpdatedUnix, [System.Text.UTF8Encoding]::new($false))
if ($bashExisted) {
    Write-Host "[Bash] 已更新 $bashrcPath 內的 venv 自動啟動區塊。" -ForegroundColor Green
} else {
    Write-Host "[Bash] 已成功寫入 $bashrcPath ！” -ForegroundColor Green
}

# ------------------------------------------------------------------------------
# 2.5 確保 login shell 讀得到 ~/.bashrc
#     bash 的 login shell 只讀 ~/.bash_profile / ~/.bash_login / ~/.profile 三者的第一個，
#     不會直接讀 ~/.bashrc（Git Bash 僅在三者皆不存在時才 fallback 到 .bashrc）。
#     若存在其中之一且未 source .bashrc，自動啟用會在登入 shell 中靜默失效。
# ------------------------------------------------------------------------------
$loginProfilePath = $null
foreach ($name in '.bash_profile', '.bash_login', '.profile') {
    $candidate = Join-Path $HOME $name
    if (Test-Path $candidate) { $loginProfilePath = $candidate; break }
}
if ($loginProfilePath) {
    # 啟發式：內容提及 bashrc 即視為已處理，避免落入過於複雜的二次 source 判斷
    $loginContent = ConvertTo-UnixLineEnding ([System.IO.File]::ReadAllText($loginProfilePath, [System.Text.Encoding]::UTF8))
    $loginMarker = "# === BASH_LOGIN_SOURCE_BASHRC_START ==="
    $loginEndMarker = "# === BASH_LOGIN_SOURCE_BASHRC_END ==="
    if ($loginContent -notmatch 'bashrc') {
        $loginCode = @'
# === BASH_LOGIN_SOURCE_BASHRC_START ===
# 由 venv.ps1 加入：讓 login shell（Git Bash 預設）載入 ~/.bashrc
if [ -f "$HOME/.bashrc" ]; then . "$HOME/.bashrc"; fi
# === BASH_LOGIN_SOURCE_BASHRC_END ===
'@
        $loginUpdated = Update-MarkedBlock -Content $loginContent -StartMarker $loginMarker -EndMarker $loginEndMarker -NewBlock $loginCode
        [System.IO.File]::WriteAllText($loginProfilePath, (ConvertTo-UnixLineEnding $loginUpdated), [System.Text.UTF8Encoding]::new($false))
        Write-Host "[Bash] 已附加 source ~/.bashrc 至 $loginProfilePath。" -ForegroundColor Green
    } else {
        Write-Host "[Bash] $loginProfilePath 已載入 ~/.bashrc，跳過。" -ForegroundColor Yellow
    }
}

# ------------------------------------------------------------------------------
# 3. 設定 BASH_ENV 使用者環境變數
#    非互動式 bash（如 AI agent 的 bash 工具）不讀 ~/.bashrc，
#    只有在環境變數 BASH_ENV 指向本檔時才會載入
# ------------------------------------------------------------------------------
$currentBashEnv = [Environment]::GetEnvironmentVariable('BASH_ENV', 'User')

if ($currentBashEnv -ne $bashrcPath) {
    if (-not [string]::IsNullOrEmpty($currentBashEnv)) {
        Write-Host "[環境變數] 注意：使用者原有的 BASH_ENV = $currentBashEnv 已被覆蓋。" -ForegroundColor Yellow
    }
    [Environment]::SetEnvironmentVariable('BASH_ENV', $bashrcPath, 'User')
    Write-Host "[環境變數] 已設定 BASH_ENV = $bashrcPath（僅對之後新誕生的程序生效）" -ForegroundColor Green
    Write-Host "         所有非互動式 bash（腳本、hooks、cron）都會載入 .bashrc 並依 cwd 自動切換 venv。" -ForegroundColor Gray
} else {
    Write-Host "[環境變數] BASH_ENV 已正確指向 ~/.bashrc，跳過。" -ForegroundColor Yellow
}

# 清理舊版架構遺留的 ~/.bash_env 包裝檔
$legacyBashEnv = Join-Path $HOME ".bash_env"
if (Test-Path $legacyBashEnv) {
    Remove-Item $legacyBashEnv -Force -ErrorAction SilentlyContinue
    Write-Host "[清理] 已刪除舊版遺留檔 ~/.bash_env" -ForegroundColor Green
}

Write-Host "`n安裝完成！請完整重開 PowerShell 與 Git Bash（若有終端機宿主如 VSCodium 也需整個重啟）即刻生效。" -ForegroundColor Cyan
