# media-downloader 計劃（yt-dlp-downloader.cs + file-dl.cs 部分內容合併）

> 狀態：已完成。

## 進度

- [x] Part 1：更名（namespace / ini / 標題 / setup.ps1 / git mv → media-downloader.cs）
- [x] Part 2：圖示改 file-dl 款（白箭頭 + 托盤，紅底）
- [x] Part 3：批量輸入改外部編輯器（urls 檔、移除舊批量邏輯）
- [x] Part 4：設定分頁（偵測列勾選框、aria2c 參數 / 批量輸入編輯器文字框）
- [x] Part 5：下載分流（aria2c 直接下載）、格式列表禁用、ini 模板與「檔案下載」option
- [x] 調整 1：偵測列順序 aria2c → yt-dlp → ffmpeg；版本文字依最長名稱＋固定間距對齊（NameWidth 共用欄寬）
- [x] 調整 2：aria2c 參數框移到 aria2c 偵測列後方（佔滿剩餘寬度），去掉「已加速 yt-dlp」括號；有偵測到 yt-dlp 才可編輯，其他情況唯讀
- [x] 調整 3：批量輸入編輯器移到按鈕列下方、勾選列表上方，文字與框同行；設定分頁間距統一為 S(12)
- [x] 調整 4：ini 模板移除所有註解行（含 SaveCore 的「; 參數選項」標題）
- [x] 調整 5：「檔案下載」option 加上 -x4 -s4 --max-tries=2 --connect-timeout=5 --timeout=5
- [x] 調整 6：aria2c 模式改吃參數列（含參數管理套用的內容）+ aria2c 參數框兩者；「檔案下載」範例
  的後段（-x4 -s4 …）是 aria2c 原生參數，供只勾 aria2c 時直傳，工具報錯與否由工具自己負責；
  aria2c 參數框改「勾了 yt-dlp 才可編輯」；批量輸入編輯器移到「重新偵測」右邊；
  aria2c 參數框與版本的間距 = 名稱到版本的間距；設定分頁列間距加大（S(20)）

## 完成備註

- 冒煙測試：`csc-build.cmd` 編譯 OK（含 --make-ico 圖示產生）；啟動 exe 自動產生 media-downloader.ini，模板含新 key（aria2Enabled=0 / ytdlpEnabled=1 / editor）與「檔案下載」option。
- 預設 `aria2Args` 是 yt-dlp 用的寫法（--downloader aria2c …）；只用 aria2c 直接下載時需自行改成原生參數（模板註解已說明）。
- 舊 `yt-dlp-downloader.ini` 不自動遷移（計劃第 8 節）。

## 1. 檔案與更名

- `yt-dlp-downloader.cs` 修改完成後 `git mv yt-dlp-downloader.cs media-downloader.cs`。
- 全域改名：
  - namespace `YtDlpDownloader` → `MediaDownloader`
  - `Config.FileName`：`yt-dlp-downloader.ini` → `media-downloader.ini`
  - 視窗標題與所有 MessageBox 標題：「yt-dlp 下載器」→「媒體下載器」
  - 頂部檔头註解、`--make-ico` 註解同步改為 media-downloader
- `setup.ps1` 打包清單同步：
  `Cs = 'media-downloader.cs'`、`Exes = @('media-downloader.exe')`、`Dest = ...\media-downloader`

## 2. 圖示（AppIcon）

- 改用 file-dl.cs 的圖形：白色下載箭頭 + 托盤條。
- 背景由藍漸層改**紅色**（沿 yt-dlp 圖示的紅 `230,18,18` 系）。
- YouTube 紅框 + 播放三角的舊圖不用。

## 3. 主畫面

- 標籤「影片」→「網址」。
- **刪除**現有批量邏輯：`PromptDialog` 多行批量框（批量輸入用途）、`_batch` 清單、
  ReadOnly「已設定為批量模式…」顯示與點擊編輯、`OpenBatch()`。
- 「批量輸入」按鈕改為 file-dl 式：
  - 用設定頁「批量輸入編輯器」（預設 `notepad`）開啟 exe 旁的 `media-downloader-urls.txt`
    （不存在自動建立）。
  - 編輯器關閉後（`OnActivated` / `proc.Exited`）重新解析檔案：
    - 空 → 自動刪除
    - 非空 → 保留，作為下載網址來源
  - urls 檔非空時：網址框唯讀顯示「已從 urls 檔載入 N 個網址」（無「點此編輯」、無點擊功能）。
- 網址來源優先序：urls 檔非空 → 以檔案為準（yt-dlp 與 aria2c 模式都用它）；
  檔案空/不存在 → 用網址框內容。「批量輸入」按鈕恆可點。
- 不搬 file-dl 的 `Picker`（沒有「選擇下載位置」按鈕）。
- 下載路徑、開啟資料夾、參數管理、ffmpeg 缺失提醒、aria2c PATH 注入等現有功能保留。

## 4. 設定分頁

### 4.1 偵測列勾選框

| 列 | 左側記號 |
|---|---|
| yt-dlp | 圓角勾選框；有偵測到 = 可點，沒有 = 灰色不可點 |
| ffmpeg | 維持現狀唯讀 ✔/✘ |
| aria2c | 圓角勾選框；有偵測到 = 可點，沒有 = 灰色不可點；勾選且 yt-dlp 也勾選時文字顯示 `aria2c（已加速 yt-dlp）` |

- 兩顆勾選框獨立勾選（非互斥），勾選狀態 = 實際呼叫：

| yt-dlp | aria2c | 「下載」實際行為 |
|---|---|---|
| ☑ | ☐ | 純 yt-dlp（參數列原樣） |
| ☑ | ☑ | yt-dlp + 把 `aria2Args` 注入參數列（沿用現有 `OnAria2Toggled` 的加入/移除機制，參數列所見 = 實際呼叫） |
| ☐ | ☑ | aria2c 直接下載 urls 檔：`aria2c <aria2Args> <參數列> -d <位置> -i <urls檔> & echo. & pause`（參數列含參數管理套用的內容；與 aria2Args 重複時不重複加） |
| ☐ | ☐ | 「下載」禁用 |

- 預設值（偵測完成後、ini 無紀錄時）：兩者皆裝 → 只勾 yt-dlp；只有 aria2c → 勾 aria2c；
  只有 yt-dlp → 勾 yt-dlp；都沒有 → 兩顆灰、下載禁用。
- 預設值只在「該工具從未勾選過」（ini 無對應 key）時套用，之後以 ini 為準。

### 4.2 其他設定分頁內容

- 「aria2c 參數」文字框（可編輯、存 ini `aria2Args`）：yt-dlp 模式 = 注入參數列的內容；
  aria2c 模式 = 附加在 aria2c 指令後。
- 「批量輸入編輯器」文字框（預設 `notepad`、存 ini `editor`）。
- 「重新偵測」「開啟設定檔位置」按鈕保留。
- 舊的「aria2c 多線程加速」勾選框（`_chkAria2`）與其說明文字移除，由偵測列勾選框取代。

## 5. 各 UI 元素在 ☐yt ☑aria 模式下的狀態

- 格式列表分頁：禁用，提示文字改「需要 yt-dlp」（原「點擊此處列出可用格式」位置）。
- 參數列：維持可編輯、不變灰；aria2c 模式會把參數列（含參數管理套用的內容，如「檔案下載」的
  -x4 -s4 …）與「aria2c 參數」都加在 aria2c 指令後；內容不合用時的報錯由 aria2c 自己負責。
- 批量輸入：照常作用（urls 檔正是 aria2c 模式的輸入）。

## 6. ini（media-downloader.ini，第一次執行自動產生）

key 一覽：

```
uiFont / uiFontSize          介面字型（沿用）
ytdlp / ffmpeg / aria2c      工具路徑（沿用）
outputDir                    下載資料夾（沿用）
defaultArgs                  啟動參數（沿用）
aria2Args                    aria2c 參數（可自訂；yt-dlp 模式=注入參數列、aria2c 模式=指令參數）
editor                       批量輸入編輯器（預設 notepad）
ytdlpEnabled = 1/0           yt-dlp 勾選狀態（新）
aria2Enabled = 1/0           aria2c 勾選狀態（沿用 key，語意改為「勾選 aria2c」）
option = 顯示名稱 | 參數字串  參數管理（沿用）
```

- option 清單新增：`option = 檔案下載 | -o "%(title)s.%(ext)s"`
- 移除：舊 `aria2Enabled` 的「加速開關」語意說明；模板註解全部配合改寫。
- 舊 `yt-dlp-downloader.ini` 不自動遷移。

## 7. 實作要點（對應現有程式碼）

1. **StatusRow 改造**：加 `Checkable` / `Checked` / `Enabled` 狀態與點擊命中判定；
   左側記號依狀態畫成勾選框（沿用 `RoundCheck` 的繪製風格）或唯讀 ✔/✘/…。
   yt-dlp、aria2c 列 `Checkable = true`，ffmpeg 列維持唯讀。
2. **勾選連動**：
   - yt-dlp 勾選狀態只影響「下載」走哪條路與 aria2c 勾選框文字（「已加速 yt-dlp」）。
   - aria2c 勾選：yt-dlp 也勾時沿用 `OnAria2Toggled` 把 `aria2Args` 加入/移出參數列；
     參數列手改時同步更新勾選狀態（沿用 `_chkGuard` 機制）。
   - aria2c 勾選框文字隨 yt-dlp 勾選狀態即時更新。
3. **編輯器流程**（自 file-dl.cs 移植改寫）：
   `EnsureUrlsFile()`、`ParseUrls()`、`DropUrlsIfEmpty()`、`OpenEditor()`（含 `proc.Exited`）、
   `OnActivated` 清理；檔名改 `media-downloader-urls.txt`；加入「網址框唯讀摘要」顯示。
4. **OnDownload 分流**：yt-dlp 勾 → 現有流程（網址來源含 urls 檔）；僅 aria2c 務 →
   file-dl 式 `cmd /c chcp 65001 >nul & aria2c <aria2Args> -d <dir> -i <urls檔> & echo. & pause`，
   下載位置需存在（不自動建立，與 file-dl 同）。
5. **PromptDialog** 保留（「儲存參數」命名仍在用），僅移除批量用途。
6. **圖示**：AppIcon 改繪 file-dl 款（白箭頭+托盤、紅底）。
7. 編譯驗證：`csc-build.cmd /nopause media-downloader.cs`（C# 5、.NET Framework 4.x 語法限制）。

## 8. 明確不做

- 不動 `file-dl.cs`。
- 不搬 `Picker` / 「選擇下載位置」。
- 不自動遷移舊 ini。
- 不做互斥勾選邏輯。
