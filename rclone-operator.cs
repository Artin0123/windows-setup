// ==========================================================================
// csc-variant: rclone-transfer MODE_TRANSFER
// csc-variant: rclone-delete MODE_DELETE
//  RcloneDrop — rclone-transfer.exe / rclone-delete.exe 共用原始碼
//
//  拖到 csc-build.cmd 上即可一次編出 rclone-transfer.exe 與 rclone-delete.exe
//  （依檔案最上方的 csc-variant 標記）；沒定義任何模式時預設為 MODE_TRANSFER。
//  另可用 build.ps1 編譯兩次：
//      /define:MODE_TRANSFER  ->  rclone-transfer.exe （上傳 / 下載 自動判斷）
//      /define:MODE_DELETE    ->  rclone-delete.exe   （從雲端刪除）
//
//  注意：Windows 內建 csc.exe 是 C# 5 編譯器，
//        因此這裡不使用 $"..."、?.、nameof 等 C# 6 以上語法。
// ==========================================================================
#if !MODE_TRANSFER && !MODE_DELETE
#define MODE_TRANSFER
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using System.Windows.Forms;

namespace RcloneDrop
{
    // ---------------------------------------------------------------- 進入點
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            // 編譯輔助：rclone-xxx.exe --make-ico out.ico（csc-build.cmd 用來產生 exe 圖示）
            if (args != null && args.Length == 2 && args[0] == "--make-ico")
            {
                try { AppIcon.WriteIco(args[1]); } catch { }
                return;
            }

            try { SetProcessDPIAware(); }
            catch { /* 舊系統沒有這個 API，忽略 */ }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 直接把檔案拖到 exe 圖示上時，路徑會由命令列傳進來
            List<string> files = new List<string>();
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (a == null) continue;
                    string t = a.Trim().Trim('"');
                    if (t.Length == 0) continue;
                    if (t.StartsWith("-") || t.StartsWith("/")) continue;
                    files.Add(t);
                }
            }

            using (MainForm form = new MainForm(files.ToArray()))
            {
                Application.Run(form);
            }
        }
    }

    // ------------------------------------------------------------- 輸出顏色
    internal enum OutKind
    {
        Normal,
        Head,
        Ok,
        Warn,
        Err,
        Cmd,
        Dim,
        Progress
    }

    // --------------------------------------------------------------- 設定檔
    internal sealed class Config
    {
        public const string FileName = "rclone-config.ini";

        public string FilePath = "";
        public string Remote = "";
        public string CloudDrives = "d";
        public string LocalDrives = "c";
        public string DownloadDir = @"%USERPROFILE%\Downloads\Rclone";
        public string UploadDir = "";
        public string RclonePath = "rclone";
        public string RcAddr = "";           // 空 = 自動偵測
        public bool AutoRefresh = true;
        public bool RefreshRecursive = false;
        public int Transfers = 20;
        public int Checkers = 20;
        public int TpsLimit = 20;

        public bool Sound = true;
        public string SoundSuccess = "";      // 空 = 預設
        public string SoundFail = "";
        public string UiFont = "Microsoft YaHei";
        public float UiFontSize = 11f;
        public string ConsoleFont = "Consolas";
        public float ConsoleFontSize = 11f;
        public string ConsoleFontCjk = "Microsoft YaHei";

        public bool CreatedNew = false;
        public string ParseError = null;

        // rclone-config.ini 範本：key = value；以 ; 或 # 開頭的整行是註解（行尾不支援註解，避免路徑被截斷）
        private const string Template = @"; Rclone Drop 設定檔
; 格式：key = value　　以 ; 或 # 開頭的整行是註解
; 路徑直接寫就好，不需要把反斜線重複

; 要使用的 rclone 遠端名稱，例如 Google Drive:
; 留空時第一次使用會跳出選單，選完會自動寫回這裡，之後不用再選
remote =

; 雲端槽位：rclone mount 掛載的磁碟代號，逗號可指定多個，大小寫不拘
; 例如 cloud = d,e
cloud = d

; 本地槽位：例如 local = c,d 代表 C 槽與 D 槽都是本機
local = c

; 下載目的地（支援 %USERPROFILE% 這類環境變數）
downloadDir = %USERPROFILE%\Downloads\Rclone

; 上傳目的地（遠端裡面的子資料夾；留空 = 遠端根目錄）
uploadDir =

; rclone 執行檔；維持 rclone 表示從 PATH 尋找
rclonePath = rclone

; rclone mount 的 RC 位址，處理完成後用它通知掛載點刷新
; 留空 = 自動從執行中的 rclone mount 命令列找出 --rc-addr（找不到就用 127.0.0.1:5572）
; 自動找不到或同時有多個換載時才需要手動填，格式為 主機:埠號，例如 rcAddr = 127.0.0.1:5572
; 注意：這個位址要和掛載時的 --rc-addr 一致（掛載時還要有 --rc）
rcAddr =

; 處理完成後是否自動刷新掛載點（true / false）
autoRefresh = true

; 刷新時是否連同所有子資料夾一起重讀（false = 只重讀有變動的那個資料夾，最快也最常用）
refreshRecursive = false

transfers = 20
checkers = 20
tpslimit = 20

; 完成時是否播放提示音（true / false）
sound = true

; 提示音可以填：
;   留空       = 預設（成功 Windows Ding、失敗 Windows Error）
;   系統音效名 = asterisk / beep / exclamation / hand / question
;   .wav 檔名  = C:\Windows\Media 底下的檔名或完整路徑，例如 chimes.wav、Windows Ding.wav、D:\sounds\ok.wav
;   none       = 這個情況不發聲
soundSuccess =
soundFail =

; 介面字型（提示文字、選單）與大小；字型必須是系統已安裝的字型，找不到會改用預設
uiFont = Microsoft YaHei
uiFontSize = 11

; 輸出區字型：純英數的行（指令、路徑、進度條）用 consoleFont，建議用等寬字型進度條才對得齊
; 只要有中文的整行改用 consoleFontCjk；兩者共用 consoleFontSize
consoleFont = Consolas
consoleFontCjk = Microsoft YaHei
consoleFontSize = 11
";

        public static Config Load(string exeDir)
        {
            Config c = new Config();
            try { c.FilePath = Path.Combine(exeDir, FileName); }
            catch { c.FilePath = FileName; }

            if (!File.Exists(c.FilePath))
            {
                try
                {
                    File.WriteAllText(c.FilePath, Template, new UTF8Encoding(true));
                    c.CreatedNew = true;
                }
                catch (Exception ex) { c.ParseError = "無法建立 " + FileName + "：" + ex.Message; }
                return c;
            }

            string raw;
            try { raw = File.ReadAllText(c.FilePath, Encoding.UTF8); }
            catch (Exception ex) { c.ParseError = ex.Message; return c; }

            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw.Substring(1);
            int lineNo = 0;
            foreach (string line0 in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                lineNo++;
                string line = line0.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    if (c.ParseError == null) c.ParseError = "第 " + lineNo + " 行不是 key = value 格式：" + line;
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                    val = val.Substring(1, val.Length - 2);
                d[key] = val;
            }

            c.Remote = GetStr(d, "remote", c.Remote);
            c.CloudDrives = GetStr(d, "cloud", c.CloudDrives);
            c.LocalDrives = GetStr(d, "local", c.LocalDrives);
            c.DownloadDir = GetStr(d, "downloadDir", c.DownloadDir);
            c.UploadDir = GetStr(d, "uploadDir", c.UploadDir);
            c.RclonePath = GetStr(d, "rclonePath", c.RclonePath);
            c.RcAddr = GetStr(d, "rcAddr", c.RcAddr);
            c.AutoRefresh = GetBool(d, "autoRefresh", c.AutoRefresh);
            c.RefreshRecursive = GetBool(d, "refreshRecursive", c.RefreshRecursive);
            c.Transfers = GetInt(d, "transfers", c.Transfers);
            c.Checkers = GetInt(d, "checkers", c.Checkers);
            c.TpsLimit = GetInt(d, "tpslimit", c.TpsLimit);
            c.Sound = GetBool(d, "sound", c.Sound);
            c.SoundSuccess = GetStr(d, "soundSuccess", c.SoundSuccess);
            c.SoundFail = GetStr(d, "soundFail", c.SoundFail);
            c.UiFont = GetStr(d, "uiFont", c.UiFont);
            c.UiFontSize = GetFloat(d, "uiFontSize", c.UiFontSize);
            c.ConsoleFont = GetStr(d, "consoleFont", c.ConsoleFont);
            c.ConsoleFontSize = GetFloat(d, "consoleFontSize", c.ConsoleFontSize);
            c.ConsoleFontCjk = GetStr(d, "consoleFontCjk", c.ConsoleFontCjk);
            return c;
        }

        // 把選擇的遠端寫回設定檔：只動 remote 那一行，保留使用者的註解與排版
        public void SaveRemote(string remote)
        {
            Remote = remote;
            try
            {
                string text = File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : Template;
                string newLine = "remote = " + remote;

                Regex re = new Regex(@"^[ \t]*remote[ \t]*=.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (re.IsMatch(text))
                    text = re.Replace(text, delegate(Match m) { return newLine; }, 1);
                else
                    text = newLine + "\r\n" + text;
                File.WriteAllText(FilePath, text, new UTF8Encoding(true));
            }
            catch { /* 寫不回不影響本次操作 */ }
        }

        // "c,d" / "C:" / "d:\" 都可以，回傳大寫字母集合
        public static HashSet<char> ParseDrives(string s)
        {
            HashSet<char> set = new HashSet<char>();
            if (s == null) return set;
            char[] sep = new char[] { ',', ';', ' ', '\t', '|' };
            foreach (string part in s.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim().TrimEnd('\\', '/', ':');
                if (t.Length == 0) continue;
                char ch = char.ToUpperInvariant(t[0]);
                if (ch >= 'A' && ch <= 'Z') set.Add(ch);
            }
            return set;
        }

        private static string GetStr(Dictionary<string, string> d, string key, string def)
        {
            string v;
            if (!d.TryGetValue(key, out v)) return def;
            return v;
        }

        private static bool GetBool(Dictionary<string, string> d, string key, bool def)
        {
            string s = GetStr(d, key, "").Trim();
            if (s.Length == 0) return def;
            return s == "1"
                || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || s.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static float GetFloat(Dictionary<string, string> d, string key, float def)
        {
            float f;
            if (float.TryParse(GetStr(d, key, "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f)
                && f >= 6f && f <= 48f) return f;
            return def;
        }

        private static int GetInt(Dictionary<string, string> d, string key, int def)
        {
            int n;
            if (int.TryParse(GetStr(d, key, "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return def;
        }
    }

    // ------------------------------------------------------- RC 位址自動偵測
    internal static class RcDetect
    {
        public const string Default = "127.0.0.1:5572";

        // 從執行中的 rclone mount 命令列找 --rc-addr。找不到回傳 null。
        // 有多個換載時，優先選換載位置是雲端槽位的那一個。
        public static string Find(HashSet<char> cloudDrives)
        {
            List<string> cmds = new List<string>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE Name LIKE 'rclone%'"))
                using (ManagementObjectCollection col = s.Get())
                {
                    foreach (ManagementBaseObject o in col)
                    {
                        string c = o["CommandLine"] as string;
                        if (!string.IsNullOrEmpty(c)) cmds.Add(c);
                    }
                }
            }
            catch { return null; }

            string matched = null, any = null;
            foreach (string c in cmds)
            {
                if (!Regex.IsMatch(c, @"(^|\s)(nfs)?mount(\s|$)", RegexOptions.IgnoreCase)) continue;
                if (!Regex.IsMatch(c, @"(^|\s)--rc(\s|$)")) continue;      // 沒開 --rc 就沒有 RC

                string addr = Default;
                Match m = Regex.Match(c, @"--rc-addr[=\s]+""?([^\s""]+)");
                if (m.Success) addr = Normalize(m.Groups[1].Value);
                else addr = "localhost:5572";

                if (any == null) any = addr;
                if (matched == null && cloudDrives != null)
                {
                    foreach (char d in cloudDrives)
                    {
                        if (Regex.IsMatch(c, @"(^|[\s""'])" + d + @":(?=[\s""'\\]|$)", RegexOptions.IgnoreCase))
                        { matched = addr; break; }
                    }
                }
            }
            return matched ?? any;
        }

        // ":5572" / "0.0.0.0:5572" 這類寫法轉成可連線的 127.0.0.1
        private static string Normalize(string a)
        {
            a = a.Trim();
            if (a.StartsWith(":")) return "127.0.0.1" + a;
            if (a.StartsWith("0.0.0.0:")) return "127.0.0.1" + a.Substring(7);
            if (a.StartsWith("[::]:")) return "127.0.0.1" + a.Substring(4);
            return a;
        }
    }

    // ------------------------------------------------------------ 字型 / 音效
    internal static class Theme
    {
        public static string UiName = "Microsoft YaHei";
        public static float UiSize = 11f;
        public static string MonoName = "Consolas";
        public static float MonoSize = 11f;
        public static string CjkName = "Microsoft YaHei";

        public static void Apply(Config c)
        {
            if (!string.IsNullOrEmpty(c.UiFont)) UiName = c.UiFont;
            UiSize = c.UiFontSize;
            if (!string.IsNullOrEmpty(c.ConsoleFont)) MonoName = c.ConsoleFont;
            MonoSize = c.ConsoleFontSize;
            if (!string.IsNullOrEmpty(c.ConsoleFontCjk)) CjkName = c.ConsoleFontCjk;
        }

        // 輸出區含中文的行用的字型
        public static Font MonoCjk()
        {
            return Make(CjkName, MonoSize, FontStyle.Regular, FontFamily.GenericSansSerif);
        }

        // 整行都是 ASCII（或進度條用的方塊符號）才算「純英數」
        public static bool NeedsCjk(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch < 0x80) continue;
                if (ch >= 0x2580 && ch <= 0x259F) continue;
                return true;
            }
            return false;
        }

        // scale：相對於設定大小的倍率（提示標題比內文大）
        public static Font Ui(float scale, FontStyle style)
        {
            return Make(UiName, UiSize * scale, style, FontFamily.GenericSansSerif);
        }

        public static Font Mono(float scale)
        {
            return Make(MonoName, MonoSize * scale, FontStyle.Regular, FontFamily.GenericMonospace);
        }

        private static Font Make(string name, float size, FontStyle style, FontFamily fallback)
        {
            try
            {
                using (FontFamily ff = new FontFamily(name))
                {
                    if (ff.IsStyleAvailable(style)) return new Font(ff, size, style);
                }
            }
            catch { /* 字型不存在 -> 使用備援 */ }
            return new Font(fallback, size, FontStyle.Regular);
        }
    }

    internal static class Sfx
    {
        private static System.Media.SoundPlayer _player;   // 保留參考，避免播完前被回收

        public static void Play(Config cfg, bool success)
        {
            if (cfg == null || !cfg.Sound) return;
            try
            {
                string v = (success ? cfg.SoundSuccess : cfg.SoundFail) ?? "";
                v = v.Trim().Trim('"');
                if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
                if (v.Length == 0) v = success ? "Windows Ding.wav" : "Windows Error.wav";

                switch (v.ToLowerInvariant())
                {
                    case "asterisk": System.Media.SystemSounds.Asterisk.Play(); return;
                    case "beep": System.Media.SystemSounds.Beep.Play(); return;
                    case "exclamation": System.Media.SystemSounds.Exclamation.Play(); return;
                    case "hand": System.Media.SystemSounds.Hand.Play(); return;
                    case "question": System.Media.SystemSounds.Question.Play(); return;
                }

                string path = v;
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", v);
                if (!File.Exists(path) && File.Exists(path + ".wav")) path = path + ".wav";

                if (File.Exists(path))
                {
                    _player = new System.Media.SoundPlayer(path);
                    _player.Play();                       // 非同步播放
                }
                else if (success) System.Media.SystemSounds.Asterisk.Play();
                else System.Media.SystemSounds.Exclamation.Play();
            }
            catch { }
        }
    }

    // --------------------------------------------------------- 一次工作的計畫
    internal sealed class Plan
    {
        public string Source = "";        // 拖進來的原始路徑
        public string Name = "";          // 顯示用名稱
        public bool IsDir;
        public string Verb = "copy";      // copy / copyto / purge / deletefile
        public string From = "";
        public string To = "";
        public string RefreshDir = "/";   // 完成後要刷新的遠端目錄
        public string Direction = "";     // 上傳 / 下載 / 刪除
        public string Describe = "";
        public string ConfirmMessage = ""; // 非空表示執行前要再問一次
    }

    // ------------------------------------------------------------ rclone 執行
    internal sealed class RcloneRunner
    {
        private readonly Config _cfg;
        private readonly MainForm _ui;
        private readonly object _procLock = new object();
        private Process _current;

        private static readonly Regex AnsiRe =
            new Regex("\x1B\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
        private static readonly Regex StampRe =
            new Regex("^\\d{4}[-/]\\d{2}[-/]\\d{2} \\d{2}:\\d{2}:\\d{2}\\s+(\\w+):\\s?(.*)$", RegexOptions.Compiled);
                    // --stats-one-line："39.672 MiB / 105.655 MiB, 38%, 4.993 MiB/s, ETA 13s (xfr#5/56)"
                    private static readonly Regex StatRe =
                        new Regex("^(.+?) / (.+?), (\\d+)%, (.+?), ETA (\\S+?)(?: \\(xfr#(\\d+)/(\\d+)\\))?$", RegexOptions.Compiled);

        public RcloneRunner(Config cfg, MainForm ui)
        {
            _cfg = cfg;
            _ui = ui;
        }

        public static string Resolve(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) nameOrPath = "rclone";
            try { if (File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath); }
            catch { }
            if (nameOrPath.IndexOfAny(new char[] { '\\', '/' }) >= 0) return nameOrPath;

            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar != null)
            {
                foreach (string dir in pathVar.Split(';'))
                {
                    string d = dir.Trim().Trim('"');
                    if (d.Length == 0) continue;
                    try
                    {
                        string full = Path.Combine(d, nameOrPath);
                        if (File.Exists(full)) return full;
                        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            && File.Exists(full + ".exe")) return full + ".exe";
                    }
                    catch { }
                }
            }
            return nameOrPath;
        }

        public void KillCurrent()
        {
            lock (_procLock)
            {
                if (_current == null) return;
                try { if (!_current.HasExited) _current.Kill(); }
                catch { }
            }
        }

        private ProcessStartInfo MakePsi(string arguments, bool redirect)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Resolve(_cfg.RclonePath);
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            if (redirect)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
            try { psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch { }
            return psi;
        }

        /// 執行 rclone，輸出即時顯示在視窗裡；回傳 exit code
        public int Run(string arguments)
        {
            ProcessStartInfo psi = MakePsi(arguments, true);
            _ui.Write("$ rclone " + arguments, OutKind.Cmd);

            Process p = new Process();
            p.StartInfo = psi;
            DataReceivedEventHandler h = delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                Emit(e.Data);
            };
            p.OutputDataReceived += h;
            p.ErrorDataReceived += h;

            try
            {
                p.Start();
                lock (_procLock) { _current = p; }
            }
            catch (Exception ex)
            {
                _ui.Write("[!] 無法啟動 rclone：" + ex.Message, OutKind.Err);
                _ui.Write("    請確認 rclone 在 PATH 裡，或修改 rclone-config.ini 的 rclonePath。", OutKind.Err);
                return -1;
            }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            lock (_procLock) { _current = null; }
            return p.ExitCode;
        }

        /// 執行 rclone 但不顯示輸出（給 vfs/refresh、listremotes 用）
        public int RunQuiet(string arguments, out string output)
        {
            output = "";
            ProcessStartInfo psi = MakePsi(arguments, true);
            Process p = new Process();
            p.StartInfo = psi;
            StringBuilder sb = new StringBuilder();
            try
            {
                p.Start();
                lock (_procLock) { _current = p; }
                DataReceivedEventHandler h = delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sb) { sb.AppendLine(e.Data); }
                };
                p.OutputDataReceived += h;
                p.ErrorDataReceived += h;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                lock (_procLock) { _current = null; }
                Thread.Sleep(60);                     // 等非同步讀取收尾
                lock (sb) { output = sb.ToString(); }
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                lock (_procLock) { _current = null; }
                output = ex.Message;
                return -1;
            }
        }

        public List<string> ListRemotes()
        {
            List<string> list = new List<string>();
            string outp;
            RunQuiet("listremotes", out outp);
            if (outp == null) return list;
            foreach (string line in outp.Split('\n'))
            {
                string t = AnsiRe.Replace(line, "").Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("Failed") || t.StartsWith("rclone:")) continue;
                list.Add(t);
            }
            return list;
        }

        private void Emit(string raw)
        {
            string line = AnsiRe.Replace(raw, "").TrimEnd();
            if (line.Trim().Length == 0) return;

            string level = null;
            string msg = line;
            Match m = StampRe.Match(line);
            if (m.Success)
            {
                level = m.Groups[1].Value.ToUpperInvariant();
                msg = m.Groups[2].Value;
            }

            if (level == "ERROR" || level == "CRITICAL" || level == "FATAL" || level == "PANIC")
            { _ui.Write("[!] " + msg, OutKind.Err); return; }

            if (level == "WARNING" || level == "WARN")
            { _ui.Write("[!] " + msg, OutKind.Warn); return; }

            if (IsStats(msg)) { _ui.WriteProgress(FormatStats(msg.Trim())); return; }

            _ui.Write(msg, OutKind.Normal);
        }

        // 把 rclone 的統計列排成進度條：████░░░░  38%  39.7 MiB / 105.7 MiB  5.0 MiB/s  ETA 13s  檔案 5/56
        private static string FormatStats(string s)
        {
            Match m = StatRe.Match(s);
            if (!m.Success) return "  " + s;
            int pct;
            if (!int.TryParse(m.Groups[3].Value, out pct)) return "  " + s;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            const int width = 20;
            int fill = pct * width / 100;
            StringBuilder sb = new StringBuilder();
            sb.Append("  ").Append('\u2588', fill).Append('\u2591', width - fill);
            sb.Append("  ").Append(pct.ToString().PadLeft(3)).Append('%');
            sb.Append("  ").Append(m.Groups[1].Value.Trim()).Append(" / ").Append(m.Groups[2].Value.Trim());
            sb.Append("  ").Append(m.Groups[4].Value.Trim());
            sb.Append("  ETA ").Append(m.Groups[5].Value);
            if (m.Groups[6].Success)
                sb.Append("  xfr ").Append(m.Groups[6].Value).Append('/').Append(m.Groups[7].Value);
            return sb.ToString();
        }

        private static bool IsStats(string s)
        {
            if (s == null) return false;
            string t = s.Trim();
            if (t.Length == 0) return false;
            if (t.StartsWith("Transferred:") || t.StartsWith("Checks:") || t.StartsWith("Deleted:") ||
                t.StartsWith("Renamed:") || t.StartsWith("Moved:") || t.StartsWith("Elapsed time:"))
                return true;
            // --stats-one-line 的進度列： "4.203 MiB / 14.305 MiB, 29%, 1.2 MiB/s, ETA 3s"
            return Regex.IsMatch(t, "\\d+(\\.\\d+)?\\s*%") && Regex.IsMatch(t, "ETA|B/s|/");
        }
    }

    // ------------------------------------------------------------ 現代化 UI 元件
    internal static class Dwm
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // 深色標題列 + Windows 11 圓角（舊系統呼叫失敗會直接忽略）
        public static void Apply(IntPtr h)
        {
            try
            {
                int dark = 1, round = 2;
                DwmSetWindowAttribute(h, 20, ref dark, 4);
                DwmSetWindowAttribute(h, 19, ref dark, 4);
                DwmSetWindowAttribute(h, 33, ref round, 4);
            }
            catch { }
        }
    }

    internal static class Gfx
    {
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            GraphicsPath p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal class RoundedPanel : Panel
    {
        public Color Fill = Color.FromArgb(19, 19, 21);
        public float Radius = 12;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color bg = Parent != null ? Parent.BackColor : BackColor;
            e.Graphics.Clear(bg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Gfx.Round(new RectangleF(0, 0, Width - 1, Height - 1), Radius))
            using (SolidBrush b = new SolidBrush(Fill))
                e.Graphics.FillPath(b, p);
        }
    }

    internal sealed class DropZone : Control
    {
        public DropZone()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Color c = ForeColor;

            RectangleF box = new RectangleF(1, 1, Width - 3, Height - 3);
            using (GraphicsPath p = Gfx.Round(box, 16))
            {
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(16, c)))
                    g.FillPath(fb, p);
                using (Pen pen = new Pen(Color.FromArgb(120, c), 2f))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, p);
                }
            }

            string text = Text ?? "";
            int sep = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string title = sep >= 0 ? text.Substring(0, sep) : text;
            string body = sep >= 0 ? text.Substring(sep + 4).Trim() : "";

            using (Font tf = Theme.Ui(16f / 9f, FontStyle.Bold))
            using (Font bf = Theme.Ui(10.5f / 9f, FontStyle.Regular))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                float w = Width - 60;
                SizeF ts = g.MeasureString(title, tf, (int)w, sf);
                SizeF bs = body.Length > 0 ? g.MeasureString(body, bf, (int)w, sf) : SizeF.Empty;
                float icon = 64, gap1 = 18, gap2 = 12;
                float total = icon + gap1 + ts.Height + (bs.Height > 0 ? gap2 + bs.Height : 0);
                float y = Math.Max(10, (Height - total) / 2f);
                float cx = Width / 2f;

                RectangleF ic = new RectangleF(cx - icon / 2, y, icon, icon);
                using (SolidBrush ib = new SolidBrush(Color.FromArgb(34, c)))
                    g.FillEllipse(ib, ic);
                using (Pen ap = new Pen(c, 3.2f))
                {
                    ap.StartCap = LineCap.Round; ap.EndCap = LineCap.Round; ap.LineJoin = LineJoin.Round;
                    float m = y + icon / 2;
                    g.DrawLine(ap, cx, m - 14, cx, m + 11);
                    g.DrawLine(ap, cx - 10, m + 1, cx, m + 11);
                    g.DrawLine(ap, cx + 10, m + 1, cx, m + 11);
                    g.DrawLine(ap, cx - 14, m + 19, cx + 14, m + 19);
                }
                y += icon + gap1;

                using (SolidBrush tb = new SolidBrush(c))
                    g.DrawString(title, tf, tb, new RectangleF(30, y, w, ts.Height + 4), sf);
                y += ts.Height + gap2;
                if (bs.Height > 0)
                    using (SolidBrush bb = new SolidBrush(Color.FromArgb(170, c)))
                        g.DrawString(body, bf, bb, new RectangleF(30, y, w, bs.Height + 4), sf);
            }
        }
    }

    internal sealed class RoundButton : Button
    {
        public Color Normal = Color.FromArgb(58, 58, 64);
        public Color Hover = Color.FromArgb(78, 78, 86);
        private bool _over, _down;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _over = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Color c = _down ? ControlPaint.Dark(Normal, 0.1f) : (_over ? Hover : Normal);
            using (GraphicsPath p = Gfx.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
            using (StringFormat sf = new StringFormat())
            using (SolidBrush tb = new SolidBrush(ForeColor))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(Text, Font, tb, new RectangleF(0, 0, Width, Height), sf);
            }
        }
    }

    // ------------------------------------------ exe 圖示（程式碼繪製；--make-ico 輸出 .ico）
    internal static class AppIcon
    {
#if MODE_DELETE
        private static readonly bool IsDelete = true;
#else
        private static readonly bool IsDelete = false;
#endif

        private static Bitmap Draw(int S)
        {
            Bitmap bmp = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float m = S * 0.04f, w = S - 2 * m, r = S * 0.22f, d = r * 2;
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddArc(m, m, d, d, 180, 90);
                    path.AddArc(m + w - d, m, d, d, 270, 90);
                    path.AddArc(m + w - d, m + w - d, d, d, 0, 90);
                    path.AddArc(m, m + w - d, d, d, 90, 90);
                    path.CloseFigure();
                    Color c1 = ColorTranslator.FromHtml(IsDelete ? "#FF8A7A" : "#56B0FF");
                    Color c2 = ColorTranslator.FromHtml(IsDelete ? "#D43232" : "#2457E6");
                    using (LinearGradientBrush br = new LinearGradientBrush(new RectangleF(0, 0, S, S), c1, c2, 60f))
                        g.FillPath(br, path);
                }
                using (Pen pen = new Pen(Color.White, S * 0.085f))
                using (Pen thin = new Pen(Color.White, S * 0.06f))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    thin.StartCap = LineCap.Round; thin.EndCap = LineCap.Round;
                    if (IsDelete)
                    {
                        g.DrawLine(pen, P(S, 0.27f, 0.30f), P(S, 0.73f, 0.30f));
                        g.DrawLine(pen, P(S, 0.42f, 0.30f), P(S, 0.42f, 0.23f));
                        g.DrawLine(pen, P(S, 0.42f, 0.23f), P(S, 0.58f, 0.23f));
                        g.DrawLine(pen, P(S, 0.58f, 0.23f), P(S, 0.58f, 0.30f));
                        g.DrawLines(pen, new PointF[] { P(S, 0.33f, 0.38f), P(S, 0.36f, 0.75f), P(S, 0.64f, 0.75f), P(S, 0.67f, 0.38f) });
                        g.DrawLine(thin, P(S, 0.45f, 0.46f), P(S, 0.46f, 0.67f));
                        g.DrawLine(thin, P(S, 0.55f, 0.46f), P(S, 0.54f, 0.67f));
                    }
                    else
                    {
                        g.DrawLine(pen, P(S, 0.36f, 0.74f), P(S, 0.36f, 0.28f));
                        g.DrawLines(pen, new PointF[] { P(S, 0.23f, 0.41f), P(S, 0.36f, 0.28f), P(S, 0.49f, 0.41f) });
                        g.DrawLine(pen, P(S, 0.64f, 0.26f), P(S, 0.64f, 0.72f));
                        g.DrawLines(pen, new PointF[] { P(S, 0.51f, 0.59f), P(S, 0.64f, 0.72f), P(S, 0.77f, 0.59f) });
                    }
                }
            }
            return bmp;
        }

        private static PointF P(int S, float x, float y) { return new PointF(x * S, y * S); }

        public static Icon CreateWindowIcon()
        {
            using (Bitmap b = Draw(32))
            {
                IntPtr h = b.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        public static void WriteIco(string path)
        {
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            List<byte[]> pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (Bitmap b = Draw(s))
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            using (FileStream fs = File.Create(path))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    bw.Write(dim); bw.Write(dim); bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((ushort)1); bw.Write((ushort)32);
                    bw.Write((uint)pngs[i].Length); bw.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) bw.Write(p);
            }
        }
    }

    // -------------------------------------- 在總管中選取路徑（行程內呼叫，比 explorer /select 快）
    internal static class ShellReveal
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ILCreateFromPathW(string pszPath);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(
            IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        public static void SelectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }

            IntPtr pidl = ILCreateFromPathW(full);
            if (pidl != IntPtr.Zero)
            {
                try
                {
                    if (SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0) return;
                }
                finally { ILFree(pidl); }
            }

            // 後備：開新的 explorer 行程（較慢）
            try
            {
                if (File.Exists(full))
                    Process.Start("explorer.exe", "/select,\"" + full + "\"");
                else if (Directory.Exists(full))
                    Process.Start("explorer.exe", "\"" + full + "\"");
            }
            catch { }
        }
    }

    // ----------------------------------------------- 齒輪按鈕（六齒盾形、中間圓孔鏤空）
    internal sealed class GearButton : Control
    {
        public Color NormalColor = Color.FromArgb(160, 162, 168);
        public Color HotColor = Color.FromArgb(230, 230, 230);
        private bool _over;

        public GearButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _over = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Color c = _over ? HotColor : NormalColor;
            float size = Math.Min(Width, Height) * 0.86f;
            float cx = Width / 2f, cy = Height / 2f;
            float round = Math.Max(1f, size * 0.07f);
            float tipR = size * 0.50f - round / 2f, rootR = size * 0.36f, holeR = size * 0.17f;

            List<PointF> pts = new List<PointF>();
            const int teeth = 6;
            double period = 2.0 * Math.PI / teeth;
            double halfRoot = period * 0.30, halfTip = period * 0.16;
            for (int k = 0; k < teeth; k++)
            {
                double a = k * period - Math.PI / 2.0;
                pts.Add(Pt(cx, cy, rootR, a - halfRoot));
                pts.Add(Pt(cx, cy, tipR, a - halfTip));
                pts.Add(Pt(cx, cy, tipR, a + halfTip));
                pts.Add(Pt(cx, cy, rootR, a + halfRoot));
                double from = a + halfRoot, to = a + period - halfRoot;
                for (int i = 1; i < 4; i++) pts.Add(Pt(cx, cy, rootR, from + (to - from) * i / 4.0));
            }
            using (GraphicsPath outer = new GraphicsPath())
            {
                outer.AddPolygon(pts.ToArray());
                using (GraphicsPath body = (GraphicsPath)outer.Clone())
                using (SolidBrush br = new SolidBrush(c))
                using (Pen pen = new Pen(c, round))
                {
                    pen.LineJoin = LineJoin.Round;
                    body.AddEllipse(cx - holeR, cy - holeR, holeR * 2f, holeR * 2f);
                    body.FillMode = FillMode.Alternate;
                    g.FillPath(br, body);
                    g.DrawPath(pen, outer);
                }
            }
        }

        private static PointF Pt(float cx, float cy, float r, double ang)
        {
            return new PointF(cx + (float)(Math.Cos(ang) * r), cy + (float)(Math.Sin(ang) * r));
        }
    }

    // ---------------------------------------------------------- 遠端選擇視窗
    internal sealed class RemotePickerForm : Form
    {
        private readonly ListBox _list;
        private readonly TextBox _custom;
        public string Selected = null;

        public RemotePickerForm(List<string> remotes)
        {
            Text = "選擇 rclone 遠端";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 520);
            MinimumSize = Size;      // 用一般標題欄，但固定大小
            MaximumSize = Size;
            Padding = new Padding(16, 8, 16, 12);
            BackColor = Color.FromArgb(30, 30, 33);
            ForeColor = Color.Gainsboro;
            Font = Theme.Ui(1f, FontStyle.Regular);

            Label tip = new Label();
            tip.Dock = DockStyle.Top;
            tip.Height = 96;
            tip.Padding = new Padding(2, 10, 2, 8);
            tip.ForeColor = Color.FromArgb(200, 200, 205);
            tip.Text = "請選擇要使用的雲端遠端\r\n（選定後會寫入設定檔，之後不用再選）";

            RoundedPanel listCard = new RoundedPanel();
            listCard.Dock = DockStyle.Fill;
            listCard.Fill = Color.FromArgb(22, 22, 24);
            listCard.Padding = new Padding(10, 10, 10, 10);

            _list = new ListBox();
            _list.Dock = DockStyle.Fill;
            _list.BackColor = Color.FromArgb(22, 22, 24);
            _list.ForeColor = Color.Gainsboro;
            _list.BorderStyle = BorderStyle.None;
            _list.ItemHeight = 24;
            _list.Font = Theme.Mono(11f / 10.5f);
            _list.ItemHeight = (int)Math.Ceiling(_list.Font.GetHeight()) + 6;
            _list.IntegralHeight = false;
            if (remotes != null)
                foreach (string r in remotes) _list.Items.Add(r);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.DoubleClick += delegate(object s, EventArgs e) { ConfirmChoice(); };
            _list.SelectedIndexChanged += delegate(object s, EventArgs e)
            {
                if (_list.SelectedItem != null) _custom.Text = _list.SelectedItem.ToString();
            };

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 150;

            Label lb2 = new Label();
            lb2.AutoSize = false;
            lb2.SetBounds(2, 18, 420, 30);
            lb2.ForeColor = Color.FromArgb(160, 160, 165);
            lb2.Text = "或直接輸入遠端名稱：";

            _custom = new TextBox();
            _custom.SetBounds(2, 54, 444, 32);
            _custom.BackColor = Color.FromArgb(45, 45, 48);
            _custom.ForeColor = Color.White;
            _custom.BorderStyle = BorderStyle.FixedSingle;
            _custom.Font = Theme.Mono(1f);

            RoundButton ok = new RoundButton();
            ok.Text = "確定";
            ok.SetBounds(244, 100, 100, 40);
            ok.Normal = Color.FromArgb(52, 120, 220);
            ok.Hover = Color.FromArgb(78, 144, 240);
            ok.Click += delegate(object s, EventArgs e) { ConfirmChoice(); };

            RoundButton cancel = new RoundButton();
            cancel.Text = "取消";
            cancel.SetBounds(354, 100, 92, 40);
            cancel.DialogResult = DialogResult.Cancel;

            bottom.Controls.Add(lb2);
            bottom.Controls.Add(_custom);
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);

            listCard.Controls.Add(_list);
            Controls.Add(listCard);
            Controls.Add(tip);
            Controls.Add(bottom);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Dwm.Apply(Handle);
        }

        private void ConfirmChoice()
        {
            string s = _custom.Text == null ? "" : _custom.Text.Trim();
            if (s.Length == 0 && _list.SelectedItem != null) s = _list.SelectedItem.ToString();
            if (s.Length == 0)
            {
                MessageBox.Show(this, "請選擇或輸入一個遠端名稱。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Selected = s;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // --------------------------------------------------------------- 主視窗
    internal sealed class MainForm : Form
    {
#if MODE_TRANSFER
        private const string Title = "Rclone Transfer　上傳 / 下載";
        private const string HintIdle =
            "將檔案或資料夾拖曳到這個視窗\r\n\r\n" +
            "本地槽位的項目　→　上傳到雲端\r\n" +
            "雲端槽位的項目　→　下載到本機\r\n\r\n" +
            "也可以直接把檔案拖到 rclone-transfer.exe 的圖示上";
        private const string HintOver = "放開滑鼠即可開始傳輸";
#else
        private const string Title = "Rclone Delete　雲端刪除";
        private const string HintIdle =
            "將「雲端硬碟」中的檔案或資料夾\r\n拖曳到這個視窗\r\n\r\n" +
            "[!] 會直接從雲端永久刪除\r\n　　不會經過 Windows 資源回收筒";
        private const string HintOver = "放開滑鼠以選取要刪除的項目";
#endif

        private static readonly Color CForm = Color.FromArgb(26, 26, 29);
        private static readonly Color CConsole = Color.FromArgb(19, 19, 21);
        private static readonly Color CHint = Color.FromArgb(185, 187, 192);
        private static readonly Color CHintHot = Color.FromArgb(126, 217, 158);
        private static readonly Color CText = Color.FromArgb(222, 222, 222);

        private readonly RichTextBox _console;
        private readonly DropZone _hint;
        private readonly RoundedPanel _card;
        private readonly GearButton _gear;
        private readonly Config _cfg;
        private readonly RcloneRunner _rclone;
        private readonly Queue<string> _queue = new Queue<string>();
        private readonly object _sync = new object();

        private bool _working;
        private int _progressStart = -1;
        private bool _headerShown;
        private Font _fontAscii;
        private Font _fontCjk;
        private string _rcAddrCache;  // 自動偵測到的 RC 位址
        private string _remote;      // 已解析的遠端（結尾一定有 :）

        public MainForm(string[] initialFiles)
        {
            Text = Title;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 540);
            MinimumSize = new Size(640, 380);
            Padding = new Padding(16);
            BackColor = CForm;
            ForeColor = CText;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { /* 拿不到就用預設圖示 */ }
            if (Icon == null) { try { Icon = AppIcon.CreateWindowIcon(); } catch { } }
            AllowDrop = true;
            KeyPreview = true;
            Font = Theme.Ui(1f, FontStyle.Regular);

            string exeDir;
            try { exeDir = Path.GetDirectoryName(Application.ExecutablePath); }
            catch { exeDir = null; }
            if (string.IsNullOrEmpty(exeDir)) exeDir = Directory.GetCurrentDirectory();
            _cfg = Config.Load(exeDir);
            Theme.Apply(_cfg);
            Font = Theme.Ui(1f, FontStyle.Regular);
            _rclone = new RcloneRunner(_cfg, this);

            _card = new RoundedPanel();
            _card.Dock = DockStyle.Fill;
            _card.Fill = CConsole;
            _card.Radius = 14;
            _card.Padding = new Padding(16, 14, 12, 14);
            _card.AllowDrop = true;

            _console = new RichTextBox();
            _console.Dock = DockStyle.Fill;
            _console.ReadOnly = true;
            _console.BorderStyle = BorderStyle.None;
            _console.BackColor = CConsole;
            _console.ForeColor = CText;
            _fontAscii = Theme.Mono(1f);
            _fontCjk = Theme.MonoCjk();
            _console.Font = _fontAscii;
            _console.DetectUrls = false;
            _console.ScrollBars = RichTextBoxScrollBars.Vertical;
            _console.WordWrap = true;
            _console.HideSelection = false;
            _console.AllowDrop = true;
            _console.Visible = false;

            _hint = new DropZone();
            _hint.Dock = DockStyle.Fill;
            _hint.ForeColor = CHint;
            _hint.AllowDrop = true;
            _hint.Text = HintIdle;

            _card.Controls.Add(_console);
            _card.Controls.Add(_hint);
            Controls.Add(_card);

            // 右上角齒輪（在虛線框內）：開啟 rclone-config.ini 所在位置
            _gear = new GearButton();
            _gear.Size = new Size(34, 34);
            _gear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _gear.BackColor = CConsole;
            _gear.Click += delegate { OpenConfigLocation(); };
            new ToolTip().SetToolTip(_gear, "開啟設定檔所在位置並選取 " + Config.FileName);
            _card.Controls.Add(_gear);
            _gear.BringToFront();
            _card.Resize += delegate { _gear.Location = new Point(_card.Width - _gear.Width - 46, 36); };
            _gear.Location = new Point(_card.Width - _gear.Width - 46, 36);
            // 只在選擇（拖曳提示）畫面顯示；進入終端畫面就隱藏，回到提示畫面會再出現
            _gear.Visible = _hint.Visible;
            _hint.VisibleChanged += delegate { _gear.Visible = _hint.Visible; };

            DragEnter += OnDragEnter;
            DragOver += OnDragEnter;
            DragLeave += OnDragLeave;
            DragDrop += OnDragDrop;
            foreach (Control c in new Control[] { _card, _hint, _console })
            {
                c.DragEnter += OnDragEnter;
                c.DragOver += OnDragEnter;
                c.DragLeave += OnDragLeave;
                c.DragDrop += OnDragDrop;
            }

            KeyDown += OnKeyDown;
            FormClosing += OnFormClosing;

            if (initialFiles != null && initialFiles.Length > 0)
            {
                // 從 exe 圖示拖放啟動：直接顯示終端畫面，不要閃一下提示
                _hint.Visible = false;
                _console.Visible = true;
                Enqueue(initialFiles);
            }
            Shown += delegate(object s, EventArgs e) { TryStart(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Dwm.Apply(Handle);
        }

        private void OpenConfigLocation()
        {
            try
            {
                string fp = _cfg.FilePath;
                if (File.Exists(fp)) ShellReveal.SelectPath(fp);
                else
                {
                    string dir = Path.GetDirectoryName(fp);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        ShellReveal.SelectPath(dir);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------ 拖曳
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                if (_hint.Visible) { _hint.ForeColor = CHintHot; _hint.Text = HintOver; }
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void OnDragLeave(object sender, EventArgs e)
        {
            if (_hint.Visible) { _hint.ForeColor = CHint; _hint.Text = HintIdle; }
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (_hint.Visible) { _hint.ForeColor = CHint; _hint.Text = HintIdle; }
            string[] files = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            Enqueue(files);
            TryStart();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; return; }   // Esc：關閉（處理中會先確認）
            if (e.KeyCode == Keys.Enter && _console.Visible && !_working)
            {
                ShowMain();                        // Enter：終端畫面回到主畫面
                e.Handled = true;
            }
        }

        private void ShowMain()
        {
            _console.Clear();
            _progressStart = -1;
            _headerShown = false;
            _console.Visible = false;
            _hint.ForeColor = CHint;
            _hint.Text = HintIdle;
            _hint.Visible = true;
            Text = Title;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_working) return;
            DialogResult r = MessageBox.Show(this,
                "仍有工作在進行中，確定要結束嗎？\r\n\r\n（進行中的 rclone 會被中止）",
                "確認結束", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
            _rclone.KillCurrent();
        }

        // ------------------------------------------------------------ 佇列
        private void Enqueue(IEnumerable<string> files)
        {
            lock (_sync)
            {
                foreach (string f in files)
                    if (!string.IsNullOrEmpty(f)) _queue.Enqueue(f);
            }
        }

        private void TryStart()
        {
            lock (_sync)
            {
                if (_working) return;
                if (_queue.Count == 0) return;
                _working = true;
            }
            Ui(delegate
            {
                _hint.Visible = false;
                _console.Visible = true;
                Text = Title + "　—　處理中…";
                if (!_headerShown) { _headerShown = true; PrintHeader(); }
                try { _console.Focus(); } catch { }
            });

            Thread t = new Thread(WorkerLoop);
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Name = "rclone-worker";
            t.Start();
        }

        private void WorkerLoop()
        {
            try
            {
                while (true)
                {
                    List<string> batch = new List<string>();
                    lock (_sync) { while (_queue.Count > 0) batch.Add(_queue.Dequeue()); }
                    if (batch.Count == 0) break;
                    ProcessBatch(batch);
                }
            }
            catch (Exception ex)
            {
                Write("[!] 未預期的錯誤：" + ex.Message, OutKind.Err);
            }
            finally
            {
                lock (_sync) { _working = false; }
                Write("（視窗保持開啟：可繼續拖曳，按 Enter 回到主畫面，Esc 關閉）", OutKind.Dim);
                Ui(delegate { Text = Title; });
            }
        }

        // ------------------------------------------------------- 主要流程
        private void ProcessBatch(List<string> paths)
        {
            string remote = ResolveRemote();
            if (remote == null) { Write("已取消。", OutKind.Warn); return; }

            HashSet<char> cloud = Config.ParseDrives(_cfg.CloudDrives);
            HashSet<char> local = Config.ParseDrives(_cfg.LocalDrives);

            List<Plan> plans = new List<Plan>();
            foreach (string p in paths)
            {
                Plan plan;
                string err;
                if (TryPlan(p, remote, cloud, local, out plan, out err)) plans.Add(plan);
                else Write("[X] " + err, OutKind.Err);
            }
            if (plans.Count == 0) return;

#if MODE_DELETE
            if (!ConfirmDelete(plans)) { Write("已取消刪除。", OutKind.Warn); return; }
#endif

            int ok = 0, fail = 0;
            foreach (Plan plan in plans)
            {
                if (plan.ConfirmMessage.Length > 0)
                {
                    Plan cur = plan;
                    bool yes = UiSync(delegate
                    {
                        return MessageBox.Show(this, cur.ConfirmMessage, "請確認",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                    });
                    if (!yes) { Write("  已略過：" + cur.Name, OutKind.Warn); continue; }
                }
                if (Execute(plan)) ok++; else fail++;
            }

            Write("", OutKind.Normal);
            Write(new string('=', 62), OutKind.Head);
            if (fail == 0)
            {
                Write(" 全部完成！共 " + ok + " 個項目", OutKind.Ok);
                Sfx.Play(_cfg, true);
            }
            else
            {
                Write(" 完成 " + ok + " 個，失敗 " + fail + " 個", OutKind.Warn);
                Sfx.Play(_cfg, false);
            }
            Write(new string('=', 62), OutKind.Head);
        }

        private bool Execute(Plan p)
        {
            Write("", OutKind.Normal);
            Write(new string('-', 62), OutKind.Head);
            Write(" " + p.Direction + "　" + p.Describe, OutKind.Head);
            Write(new string('-', 62), OutKind.Head);

            StringBuilder args = new StringBuilder();
            args.Append(p.Verb).Append(' ').Append(Q(p.From));
            if (p.To.Length > 0) args.Append(' ').Append(Q(p.To));

            if (p.Verb == "copy" || p.Verb == "copyto")
            {
                args.Append(" --transfers ").Append(_cfg.Transfers)
                    .Append(" --checkers ").Append(_cfg.Checkers)
                    .Append(" --tpslimit ").Append(_cfg.TpsLimit)
                    // 輸出被導向時 --progress 的進度會黏成一行，
                    // 改用 log 版單行統計，再由視窗做「同一行原地更新」。
                    .Append(" --stats 1s --stats-one-line")
                    .Append(" --stats-log-level NOTICE --log-level NOTICE");
            }

            int code = _rclone.Run(args.ToString());
            bool success = (code == 0);

            if (success) Write(" [OK] " + p.Direction + "完成：" + p.Name, OutKind.Ok);
            else Write(" [X] " + p.Direction + "失敗（rclone 回傳 " + code + "）", OutKind.Err);

            if (success && _cfg.AutoRefresh) DoRefresh(p.RefreshDir);
            return success;
        }

        // 處理完成後通知掛載點刷新 VFS 快取
        private void DoRefresh(string dir)
        {
            string addr = (_cfg.RcAddr ?? "").Trim();
            bool auto = addr.Length == 0;
            if (auto)
            {
                if (_rcAddrCache == null)
                {
                    string found = RcDetect.Find(Config.ParseDrives(_cfg.CloudDrives));
                    _rcAddrCache = found ?? RcDetect.Default;
                    Write(found != null
                        ? " 自動偵測到 RC 位址：" + _rcAddrCache
                        : " 找不到執行中的 rclone mount（需要 --rc），改用預設 RC 位址：" + _rcAddrCache,
                        found != null ? OutKind.Dim : OutKind.Warn);
                }
                addr = _rcAddrCache;
            }

            // rclone 的根目錄要用空字串（省略 dir），dir=/ 會回傳 "file does not exist"
            string cur = (dir ?? "").Replace('\\', '/').Trim('/');
            Write(" 處理完成！正在通知掛載點刷新…（dir=/" + cur + "  rc=" + addr + "）", OutKind.Normal);

            string first = "";
            while (true)
            {
                string args = "rc vfs/refresh"
                    + (cur.Length > 0 ? " dir=" + Q(cur) : "")
                    + " recursive=" + (_cfg.RefreshRecursive ? "true" : "false")
                    + " --rc-addr " + addr;
                string output;
                int code = _rclone.RunQuiet(args, out output);

                // exit code 0 不代表成功：結果 JSON 裡每個目錄的值都要是 "OK"
                string bad = null;
                if (output != null)
                {
                    foreach (Match m in Regex.Matches(output, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                    {
                        if (m.Groups[1].Value == "error" || m.Groups[2].Value != "OK") { bad = m.Groups[2].Value; break; }
                    }
                }

                if (code == 0 && bad == null)
                {
                    Write(" [OK] 掛載點已刷新：/" + cur, OutKind.Ok);
                    return;
                }

                if (first.Length == 0)
                {
                    if (bad != null) first = bad;
                    else if (output != null)
                        foreach (string l in output.Split('\n'))
                            if (l.Trim().Length > 0) { first = l.Trim(); break; }
                }

                // 目錄還不在掛載點快取裡（例如剛上傳的新資料夾），改刷新上一層
                if (code == 0 && cur.Length > 0)
                {
                    int i = cur.LastIndexOf('/');
                    cur = i < 0 ? "" : cur.Substring(0, i);
                    continue;
                }
                break;
            }
            if (auto) _rcAddrCache = null;     // 下次重新偵測
            Write(" [!] 刷新失敗（掛載點可能沒開 --rc，不影響剛剛的結果）：" + first, OutKind.Warn);
        }

        private string ResolveRemote()
        {
            if (!string.IsNullOrEmpty(_remote)) return _remote;

            string cfgRemote = (_cfg.Remote ?? "").Trim().Trim('"');
            if (cfgRemote.Length > 0)
            {
                _remote = NormRemote(cfgRemote);
                Write(" 使用設定中的遠端：" + _remote, OutKind.Normal);
                return _remote;
            }

            Write(" rclone-config.ini 還沒指定 remote，正在讀取可用的雲端清單…", OutKind.Normal);
            List<string> remotes = _rclone.ListRemotes();
            if (remotes.Count == 0)
                Write(" [!] rclone listremotes 沒有回傳任何遠端，請手動輸入名稱。", OutKind.Warn);

            string picked = UiSync(delegate { return PickRemote(remotes); });
            if (string.IsNullOrEmpty(picked)) return null;

            picked = NormRemote(picked);
            _cfg.SaveRemote(picked);
            Write(" 已選擇遠端：" + picked + "（已寫回 rclone-config.ini，下次不用再選）", OutKind.Ok);
            _remote = picked;
            return _remote;
        }

        private string PickRemote(List<string> remotes)
        {
            using (RemotePickerForm f = new RemotePickerForm(remotes))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                return f.Selected;
            }
        }

        private void PrintHeader()
        {
            string bar = new string('=', 62);
            WriteNow(bar, OutKind.Head);
            WriteNow(" " + Title, OutKind.Head);
            WriteNow(bar, OutKind.Head);
            WriteNow(" 設定檔　: " + _cfg.FilePath, OutKind.Normal);
            if (_cfg.CreatedNew)
                WriteNow(" 　　　　　（第一次執行，已建立預設 rclone-config.ini，可自行修改）", OutKind.Warn);
            if (!string.IsNullOrEmpty(_cfg.ParseError))
                WriteNow(" [!] 設定解析失敗：" + _cfg.ParseError + "（本次改用預設值）", OutKind.Err);
            WriteNow(" 雲端槽位: " + FmtDrives(_cfg.CloudDrives)
                   + "　　本地槽位: " + FmtDrives(_cfg.LocalDrives), OutKind.Normal);
#if MODE_TRANSFER
            WriteNow(" 上傳到　: " + DescribeUploadTarget(), OutKind.Normal);
            WriteNow(" 下載到　: " + Expand(_cfg.DownloadDir), OutKind.Normal);
#else
            WriteNow(" 動作　　: 從雲端永久刪除（purge / deletefile）", OutKind.Normal);
#endif
            if (_cfg.AutoRefresh)
                WriteNow(" 完成後　: 自動刷新掛載點（RC 位址："
                    + (string.IsNullOrEmpty((_cfg.RcAddr ?? "").Trim()) ? "自動偵測" : _cfg.RcAddr.Trim()) + "）", OutKind.Normal);
            WriteNow(bar, OutKind.Head);
        }

        private string DescribeUploadTarget()
        {
            string r = string.IsNullOrEmpty(_cfg.Remote) ? "（尚未選擇）" : NormRemote(_cfg.Remote);
            string up = CleanRemoteDir(_cfg.UploadDir);
            return r + "/" + up;
        }

        // ------------------------------------------------- 路徑 → 工作計畫
        private static bool TryGetDrive(string path, out char drive)
        {
            drive = '\0';
            try
            {
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return false;
                if (root.StartsWith("\\\\") || root.StartsWith("//")) return false;   // UNC 不支援
                if (root.Length >= 2 && root[1] == ':')
                {
                    drive = char.ToUpperInvariant(root[0]);
                    return drive >= 'A' && drive <= 'Z';
                }
            }
            catch { }
            return false;
        }

        // "a/b/c.zip" -> "a/b"；"c.zip" -> "/"
        private static string ParentRemoteDir(string remoteRel)
        {
            if (string.IsNullOrEmpty(remoteRel)) return "/";
            int i = remoteRel.LastIndexOf('/');
            if (i <= 0) return "/";
            return remoteRel.Substring(0, i);
        }

#if MODE_TRANSFER
        private bool TryPlan(string src, string remote, HashSet<char> cloud, HashSet<char> local,
                             out Plan plan, out string err)
        {
            plan = null;
            err = null;

            char drive;
            if (!TryGetDrive(src, out drive))
            {
                err = "無法判斷磁碟代號（不支援 UNC 路徑）：" + src;
                return false;
            }
            if (!File.Exists(src) && !Directory.Exists(src))
            {
                err = "找不到這個項目：" + src;
                return false;
            }

            if (cloud.Contains(drive)) return PlanDownload(src, drive, remote, out plan, out err);
            if (local.Count == 0 || local.Contains(drive)) return PlanUpload(src, remote, out plan, out err);

            err = drive + ": 不是設定中的雲端槽位（" + FmtDrives(_cfg.CloudDrives)
                + "）也不是本地槽位（" + FmtDrives(_cfg.LocalDrives)
                + "）。請修改 rclone-config.ini 的 cloud / local 欄位。";
            return false;
        }

        // 本機 → 雲端。資料夾用 copy、單一檔案用 copyto，
        // 這樣才不會出現 "xxx.jpg\xxx.jpg" 這種多包一層同名資料夾的問題。
        private bool PlanUpload(string src, string remote, out Plan plan, out string err)
        {
            plan = null;
            err = null;

            string norm = src.TrimEnd('\\', '/');
            string name = Path.GetFileName(norm);
            if (string.IsNullOrEmpty(name))
            {
                err = "不能上傳整個磁碟：" + src;
                return false;
            }

            bool isDir = Directory.Exists(src);
            string up = CleanRemoteDir(_cfg.UploadDir);
            string target = remote + "/" + (up.Length == 0 ? name : up + "/" + name);

            Plan p = new Plan();
            p.Source = norm;
            p.Name = name;
            p.IsDir = isDir;
            p.Verb = isDir ? "copy" : "copyto";
            p.From = norm;
            p.To = target;
            p.RefreshDir = "/" + up;
            p.Direction = "上傳";
            p.Describe = (isDir ? "資料夾  " : "檔案　  ") + norm + "\r\n　　　→ " + target;
            plan = p;
            return true;
        }

        // 雲端 → 本機。同樣區分 copy / copyto。
        private bool PlanDownload(string src, char drive, string remote, out Plan plan, out string err)
        {
            plan = null;
            err = null;

            string full;
            try { full = Path.GetFullPath(src); }
            catch { full = src; }

            string rel = full.Length <= 3 ? "" : full.Substring(3).Trim('\\');
            string relRemote = rel.Replace('\\', '/');
            bool isDir = Directory.Exists(src);

            string destRoot = Expand(_cfg.DownloadDir);
            if (string.IsNullOrEmpty(destRoot))
                destRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads\\Rclone");

            Plan p = new Plan();
            p.Source = src;
            p.Name = rel.Length == 0 ? (drive + ":\\ 整個雲端硬碟") : Path.GetFileName(rel.TrimEnd('\\', '/'));
            p.IsDir = isDir;
            p.Verb = isDir ? "copy" : "copyto";
            p.From = remote + "/" + relRemote;
            p.To = rel.Length == 0 ? destRoot : Path.Combine(destRoot, rel);
            p.RefreshDir = ParentRemoteDir(relRemote);
            p.Direction = "下載";
            p.Describe = (isDir ? "資料夾  " : "檔案　  ") + p.From + "\r\n　　　→ " + p.To;

            if (rel.Length == 0)
            {
                p.ConfirmMessage = "你拖進來的是整個雲端磁碟根目錄。\r\n\r\n"
                    + "這會把 " + p.From + " 全部下載到：\r\n" + destRoot + "\r\n\r\n確定要繼續嗎？";
            }
            plan = p;
            return true;
        }
#else
        private bool TryPlan(string src, string remote, HashSet<char> cloud, HashSet<char> local,
                             out Plan plan, out string err)
        {
            plan = null;
            err = null;

            char drive;
            if (!TryGetDrive(src, out drive))
            {
                err = "無法判斷磁碟代號（不支援 UNC 路徑）：" + src;
                return false;
            }
            if (!cloud.Contains(drive))
            {
                err = "「" + src + "」不是雲端槽位（設定為 " + FmtDrives(_cfg.CloudDrives)
                    + "）。請從 rclone 掛載的雲端硬碟裡拖曳要刪除的項目。";
                return false;
            }

            string full;
            try { full = Path.GetFullPath(src); }
            catch { full = src; }

            string rel = full.Length <= 3 ? "" : full.Substring(3).Trim('\\');
            if (rel.Length == 0)
            {
                err = "不能刪除整個雲端硬碟（拖進來的是磁碟根目錄）。";
                return false;
            }

            string relRemote = rel.Replace('\\', '/');
            bool isDir = Directory.Exists(src);

            Plan p = new Plan();
            p.Source = src;
            p.Name = Path.GetFileName(rel.TrimEnd('\\', '/'));
            p.IsDir = isDir;
            p.Verb = isDir ? "purge" : "deletefile";
            p.From = remote + "/" + relRemote;
            p.To = "";
            p.RefreshDir = ParentRemoteDir(relRemote);
            p.Direction = "刪除";
            p.Describe = (isDir ? "資料夾  " : "檔案　  ") + p.From;
            plan = p;
            return true;
        }

        private bool ConfirmDelete(List<Plan> plans)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("即將從雲端永久刪除以下 ").Append(plans.Count).Append(" 個項目：\r\n\r\n");
            int n = 0;
            foreach (Plan p in plans)
            {
                n++;
                if (n <= 8) sb.Append("　• ").Append(p.From).Append("\r\n");
            }
            if (plans.Count > 8) sb.Append("　…等共 ").Append(plans.Count).Append(" 項\r\n");
            sb.Append("\r\n[!] 這不會經過 Windows 資源回收筒，無法還原。\r\n\r\n確定要刪除嗎？");

            string msg = sb.ToString();
            return UiSync(delegate
            {
                return MessageBox.Show(this, msg, "確認刪除", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            });
        }
#endif

        // ------------------------------------------------------- 小工具
        private static string NormRemote(string r)
        {
            string t = (r ?? "").Trim().Trim('"').Trim();
            if (t.Length == 0) return t;
            if (!t.EndsWith(":")) t = t + ":";
            return t;
        }

        private static string CleanRemoteDir(string s)
        {
            if (s == null) return "";
            string t = s.Trim().Replace('\\', '/').Trim('/');
            while (t.IndexOf("//", StringComparison.Ordinal) >= 0) t = t.Replace("//", "/");
            return t;
        }

        private static string Expand(string s)
        {
            if (s == null) return "";
            try { return Environment.ExpandEnvironmentVariables(s); }
            catch { return s; }
        }

        private static string FmtDrives(string s)
        {
            HashSet<char> set = Config.ParseDrives(s);
            if (set.Count == 0) return "（未設定）";
            List<char> list = new List<char>(set);
            list.Sort();
            List<string> parts = new List<string>();
            foreach (char c in list) parts.Add(c + ":");
            return string.Join(" ", parts.ToArray());
        }

        /// 依 Windows 命令列規則跳脫並加上引號
        internal static string Q(string s)
        {
            if (s == null) return "\"\"";
            bool need = s.Length == 0;
            foreach (char c in s)
            {
                if (c == ' ' || c == '\t' || c == '"' || c == '&' || c == '(' || c == ')' ||
                    c == '^' || c == '|' || c == '<' || c == '>') { need = true; break; }
            }
            if (!need) return s;

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int bs = 0;
            foreach (char c in s)
            {
                if (c == '\\') { bs++; sb.Append(c); }
                else if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; }
                else { bs = 0; sb.Append(c); }
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------- UI 執行緒與輸出
        private void Ui(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private T UiSync<T>(Func<T> f)
        {
            if (IsDisposed || !IsHandleCreated) return default(T);
            if (!InvokeRequired) return f();
            object res = null;
            try { Invoke((MethodInvoker)delegate { res = f(); }); }
            catch (ObjectDisposedException) { return default(T); }
            catch (InvalidOperationException) { return default(T); }
            return (T)res;
        }

        public void Write(string text, OutKind kind)
        {
            Ui(delegate { WriteNow(text, kind); });
        }

        public void WriteProgress(string text)
        {
            Ui(delegate { WriteNow(text, OutKind.Progress); });
        }

        private static Color ColorFor(OutKind k)
        {
            switch (k)
            {
                case OutKind.Head: return Color.FromArgb(130, 190, 255);
                case OutKind.Ok: return Color.FromArgb(126, 217, 158);
                case OutKind.Warn: return Color.FromArgb(240, 200, 90);
                case OutKind.Err: return Color.FromArgb(255, 120, 110);
                case OutKind.Cmd: return Color.FromArgb(150, 150, 158);
                case OutKind.Dim: return Color.FromArgb(128, 128, 134);
                case OutKind.Progress: return Color.FromArgb(110, 215, 215);
                default: return CText;
            }
        }

        private void WriteNow(string text, OutKind kind)
        {
            if (_console.IsDisposed) return;
            if (text == null) text = "";

            TrimIfNeeded();

            bool progress = (kind == OutKind.Progress);
            Color c = ColorFor(kind);

            if (progress)
            {
                if (_progressStart >= 0 && _progressStart <= _console.TextLength)
                {
                    // 原地更新：選取舊的進度列直接覆蓋（不能再經過 AppendRaw，它會把選取範圍重設到尾端）
                    _console.Select(_progressStart, _console.TextLength - _progressStart);
                    _console.SelectionFont = Theme.NeedsCjk(text) ? _fontCjk : _fontAscii;
                    _console.SelectionColor = c;
                    _console.SelectedText = text;
                    _console.SelectionColor = CText;
                    _console.SelectionStart = _console.TextLength;
                    _console.SelectionLength = 0;
                    _console.ScrollToCaret();
                    return;
                }
                if (_console.TextLength > 0) AppendRaw("\r\n", CText);
                _progressStart = _console.TextLength;
            }
            else
            {
                if (_progressStart >= 0) { AppendRaw("\r\n", CText); _progressStart = -1; }
            }

            AppendRaw(text, c);
            if (!progress) AppendRaw("\r\n", CText);

            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
            _console.ScrollToCaret();
        }

        private void AppendRaw(string s, Color c)
        {
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
            _console.SelectionFont = Theme.NeedsCjk(s) ? _fontCjk : _fontAscii;
            _console.SelectionColor = c;
            _console.SelectedText = s;
            _console.SelectionColor = CText;
        }

        // 長時間傳輸時避免 RichTextBox 愈來愈慢
        private void TrimIfNeeded()
        {
            if (_console.TextLength < 240000) return;
            int cut = _console.TextLength - 120000;
            int nl = _console.Text.IndexOf('\n', cut);
            if (nl <= 0) return;
            _console.Select(0, nl + 1);
            _console.SelectedText = "";
            _progressStart = -1;
        }
    }
}
