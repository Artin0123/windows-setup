// yt-dlp-downloader.cs — 單檔 WinForms yt-dlp 圖形介面（.NET Framework 4.x，C# 5 可編）
//
// 編譯：把本檔拖到 csc-build.cmd 上，或  csc-build.cmd /nopause yt-dlp-downloader.cs
//
// 功能：
//   · 影片網址（單行）；「批量輸入」可貼多個網址（一行一個，按完成才會記住）
//   · 參數列可直接編輯，「儲存」把目前參數存成「參數管理」裡的項目
//   · 下載路徑（預設 %USERPROFILE%\Downloads）與「開啟資料夾」
//   · 分頁「格式列表」：執行 yt-dlp --list-formats 後以表格顯示，點一下套用 / 再點取消，
//     影像 + 音訊可疊加（以 + 連接寫入 -f，-f 永遠排在參數最前面）；同類型只是切換
//   · 分頁「參數管理」：參數項目表格（一次只能選一個），可新增 / 編輯 / 刪除
//   · 分頁「設定」：開啟設定檔位置（檔案總管選取 yt-dlp-downloader.ini）、
//     yt-dlp / ffmpeg / aria2c 偵測狀態（PATH 或 ini 指定路徑）、aria2c 多線程加速開關
//   · 依賴「按需禁用」：沒有 yt-dlp 才禁用下載 / 列出格式；沒有 ffmpeg 只在需要合併或轉檔時提醒
//
// ini（與 exe 同目錄，第一次執行自動產生）：
//   uiFont / uiFontSize / ytdlp / ffmpeg / aria2c / outputDir / defaultArgs / aria2Enabled / aria2Args
//   option = 顯示名稱 | 參數字串      （可有多行；舊版 "| 0/1 | 0/1" 尾巴與 profile 行仍可讀入）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace YtDlpDownloader
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            // 編譯輔助：yt-dlp-downloader.exe --make-ico out.ico（csc-build.cmd 用來產生 exe 圖示）
            if (args != null && args.Length == 2 && args[0] == "--make-ico")
            {
                try { AppIcon.WriteIco(args[1]); } catch { }
                return;
            }

            // 不宣告 DPI 感知的話，Windows 會把整個視窗當點陣圖放大 → 文字模糊
            try { SetProcessDPIAware(); }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ------------------------------------------------------------------ 主題色
    internal static class Theme
    {
        public static readonly Color Back = Color.FromArgb(30, 31, 36);
        public static readonly Color Card = Color.FromArgb(41, 42, 49);
        public static readonly Color Input = Color.FromArgb(24, 25, 29);
        public static readonly Color Border = Color.FromArgb(66, 68, 78);
        public static readonly Color Accent = Color.FromArgb(76, 134, 232);
        public static readonly Color Btn = Color.FromArgb(62, 64, 74);
        public static readonly Color Text = Color.FromArgb(230, 231, 235);
        public static readonly Color Dim = Color.FromArgb(150, 153, 162);
        public static readonly Color Good = Color.FromArgb(110, 200, 130);
        public static readonly Color Bad = Color.FromArgb(240, 100, 100);

        public static readonly float Scale = GetScale();
        private static float GetScale()
        {
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f); }
            catch { return 1f; }
        }
        public static int S(int v) { return (int)Math.Round(v * Scale); }

        private static int _corner;
        public static int Corner
        {
            get { return _corner > 0 ? _corner : S(13); }
            set { _corner = value; }
        }

        public static Color BackBehind(Control c)
        {
            for (Control p = c.Parent; p != null; p = p.Parent)
                if (p.BackColor.A == 255) return p.BackColor;
            return Back;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Max(2f, Math.Min(radius * 2f, Math.Min(r.Width, r.Height)));
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ------------------------------------------------------------------ 資料類別
    internal sealed class ArgOption
    {
        public string Name = "";
        public string Args = "";

        // 正規化後前後加空白的比對用字串（Args 沒變就不重算，表格重繪時不必每格都跑一次）
        private string _src, _key;
        public string Key
        {
            get
            {
                if (_src != Args) { _src = Args; _key = " " + ArgsText.Norm(Args) + " "; }
                return _key;
            }
        }
    }

    internal sealed class FormatItem
    {
        public string Code;        // format id
        public string[] Cells;     // 表格各欄
        public int Kind;           // 0 = 影音合一, 1 = 僅影像, 2 = 僅音訊
        public FormatItem(string code, string[] cells) { Code = code; Cells = cells; }
    }

    // ------------------------------------------------------------------ 設定檔
    internal sealed class Config
    {
        public const string FileName = "yt-dlp-downloader.ini";

        public string FilePath = "";
        public string ExeDir = "";
        public string UiFont = "Microsoft YaHei UI";
        public float UiFontSize = 11f;
        public string YtDlp = "";
        public string Ffmpeg = "";
        public string Aria2c = "";
        public string OutputDir = "";
        public string DefaultArgs = "";
        public const string DefaultAria2Args = "--downloader aria2c --downloader-args \"aria2c:-x 16 -s 16 -k 1M\"";
        public string Aria2Args = DefaultAria2Args;
        public bool Aria2Enabled = true;
        public List<ArgOption> Options = new List<ArgOption>();

        private const string Template =
@"; yt-dlp 下載器設定檔
; 格式：key = value　　以 ; 或 # 開頭的整行是註解

; 介面字型與大小
uiFont = Microsoft YaHei UI
uiFontSize = 11

; 工具路徑（留空 = 從 PATH 尋找）。可填 exe 完整路徑或所在資料夾
ytdlp =
ffmpeg =
aria2c =

; 下載資料夾（留空 = %USERPROFILE%\Downloads；相對路徑以 exe 目錄為基準）
outputDir =

; 啟動時參數列的初始內容
defaultArgs =

; aria2c 下載加速（偵測到 aria2c 時預設啟用，可在「設定」分頁開關）
aria2Enabled = 1
aria2Args = --downloader aria2c --downloader-args ""aria2c:-x 16 -s 16 -k 1M""

; 參數管理  option = 顯示名稱 | 參數字串
option = 僅最佳音訊 | -f bestaudio
option = 1080p 視訊 + 最佳音訊 | bestvideo[format_note*=1080p]+bestaudio
option = 1080p mp4 視訊 + 最佳音訊 | bestvideo[format_note*=1080p][ext=mp4]+bestaudio
option = 下載縮圖 | --write-thumbnail --skip-download
option = 下載所有字幕 | --write-all-subs --skip-download
";

        public static Config Load(string exeDir)
        {
            Config c = new Config();
            c.ExeDir = exeDir;
            try { c.FilePath = Path.Combine(exeDir, FileName); }
            catch { c.FilePath = FileName; }

            if (!File.Exists(c.FilePath))
            {
                try { File.WriteAllText(c.FilePath, Template.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true)); }
                catch { }
            }

            string raw;
            try { raw = File.Exists(c.FilePath) ? File.ReadAllText(c.FilePath, Encoding.UTF8) : Template; }
            catch { raw = Template; }
            if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw.Substring(1);

            string migrated = null;
            bool hasAria2Key = false;
            foreach (string line0 in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = line0.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();

                if (key == "option" || key == "profile")
                {
                    string[] p = val.Split('|');
                    if (p.Length < 2) continue;
                    string name = p[0].Trim();
                    int end = p.Length;
                    // 舊格式：名稱 | 參數 | 0/1 | 0/1
                    if (key == "option" && end >= 4 && (p[end - 1].Trim() == "0" || p[end - 1].Trim() == "1")
                        && (p[end - 2].Trim() == "0" || p[end - 2].Trim() == "1")) end -= 2;
                    string args = string.Join("|", p, 1, end - 1).Trim();
                    if (name.Length == 0 || args.Length == 0) continue;
                    // aria2c 選項已搬到「設定」分頁
                    if (args.IndexOf("aria2c", StringComparison.OrdinalIgnoreCase) >= 0) { migrated = args; continue; }
                    ArgOption o = new ArgOption();
                    o.Name = name; o.Args = args;
                    c.Options.Add(o);
                    continue;
                }

                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                    val = val.Substring(1, val.Length - 2);

                switch (key)
                {
                    case "uifont": if (val.Length > 0) c.UiFont = val; break;
                    case "uifontsize":
                        float f;
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f)
                            && f >= 8f && f <= 36f) c.UiFontSize = f;
                        break;
                    case "ytdlp": c.YtDlp = val; break;
                    case "ffmpeg": c.Ffmpeg = val; break;
                    case "aria2c": c.Aria2c = val; break;
                    case "outputdir": c.OutputDir = val; break;
                    case "defaultargs": c.DefaultArgs = val; break;
                    case "aria2enabled": c.Aria2Enabled = val != "0"; break;
                    case "aria2args": if (val.Length > 0) { c.Aria2Args = val; hasAria2Key = true; } break;
                }
            }
            if (!hasAria2Key && migrated != null) c.Aria2Args = migrated;
            return c;
        }

        // 只改寫 option / profile 行，其他內容（含註解）保持原樣
        public void Save() { lock (IoLock) { SaveCore(); } }

        private static readonly object IoLock = new object();

        private void SaveCore()
        {
            try
            {
                string text = File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : Template;
                text = Regex.Replace(text, @"^[ \t]*(option|profile)[ \t]*=.*(\r?\n|$)", "",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"^; (參數選項|已儲存的參數)[ \t]*(\r?\n|$)", "", RegexOptions.Multiline);
                StringBuilder sb = new StringBuilder(text.TrimEnd());
                sb.Append("\r\n\r\n; 參數選項\r\n");
                foreach (ArgOption o in Options)
                {
                    // 名稱含 | 或換行會破壞 ini 格式
                    string name = o.Name.Replace('|', '\uFF5C').Replace("\r", " ").Replace("\n", " ");
                    sb.Append("option = " + name + " | " + o.Args.Replace("\r", " ").Replace("\n", " ") + "\r\n");
                }
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                MessageBox.Show("儲存設定檔失敗：\r\n" + ex.Message, "yt-dlp 下載器");
            }
        }

        // 只更新單一 key = value
        public void SaveScalar(string key, string value) { lock (IoLock) { SaveScalarCore(key, value); } }

        private void SaveScalarCore(string key, string value)
        {
            try
            {
                string text = File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : Template;
                string line = key + " = " + value;
                Regex re = new Regex(@"^[ \t]*" + Regex.Escape(key) + @"[ \t]*=.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                text = re.IsMatch(text) ? re.Replace(text, delegate(Match m) { return line; }, 1)
                                        : text.TrimEnd() + "\r\n" + line + "\r\n";
                File.WriteAllText(FilePath, text, new UTF8Encoding(true));
            }
            catch { }
        }

        public Font MakeUiFont(float scale, FontStyle style)
        {
            float size = Math.Max(8f, UiFontSize * scale);
            try { return new Font(string.IsNullOrEmpty(UiFont) ? "Microsoft YaHei UI" : UiFont, size, style); }
            catch
            {
                try { return new Font("Microsoft YaHei UI", size, style); }
                catch { return new Font(FontFamily.GenericSansSerif, size, style); }
            }
        }

        public string ResolveOutputDir()
        {
            string s = (OutputDir ?? "").Trim();
            if (s.Length == 0) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            s = Environment.ExpandEnvironmentVariables(s);
            try { return Path.GetFullPath(Path.Combine(ExeDir, s)); }
            catch { return s; }
        }
    }

    // ------------------------------------------------------------------ 外部工具偵測 / 執行
    internal sealed class ToolInfo
    {
        public bool Found;
        public string Exe = "";       // 實際使用的執行檔（可能只是名稱）
        public string Version = "";
    }

    internal static class Tools
    {
        // ini 有指定且存在就用 ini；否則只用名稱（交給 PATH）
        public static string Resolve(string name, string configured)
        {
            string c = (configured ?? "").Trim();
            if (c.Length > 0)
            {
                c = Environment.ExpandEnvironmentVariables(c);
                try
                {
                    if (File.Exists(c)) return c;
                    if (Directory.Exists(c))
                    {
                        string p = Path.Combine(c, name + ".exe");
                        if (File.Exists(p)) return p;
                    }
                }
                catch { }
            }
            return name;
        }

        public static ToolInfo Detect(string name, string configured, string versionArg)
        {
            ToolInfo t = new ToolInfo();
            t.Exe = Resolve(name, configured);
            string so, se; int code;
            if (Run(t.Exe, versionArg, 12000, out so, out se, out code))
            {
                t.Found = true;
                string first = "";
                foreach (string l in (so + "\n" + se).Replace("\r", "").Split('\n'))
                    if (l.Trim().Length > 0) { first = l.Trim(); break; }
                Match m = Regex.Match(first, @"version\s+(\S+)", RegexOptions.IgnoreCase);
                t.Version = m.Success ? m.Groups[1].Value : first;
                if (t.Version.Length > 40) t.Version = t.Version.Substring(0, 40);
            }
            return t;
        }

        // 回傳 false = 無法啟動；其餘（含非 0 結束碼）視為可啟動
        public static bool Run(string exe, string args, int timeoutMs, out string stdout, out string stderr, out int exitCode)
        {
            stdout = ""; stderr = ""; exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";

                MemoryStream mo = new MemoryStream(), me = new MemoryStream();
                using (Process p = Process.Start(psi))
                {
                    Thread to = Pump(p.StandardOutput.BaseStream, mo);
                    Thread te = Pump(p.StandardError.BaseStream, me);
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                    }
                    p.WaitForExit();
                    to.Join(3000); te.Join(3000);
                    exitCode = p.ExitCode;
                }
                lock (mo) stdout = Decode(mo.ToArray());
                lock (me) stderr = Decode(me.ToArray());
                return true;
            }
            catch (Exception) { return false; }   // 找不到檔案、權限、被防毒擋下…都視為「無法啟動」
        }

        private static Thread Pump(Stream src, MemoryStream dst)
        {
            Thread t = new Thread(delegate()
            {
                byte[] buf = new byte[8192];
                int n;
                try { while ((n = src.Read(buf, 0, buf.Length)) > 0) lock (dst) dst.Write(buf, 0, n); }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
            return t;
        }

        // 先試 UTF-8；失敗就改用系統 ANSI 碼頁（打包版 yt-dlp 在管線輸出時常用它，否則會出現 � 亂碼）
        private static string Decode(byte[] b)
        {
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(b); }
        }
    }

    // ------------------------------------------------------------------ 參數字串處理
    internal static class ArgsText
    {
        private static readonly Regex FmtRe = new Regex(
            "(?<=^|\\s)(?:-f|--format)(?:\\s+|=)(?:\"[^\"]*\"|'[^']*'|\\S+)", RegexOptions.Compiled);
        private static readonly Regex OutRe = new Regex(
            "(?<=^|\\s)(?:-P|--paths|-o|--output)(?:\\s|=)", RegexOptions.Compiled);

        // 連續空白（含換行 / Tab）合併成一個空格並去頭尾
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            bool sp = false;
            foreach (char ch in s)
            {
                if (char.IsWhiteSpace(ch)) { sp = true; continue; }
                if (sp && sb.Length > 0) sb.Append(' ');
                sp = false;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        public static bool Has(string args, string opt)
        {
            string o = Norm(opt);
            if (o.Length == 0) return false;
            return (" " + Norm(args) + " ").Contains(" " + o + " ");
        }

        public static string Add(string args, string opt)
        {
            if (Has(args, opt)) return Norm(args);
            return Norm(args + " " + opt);
        }

        public static string Remove(string args, string opt)
        {
            string o = " " + Norm(opt) + " ";
            if (o.Trim().Length == 0) return Norm(args);
            string a = " " + Norm(args) + " ";
            int i = a.IndexOf(o, StringComparison.Ordinal);
            while (i >= 0)
            {
                a = a.Substring(0, i) + " " + a.Substring(i + o.Length);
                i = a.IndexOf(o, StringComparison.Ordinal);
            }
            return Norm(a);
        }

        public static string GetFormat(string args)
        {
            Match m = FmtRe.Match(args ?? "");
            if (!m.Success) return "";
            string v = Regex.Replace(m.Value, @"^(?:-f|--format)(?:\s+|=)", "");
            return v.Trim('"', '\'');
        }

        // code 為空 → 移除 -f；否則取代或附加
        public static string SetFormat(string args, string code)
        {
            args = args ?? "";
            string rep = "";
            if (!string.IsNullOrEmpty(code))
            {
                string c = Regex.IsMatch(code, "[\\s&<>|^]") ? "\"" + code + "\"" : code;
                rep = "-f " + c;
            }
            if (FmtRe.IsMatch(args))
            {
                string r = FmtRe.Replace(args, delegate(Match m) { return rep; }, 1);
                return MoveFormatFirst(r);
            }
            return rep.Length == 0 ? Norm(args) : Norm(rep + " " + args);
        }

        public static string MoveFormatFirst(string args)
        {
            args = args ?? "";
            Match m = FmtRe.Match(args);
            if (!m.Success) return Norm(args);
            return Norm(m.Value + " " + args.Remove(m.Index, m.Length));
        }

        public static bool HasOutputOption(string args) { return OutRe.IsMatch(args ?? ""); }

        // 結尾的反斜線要加倍，否則 D:\ 會把收尾的引號跳脫掉
        public static string Quote(string s)
        {
            s = (s ?? "").Replace("\"", "\\\"");
            int n = 0;
            while (n < s.Length && s[s.Length - 1 - n] == '\\') n++;
            return "\"" + s + new string('\\', n) + "\"";
        }
    }

    // ------------------------------------------------------------------ 自繪控制項
    internal sealed class RoundButton : Button
    {
        private bool _over, _down;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Btn;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _over = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BackBehind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill = BackColor;
            Color fore = ForeColor;
            if (!Enabled)
            {
                fill = Color.FromArgb(48, 49, 56);
                fore = Color.FromArgb(105, 108, 116);
            }
            else if (_down) fill = ControlPaint.Dark(fill, 0.05f);
            else if (_over) fill = ControlPaint.Light(fill, 0.15f);

            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.Round(r, Theme.Corner))
            using (SolidBrush br = new SolidBrush(fill))
                g.FillPath(br, path);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class RoundBox : Panel
    {
        public readonly TextBox Inner = new TextBox();
        private readonly bool _multi;

        public RoundBox(bool multiline)
        {
            _multi = multiline;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Inner.BorderStyle = BorderStyle.None;
            Inner.BackColor = Theme.Input;
            Inner.ForeColor = Theme.Text;
            Inner.Multiline = multiline;
            if (multiline)
            {
                Inner.AcceptsReturn = true;
                Inner.ScrollBars = ScrollBars.Vertical;
                Inner.WordWrap = false;
            }
            Controls.Add(Inner);
            Inner.Enter += delegate { Invalidate(); };
            Inner.Leave += delegate { Invalidate(); };
            Inner.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.A) { Inner.SelectAll(); e.SuppressKeyPress = true; }
            };
            Click += delegate { Inner.Focus(); };
        }

        private void DoInnerLayout()
        {
            if (_multi) Inner.SetBounds(12, 9, Math.Max(10, Width - 24), Math.Max(10, Height - 18));
            else
            {
                Inner.Width = Math.Max(10, Width - 24);
                Inner.Location = new Point(12, Math.Max(0, (Height - Inner.Height) / 2));
            }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); DoInnerLayout(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); DoInnerLayout(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BackBehind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath path = Theme.Round(r, Theme.Corner))
            using (SolidBrush br = new SolidBrush(Theme.Input))
            using (Pen pen = new Pen(Inner.Focused ? Theme.Accent : Theme.Border, 1.4f))
            {
                g.FillPath(br, path);
                g.DrawPath(pen, path);
            }
        }
    }

    // ------------------------------------------------------------------ 對話框
    internal class DarkDialog : Form
    {
        protected int U;   // 字高

        protected DarkDialog(Font font, string title)
        {
            Font = font;
            Text = title;
            U = font.Height;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            ShowIcon = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
        }

        protected Label MakeLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text; l.AutoSize = false;
            l.SetBounds(x, y, w, U + 4);
            l.ForeColor = Theme.Text; l.BackColor = Color.Transparent;
            Controls.Add(l);
            return l;
        }

        protected RoundButton MakeButton(string text, int x, int y, int w, int h, DialogResult dr)
        {
            RoundButton b = new RoundButton();
            b.Text = text; b.SetBounds(x, y, w, h); b.DialogResult = dr;
            Controls.Add(b);
            return b;
        }
    }

    internal sealed class PromptDialog : DarkDialog
    {
        private readonly RoundBox _box;

        private PromptDialog(Font font, string title, string label, string initial, bool multi) : base(font, title)
        {
            int w = Theme.S(multi ? 640 : 540), rowH = U + 18, boxH = multi ? U * 12 : rowH, btnH = rowH + 2;
            MakeLabel(label, 18, 16, w - 36);
            _box = new RoundBox(multi);
            _box.SetBounds(18, 16 + U + 12, w - 36, boxH);
            _box.Inner.Text = initial ?? "";
            Controls.Add(_box);
            int by = _box.Bottom + 16;
            RoundButton ok = MakeButton("完成", w - 18 - 90 * 2 - 10, by, 90, btnH, DialogResult.OK);
            ok.BackColor = Theme.Accent;
            RoundButton cancel = MakeButton("取消", w - 18 - 90, by, 90, btnH, DialogResult.Cancel);
            ClientSize = new Size(w, by + btnH + 16);
            AcceptButton = multi ? null : ok;
            CancelButton = cancel;
            Shown += delegate { _box.Inner.Focus(); _box.Inner.SelectAll(); };
        }

        public static string Ask(IWin32Window owner, Font font, string title, string label, string initial, bool multi)
        {
            using (PromptDialog d = new PromptDialog(font, title, label, initial, multi))
                return d.ShowDialog(owner) == DialogResult.OK ? d._box.Inner.Text : null;
        }
    }

    internal sealed class OptionDialog : DarkDialog
    {
        private readonly RoundBox _name, _args;

        private OptionDialog(Font font, ArgOption o) : base(font, o == null ? "新增參數" : "編輯參數")
        {
            int w = Theme.S(580), rowH = U + 18, btnH = rowH + 2, y = 16;
            MakeLabel("顯示名稱", 18, y, w - 36); y += U + 8;
            _name = new RoundBox(false); _name.SetBounds(18, y, w - 36, rowH); Controls.Add(_name); y += rowH + 12;
            MakeLabel("參數字串（例如 --no-playlist、-x --audio-format mp3）", 18, y, w - 36); y += U + 8;
            _args = new RoundBox(false); _args.SetBounds(18, y, w - 36, rowH); Controls.Add(_args); y += rowH + 18;

            RoundButton ok = MakeButton("確定", w - 18 - 90 * 2 - 10, y, 90, btnH, DialogResult.OK);
            ok.BackColor = Theme.Accent;
            RoundButton cancel = MakeButton("取消", w - 18 - 90, y, 90, btnH, DialogResult.Cancel);
            ClientSize = new Size(w, y + btnH + 16);
            AcceptButton = ok; CancelButton = cancel;

            if (o != null) { _name.Inner.Text = o.Name; _args.Inner.Text = o.Args; }
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (DialogResult == DialogResult.OK &&
                    (_name.Inner.Text.Trim().Length == 0 || _args.Inner.Text.Trim().Length == 0))
                {
                    MessageBox.Show(this, "顯示名稱與參數字串都要填寫。", Text);
                    e.Cancel = true;
                }
            };
            Shown += delegate { _name.Inner.Focus(); };
        }

        public static ArgOption Edit(IWin32Window owner, Font font, ArgOption existing)
        {
            using (OptionDialog d = new OptionDialog(font, existing))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                ArgOption o = new ArgOption();
                o.Name = d._name.Inner.Text.Trim();
                o.Args = ArgsText.Norm(d._args.Inner.Text);
                return o;
            }
        }
    }

    // ------------------------------------------------------------------ 表格清單
    // 無勾選框、每欄靠左；點一次 = 套用（高亮），再點 = 取消。內容超出寬度時底部出現水平捲軸（不會蓋到列）。
    internal sealed class GridList : ListView
    {
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string app, string idlist);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // 表頭不能調整欄寬，滑鼠停在分隔線上也不該變成左右箭頭
        private sealed class HeaderHook : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0020)   // WM_SETCURSOR
                {
                    Cursor.Current = Cursors.Arrow;
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
            }
        }
        private HeaderHook _hdrHook;

        public Func<object, bool> IsOn;
        public bool ShowCurrent;
        public int DimFromColumn = -1;
        public event Action<int> ItemClicked;
        private int[] _nat = new int[0];
        private static readonly Color OnFill = Color.FromArgb(56, 98, 178);
        private static readonly TextFormatFlags Fl = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        public GridList()
        {
            View = View.Details;
            FullRowSelect = true;
            MultiSelect = false;
            HideSelection = false;
            HeaderStyle = ColumnHeaderStyle.Nonclickable;
            OwnerDraw = true;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            Scrollable = true;
            DoubleBuffered = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { SetWindowTheme(Handle, "DarkMode_Explorer", null); } catch { }
            ApplyRowHeight();

            IntPtr hh = SendMessage(Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
            if (hh != IntPtr.Zero)
            {
                if (_hdrHook == null) _hdrHook = new HeaderHook();
                if (_hdrHook.Handle != IntPtr.Zero) _hdrHook.ReleaseHandle();
                _hdrHook.AssignHandle(hh);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_hdrHook != null && _hdrHook.Handle != IntPtr.Zero) _hdrHook.ReleaseHandle();
            base.OnHandleDestroyed(e);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            ApplyRowHeight();
        }

        private void ApplyRowHeight()
        {
            ImageList old = SmallImageList;
            ImageList il = new ImageList();
            il.ImageSize = new Size(1, Font.Height + Theme.S(12));
            SmallImageList = il;
            if (old != null) old.Dispose();
        }

        public void Load(string[] headers, List<string[]> rows, List<object> tags)
        {
            BeginUpdate();
            Items.Clear();
            Columns.Clear();
            foreach (string h in headers) Columns.Add(h, 100);
            _nat = new int[headers.Length];
            Size big = new Size(int.MaxValue, 100);
            TextFormatFlags mf = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            for (int c = 0; c < headers.Length; c++)
                _nat[c] = TextRenderer.MeasureText(headers[c], Font, big, mf).Width;
            for (int r = 0; r < rows.Count; r++)
            {
                ListViewItem it = new ListViewItem(rows[r][0]);
                for (int c = 1; c < headers.Length; c++) it.SubItems.Add(c < rows[r].Length ? rows[r][c] : "");
                it.Tag = tags[r];
                Items.Add(it);
                for (int c = 0; c < headers.Length; c++)
                {
                    string t = c < rows[r].Length ? rows[r][c] : "";
                    _nat[c] = Math.Max(_nat[c], TextRenderer.MeasureText(t, Font, big, mf).Width);
                }
            }
            FitColumns();
            EndUpdate();
            if (IsHandleCreated) BeginInvoke(new Action(FitColumns));
        }

        private int Avail()
        {
            int avail = ClientSize.Width;
            bool vsShown = IsHandleCreated && (GetWindowLong(Handle, -16) & 0x00200000) != 0;
            int rowH = Font.Height + Theme.S(12);
            if (!vsShown && (Items.Count + 1) * rowH > ClientSize.Height) avail -= SystemInformation.VerticalScrollBarWidth;
            return avail;
        }

        // 每欄寬度 = 該欄最長內容（含表頭）+ 左側縮排 + 四個空格
        private int Pad()
        {
            Size big = new Size(int.MaxValue, 100);
            TextFormatFlags mf = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int fourSpaces = TextRenderer.MeasureText("x    x", Font, big, mf).Width
                           - TextRenderer.MeasureText("xx", Font, big, mf).Width;
            return Theme.S(12) + fourSpaces;
        }

        // 最後一欄：內容寬與「補滿剩餘空間」兩者較大者 → 右側不會露出空白，內容太長則出現橫向捲軸
        private int LastWidth()
        {
            int last = Columns.Count - 1, others = 0;
            for (int c = 0; c < last; c++) others += Columns[c].Width;
            return Math.Max(_nat[last] + Pad(), Avail() - others);
        }

        private void FitColumns()
        {
            if (Columns.Count == 0 || _nat.Length != Columns.Count) return;
            int pad = Pad();
            for (int c = 0; c < Columns.Count; c++) Columns[c].Width = _nat[c] + pad;
            Columns[Columns.Count - 1].Width = LastWidth();
        }

        // 表頭的拖曳 / 雙擊調整直接在訊息層擋掉：欄寬一律由程式依內容決定
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x004E)   // WM_NOTIFY
            {
                int code = System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, IntPtr.Size * 2);
                // HDN_BEGINTRACKA/W、HDN_DIVIDERDBLCLICKA/W
                if (code == -306 || code == -326 || code == -305 || code == -325)
                {
                    m.Result = (IntPtr)1;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated && Columns.Count > 0) FitColumns();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && IsHandleCreated && Columns.Count > 0) FitColumns();
        }

        public object CurrentTag
        {
            get { return SelectedItems.Count > 0 ? SelectedItems[0].Tag : null; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;   // 右鍵不套用
            ListViewHitTestInfo h = HitTest(e.Location);
            if (h.Item != null)
            {
                h.Item.Selected = true;
                if (ItemClicked != null) ItemClicked(h.Item.Index);
            }
            Invalidate();
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (SolidBrush br = new SolidBrush(Color.FromArgb(34, 35, 41)))
                e.Graphics.FillRectangle(br, e.Bounds);
            using (Pen pen = new Pen(Theme.Border))
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            int p = Theme.S(12);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Font,
                new Rectangle(e.Bounds.X + p, e.Bounds.Y, Math.Max(4, e.Bounds.Width - p), e.Bounds.Height),
                Theme.Dim, Fl);
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            e.DrawDefault = false;
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            Rectangle b = e.Bounds;
            if (e.ColumnIndex == 0) b.Width = Columns[0].Width;
            bool on = IsOn != null && IsOn(e.Item.Tag);
            using (SolidBrush br = new SolidBrush(on ? OnFill : BackColor))
                e.Graphics.FillRectangle(br, b);
            if (ShowCurrent && e.Item.Selected)
                using (Pen pen = new Pen(on ? Theme.Accent : Theme.Border))
                {
                    e.Graphics.DrawLine(pen, b.Left, b.Top, b.Right, b.Top);
                    e.Graphics.DrawLine(pen, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
                }
            Color c = (DimFromColumn >= 0 && e.ColumnIndex >= DimFromColumn)
                ? (on ? Color.FromArgb(205, 218, 242) : Theme.Dim) : Theme.Text;
            int p = Theme.S(12);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font,
                new Rectangle(b.X + p, b.Y, Math.Max(4, b.Width - p), b.Height), c, Fl);
        }
    }

    // ------------------------------------------------------------------ 分頁按鈕（上半圓角）
    internal sealed class TabStrip : Control
    {
        public string[] Tabs = new string[0];
        private int _selected;
        public event EventHandler SelectedChanged;

        public int Selected
        {
            get { return _selected; }
            set { _selected = value; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); }
        }

        public TabStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        // 平均分配整個寬度，分頁之間沒有空隙
        private Rectangle RectOf(int i)
        {
            int n = Math.Max(1, Tabs.Length);
            int x0 = Width * i / n, x1 = Width * (i + 1) / n;
            return new Rectangle(x0, 0, x1 - x0, Height);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (int i = 0; i < Tabs.Length; i++)
                if (RectOf(i).Contains(e.Location)) { if (i != _selected) Selected = i; return; }
        }

        private void DrawTab(Graphics g, int i, bool sel)
        {
            Rectangle r = RectOf(i);
            float d = Theme.Corner * 2f;
            using (GraphicsPath p = new GraphicsPath())
            using (SolidBrush br = new SolidBrush(sel ? Theme.Card : Color.FromArgb(50, 51, 59)))
            {
                p.StartFigure();
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
                p.CloseFigure();
                g.FillPath(br, p);
            }
            TextRenderer.DrawText(g, Tabs[i], Font, r, sel ? Theme.Text : Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BackBehind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < Tabs.Length; i++) if (i != _selected) DrawTab(g, i, false);
            if (_selected >= 0 && _selected < Tabs.Length) DrawTab(g, _selected, true);
        }
    }

    internal sealed class ContentPanel : Panel
    {
        public bool SquareTopLeft = true;
        public bool SquareTopRight;

        public ContentPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0, 0, Width, Height);
            using (GraphicsPath p = Theme.Round(r, Theme.Corner))
            using (SolidBrush br = new SolidBrush(Theme.Card))
            {
                g.FillPath(br, p);
                if (SquareTopLeft) g.FillRectangle(br, 0, 0, Theme.Corner + 2, Theme.Corner + 2);
                if (SquareTopRight) g.FillRectangle(br, Width - Theme.Corner - 2, 0, Theme.Corner + 2, Theme.Corner + 2);
            }
        }
    }

    // ------------------------------------------------------------------ 圓角深色勾選框
    internal sealed class RoundCheck : Control
    {
        public event EventHandler CheckedChanged;
        private bool _checked, _over;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                Update();   // 先把新狀態畫出來，再做後續（寫入參數等）工作
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public RoundCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // 快速連點也算兩次點擊
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (Enabled && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Checked = !Checked;
        }
        protected override void OnMouseEnter(EventArgs e) { _over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _over = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BackBehind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int side = Math.Max(Theme.S(18), Font.Height + 2);
            RectangleF box = new RectangleF(0.5f, (Height - side) / 2f, side - 1f, side - 1f);
            Color border = !Enabled ? Color.FromArgb(70, 72, 80) : (_checked || _over) ? Theme.Accent : Theme.Border;
            using (GraphicsPath p = Theme.Round(box, 6))
            using (SolidBrush br = new SolidBrush(_checked ? (Enabled ? Theme.Accent : Color.FromArgb(70, 72, 80)) : Theme.Input))
            using (Pen pen = new Pen(border, 1.4f))
            {
                g.FillPath(br, p);
                g.DrawPath(pen, p);
            }
            if (_checked)
                using (Pen tick = new Pen(Color.White, Math.Max(2f, side / 8f)))
                {
                    tick.StartCap = LineCap.Round; tick.EndCap = LineCap.Round; tick.LineJoin = LineJoin.Round;
                    float x = box.X, y = box.Y, w = box.Width, h = box.Height;
                    g.DrawLines(tick, new PointF[] {
                        new PointF(x + w * 0.24f, y + h * 0.52f),
                        new PointF(x + w * 0.43f, y + h * 0.71f),
                        new PointF(x + w * 0.77f, y + h * 0.31f) });
                }
            Rectangle tr = new Rectangle(side + Theme.S(12), 0, Math.Max(10, Width - side - Theme.S(12)), Height);
            TextRenderer.DrawText(g, Text, Font, tr, Enabled ? Theme.Text : Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    // ------------------------------------------------------------------ 在總管中選取路徑（比 explorer /select 快）
    internal static class ShellReveal
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ILCreateFromPathW(string pszPath);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        public static void SelectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }

            IntPtr pidl = ILCreateFromPathW(full);
            if (pidl == IntPtr.Zero)
            {
                try
                {
                    if (File.Exists(full))
                        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + full + "\"", UseShellExecute = true });
                    else if (Directory.Exists(full))
                        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + full + "\"", UseShellExecute = true });
                }
                catch { }
                return;
            }
            try { SHOpenFolderAndSelectItems(pidl, 0, null, 0); }
            finally { ILFree(pidl); }
        }
    }

    // ------------------------------------------------------------------ 主視窗
    internal sealed class MainForm : Form
    {
        private const string FormatHintIdle = "點擊此處列出可用格式\r\n（需先輸入影片網址）";

        private readonly Config _cfg;
        private readonly Label _lblUrl, _lblArgs, _lblPath, _lblFmtHint;
        private readonly StatusRow _lblDetYt, _lblDetFf, _lblDetAr;
        private readonly RoundBox _boxUrl, _boxArgs, _boxPath;
        private readonly RoundButton _btnBatch, _btnDownload, _btnSave, _btnOpenFolder, _btnAdd, _btnEdit, _btnDel, _btnOpenIni, _btnRedetect;
        private readonly RoundCheck _chkAria2;
        private readonly TabStrip _tabs;
        private readonly ContentPanel _panel;
        private readonly GridList _gridFormats, _gridOptions;

        private List<FormatItem> _allFormats = new List<FormatItem>();
        private List<FormatItem> _selFormats = new List<FormatItem>();
        private string[] _fmtHeaders = new string[0];
        private List<string> _batch;   // 非 null = 批量模式
        private ToolInfo _yt = new ToolInfo(), _ff = new ToolInfo(), _ar = new ToolInfo();
        private bool _detected, _aria2Applied, _listing, _fmtLoaded, _fmtDirty, _updating, _chkGuard;
        private int _detectSeq;
        private string _argsKey = " ";   // 目前參數列正規化後前後加空白，供清單重繪時比對

        public MainForm()
        {
            string exeDir;
            try { exeDir = Path.GetDirectoryName(Application.ExecutablePath); }
            catch { exeDir = Environment.CurrentDirectory; }
            _cfg = Config.Load(exeDir);

            AutoScaleMode = AutoScaleMode.None;
            Text = "yt-dlp 下載器";
            try { Icon = AppIcon.CreateWindowIcon(); } catch { }
            Font = _cfg.MakeUiFont(1f, FontStyle.Regular);
            BackColor = Theme.Back; ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;
            int lh = Font.Height, rowH = lh + Theme.S(18);
            Theme.Corner = Math.Max(Theme.S(8), rowH / 3);
            int needH = Theme.S(22) + 3 * (rowH + Theme.S(12)) + Theme.S(6) + rowH + lh * 14 + Theme.S(22);
            ClientSize = new Size(Math.Max(Theme.S(820), lh * 38), needH);
            MinimumSize = new Size(Size.Width - Theme.S(60), Size.Height - Theme.S(80));

            _lblUrl = MakeLabel("影片");
            _boxUrl = new RoundBox(false);
            _btnBatch = MakeBtn("批量輸入", Theme.Btn);
            _btnDownload = MakeBtn("下載", Theme.Accent);

            _lblArgs = MakeLabel("參數");
            _boxArgs = new RoundBox(false);
            _btnSave = MakeBtn("儲存", Theme.Btn);

            _lblPath = MakeLabel("下載路徑");
            _boxPath = new RoundBox(false);
            _btnOpenFolder = MakeBtn("開啟資料夾", Theme.Btn);

            _tabs = new TabStrip();
            _tabs.Tabs = new string[] { "格式列表", "參數管理", "設定" };
            _panel = new ContentPanel();

            _btnAdd = MakeBtn("新增", Theme.Btn);
            _btnEdit = MakeBtn("編輯", Theme.Btn);
            _btnDel = MakeBtn("刪除", Theme.Btn);
            _btnOpenIni = MakeBtn("開啟設定檔位置", Theme.Btn);
            _btnRedetect = MakeBtn("重新偵測", Theme.Btn);

            _lblFmtHint = new Label();
            _lblFmtHint.AutoSize = false;
            _lblFmtHint.BackColor = Theme.Card; _lblFmtHint.ForeColor = Theme.Dim;
            _lblFmtHint.TextAlign = ContentAlignment.MiddleCenter;
            _lblFmtHint.Cursor = Cursors.Hand;

            _lblDetYt = new StatusRow("yt-dlp");
            _lblDetFf = new StatusRow("ffmpeg");
            _lblDetAr = new StatusRow("aria2c");
            _chkAria2 = new RoundCheck();

            _gridFormats = new GridList();
            _gridFormats.IsOn = delegate(object o) { return _selFormats.Contains(o as FormatItem); };
            _gridOptions = new GridList();
            _gridOptions.ShowCurrent = true;
            _gridOptions.DimFromColumn = 1;
            _gridOptions.IsOn = delegate(object o) { ArgOption a = o as ArgOption; return a != null && _argsKey.Contains(a.Key); };

            _panel.Controls.Add(_btnAdd); _panel.Controls.Add(_btnEdit); _panel.Controls.Add(_btnDel);
            _panel.Controls.Add(_btnOpenIni); _panel.Controls.Add(_btnRedetect);
            _panel.Controls.Add(_lblDetYt); _panel.Controls.Add(_lblDetFf); _panel.Controls.Add(_lblDetAr);
            _panel.Controls.Add(_chkAria2);
            _panel.Controls.Add(_lblFmtHint); _panel.Controls.Add(_gridFormats); _panel.Controls.Add(_gridOptions);

            Controls.Add(_boxUrl); Controls.Add(_btnBatch); Controls.Add(_btnDownload);
            Controls.Add(_boxArgs); Controls.Add(_btnSave);
            Controls.Add(_boxPath); Controls.Add(_btnOpenFolder);
            Controls.Add(_tabs); Controls.Add(_panel);

            ToolTip tip = new ToolTip();
            tip.SetToolTip(_btnSave, "把目前參數存成「參數管理」裡的項目");
            tip.SetToolTip(_btnBatch, "貼上多個網址（一行一個）");

            _boxPath.Inner.Text = _cfg.ResolveOutputDir();

            string initArgs = ArgsText.Norm(_cfg.DefaultArgs);
            _boxArgs.Inner.Text = initArgs;
            _argsKey = " " + initArgs + " ";

            RefillOptions();
            RefreshFormatList();
            UpdateDetectLabels();
            ShowTab(0);
            UpdateEnabled();

            _boxArgs.Inner.TextChanged += delegate { OnArgsChanged(); };
            _boxUrl.Inner.TextChanged += delegate { if (_batch == null) ResetFormats(); };
            _boxUrl.Inner.Click += delegate { if (_batch != null) OpenBatch(); };
            _boxPath.Inner.Leave += delegate { SavePath(); };
            _btnBatch.Click += delegate { OpenBatch(); };
            _btnSave.Click += OnSaveArgs;
            _btnAdd.Click += OnAddOption;
            _btnEdit.Click += OnEditOption;
            _btnDel.Click += OnDeleteOption;
            _btnDownload.Click += OnDownload;
            _btnOpenFolder.Click += OnOpenFolder;
            _btnOpenIni.Click += OnOpenIni;
            _btnRedetect.Click += delegate { StartDetect(); };
            _chkAria2.CheckedChanged += OnAria2Toggled;
            _lblFmtHint.Click += delegate { StartListFormats(); };
            _gridFormats.ItemClicked += OnFormatClicked;
            _gridOptions.ItemClicked += OnOptionClicked;
            _tabs.SelectedChanged += delegate { ShowTab(_tabs.Selected); };
            Resize += delegate { DoLayout(); };
            Shown += delegate { DoLayout(); StartDetect(); };
            DoLayout();
        }

        // -------------------------------------------------------------- 建構輔助
        private Label MakeLabel(string text)
        {
            Label l = new Label();
            l.Text = text; l.AutoSize = false; l.BackColor = Color.Transparent;
            l.ForeColor = Theme.Text; l.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(l);
            return l;
        }

        private RoundButton MakeBtn(string text, Color back)
        {
            RoundButton b = new RoundButton();
            b.Text = text; b.BackColor = back;
            return b;
        }

        // -------------------------------------------------------------- 版面
        private void DoLayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            int pad = Theme.S(22), gap = Theme.S(12);
            int lh = Font.Height;
            int rowH = lh + Theme.S(18);
            int labW = TextRenderer.MeasureText("下載路徑", Font).Width + Theme.S(18);
            int rightW = Math.Max(Theme.S(110), TextRenderer.MeasureText("開啟資料夾", Font).Width + Theme.S(36));
            int batchW = TextRenderer.MeasureText("批量輸入", Font).Width + Theme.S(36);
            int rightEdge = W - pad;
            int xIn = pad + labW;

            int y = pad;
            _lblUrl.SetBounds(pad, y, labW, rowH);
            _btnDownload.SetBounds(rightEdge - rightW, y, rightW, rowH);
            _btnBatch.SetBounds(rightEdge - rightW - gap - batchW, y, batchW, rowH);
            _boxUrl.SetBounds(xIn, y, _btnBatch.Left - gap - xIn, rowH);
            y += rowH + gap;

            _lblArgs.SetBounds(pad, y, labW, rowH);
            _btnSave.SetBounds(rightEdge - rightW, y, rightW, rowH);
            _boxArgs.SetBounds(xIn, y, _btnSave.Left - gap - xIn, rowH);
            y += rowH + gap;

            _lblPath.SetBounds(pad, y, labW, rowH);
            _btnOpenFolder.SetBounds(rightEdge - rightW, y, rightW, rowH);
            _boxPath.SetBounds(xIn, y, _btnOpenFolder.Left - gap - xIn, rowH);
            y += rowH + Theme.S(18);

            int bottom = H - pad;   // 分頁一路延伸到與左右相同的下邊距

            _tabs.SetBounds(pad, y, W - pad * 2, rowH);
            int py = y + rowH - 1;
            _panel.SetBounds(pad, py, W - pad * 2, Math.Max(Theme.S(80), bottom - py));
            LayoutPanel();
        }

        // 讓 ListView 的下方圓角貼合面板
        private static void RoundRegion(Control c, bool tl, bool tr, bool br, bool bl, int rad)
        {
            if (c.Width < 4 || c.Height < 4) return;
            int d = rad * 2, w = c.Width, h = c.Height;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.StartFigure();
                if (tl) p.AddArc(0, 0, d, d, 180, 90); else p.AddLine(0, 0, 1, 0);
                if (tr) p.AddArc(w - d, 0, d, d, 270, 90); else p.AddLine(w - 1, 0, w, 0);
                if (br) p.AddArc(w - d, h - d, d, d, 0, 90); else p.AddLine(w, h - 1, w, h);
                if (bl) p.AddArc(0, h - d, d, d, 90, 90); else p.AddLine(1, h, 0, h);
                p.CloseFigure();
                Region old = c.Region;
                c.Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        private void LayoutPanel()
        {
            int pw = _panel.Width, ph = _panel.Height, in1 = Theme.S(18);
            int lh = Font.Height;
            int rowH = lh + Theme.S(14);
            int bg = Theme.S(10);
            int bw = Math.Max(Theme.S(84), TextRenderer.MeasureText("新增", Font).Width + Theme.S(40));
            _btnAdd.SetBounds(in1, in1, bw, rowH);
            _btnEdit.SetBounds(in1 + bw + bg, in1, bw, rowH);
            _btnDel.SetBounds(in1 + (bw + bg) * 2, in1, bw, rowH);

            // 表格直接填滿面板寬度與高度
            int top = _tabs.Selected == 1 ? in1 + rowH + Theme.S(14) : 0;
            Rectangle r = new Rectangle(0, top, pw, Math.Max(20, ph - top));
            _gridFormats.Bounds = r;
            _gridOptions.Bounds = r;
            _lblFmtHint.Bounds = r;
            RoundRegion(_gridFormats, false, true, true, true, Theme.Corner);
            RoundRegion(_gridOptions, false, false, true, true, Theme.Corner);

            // 設定分頁
            int iw = TextRenderer.MeasureText("開啟設定檔位置", Font).Width + Theme.S(44);
            int rw = TextRenderer.MeasureText("重新偵測", Font).Width + Theme.S(44);
            _btnOpenIni.SetBounds(in1, in1, iw, rowH);
            _btnRedetect.SetBounds(in1 + iw + bg, in1, rw, rowH);
            int y = in1 + rowH + Theme.S(22);
            int lineH = lh + Theme.S(12);
            _lblDetYt.SetBounds(in1, y, pw - in1 * 2, lineH); y += lineH;
            _lblDetFf.SetBounds(in1, y, pw - in1 * 2, lineH); y += lineH;
            _lblDetAr.SetBounds(in1, y, pw - in1 * 2, lineH); y += lineH + Theme.S(20);
            _chkAria2.SetBounds(in1, y, pw - in1 * 2, rowH);
        }

        private void ShowTab(int i)
        {
            _panel.SquareTopLeft = i == 0;
            _panel.SquareTopRight = i == 2;
            bool par = i == 1, set = i == 2;
            _btnAdd.Visible = _btnEdit.Visible = _btnDel.Visible = par;
            _gridOptions.Visible = par;
            _btnOpenIni.Visible = _btnRedetect.Visible = set;
            _lblDetYt.Visible = _lblDetFf.Visible = _lblDetAr.Visible = _chkAria2.Visible = set;
            RefreshFormatList();
            LayoutPanel();
            _panel.Invalidate();
        }

        // -------------------------------------------------------------- 依賴偵測
        private void StartDetect()
        {
            _lblDetYt.SetState(0, "偵測中…"); _lblDetFf.SetState(0, "偵測中…"); _lblDetAr.SetState(0, "偵測中…");
            string y = _cfg.YtDlp, f = _cfg.Ffmpeg, a = _cfg.Aria2c;
            int seq = ++_detectSeq;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ToolInfo ty = Tools.Detect("yt-dlp", y, "--version");
                ToolInfo tf = Tools.Detect("ffmpeg", f, "-version");
                ToolInfo ta = Tools.Detect("aria2c", a, "--version");
                try { BeginInvoke(new Action(delegate { if (seq == _detectSeq) ApplyDetect(ty, tf, ta); })); }
                catch { }
            });
        }

        private void ApplyDetect(ToolInfo ty, ToolInfo tf, ToolInfo ta)
        {
            _yt = ty; _ff = tf; _ar = ta; _detected = true;

            // 偵測到 aria2c：依設定預設啟用（只套用一次）
            if (_ar.Found && !_aria2Applied)
            {
                _aria2Applied = true;
                if (_cfg.Aria2Enabled) _boxArgs.Inner.Text = ArgsText.Add(_boxArgs.Inner.Text, _cfg.Aria2Args);
            }
            UpdateDetectLabels();
            UpdateEnabled();
        }

        private static void SetDet(StatusRow r, ToolInfo t)
        {
            if (t.Found) r.SetState(1, t.Version); else r.SetState(2, "未偵測到");
        }

        private void UpdateDetectLabels()
        {
            if (!_detected)
            {
                _lblDetYt.SetState(0, "偵測中…"); _lblDetFf.SetState(0, "偵測中…"); _lblDetAr.SetState(0, "偵測中…");
            }
            else
            {
                SetDet(_lblDetYt, _yt);
                SetDet(_lblDetFf, _ff);
                SetDet(_lblDetAr, _ar);
            }
            _chkGuard = true;
            _chkAria2.Enabled = _ar.Found;
            _chkAria2.Text = _ar.Found ? "aria2c 多線程加速（若無法下載，嘗試關閉此功能）" : "aria2c 多線程加速（未偵測到 aria2c）";
            _chkAria2.Checked = _ar.Found && ArgsText.Has(_boxArgs.Inner.Text, _cfg.Aria2Args);
            _chkGuard = false;
        }

        private void UpdateEnabled()
        {
            _btnDownload.Enabled = _yt.Found && _detected;
            RefreshFormatList();
        }

        private void OnArgsChanged()
        {
            _argsKey = " " + ArgsText.Norm(_boxArgs.Inner.Text) + " ";
            if (!_updating) SyncFormatsFromArgs();
            _gridOptions.Invalidate();
            _gridFormats.Invalidate();
            if (_ar.Found && !_chkGuard)
            {
                _chkGuard = true;
                _chkAria2.Checked = ArgsText.Has(_boxArgs.Inner.Text, _cfg.Aria2Args);
                _chkGuard = false;
            }
        }

        private void OnAria2Toggled(object sender, EventArgs e)
        {
            if (_chkGuard || !_ar.Found) return;
            string a = _boxArgs.Inner.Text;
            _chkGuard = true;
            _boxArgs.Inner.Text = _chkAria2.Checked ? ArgsText.Add(a, _cfg.Aria2Args) : ArgsText.Remove(a, _cfg.Aria2Args);
            _chkGuard = false;
            _cfg.Aria2Enabled = _chkAria2.Checked;
            string flag = _chkAria2.Checked ? "1" : "0";
            ThreadPool.QueueUserWorkItem(delegate { _cfg.SaveScalar("aria2Enabled", flag); });
        }

        private void OnOpenIni(object sender, EventArgs e)
        {
            try { ShellReveal.SelectPath(File.Exists(_cfg.FilePath) ? _cfg.FilePath : _cfg.ExeDir); }
            catch (Exception ex) { MessageBox.Show(this, "無法開啟檔案總管：\r\n" + ex.Message, Text); }
        }

        // -------------------------------------------------------------- 批量 / 網址
        private List<string> GetUrls()
        {
            if (_batch != null) return new List<string>(_batch);
            List<string> urls = new List<string>();
            string u = _boxUrl.Inner.Text.Trim();
            if (u.Length > 0) urls.Add(u);
            return urls;
        }

        private void OpenBatch()
        {
            string initial = _batch != null ? string.Join("\r\n", _batch.ToArray()) : _boxUrl.Inner.Text.Trim();
            string res = PromptDialog.Ask(this, Font, "批量輸入", "一行一個網址（按「完成」才會記住；清空後完成 = 取消批量模式）", initial, true);
            if (res == null) return;
            List<string> list = new List<string>();
            foreach (string l in res.Replace("\r", "").Split('\n'))
                if (l.Trim().Length > 0) list.Add(l.Trim());

            if (list.Count <= 1)
            {
                _batch = null;
                _boxUrl.Inner.ReadOnly = false;
                _boxUrl.Inner.ForeColor = Theme.Text;
                _boxUrl.Inner.Text = list.Count == 1 ? list[0] : "";
            }
            else
            {
                _batch = list;
                _boxUrl.Inner.ReadOnly = true;
                _boxUrl.Inner.ForeColor = Theme.Dim;
                _boxUrl.Inner.Text = "已設定為批量模式：共 " + list.Count + " 個網址（一行一個，點此編輯）";
            }
            ResetFormats();
        }

        // -------------------------------------------------------------- 下載路徑
        private string CurrentOutDir()
        {
            string s = Environment.ExpandEnvironmentVariables(_boxPath.Inner.Text.Trim());
            if (s.Length == 0) return _cfg.ResolveOutputDir();
            try { return Path.GetFullPath(Path.Combine(_cfg.ExeDir, s)); }
            catch { return s; }
        }

        private void SavePath()
        {
            string t = _boxPath.Inner.Text.Trim();
            if (t == _cfg.OutputDir) return;
            if (_cfg.OutputDir.Length == 0 && string.Equals(t, _cfg.ResolveOutputDir(), StringComparison.OrdinalIgnoreCase)) return;
            _cfg.OutputDir = t;
            _cfg.SaveScalar("outputDir", t);
        }

        // -------------------------------------------------------------- 參數管理（一次只能選一個）
        private void RefillOptions()
        {
            List<string[]> rows = new List<string[]>();
            List<object> tags = new List<object>();
            foreach (ArgOption o in _cfg.Options)
            {
                rows.Add(new string[] { o.Name, o.Args });
                tags.Add(o);
            }
            _gridOptions.Load(new string[] { "名稱", "參數" }, rows, tags);
        }

        private void OnOptionClicked(int i)
        {
            ArgOption o = _gridOptions.Items[i].Tag as ArgOption;
            if (o == null) return;
            string a = _boxArgs.Inner.Text;
            bool wasOn = ArgsText.Has(a, o.Args);
            foreach (ArgOption p in _cfg.Options)
                if (p != o && ArgsText.Has(a, p.Args)) a = ArgsText.Remove(a, p.Args);
            if (wasOn) a = ArgsText.Remove(a, o.Args);
            else
            {
                if (ArgsText.GetFormat(o.Args).Length > 0) a = ArgsText.SetFormat(a, "");
                a = ArgsText.MoveFormatFirst(ArgsText.Add(a, o.Args));
            }
            _boxArgs.Inner.Text = a;
        }

        private void OnSaveArgs(object sender, EventArgs e)
        {
            string args = ArgsText.Norm(ArgsText.Remove(_boxArgs.Inner.Text, _cfg.Aria2Args));
            if (args.Length == 0) { MessageBox.Show(this, "參數列是空的，沒有可儲存的內容。", Text); return; }
            string name = PromptDialog.Ask(this, Font, "儲存參數", "為這組參數取個名稱", "", false);
            if (name == null || name.Trim().Length == 0) return;
            name = name.Trim();
            ArgOption hit = null;
            foreach (ArgOption o in _cfg.Options)
                if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) { hit = o; break; }
            if (hit == null) { hit = new ArgOption(); hit.Name = name; _cfg.Options.Add(hit); }
            hit.Args = args;
            _cfg.Save();
            RefillOptions();
            _tabs.Selected = 1;
        }

        private void OnAddOption(object sender, EventArgs e)
        {
            ArgOption o = OptionDialog.Edit(this, Font, null);
            if (o == null) return;
            _cfg.Options.Add(o); _cfg.Save();
            RefillOptions();
        }

        private void OnEditOption(object sender, EventArgs e)
        {
            ArgOption old = _gridOptions.CurrentTag as ArgOption;
            if (old == null) { MessageBox.Show(this, "請先在清單中點選要編輯的項目。", Text); return; }
            ArgOption neu = OptionDialog.Edit(this, Font, old);
            if (neu == null) return;
            string a = _boxArgs.Inner.Text;
            if (ArgsText.Has(a, old.Args)) a = ArgsText.MoveFormatFirst(ArgsText.Add(ArgsText.Remove(a, old.Args), neu.Args));
            old.Name = neu.Name; old.Args = neu.Args;
            _cfg.Save();
            _boxArgs.Inner.Text = a;
            RefillOptions();
        }

        private void OnDeleteOption(object sender, EventArgs e)
        {
            ArgOption o = _gridOptions.CurrentTag as ArgOption;
            if (o == null) { MessageBox.Show(this, "請先在清單中點選要刪除的項目。", Text); return; }
            if (MessageBox.Show(this, "刪除「" + o.Name + "」？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _boxArgs.Inner.Text = ArgsText.Remove(_boxArgs.Inner.Text, o.Args);
            _cfg.Options.Remove(o); _cfg.Save();
            RefillOptions();
        }

        // -------------------------------------------------------------- 格式列表
        private void ResetFormats()
        {
            _fmtLoaded = false;
            _allFormats = new List<FormatItem>();
            _selFormats = new List<FormatItem>();
            RefreshFormatList();
        }

        private void RefreshFormatList()
        {
            if (_gridFormats == null || _tabs == null) return;
            if (_fmtLoaded && _fmtDirty)
            {
                _fmtDirty = false;
                List<string[]> rows = new List<string[]>();
                List<object> tags = new List<object>();
                foreach (FormatItem f in _allFormats) { rows.Add(f.Cells); tags.Add(f); }
                _gridFormats.Load(_fmtHeaders, rows, tags);
            }
            bool fmtTab = _tabs.Selected == 0;
            _gridFormats.Visible = fmtTab && _fmtLoaded;
            _lblFmtHint.Visible = fmtTab && !_fmtLoaded;
            if (_listing) _lblFmtHint.Text = "解析中…";
            else if (_detected && !_yt.Found) _lblFmtHint.Text = "未偵測到 yt-dlp";
            else _lblFmtHint.Text = FormatHintIdle;
            _lblFmtHint.Cursor = (_listing || (_detected && !_yt.Found)) ? Cursors.Default : Cursors.Hand;
            _gridFormats.Invalidate();
        }

        // 不同種類（影像 / 音訊）才會疊加多選；同種類只是切換；完整檔（影音合一）則單選
        private void OnFormatClicked(int i)
        {
            FormatItem f = _gridFormats.Items[i].Tag as FormatItem;
            if (f == null) return;
            if (_selFormats.Contains(f)) _selFormats.Remove(f);
            else
            {
                if (f.Kind == 0) _selFormats.Clear();
                else _selFormats.RemoveAll(delegate(FormatItem x) { return x.Kind == 0 || x.Kind == f.Kind; });
                _selFormats.Add(f);
            }
            _selFormats.Sort(delegate(FormatItem a, FormatItem b) { return a.Kind.CompareTo(b.Kind); });
            WriteFormatArg();
        }

        private void WriteFormatArg()
        {
            List<string> codes = new List<string>();
            foreach (FormatItem f in _selFormats) codes.Add(f.Code);
            _updating = true;
            _boxArgs.Inner.Text = ArgsText.SetFormat(_boxArgs.Inner.Text, string.Join("+", codes.ToArray()));
            _updating = false;
            _gridFormats.Invalidate();
            _gridOptions.Invalidate();
        }

        // 手動改 -f 時讓清單高亮跟著變
        private void SyncFormatsFromArgs()
        {
            if (!_fmtLoaded) return;
            string v = ArgsText.GetFormat(_boxArgs.Inner.Text);
            List<FormatItem> sel = new List<FormatItem>();
            if (v.Length > 0)
                foreach (string code in v.Split('+'))
                    foreach (FormatItem f in _allFormats)
                        if (f.Code == code) { sel.Add(f); break; }
            _selFormats = sel;
        }

        private void StartListFormats()
        {
            if (_listing || !_yt.Found) return;
            List<string> urls = GetUrls();
            if (urls.Count == 0) { MessageBox.Show(this, "請先輸入影片網址。", Text); return; }
            string url = urls[0];
            string exe = _yt.Exe;
            _listing = true; UpdateEnabled();

            ThreadPool.QueueUserWorkItem(delegate
            {
                string so, se; int code;
                bool started = Tools.Run(exe, "--list-formats --playlist-items 1 \"" + url + "\"", 120000, out so, out se, out code);
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _listing = false;
                        FinishListFormats(started, so, se, code);
                        UpdateEnabled();
                    }));
                }
                catch { }
            });
        }

        private void FinishListFormats(bool started, string so, string se, int code)
        {
            if (!started)
            {
                MessageBox.Show(this, "無法啟動 yt-dlp。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string[] headers;
            List<FormatItem> items = ParseFormats(so, out headers);
            if (items.Count == 0)
            {
                string msg = (se ?? "").Trim();
                if (msg.Length == 0) msg = (so ?? "").Trim();
                if (msg.Length > 900) msg = msg.Substring(msg.Length - 900);
                MessageBox.Show(this, "解析格式失敗（結束碼 " + code + "）。\r\n\r\n" + msg, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _fmtHeaders = headers;
            _allFormats = items;
            _selFormats = new List<FormatItem>();
            _fmtLoaded = true;
            _fmtDirty = true;
            _updating = true;
            _boxArgs.Inner.Text = ArgsText.SetFormat(_boxArgs.Inner.Text, "");
            _updating = false;
            RefreshFormatList();
        }

        // 依表頭文字的位置切欄：欄與欄之間一定有一個「全部列皆為空白」的位置
        private static List<FormatItem> ParseFormats(string output, out string[] headers)
        {
            headers = new string[0];
            List<FormatItem> res = new List<FormatItem>();
            string[] lines = (output ?? "").Replace("\r", "").Split('\n');
            int h = -1;
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"^\s*ID\s+EXT\b")) { h = i; break; }
            if (h < 0) return res;

            int start = h + 1;
            if (start < lines.Length && Regex.IsMatch(lines[start], "^[^A-Za-z0-9]{5,}$")) start++;

            List<string> raws = new List<string>();
            for (int i = start; i < lines.Length; i++)
            {
                string t = lines[i].TrimEnd();
                if (t.Trim().Length == 0 || t.TrimStart().StartsWith("[")) continue;
                if (t.IndexOf("storyboard", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                raws.Add(t);
            }
            if (raws.Count == 0) return res;

            string hdr = Clean(lines[h]).TrimEnd().Replace("MORE INFO", "MORE_INFO");
            List<string> names = new List<string>();
            List<int> idx = new List<int>(), ends = new List<int>();
            foreach (Match m in Regex.Matches(hdr, @"\S+"))
            {
                names.Add(m.Value.Replace("_", " "));
                idx.Add(m.Index); ends.Add(m.Index + m.Length);
            }
            List<string> rows = new List<string>();
            foreach (string r in raws) rows.Add(Clean(r));

            int maxLen = hdr.Length;
            foreach (string r in rows) maxLen = Math.Max(maxLen, r.Length);
            bool[] blank = new bool[maxLen + 1];
            for (int p = 0; p <= maxLen; p++)
            {
                bool b = p >= hdr.Length || hdr[p] == ' ';
                for (int k = 0; b && k < rows.Count; k++) if (p < rows[k].Length && rows[k][p] != ' ') b = false;
                blank[p] = b;
            }

            int n = names.Count;
            int[] bound = new int[n + 1];
            bound[0] = 0;
            for (int c = 1; c < n; c++)
            {
                int pos = idx[c];
                for (int p = idx[c] - 1; p >= ends[c - 1]; p--)
                    if (blank[p]) { pos = p + 1; break; }
                bound[c] = pos;
            }
            bound[n] = maxLen + 1;

            List<string[]> cells = new List<string[]>();
            for (int k = 0; k < rows.Count; k++)
            {
                string[] cs = new string[n];
                for (int c = 0; c < n; c++)
                {
                    int a = Math.Min(bound[c], rows[k].Length), b = Math.Min(bound[c + 1], rows[k].Length);
                    cs[c] = rows[k].Substring(a, Math.Max(0, b - a)).Trim();
                }
                cells.Add(cs);
            }

            // 整欄皆空的欄位（ID 除外）不顯示
            List<int> keep = new List<int>();
            for (int c = 0; c < n; c++)
            {
                bool any = c == 0;
                for (int k = 0; !any && k < cells.Count; k++) if (cells[k][c].Length > 0) any = true;
                if (any) keep.Add(c);
            }
            headers = new string[keep.Count];
            for (int j = 0; j < keep.Count; j++) headers[j] = names[keep[j]];

            for (int k = 0; k < cells.Count; k++)
            {
                if (cells[k][0].Length == 0) continue;
                string[] cs = new string[keep.Count];
                for (int j = 0; j < keep.Count; j++) cs[j] = cells[k][keep[j]];
                FormatItem f = new FormatItem(cs[0], cs);
                string low = raws[k];
                f.Kind = low.IndexOf("audio only", StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                       : low.IndexOf("video only", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
                res.Add(f);
            }
            return res;
        }

        // 把分隔線、「約略」符號、亂碼字元換成空白（長度不變，欄位位置不會位移）
        private static string Clean(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                bool bad = ch == '|' || ch == '~' || ch == '\uFFFD';
                if (!bad && ch > 127)
                {
                    UnicodeCategory cat = char.GetUnicodeCategory(ch);
                    bad = cat == UnicodeCategory.MathSymbol || cat == UnicodeCategory.OtherSymbol
                        || cat == UnicodeCategory.ModifierSymbol || cat == UnicodeCategory.CurrencySymbol
                        || cat == UnicodeCategory.PrivateUse || cat == UnicodeCategory.OtherNotAssigned;
                }
                sb.Append(bad ? ' ' : ch);
            }
            return sb.ToString();
        }

        // -------------------------------------------------------------- 下載
        private void OnDownload(object sender, EventArgs e)
        {
            List<string> urls = GetUrls();
            if (urls.Count == 0) { MessageBox.Show(this, "請先輸入影片網址（或用「批量輸入」貼上多個）。", Text); return; }
            string args = ArgsText.Norm(_boxArgs.Inner.Text);

            if (!_ff.Found)
            {
                string fmt = ArgsText.GetFormat(args);
                bool needs = fmt.Contains("+") || Regex.IsMatch(args, @"(^|\s)(-x|--extract-audio|--merge-output-format|--recode-video|--embed-thumbnail)(\s|$)");
                if (needs && MessageBox.Show(this,
                    "未偵測到 ffmpeg，目前的設定（合併影音或轉檔）可能失敗。\r\n仍要繼續下載嗎？",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }

            SavePath();
            string outDir = CurrentOutDir();
            try { Directory.CreateDirectory(outDir); } catch { }

            StringBuilder cmd = new StringBuilder();
            cmd.Append("chcp 65001 >nul & ");
            cmd.Append(ArgsText.Quote(_yt.Exe));
            if (!ArgsText.HasOutputOption(args)) cmd.Append(" -P " + ArgsText.Quote(outDir));
            if (_ff.Found && _ff.Exe.IndexOf('\\') >= 0 && args.IndexOf("--ffmpeg-location", StringComparison.Ordinal) < 0)
                cmd.Append(" --ffmpeg-location " + ArgsText.Quote(Path.GetDirectoryName(_ff.Exe)));
            if (args.Length > 0) cmd.Append(" " + args);
            foreach (string u in urls) cmd.Append(" " + ArgsText.Quote(u));
            cmd.Append(" & echo. & pause");

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + cmd.ToString());
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Directory.Exists(outDir) ? outDir : _cfg.ExeDir;
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                if (_ar.Found && _ar.Exe.IndexOf('\\') >= 0)
                    psi.EnvironmentVariables["PATH"] = Path.GetDirectoryName(_ar.Exe) + ";" + psi.EnvironmentVariables["PATH"];
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "啟動下載失敗：\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnOpenFolder(object sender, EventArgs e)
        {
            SavePath();
            string dir = CurrentOutDir();
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "無法開啟資料夾：\r\n" + ex.Message, Text);
            }
        }
    }

    // ------------------------------------------------------------------ 偵測狀態列（打勾 / 叉靠左，名稱與內容各自對齊）
    internal sealed class StatusRow : Control
    {
        public string Title;
        public int State;          // 0 偵測中, 1 有, 2 無
        public string Detail = "";

        public StatusRow(string title)
        {
            Title = title;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetState(int state, string detail)
        {
            State = state; Detail = detail ?? "";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BackBehind(this));
            TextFormatFlags fl = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int markW = Theme.S(30);
            int nameW = TextRenderer.MeasureText("yt-dlp", Font).Width + Theme.S(28);
            string mark = State == 1 ? "\u2714" : State == 2 ? "\u2718" : "\u2026";
            Color mc = State == 1 ? Theme.Good : State == 2 ? Theme.Bad : Theme.Dim;
            TextRenderer.DrawText(g, mark, Font, new Rectangle(0, 0, markW, Height), mc, fl);
            TextRenderer.DrawText(g, Title, Font, new Rectangle(markW, 0, nameW, Height), Theme.Text, fl);
            TextRenderer.DrawText(g, Detail, Font, new Rectangle(markW + nameW, 0, Math.Max(10, Width - markW - nameW), Height),
                State == 2 ? Theme.Bad : Theme.Dim, fl);
        }
    }

    // ------------------------------------------------------------------ 應用程式圖示
    // 全程式碼繪製：視窗圖示直接用；exe 圖示由 `--make-ico 路徑` 輸出，csc-build.cmd 編譯時自動呼叫。
    internal static class AppIcon
    {
        private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

        public static Bitmap Render(int S)
        {
            Bitmap bmp = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // 圓角底板（藍色漸層）
                RectangleF box = new RectangleF(S * 0.03f, S * 0.03f, S * 0.94f, S * 0.94f);
                using (GraphicsPath bg = Theme.Round(box, S * 0.22f))
                using (LinearGradientBrush lg = new LinearGradientBrush(box,
                    Color.FromArgb(96, 156, 250), Color.FromArgb(38, 84, 186), 90f))
                    g.FillPath(lg, bg);

                // 向下箭頭
                PointF[] arrow = new PointF[] {
                    P(S, 0.43f, 0.17f), P(S, 0.57f, 0.17f), P(S, 0.57f, 0.43f), P(S, 0.70f, 0.43f),
                    P(S, 0.50f, 0.66f), P(S, 0.30f, 0.43f), P(S, 0.43f, 0.43f) };
                float w = Math.Max(1f, S * 0.045f);
                using (SolidBrush br = new SolidBrush(Color.White))
                using (Pen pen = new Pen(Color.White, w))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.FillPolygon(br, arrow);
                    g.DrawPolygon(pen, arrow);
                }

                // 底部托盤
                using (Pen tray = new Pen(Color.White, Math.Max(1.5f, S * 0.07f)))
                {
                    tray.StartCap = LineCap.Round; tray.EndCap = LineCap.Round; tray.LineJoin = LineJoin.Round;
                    g.DrawLines(tray, new PointF[] { P(S, 0.25f, 0.62f), P(S, 0.25f, 0.79f), P(S, 0.75f, 0.79f), P(S, 0.75f, 0.62f) });
                }
            }
            return bmp;
        }

        private static PointF P(int S, float x, float y) { return new PointF(S * x, S * y); }

        public static Icon CreateWindowIcon()
        {
            using (Bitmap bmp = Render(64))
            {
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        public static void WriteIco(string file)
        {
            List<byte[]> pngs = new List<byte[]>();
            foreach (int sz in Sizes)
            {
                using (Bitmap b = Render(sz))
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }
            using (FileStream fs = File.Create(file))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)Sizes.Length);
                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    byte dim = (byte)(Sizes[i] >= 256 ? 0 : Sizes[i]);
                    bw.Write(dim); bw.Write(dim); bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((ushort)1); bw.Write((ushort)32);
                    bw.Write((uint)pngs[i].Length); bw.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) bw.Write(p);
            }
        }
    }
}
