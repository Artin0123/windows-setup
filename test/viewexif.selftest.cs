// test/viewexif.selftest.cs — viewexif 的純函數自我測試（開發用，可選檔）
//
// 編譯：csc-build.cmd test/viewexif.selftest.cs viewexif.cs
//       （測試檔必須放第一個：檔內的 csc-variant 行會帶出 SELFTEST 定義，
//        產出主控台版 viewexif-selftest.exe；主檔單獨建置時測試碼完全不編入）
// 執行：viewexif-selftest.exe --selftest   （不開視窗、不碰 exiftool；結束碼 0 = 全部通過）
// 測的對象：主檔 internal 的 JP / Cfg 解析與 Program.FormatValue / EmitJson，需與主檔同組件
//
// csc-variant: viewexif-selftest SELFTEST
#if SELFTEST
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace viewexif {
  internal static class SelfTest {
    static int _failed;

    static void Ok(bool cond, string name) {
      if (cond) { Console.WriteLine("PASS  " + name); return; }
      _failed++;
      Console.WriteLine("FAIL  " + name);
    }

    public static int RunAll() {
      try { Console.OutputEncoding = Encoding.UTF8; } catch { }
      TestJP();
      TestDecode();
      TestFormatValue();
      TestCfgParse();
      TestCfgIO();
      Console.WriteLine(_failed == 0 ? "全部通過" : _failed + " 項失敗");
      return _failed == 0 ? 0 : 1;
    }

    // ---- JP：嚴格 JSON 掃描 ----
    static void TestJP() {
      JNode n; int end;
      Ok(JP.TryParseAt("{\"a\":1,\"b\":[true,null]}", 0, out n, out end) && n.Kind == 0 && n.Keys.Count == 2, "JP 物件+巢狀陣列");
      Ok(n != null && n.Items[1].Kind == 1 && n.Items[1].Items.Count == 2 && n.Items[1].Items[0].Raw == "true", "JP 陣列元素 literal");
      Ok(JP.TryParseAt("{} x", 0, out n, out end) && end == 2, "JP 尾端雜物以 end 回報");
      Ok(!JP.TryParseAt("{\"a\":}", 0, out n, out end), "JP 缺值拒絕");
      Ok(!JP.TryParseAt("[1,]", 0, out n, out end), "JP 尾逗號拒絕");
      Ok(!JP.TryParseAt("\"abc", 0, out n, out end), "JP 未閉合字串拒絕");
      Ok(!JP.TryParseAt("\"\\u00zz\"", 0, out n, out end), "JP 非 hex 的 \\u 拒絕");
      Ok(!JP.TryParseAt("tru", 0, out n, out end), "JP 截斷 literal 拒絕");
      Ok(JP.TryParseAt("{\"k\":1,\"k\":2}", 0, out n, out end) && n.Keys.Count == 2 && n.Items[1].Raw == "2", "JP 重複鍵保留");
      Ok(JP.TryParseAt("-0.5e+10", 0, out n, out end) && n.Kind == 3 && n.Raw == "-0.5e+10", "JP 科學記號數字");
      Ok(JP.TryParseAt("null", 0, out n, out end) && n.Raw == "null", "JP null");
      Ok(!JP.TryParseAt(new string('[', 250), 0, out n, out end), "JP 深度上限拒絕");
    }

    // ---- JP.Decode：JSON 字串反跳脫（含防禦路徑） ----
    static void TestDecode() {
      Ok(JP.Decode("a\\nb") == "a\nb", "Decode \\n");
      Ok(JP.Decode("\\u4e2d\\u6587") == "中文", "Decode \\uXXXX");
      Ok(JP.Decode("plain") == "plain", "Decode 無跳脫原樣");
      Ok(JP.Decode("x\\") == "x\\", "Decode 尾端孤立反斜線");
      Ok(JP.Decode("\\u12") == "\\u12", "Decode \\u 不足 4 碼原樣保留不炸");
      Ok(JP.Decode("\\q") == "q", "Decode 未知跳脫");
    }

    // ---- Program.FormatValue / EmitJson：值格式化 ----
    static string Plain(List<Line> ls) {
      List<string> p = new List<string>();
      foreach (Line l in ls) p.Add(l.Plain());
      return string.Join("\r\n", p.ToArray());
    }

    static void TestFormatValue() {
      Ok(Plain(Program.FormatValue("{\"a\":{\"b\":{}}}")) == "{\r\n  \"a\": {\r\n    \"b\": {}\r\n  }\r\n}", "FormatValue 巢狀 + 空容器單行");
      Ok(Plain(Program.FormatValue("[1,2]")) == "[\r\n  1,\r\n  2\r\n]", "FormatValue 陣列逐行 + 同行逗號");
      Ok(Plain(Program.FormatValue("a\n\nb")) == "a\r\nb", "FormatValue 純文字去空行");
      Ok(Plain(Program.FormatValue("{ not json")) == "{ not json", "FormatValue 非完整 JSON 原樣");
      Ok(Plain(Program.FormatValue("\"a\\nb\"")) == "\"a\\nb\"", "FormatValue 頂層字串保留跳脫");
      Ok(Plain(Program.FormatValue("01")) == "01", "FormatValue 非法數字原樣");
      Ok(Program.FormatValue("  ").Count == 0, "FormatValue 空白輸入");
    }

    // ---- Cfg：ini 欄位解析 ----
    static void TestCfgParse() {
      Ok(Cfg.ParseBool("ON", false) && !Cfg.ParseBool("maybe", false) && Cfg.ParseBool("x", true), "Cfg.ParseBool 各形式與預設");
      Ok(Cfg.ParseColor("#FF8000", Color.Black) == Color.FromArgb(255, 128, 0), "Cfg.ParseColor #RRGGBB");
      Ok(Cfg.ParseColor("ff8000", Color.Black) == Color.FromArgb(255, 128, 0), "Cfg.ParseColor 無 #");
      Ok(Cfg.ParseColor("zz", Color.Black) == Color.Black, "Cfg.ParseColor 非法回退");
      Ok(Cfg.Hex(Color.FromArgb(0, 128, 255)) == "0080FF", "Cfg.Hex");
    }

    // ---- Cfg：ini 讀寫往返與邊界（用暫存檔） ----
    static void TestCfgIO() {
      string tmp = Path.Combine(Path.GetTempPath(), "viewexif-selftest-" + Guid.NewGuid().ToString("N") + ".ini");
      try {
        Cfg c1 = new Cfg();
        c1.Font = "Consolas"; c1.Size = 13f; c1.Wrap = false;
        c1.Bg = Color.FromArgb(1, 2, 3); c1.Fg = Color.FromArgb(4, 5, 6); c1.Key = Color.FromArgb(7, 8, 9);
        c1.X = -8; c1.Y = 100; c1.W = 900; c1.H = 700;
        c1.Save(tmp);
        Cfg c2 = new Cfg(); c2.Load(tmp);
        Ok(c2.Font == "Consolas" && c2.Size == 13f && !c2.Wrap, "Cfg 往返 font/size/wrap");
        Ok(c2.Bg == c1.Bg && c2.Fg == c1.Fg && c2.Key == c1.Key, "Cfg 往返顏色");
        Ok(c2.X == -8 && c2.Y == 100 && c2.W == 900 && c2.H == 700, "Cfg 往返視窗位置");

        Cfg c4 = new Cfg();   // X 仍是 int.MinValue：Save 不得寫出 sentinel 值
        c4.Save(tmp);
        Cfg c5 = new Cfg(); c5.Load(tmp);
        Ok(c5.X == int.MinValue && c5.W == 840 && c5.H == 640, "Cfg 未定位時不寫 x/y sentinel");

        File.WriteAllText(tmp,
          "font_size=5\r\nwidth=100\r\nheight=0\r\nwrap=yes\r\n[sec]\r\n; comment\r\n# comment\r\nbogus line\r\n", Encoding.UTF8);
        Cfg c3 = new Cfg();
        c3.Load(tmp);
        Ok(c3.Size == 11f && c3.W == 840 && c3.H == 640 && c3.Wrap, "Cfg 邊界值拒絕 + 區段/註解/非鍵值行忽略");
      } finally {
        try { File.Delete(tmp); } catch { }
      }
    }
  }
}
#endif
