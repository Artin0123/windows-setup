// test/media-downloader.selftest.cs — media-downloader 的純函數自我測試（開發用，可選檔）
//
// 編譯：csc-build.cmd test/media-downloader.selftest.cs media-downloader.cs
//       （測試檔必須放第一個：檔內的 csc-variant 行會帶出 SELFTEST 定義，
//        產出主控台版 media-downloader-selftest.exe；主檔單獨建置時測試碼完全不編入）
// 執行：media-downloader-selftest.exe --selftest   （不開視窗、不碰網路；結束碼 0 = 全部通過）
// 測的對象：主檔 internal 的 ParseUrls / ParseFormats 與 ArgsText / Config，需與主檔同組件
//
// csc-variant: media-downloader-selftest SELFTEST
#if SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaDownloader
{
    // ------------------------------------------------------------------ 自我測試
    // media-downloader.exe --selftest：只測純函數（不開視窗、不碰網路），結束碼 0 = 全部通過
    internal static class SelfTest
    {
        private static int _failed;

        private static void Ok(bool cond, string name)
        {
            if (cond) { Console.WriteLine("PASS  " + name); return; }
            _failed++;
            Console.WriteLine("FAIL  " + name);
        }

        public static int RunAll()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            TestArgsText();
            TestQuote();
            TestParseUrls();
            TestParseFormats();
            TestConfigOptions();
            Console.WriteLine(_failed == 0 ? "全部通過" : _failed + " 項失敗");
            return _failed == 0 ? 0 : 1;
        }

        private static void TestArgsText()
        {
            Ok(ArgsText.Norm("  a \t b\r\nc  ") == "a b c", "Norm 合併空白");
            Ok(ArgsText.Norm("") == "", "Norm 空字串");
            Ok(ArgsText.Add("-x", "--no-playlist") == "-x --no-playlist", "Add 附加");
            Ok(ArgsText.Add("-x --no-playlist", "--no-playlist") == "-x --no-playlist", "Add 已存在不重複");
            Ok(ArgsText.Remove("a --no-playlist b", "--no-playlist") == "a b", "Remove 移除");
            Ok(ArgsText.GetFormat("--no-playlist -f 137+140 -o x") == "137+140", "GetFormat 取值");
            Ok(ArgsText.GetFormat("-f='best' --no-playlist") == "best", "GetFormat 等號形式+引號");
            Ok(ArgsText.SetFormat("--no-playlist", "137+140") == "-f 137+140 --no-playlist", "SetFormat 附加且置前");
            Ok(ArgsText.SetFormat("-f 18 --no-playlist", "137+140") == "-f 137+140 --no-playlist", "SetFormat 取代");
            Ok(ArgsText.SetFormat("-f 18 --no-playlist", "") == "--no-playlist", "SetFormat 空碼移除");
            Ok(ArgsText.SetFormat("-f 18 --no-playlist", "bestvideo+bestaudio/best") == "-f bestvideo+bestaudio/best --no-playlist", "SetFormat + 不需引號");
            Ok(ArgsText.SetFormat("-f 18", "a b|c") == "-f \"a b|c\"", "SetFormat 含空白/| 補引號");
            Ok(ArgsText.MoveFormatFirst("--no-playlist -f 18 -o x") == "-f 18 --no-playlist -o x", "MoveFormatFirst");
        }

        private static void TestQuote()
        {
            Ok(ArgsText.Quote("plain") == "\"plain\"", "Quote 基本包裹");
            Ok(ArgsText.Quote("a\"b") == "\"a\\\"b\"", "Quote 內部引號跳脫");
            Ok(ArgsText.Quote("D:\\dir\\") == "\"D:\\dir\\\\\"", "Quote 尾部反斜線加倍");
        }

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "md-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TestParseUrls()
        {
            string dir = TempDir(), file = Path.Combine(dir, "urls.txt");
            try
            {
                File.WriteAllText(file, "# comment\r\nhttp://a/\"&calc&echo\"\r\nhttp://a/x\r\nhttp://a/X\r\nftp://b/y\r\n",
                    new UTF8Encoding(false));
                List<string> urls = MainForm.ParseUrls(file);
                Ok(urls.Count == 2, "ParseUrls 註解/去重/過濾非 http(s)");
                Ok(urls.Count > 0 && urls[0].IndexOf('"') < 0 && urls[0].Contains("%22"),
                    "ParseUrls 引號消毒為 %22（命令注入回歸）");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static void TestParseFormats()
        {
            string sample =
                "[youtube] Extracting URL: https://example.invalid/watch?v=x\n" +
                "ID     EXT    RESOLUTION   FPS  CH  │  FILESIZE     TBR   PROTO   │ VCODEC        VBR   ACODEC      ABR   ASR   MORE INFO\n" +
                "─────────────────────────────────────────────────────────────────────────────\n" +
                "137    mp4    1920x1080    30          100.00MiB    300k  https     avc1.640028   300k                          video only 1080p\n" +
                "140    m4a    audio only               10.00MiB     50k   https                         mp4a.40.2         44k\n";
            string[] headers;
            List<FormatItem> items = MainForm.ParseFormats(sample, out headers);
            Ok(items.Count == 2, "ParseFormats 跳過分隔線/storyboard");
            Ok(items.Count > 0 && items[0].Code == "137" && items[0].Kind == 1, "ParseFormats video only → Kind 1");
            Ok(items.Count > 1 && items[1].Code == "140" && items[1].Kind == 2, "ParseFormats audio only → Kind 2");
            Ok(headers.Length > 0 && headers[0] == "ID", "ParseFormats 表頭");
            bool hasAbr = false;
            foreach (string h in headers) if (h == "ABR") hasAbr = true;
            Ok(!hasAbr, "ParseFormats 整欄空的欄位不顯示");
        }

        private static void TestConfigOptions()
        {
            string dir = TempDir();
            try
            {
                string ini = Path.Combine(dir, Config.FileName);
                File.WriteAllText(ini,
                    "useAria2 = 1\r\n" +
                    "option = 舊格式 | -f mp4 | 1 | 0\r\n" +
                    "option = 一般 | --no-playlist\r\n",
                    new UTF8Encoding(false));
                Config c = Config.Load(dir);
                Ok(c.UseAria2, "Config 讀取 useAria2");
                Ok(c.Options.Count == 2, "Config 讀取 option 列");
                Ok(c.Options.Count == 2 && c.Options[0].Name == "舊格式" && c.Options[0].Args == "-f mp4",
                    "Config 舊格式尾巴 0/1 剝除");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
#endif
