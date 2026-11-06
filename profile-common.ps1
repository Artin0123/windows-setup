# profile-common.ps1 - shell-env.ps1 與 venv.ps1 共用的定義（以 dot-source 載入，本身不做任何事）
#
# ※※※ 本檔必須以 UTF-8 with BOM 儲存（PS 5.1 對無 BOM 的 .ps1 以 ANSI 解析，中文會亂碼）※※※

# 兩個引擎的 CurrentUserCurrentHost profile：pwsh 正典 + PS5（內容相同，版本差異用 profile 內部 gate 處理）
$docDir = [Environment]::GetFolderPath('MyDocuments')
$PsProfileTargets = @(
    (Join-Path $docDir 'PowerShell\Microsoft.PowerShell_profile.ps1'),
    (Join-Path $docDir 'WindowsPowerShell\Microsoft.PowerShell_profile.ps1')
)

# 無頭判定：兩個 profile 區塊（SHELL_ENV 在檔頭、NATIVE_VENV 在檔尾）各自內嵌一份，
# 因為兩者要能獨立部署；唯一來源在這裡，改判定只改這一處。
# 用法：在區塊字串裡放一行 __HEADLESS_SNIPPET__，再 .Replace('__HEADLESS_SNIPPET__', $HeadlessSnippet)。
# 產出變數 $__isHeadless。
$HeadlessSnippet = (@'
# === 無頭判定 ===
# 輸出被重導，或以 -NonInteractive / -EncodedCommand / -File / -Command 啟動者視為無頭（不畫 prompt、不註冊熱鍵）。
# 例外：帶 -NoExit 者一定是互動——VSCode/Cursor 終端就是以「-NoExit -Command 注入 shell integration」啟動，
# 以此判斷，不必依賴 TERM_PROGRAM 這類宿主專屬的環境變數。
$__cl = [Environment]::GetCommandLineArgs()
$__isHeadless = [Console]::IsOutputRedirected -or [bool]($__cl -match '^-(NonI|NonInteractive)$') -or
    (-not [bool]($__cl -match '^-noe') -and [bool]($__cl -match '^-(c|command|e|ec|encodedcommand|f|file)$'))
'@ -replace "`r`n", "`n").Trim("`n")
