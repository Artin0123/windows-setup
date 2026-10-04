# move-pi-sessions.cs：session 目錄名解碼、搬移與 GUI 流程修正

這次修改把 session 資料夾名稱的解碼改成用檔案系統回溯驗證，讓「拖入 `--C--Users-artin-Sync-coding-zenasr--` 當目標」還原成 `C:\Users\artin\Sync\coding\zenasr`，不再產生巢狀目錄名。同時依報告處理 B-1..B-10、R-1..R-12、P-1..P-6 與 GUI 第 6 節。`MoveOne` 改成只重寫 header 那一行，其餘位元組串流複製；`ClassifyDrop` 改看 `sessionsRoot` 與 jsonl header；目標改存 `_targetDir`。我獨立重新編譯並重跑 harness，67 項全過，沒有新回歸。

Watch for：GUI 流程（`_targetDir` 選用、確認對話框警告行、自動清空來源、根目錄警告、`BeginInvoke`、`_working` 檢查）只有讀碼與編譯驗證，我和 coder 都沒有實際操作過（confirmed，見下方）。

**Verdict**: APPROVED

## High-level view

解碼不再把 `-` 一律當 `\`，而是由左到右對檔案系統回溯，取第一個真的存在的路徑；走不通才退回推測並標記 `inferred`，確認對話框會多一行警告。原始巢狀目錄名 bug 在空資料夾、有 jsonl、結尾斜線三種情況都還原正確，且 `FindTargetDir` 回到同一個資料夾。

`ClassifyDrop` 不再只看名稱，必須父資料夾是 sessions 根目錄或裡面有 `type:session` 的 jsonl；拖入的 session 資料夾被記為 `_targetDir`，不會因 jsonl cwd 與目錄名不一致而進到別的資料夾。

`MoveOne` 只改寫 header 行，BOM 與非 UTF-8 位元組保留，dst 已存在一律拒絕，來源以 `FileShare.Read` 開啟所以 pi 正在寫的檔仍會被拒絕。`ReadHeader`、`MoveOne`、`RewriteHeaderCwd` 共用同一組掃描上限，B1/B2 不再出現「寫入成功但驗證失敗」。

<details>
<summary>Issues (3)</summary>

1. **GUI 流程未實測（likely 無問題，未驗證）** — `TryConvert` 選用 `_targetDir`、警告行、自動清空、`BeginInvoke` 等只靠讀碼；建議合併前手動拖放跑一次。非 blocking。
2. **`HasSessionHeader` 對任意拖入資料夾都會讀 jsonl 檔頭（possible）** — 拖入含大量 jsonl 的非 session 資料夾時會依序掃檔頭直到命中；一般專案資料夾無 jsonl，成本可忽略，有需要再限制掃描檔數。
3. **UNC session 資料夾無 jsonl 時解碼回 null（confirmed，C11e）** — 行為與報告「合理保守」一致，GUI 會提示無法解析，不需修改。

</details>

<details>
<summary>Details</summary>

## 獨立驗證結果

編譯：`csc.exe`（Framework64 v4.0.30319，C# 5）以 `/target:winexe` 編譯 `move-pi-sessions.cs`，0 錯誤。grep 檔內沒有 `$"..."`、`?.`、`nameof`、`out var`、expression-bodied 成員（只有註解提到這些字樣、Regex 字串含 `=.*`）。另以 `/main:MovePiSessions.Harness` 與我自寫的丟棄式 harness 一起編譯，呼叫 internal `PiPaths`，結果 `pass=67 fail=0`。

涵蓋案例（全部通過）：原始 bug（空資料夾、結尾斜線、有 jsonl、`FindTargetDir` 不產生 `sessions---` 巢狀名）、C1、C2、C3-nonexistent（退路，inferred=true）、C3-with-my-app（以 scratch 內真實 `x\my-app` 驗證，inferred=false）、C3-ambiguous（同時有 `x\my`）、C3b、C4、C5、C6、C7、C8、C9、C10、C10b、B3 兩案、C11b/c/d、C12、C12b、C13、C14c/d（有無檔案系統）/e/f、S1、S3、S4、F2、F3、F4、R1..R11、B1（header 在 1MB 之後）、B2（600KB 單行 header）、M1（BOM+CRLF 位元組保留）、M2/M2b/M2c、M3、M4、M5、M5b、M6、M7、M8、M9b、M10、無結尾換行、header 無 cwd、不留 `.tmp` 檔、K1/K2。

C3-with-my-app 的 scratch 路徑本身含 `windows-setup`、`mps-fix-tmp` 等帶 `-` 的目錄，等於對一條很長的真實路徑做了回溯驗證，仍解出正確結果。

## 回溯解碼與 inferred 旗標

`ResolveByFs` 每一步都要求目錄存在，空片段略過，所以 `--C--x-my--app--` 不再產生 `my\\app`（C14d 兩種情況皆通過：無檔案系統時得 `C:\zzq9\my\app`，有 `my--app` 真實目錄時得該目錄且 inferred=false）。`DecodeSessionDirName` 的退路只在 `bestIdx==0` 且磁碟根目錄存在時才把 inferred 設 false，`--D----` 得 `D:\`（C11d）。

## _targetDir 與分類

C12b 在 PiPaths 層確認：jsonl cwd 與目錄名不一致時，`ClassifyDrop` 保留拖入的 sessionDir，而重新編碼會指向另一個資料夾，所以 `TryConvert` 選用 `_targetDir` 的分支是必要的。該分支由讀碼確認（約 1819 行）：`_targetDir` 存在且資料夾還在才用，否則退回 `FindTargetDir`；`ClearTarget` 與非 session 分支都會把 `_targetDir` 設 null。

## 未覆蓋範圍

GUI 相關行為只有編譯與讀碼，我沒有啟動視窗操作。`_working` 檢查（1514、1635、1760、1772 行）、`OnDragDrop` 的 `BeginInvoke`（1734 行）、`_sourceInferred` 警告（1861 行）都在程式中可見，coder 筆記也誠實標明未實測，因此不算缺漏證據，但不是行為驗證。

## 殘留檢查

審查結束後已刪除整個 `mps-fix-tmp`。`git status` 只有 `move-pi-sessions.cs` 修改與原本未追蹤的 `.agents/`；repo 內搜尋不到 `chk.exe`、`h.exe`、`h.cs`。

</details>

<details>
<summary>File map</summary>

- `move-pi-sessions.cs` — 唯一被修改的檔案（解碼回溯、ClassifyDrop、MoveOne 串流重寫、ReadHeader/掃描器、PathKey/SameKey、AtomicWrite、WrapPath、GUI 狀態與對話框）。完整差異：`git diff -- move-pi-sessions.cs`。

</details>
