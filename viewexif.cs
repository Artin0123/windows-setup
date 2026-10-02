// viewexif - ExifTool metadata viewer (two columns: name | value)
// Build: csc /nologo /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:viewexif.exe viewexif.cs
// Usage: viewexif.exe <image>      Config: viewexif-config.ini (next to the exe)
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace viewexif {
  // kind: 0 text, 1 key, 2 value, 3 section header, 4 dim punctuation
  class Seg { public string T; public int K; public Seg(string t, int k) { T = t; K = k; } }
  class Line {
    public int Lv; public List<Seg> S = new List<Seg>();
    public Line(int lv) { Lv = lv; }
    public Line Add(string t, int k) { S.Add(new Seg(t, k)); return this; }
    public string Plain() {
      StringBuilder b = new StringBuilder(new string(' ', Lv * 2));
      foreach (Seg s in S) b.Append(s.T);
      return b.ToString();
    }
  }

  class Cfg {
    public string Font = "Microsoft YaHei";
    public float Size = 11f;         // points, same as rclone-operator
    public bool Wrap = true;
    public Color Bg = Color.FromArgb(30, 30, 30), Fg = Color.FromArgb(225, 225, 225),
      Key = Color.FromArgb(127, 200, 248), Value = Color.FromArgb(168, 216, 160),
      Section = Color.FromArgb(255, 158, 100), Dim = Color.FromArgb(122, 122, 122),
      Done = Color.FromArgb(80, 200, 120),
      Bar = Color.FromArgb(37, 37, 38);
    public int X = int.MinValue, Y = 0, W = 840, H = 640;

    static string Hex(Color c) { return (c.R << 16 | c.G << 8 | c.B).ToString("X6"); }
    static Color ParseColor(string v, Color def) {
      try {
        int n = Convert.ToInt32(v.Trim().TrimStart('#'), 16);
        return Color.FromArgb((n >> 16) & 255, (n >> 8) & 255, n & 255);
      } catch { return def; }
    }
    static bool ParseBool(string v, bool def) {
      v = v.ToLowerInvariant();
      if (v == "true" || v == "1" || v == "yes" || v == "on") return true;
      if (v == "false" || v == "0" || v == "no" || v == "off") return false;
      return def;
    }

    public void Load(string path) {
      if (!File.Exists(path)) return;
      foreach (string raw in File.ReadAllLines(path, Encoding.UTF8)) {
        string l = raw.Trim();
        if (l.Length == 0 || l[0] == ';' || l[0] == '#' || l[0] == '[') continue;
        int eq = l.IndexOf('=');
        if (eq < 0) continue;
        string k = l.Substring(0, eq).Trim().ToLowerInvariant(), v = l.Substring(eq + 1).Trim();
        int n; float f;
        switch (k) {
          case "font": if (v.Length > 0) Font = v; break;
          case "font_size": if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f) && f >= 6 && f <= 48) Size = f; break;
          case "wrap": Wrap = ParseBool(v, Wrap); break;
          case "bg": Bg = ParseColor(v, Bg); break;
          case "fg": Fg = ParseColor(v, Fg); break;
          case "key_color": Key = ParseColor(v, Key); break;
          case "value_color": Value = ParseColor(v, Value); break;
          case "section_color": Section = ParseColor(v, Section); break;
          case "dim_color": Dim = ParseColor(v, Dim); break;
          case "done_color": Done = ParseColor(v, Done); break;
          case "toolbar_bg": Bar = ParseColor(v, Bar); break;
          case "x": if (int.TryParse(v, out n)) X = n; break;
          case "y": if (int.TryParse(v, out n)) Y = n; break;
          case "width": if (int.TryParse(v, out n) && n > 200) W = n; break;
          case "height": if (int.TryParse(v, out n) && n > 150) H = n; break;
        }
      }
    }

    public void Save(string path) {
      StringBuilder s = new StringBuilder();
      s.AppendLine("; viewexif config (rewritten on exit; edit while viewexif is closed)");
      s.AppendLine("[view]");
      s.AppendLine("font=" + Font);
      s.AppendLine("font_size=" + Size.ToString(CultureInfo.InvariantCulture));
      s.AppendLine("wrap=" + (Wrap ? "true" : "false"));
      s.AppendLine("[colors]");
      s.AppendLine("bg=" + Hex(Bg)); s.AppendLine("fg=" + Hex(Fg));
      s.AppendLine("key_color=" + Hex(Key)); s.AppendLine("value_color=" + Hex(Value));
      s.AppendLine("section_color=" + Hex(Section)); s.AppendLine("dim_color=" + Hex(Dim));
      s.AppendLine("done_color=" + Hex(Done));
      s.AppendLine("toolbar_bg=" + Hex(Bar));
      s.AppendLine("[window]");
      s.AppendLine("x=" + X); s.AppendLine("y=" + Y); s.AppendLine("width=" + W); s.AppendLine("height=" + H);
      File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }
  }

  // ===================== owner-drawn viewer (exact layout, no RichEdit) =====================
  class VSeg { public int X; public string T; public int K; public int Off; }
  class VLine { public int Y; public List<VSeg> S = new List<VSeg>(); }
  class Row {
    public string Key, Copy, Val;   // Val = value text, logical lines joined by \n (selection/offset space)
    public List<Line> Lines;
    public List<VLine> VL = new List<VLine>();
    public int Top, H;
  }

  class Viewer : Control {
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    public Cfg C;
    public List<Row> Rows;
    public VScrollBar VS = new VScrollBar();
    public HScrollBar HS = new HScrollBar();
    public Action OnToggleWrap, OnOpenIni;

    Font fN, fB, fW;
    int lh, padL, padT, padR, padB, btnW, btnH, iconCol, keyW, lvPx, rowGap, valueX, contentMax;
    float scale = 1f;
    int scrollY, scrollX;
    Rectangle wrapRect, iniRect;
    int hover = -1;            // -2 = wrap button, >=0 = copy button of that row
    int doneRow = -1;
    Timer doneTimer = new Timer();
    bool inLayout;

    public Viewer(Cfg c, List<Row> rows) {
      C = c; Rows = rows;
      SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
               ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
      TabStop = true;
      BackColor = c.Bg;
      doneTimer.Interval = 1200;
      doneTimer.Tick += delegate { doneTimer.Stop(); doneRow = -1; Invalidate(); };
      VS.Dock = DockStyle.Right;
      HS.Dock = DockStyle.Bottom;
      VS.ValueChanged += delegate { if (!inLayout) { scrollY = VS.Value; Invalidate(); } };
      HS.ValueChanged += delegate { if (!inLayout) { scrollX = HS.Value; Invalidate(); } };
    }

    public void DarkScrollbars() {
      try { SetWindowTheme(VS.Handle, "DarkMode_Explorer", null); SetWindowTheme(HS.Handle, "DarkMode_Explorer", null); } catch { }
    }

    // TextRenderer.MeasureText adds a constant overhang (~12px) to every string, which made gaps between
    // words/segments far too wide. Subtract it so Meas() is the true advance width.
    static Dictionary<Font, int> overhang = new Dictionary<Font, int>();
    static int Raw(string s, Font f) {
      return TextRenderer.MeasureText(s, f, new Size(1 << 20, 1 << 10),
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
    }
    static int Meas(string s, Font f) {
      if (s.Length == 0) return 0;
      int k;
      if (!overhang.TryGetValue(f, out k)) { k = 2 * Raw("a", f) - Raw("aa", f); overhang[f] = k; }
      return Math.Max(0, Raw(s, f) - k);
    }

    // largest n such that s.Substring(start, n) fits in room px (binary search on measured prefixes)
    static int FitCount(string s, int start, Font f, int room) {
      int lo = 0, hi = s.Length - start;
      while (lo < hi) {
        int mid = (lo + hi + 1) / 2;
        if (Meas(s.Substring(start, mid), f) <= room) lo = mid; else hi = mid - 1;
      }
      return lo;
    }

    public void DoLayout() {
      if (Width < 50 || Height < 50) return;
      inLayout = true;
      using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
      if (fN != null) { fN.Dispose(); fB.Dispose(); fW.Dispose(); }
      overhang.Clear();
      fN = new Font(C.Font, C.Size);
      fB = new Font(C.Font, C.Size, FontStyle.Bold);
      fW = new Font(C.Font, 10f);
      lh = fN.Height;
      padL = (int)(26 * scale); padT = (int)(28 * scale); padR = (int)(14 * scale); padB = (int)(20 * scale);
      rowGap = (int)(16 * scale);
      btnH = lh + (int)(4 * scale); btnW = btnH + (int)(6 * scale);
      iconCol = btnW + (int)(14 * scale);
      lvPx = (int)Math.Round(C.Size * scale * 96f / 72f * 1.6f);

      int maxKey = 0;
      foreach (Row r in Rows) { int w = Meas(r.Key, fN); if (w > maxKey) maxKey = w; }
      keyW = Math.Min(maxKey, (int)(Width * 0.42f)) + (int)(18 * scale);
      valueX = padL + iconCol + keyW;

      Size ts = TextRenderer.MeasureText("Wrap: OFF", fW, new Size(1 << 20, 1 << 10), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
      int ww = ts.Width + (int)(36 * scale), wh = ts.Height + (int)(16 * scale);
      wrapRect = new Rectangle(Width - ww - (int)(24 * scale), padT, ww, wh);
      iniRect = new Rectangle(wrapRect.X - wh - (int)(10 * scale), padT, wh, wh);   // square gear button

      int sp = Math.Max(1, Meas("a a", fN) - Meas("aa", fN));
      int spB = Math.Max(1, Meas("a a", fB) - Meas("aa", fB));
      int avail0 = C.Wrap ? Math.Max(120, Width - valueX - padR) : (1 << 28);
      contentMax = 0;

      int y = padT;
      foreach (Row r in Rows) {
        r.Top = y; r.VL.Clear();
        int avail = avail0;
        if (C.Wrap && y < wrapRect.Bottom + rowGap) avail = Math.Max(120, Math.Min(avail0, iniRect.X - (int)(16 * scale) - valueX));
        int vy = 0, lineBase = 0;
        foreach (Line ln in r.Lines) {
          int indent = ln.Lv * lvPx, x = indent;
          VLine vl = new VLine(); vl.Y = vy; r.VL.Add(vl);
          int segBase = 0, wrapX = indent;   // wrapX: where continuation lines start
          foreach (Seg sg in ln.S) {
            // "key: value" lines: wrapped value text lines up under the start of the value (hanging indent)
            if (sg.K == 2 && segBase > 0 && wrapX == indent && x - indent <= (Math.Min(avail, 1 << 20) - indent) / 2) wrapX = x;
            Font f = sg.K == 3 ? fB : fN;
            int spw = sg.K == 3 ? spB : sp;
            foreach (Match m in Regex.Matches(sg.T, @"\s+|\S+")) {
              string tk = m.Value;
              bool isSp = char.IsWhiteSpace(tk[0]);
              int w = isSp ? spw * tk.Length : Meas(tk, f);
              if (C.Wrap && x + w > avail) {
                if (isSp) continue;
                if (x > wrapX) { vy += lh; vl = new VLine(); vl.Y = vy; r.VL.Add(vl); x = wrapX; }
                if (x + w > avail) {
                  // token longer than a whole line (paths, hashes): break it by measured prefixes
                  int pos = 0;
                  while (pos < tk.Length) {
                    int n = FitCount(tk, pos, f, avail - x);
                    if (n < 1) n = 1;
                    VSeg s1 = new VSeg(); s1.X = x; s1.T = tk.Substring(pos, n); s1.K = sg.K; s1.Off = lineBase + segBase + m.Index + pos; vl.S.Add(s1);
                    int w1 = Meas(s1.T, f);
                    pos += n;
                    if (x + w1 > contentMax) contentMax = x + w1;
                    if (pos < tk.Length) { vy += lh; vl = new VLine(); vl.Y = vy; r.VL.Add(vl); x = wrapX; }
                    else x += w1;
                  }
                  continue;
                }
              }
              VSeg s = new VSeg(); s.X = x; s.T = tk; s.K = sg.K; s.Off = lineBase + segBase + m.Index; vl.S.Add(s);
              x += w;
              if (x > contentMax) contentMax = x;
            }
            segBase += sg.T.Length;
          }
          lineBase += segBase + 1;
          vy += lh;
        }
        if (r.VL.Count == 0) vy = lh;
        r.H = Math.Max(vy, lh);
        y += r.H + rowGap;
      }
      int total = y + padB;

      HS.Visible = !C.Wrap;
      int viewH = Height;
      int valueView = Math.Max(1, Width - valueX - padR);
      VS.Maximum = Math.Max(total, viewH) - 1;
      VS.LargeChange = Math.Max(1, viewH);
      VS.SmallChange = lh * 3;
      VS.Enabled = total > viewH;
      int maxY = Math.Max(0, total - viewH);
      if (scrollY > maxY) scrollY = maxY;
      VS.Value = scrollY;

      HS.Maximum = Math.Max(contentMax, valueView) - 1;
      HS.LargeChange = valueView;
      HS.SmallChange = (int)(40 * scale);
      HS.Enabled = contentMax > valueView;
      int maxX = Math.Max(0, contentMax - valueView);
      if (C.Wrap || scrollX > maxX) scrollX = C.Wrap ? 0 : maxX;
      HS.Value = Math.Min(scrollX, Math.Max(0, HS.Maximum - HS.LargeChange + 1));
      inLayout = false;
      Invalidate();
    }

    int MaxY() { return Math.Max(0, VS.Maximum + 1 - VS.LargeChange); }
    public void ScrollTo(int y) {
      y = Math.Max(0, Math.Min(MaxY(), y));
      scrollY = y;
      if (VS.Value != y) { inLayout = true; VS.Value = y; inLayout = false; }
      Invalidate();
    }

    // ---- text selection (values: across rows; keys: inside one row) ----
    static readonly Color SelBg = Color.FromArgb(38, 79, 120);
    int selMode;                 // 0 none, 1 key text, 2 value text
    int sR1, sO1, sR2, sO2;      // anchor row/offset, caret row/offset
    bool dragging;
    int curKind;                 // 0 default, 1 hand, 2 text

    int KeyX() { return padL + iconCol; }

    int RowAtY(int y) {
      for (int i = 0; i < Rows.Count; i++)
        if (y < Rows[i].Top - scrollY + Rows[i].H + rowGap) return i;
      return Rows.Count - 1;
    }

    // nearest character boundary in s (drawn from startX) for pixel x
    static int CharAtX(string s, int startX, int x, Font f) {
      int lo = 0, hi = s.Length;
      while (lo < hi) {
        int mid = (lo + hi + 1) / 2;
        if (startX + Meas(s.Substring(0, mid), f) <= x) lo = mid; else hi = mid - 1;
      }
      if (lo < s.Length) {
        int a = startX + Meas(s.Substring(0, lo), f), b = startX + Meas(s.Substring(0, lo + 1), f);
        if (x > (a + b) / 2) lo++;
      }
      return lo;
    }

    int ValOffset(Row r, Point p) {
      int ry = r.Top - scrollY;
      if (p.Y < ry) return 0;
      if (p.Y >= ry + r.H || r.VL.Count == 0) return r.Val.Length;
      int li = Math.Min(Math.Max(0, (p.Y - ry) / lh), r.VL.Count - 1);
      VLine vl = r.VL[li];
      while (vl.S.Count == 0 && li > 0) { li--; vl = r.VL[li]; }
      if (vl.S.Count == 0) return 0;
      int tx = p.X - valueX + scrollX;
      foreach (VSeg sg in vl.S) {
        Font f = sg.K == 3 ? fB : fN;
        int w = Meas(sg.T, f);
        if (tx < sg.X) return sg.Off;
        if (tx <= sg.X + w) return sg.Off + CharAtX(sg.T, sg.X, tx, f);
      }
      VSeg last = vl.S[vl.S.Count - 1];
      return last.Off + last.T.Length;
    }

    void SelRange(out int r1, out int o1, out int r2, out int o2) {
      if (sR1 < sR2 || (sR1 == sR2 && sO1 <= sO2)) { r1 = sR1; o1 = sO1; r2 = sR2; o2 = sO2; }
      else { r1 = sR2; o1 = sO2; r2 = sR1; o2 = sO1; }
    }

    bool HasSelection() {
      if (selMode == 0) return false;
      return !(sR1 == sR2 && sO1 == sO2);
    }

    string SelectedText() {
      if (!HasSelection()) return "";
      int r1, o1, r2, o2; SelRange(out r1, out o1, out r2, out o2);
      if (selMode == 1) return Rows[r1].Key.Substring(Math.Min(o1, o2), Math.Abs(o2 - o1));
      StringBuilder sb = new StringBuilder();
      for (int i = r1; i <= r2; i++) {
        string v = Rows[i].Val;
        int a = i == r1 ? o1 : 0, b = i == r2 ? o2 : v.Length;
        a = Math.Min(a, v.Length); b = Math.Min(Math.Max(a, b), v.Length);
        if (i > r1) sb.Append("\r\n");
        sb.Append(v.Substring(a, b - a).Replace("\n", "\r\n"));
      }
      return sb.ToString();
    }

    public void SelectAll() {
      if (Rows.Count == 0) return;
      selMode = 2; sR1 = 0; sO1 = 0; sR2 = Rows.Count - 1; sO2 = Rows[sR2].Val.Length;
      Invalidate();
    }

    void ExpandWord(Row r, int off, out int a, out int b) {
      string v = r.Val;
      a = Math.Min(off, v.Length); b = a;
      while (a > 0 && !char.IsWhiteSpace(v[a - 1]) && v[a - 1] != ',') a--;
      while (b < v.Length && !char.IsWhiteSpace(v[b]) && v[b] != ',') b++;
    }

    // draw part of a segment with the selection background behind chars [a, b) (offsets inside seg.T)
    void DrawSegSel(Graphics g, VSeg sg, int sx, int ly, Color fore, int a, int b, TextFormatFlags tf) {
      Font f = sg.K == 3 ? fB : fN;
      string t = sg.T;
      int xa = sx + Meas(t.Substring(0, a), f), xb = sx + Meas(t.Substring(0, b), f);
      if (a > 0) TextRenderer.DrawText(g, t.Substring(0, a), f, new Point(sx, ly), fore, C.Bg, tf);
      if (b > a) {
        using (SolidBrush br = new SolidBrush(SelBg)) g.FillRectangle(br, xa, ly, Math.Max(1, xb - xa), lh);
        TextRenderer.DrawText(g, t.Substring(a, b - a), f, new Point(xa, ly), fore, SelBg, tf);
      }
      if (b < t.Length) TextRenderer.DrawText(g, t.Substring(b), f, new Point(xb, ly), fore, C.Bg, tf);
    }

    Rectangle BtnRect(Row r) {
      return new Rectangle(padL, r.Top - scrollY + (lh - btnH) / 2, btnW, btnH);
    }

    int HitTest(Point p) {
      if (wrapRect.Contains(p)) return -2;
      if (iniRect.Contains(p)) return -3;
      for (int i = 0; i < Rows.Count; i++) {
        Row r = Rows[i];
        int ry = r.Top - scrollY;
        if (ry > Height) break;
        if (ry + r.H < 0) continue;
        if (BtnRect(r).Contains(p)) return i;
      }
      return -1;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Focus(); }
    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      if (dragging && Rows.Count > 0) {
        if (e.Y < 0) ScrollTo(scrollY - lh); else if (e.Y > Height) ScrollTo(scrollY + lh);
        if (selMode == 2) {
          int ri = RowAtY(e.Y);
          sR2 = ri; sO2 = ValOffset(Rows[ri], e.Location);
        } else if (selMode == 1) {
          sO2 = CharAtX(Rows[sR1].Key, KeyX(), e.X, fN); sR2 = sR1;
        }
        Invalidate();
        return;
      }
      int h = HitTest(e.Location);
      int kind = h != -1 ? 1 : (e.X >= KeyX() ? 2 : 0);
      if (kind != curKind) {
        curKind = kind;
        Cursor = kind == 1 ? Cursors.Hand : (kind == 2 ? Cursors.IBeam : Cursors.Default);
      }
      if (h != hover) { hover = h; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      if (hover != -1) { hover = -1; Cursor = Cursors.Default; Invalidate(); }
    }
    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      Focus();
      if (e.Button != MouseButtons.Left) return;
      int h = HitTest(e.Location);
      if (h == -2) { if (OnToggleWrap != null) OnToggleWrap(); return; }
      if (h == -3) { if (OnOpenIni != null) OnOpenIni(); return; }
      if (h >= 0) {
        string t = Rows[h].Copy;
        try { Clipboard.SetText(t.Length > 0 ? t : " "); } catch { return; }
        doneRow = h; doneTimer.Stop(); doneTimer.Start();
        Invalidate();
        return;
      }
      selMode = 0; dragging = false;
      if (Rows.Count > 0) {
        int ri = RowAtY(e.Y);
        if (e.X >= valueX) {
          selMode = 2; sR1 = sR2 = ri; sO1 = sO2 = ValOffset(Rows[ri], e.Location); dragging = true;
        } else if (e.X >= KeyX()) {
          Row r = Rows[ri];
          if (e.Y >= r.Top - scrollY && e.Y < r.Top - scrollY + lh) {
            selMode = 1; sR1 = sR2 = ri; sO1 = sO2 = CharAtX(r.Key, KeyX(), e.X, fN); dragging = true;
          }
        }
      }
      Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; }
    protected override void OnMouseDoubleClick(MouseEventArgs e) {
      base.OnMouseDoubleClick(e);
      if (e.Button != MouseButtons.Left || Rows.Count == 0 || HitTest(e.Location) != -1) return;
      int ri = RowAtY(e.Y);
      Row r = Rows[ri];
      if (e.X >= valueX) {
        int a, b; ExpandWord(r, ValOffset(r, e.Location), out a, out b);
        selMode = 2; sR1 = sR2 = ri; sO1 = a; sO2 = b; dragging = false; Invalidate();
      } else if (e.X >= KeyX()) {
        selMode = 1; sR1 = sR2 = ri; sO1 = 0; sO2 = r.Key.Length; dragging = false; Invalidate();
      }
    }
    protected override void OnMouseWheel(MouseEventArgs e) {
      base.OnMouseWheel(e);
      ScrollTo(scrollY - e.Delta / 120 * lh * 3);
    }
    protected override bool IsInputKey(Keys k) {
      switch (k & Keys.KeyCode) {
        case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: return true;
      }
      return base.IsInputKey(k);
    }
    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; return; }
      if (e.KeyCode == Keys.Escape && selMode != 0) { selMode = 0; Invalidate(); e.Handled = true; return; }
      if (e.Control && e.KeyCode == Keys.C) {
        string t = SelectedText();
        if (t.Length == 0) {
          StringBuilder sb = new StringBuilder();
          foreach (Row r in Rows) sb.AppendLine(r.Key + " : " + r.Copy.Replace("\r\n", "\r\n    "));
          t = sb.ToString();
        }
        try { Clipboard.SetText(t); } catch { }
        e.Handled = true; return;
      }
      switch (e.KeyCode) {
        case Keys.Up: ScrollTo(scrollY - lh * 3); break;
        case Keys.Down: ScrollTo(scrollY + lh * 3); break;
        case Keys.PageUp: ScrollTo(scrollY - Height + lh * 2); break;
        case Keys.PageDown: ScrollTo(scrollY + Height - lh * 2); break;
        case Keys.Home: ScrollTo(0); break;
        case Keys.End: ScrollTo(int.MaxValue / 2); break;
      }
    }

    // ---- painting ----
    static System.Drawing.Drawing2D.GraphicsPath RoundPath(RectangleF r, float rad) {
      System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
      float d = rad * 2;
      p.AddArc(r.X, r.Y, d, d, 180, 90);
      p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
      p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
      p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
      p.CloseFigure();
      return p;
    }

    // mode 0 = text button, 1 = copy icon, 2 = green check, 3 = gear
    void DrawBtn(Graphics g, Rectangle b, int mode, bool hot, string text, Color fore) {
      g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      Color border = mode == 2 ? C.Done : Color.FromArgb(75, 75, 78);
      Color fillCol = hot ? Color.FromArgb(62, 62, 66) : Color.FromArgb(42, 42, 45);
      RectangleF r = new RectangleF(b.X + 0.5f, b.Y + 0.5f, b.Width - 1, b.Height - 1);
      using (System.Drawing.Drawing2D.GraphicsPath gp = RoundPath(r, 6 * scale))
      using (SolidBrush fill = new SolidBrush(fillCol))
      using (Pen pen = new Pen(border, 1f)) { g.FillPath(fill, gp); g.DrawPath(pen, gp); }
      float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
      if (mode == 1) {
        float s = Math.Min(b.Width, b.Height) * 0.36f, o = s * 0.28f;
        using (Pen p = new Pen(Color.FromArgb(185, 185, 190), 1.4f))
        using (SolidBrush fill = new SolidBrush(fillCol)) {
          g.DrawRectangle(p, cx - s / 2 - o, cy - s / 2 - o, s, s);
          g.FillRectangle(fill, cx - s / 2 + o, cy - s / 2 + o, s, s);
          g.DrawRectangle(p, cx - s / 2 + o, cy - s / 2 + o, s, s);
        }
      } else if (mode == 3) {
        // six-tooth "shield" gear, solid with a hollow hub (same shape as move-pi-sessions / rclone-operator)
        GearShape.Fill(g, hot ? Color.FromArgb(230, 230, 230) : Color.FromArgb(160, 162, 168), cx, cy, Math.Min(b.Width, b.Height) * 0.66f);
      } else if (mode == 2) {
        float w = b.Width, h = b.Height;
        using (Pen p = new Pen(C.Done, 2.2f)) {
          p.StartCap = System.Drawing.Drawing2D.LineCap.Round; p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
          p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
          g.DrawLines(p, new PointF[] {
            new PointF(b.X + w * 0.28f, b.Y + h * 0.52f), new PointF(b.X + w * 0.44f, b.Y + h * 0.68f), new PointF(b.X + w * 0.74f, b.Y + h * 0.32f) });
        }
      }
      g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
      if (mode == 0)
        TextRenderer.DrawText(g, text, fW, b, fore,
          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      g.Clear(C.Bg);
      if (fN == null) return;
      Color[] kc = { C.Fg, C.Key, C.Value, C.Section, C.Dim };
      TextFormatFlags tf = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
      Rectangle valueClip = new Rectangle(valueX, 0, Math.Max(1, Width - valueX), Height);
      for (int i = 0; i < Rows.Count; i++) {
        Row r = Rows[i];
        int ry = r.Top - scrollY;
        if (ry > Height) break;
        if (ry + r.H < 0) continue;
        DrawBtn(g, BtnRect(r), doneRow == i ? 2 : 1, hover == i, null, C.Fg);
        if (selMode == 1 && sR1 == i && sO1 != sO2) {
          int ka = Math.Min(sO1, sO2), kb = Math.Min(Math.Max(sO1, sO2), r.Key.Length);
          int kx1 = KeyX() + Meas(r.Key.Substring(0, ka), fN), kx2 = KeyX() + Meas(r.Key.Substring(0, kb), fN);
          using (SolidBrush br = new SolidBrush(SelBg)) g.FillRectangle(br, kx1, ry, Math.Max(1, kx2 - kx1), lh);
        }
        TextRenderer.DrawText(g, r.Key, fN, new Rectangle(KeyX(), ry, keyW - (int)(8 * scale), lh), C.Key,
          (selMode == 1 && sR1 == i && sO1 != sO2) ? SelBg : C.Bg, tf | TextFormatFlags.EndEllipsis);
        g.SetClip(valueClip);
        int s0 = 0, e0 = -1;
        if (selMode == 2) {
          int r1, o1, r2, o2; SelRange(out r1, out o1, out r2, out o2);
          if (i >= r1 && i <= r2) { s0 = i == r1 ? o1 : 0; e0 = i == r2 ? o2 : r.Val.Length; }
        }
        foreach (VLine vl in r.VL) {
          int ly = ry + vl.Y;
          if (ly > Height || ly + lh < 0) continue;
          foreach (VSeg sg in vl.S) {
            int sx = valueX - scrollX + sg.X;
            int a = Math.Max(s0, sg.Off) - sg.Off, b = Math.Min(e0, sg.Off + sg.T.Length) - sg.Off;
            if (e0 >= 0 && b > a && !(sR1 == sR2 && sO1 == sO2))
              DrawSegSel(g, sg, sx, ly, kc[sg.K], Math.Max(0, a), Math.Min(sg.T.Length, b), tf);
            else
              TextRenderer.DrawText(g, sg.T, sg.K == 3 ? fB : fN, new Point(sx, ly), kc[sg.K], C.Bg, tf);
          }
        }
        g.ResetClip();
      }
      DrawBtn(g, iniRect, 3, hover == -3, null, C.Fg);
      DrawBtn(g, wrapRect, 0, hover == -2, C.Wrap ? "Wrap: ON" : "Wrap: OFF",
        C.Wrap ? Color.FromArgb(130, 210, 255) : Color.FromArgb(200, 200, 200));
    }
  }

  // Six-tooth shield gear: wide-root / narrow-tip trapezoid teeth, solid body, hub hole cut out.
  static class GearShape {
    public static void Fill(Graphics g, Color c, float cx, float cy, float size) {
      float tipR = size * 0.50f, rootR = size * 0.36f, holeR = size * 0.17f;
      float round = Math.Max(1f, size * 0.07f);   // rounded corners: stroke the outline with the same color
      tipR -= round / 2f;
      using (System.Drawing.Drawing2D.GraphicsPath outer = BuildOuter(cx, cy, tipR, rootR, 6))
      using (System.Drawing.Drawing2D.GraphicsPath body = (System.Drawing.Drawing2D.GraphicsPath)outer.Clone())
      using (SolidBrush br = new SolidBrush(c))
      using (Pen pen = new Pen(c, round)) {
        pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
        body.AddEllipse(cx - holeR, cy - holeR, holeR * 2f, holeR * 2f);
        body.FillMode = System.Drawing.Drawing2D.FillMode.Alternate;   // outline + inner circle => hole
        g.FillPath(br, body);
        g.DrawPath(pen, outer);
      }
    }

    static System.Drawing.Drawing2D.GraphicsPath BuildOuter(float cx, float cy, float tipR, float rootR, int teeth) {
      double period = 2.0 * Math.PI / teeth, halfRoot = period * 0.30, halfTip = period * 0.16;
      List<PointF> pts = new List<PointF>();
      for (int k = 0; k < teeth; k++) {
        double a = k * period - Math.PI / 2.0;
        pts.Add(Pt(cx, cy, rootR, a - halfRoot));
        pts.Add(Pt(cx, cy, tipR, a - halfTip));
        pts.Add(Pt(cx, cy, tipR, a + halfTip));
        pts.Add(Pt(cx, cy, rootR, a + halfRoot));
        double from = a + halfRoot, to = a + period - halfRoot;
        for (int i = 1; i < 4; i++) pts.Add(Pt(cx, cy, rootR, from + (to - from) * i / 4.0));
      }
      System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
      path.AddPolygon(pts.ToArray());
      return path;
    }

    static PointF Pt(float cx, float cy, float r, double ang) {
      return new PointF(cx + (float)(Math.Cos(ang) * r), cy + (float)(Math.Sin(ang) * r));
    }
  }

  // Open Explorer on the file's folder with the file selected (in-process call, faster than explorer /select).
  static class ShellReveal {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr ILCreateFromPathW(string pszPath);
    [DllImport("shell32.dll")]
    static extern void ILFree(IntPtr pidl);
    [DllImport("shell32.dll")]
    static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

    public static void SelectPath(string path) {
      if (string.IsNullOrEmpty(path)) return;
      string full;
      try { full = Path.GetFullPath(path); } catch { return; }
      IntPtr pidl = ILCreateFromPathW(full);
      if (pidl == IntPtr.Zero) {
        try {
          if (File.Exists(full)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + full + "\"") { UseShellExecute = true });
          else if (Directory.Exists(full)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + full + "\"") { UseShellExecute = true });
        } catch { }
        return;
      }
      try { SHOpenFolderAndSelectItems(pidl, 0, null, 0); }   // cidl = 0: pidl is the item itself, opens its parent and selects it
      finally { ILFree(pidl); }
    }
  }

  static class Program {
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args) {
      if (args.Length == 0 || !File.Exists(args[0])) return;
      string target = args[0];
      try { SetProcessDPIAware(); } catch { }   // avoids bitmap-scaled (blurry) text
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);

      string rawJson = "";
      try {
        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = "exiftool";
        psi.Arguments = "-j -G1 -fast -charset UTF8 \"" + target + "\"";
        psi.RedirectStandardOutput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        using (Process p = Process.Start(psi)) { rawJson = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
      } catch { return; }
      if (string.IsNullOrWhiteSpace(rawJson)) return;

      JNode root; int rootEnd;
      if (!JP.TryParseAt(rawJson, 0, out root, out rootEnd) || root.Kind != 1 || root.Items.Count == 0 || root.Items[0].Kind != 0) return;
      JNode obj = root.Items[0];

      string iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "viewexif-config.ini");
      Cfg cfg = new Cfg();
      cfg.Load(iniPath);

      List<Row> rows = new List<Row>();
      for (int ki = 0; ki < obj.Items.Count; ki++) {
        Row r = new Row();
        r.Key = JP.Decode(obj.Keys[ki]);
        r.Lines = FormatValue(JP.NodeText(rawJson, obj.Items[ki]), cfg);
        List<string> parts = new List<string>();
        foreach (Line ln in r.Lines) parts.Add(ln.Plain());
        r.Copy = string.Join("\r\n", parts.ToArray());
        List<string> vparts = new List<string>();
        foreach (Line ln in r.Lines) { StringBuilder lb = new StringBuilder(); foreach (Seg sg in ln.S) lb.Append(sg.T); vparts.Add(lb.ToString()); }
        r.Val = string.Join("\n", vparts.ToArray());
        rows.Add(r);
      }

      Form form = new Form();
      form.Text = "Metadata: " + Path.GetFileName(target);
      form.ShowIcon = false;
      form.BackColor = cfg.Bg;
      bool restored = false;
      if (cfg.X != int.MinValue) {
        Point pt = new Point(cfg.X, cfg.Y);
        if (Screen.FromPoint(pt).WorkingArea.Contains(pt)) {
          form.StartPosition = FormStartPosition.Manual;
          form.Location = pt; form.Size = new Size(cfg.W, cfg.H);
          restored = true;
        }
      }
      if (!restored) { form.StartPosition = FormStartPosition.CenterScreen; form.Size = new Size(cfg.W, cfg.H); }

      form.FormClosing += delegate {
        if (form.WindowState == FormWindowState.Normal) {
          cfg.X = form.Location.X; cfg.Y = form.Location.Y; cfg.W = form.Width; cfg.H = form.Height;
        }
        try { cfg.Save(iniPath); } catch { }
      };
      form.HandleCreated += delegate {
        int dark = 1;
        DwmSetWindowAttribute(form.Handle, 20, ref dark, 4);
        DwmSetWindowAttribute(form.Handle, 19, ref dark, 4);
      };

      Viewer v = new Viewer(cfg, rows);
      v.Dock = DockStyle.Fill;
      v.OnToggleWrap = delegate { cfg.Wrap = !cfg.Wrap; v.DoLayout(); };
      v.OnOpenIni = delegate {
        try {
          if (!File.Exists(iniPath)) cfg.Save(iniPath);   // first run: create it so there is something to select
          ShellReveal.SelectPath(iniPath);
        } catch { }
      };
      v.HS.Visible = !cfg.Wrap;
      form.Controls.Add(v);          // Fill first, then edge-docked scrollbars
      form.Controls.Add(v.VS);
      form.Controls.Add(v.HS);
      form.Shown += delegate { v.DarkScrollbars(); v.DoLayout(); v.Focus(); };
      Application.Run(form);
    }

    // ===================== value formatting =====================
    // Plain text is printed exactly as read. A { ... } / [ ... ] block is expanded into an indented tree
    // only when it is a complete, strictly valid JSON value (no dependency on the text before it).
    static List<Line> FormatValue(string text0, Cfg cfg) {
      List<Line> outp = new List<Line>();
      string text = text0.Trim();
      if (text.Length == 0) return outp;

      text = text.Replace("\r\n", "\n").Replace('\r', '\n');

      StringBuilder cur = new StringBuilder();
      int len = text.Length, i = 0;
      while (i < len) {
        char c = text[i];
        if (c == '{' || c == '[') {
          JNode node; int end;
          if (JP.TryParseAt(text, i, out node, out end) && Worth(node)) {
            FlushText(cur, outp, cfg);
            Emit(node, 1, null, outp);
            i = end;
            while (i < len && text[i] == '\n') i++;
            continue;
          }
        }
        if (c == '\n') { FlushText(cur, outp, cfg); i++; continue; }
        cur.Append(c); i++;
      }
      FlushText(cur, outp, cfg);
      return outp;
    }

    // Skip things that are valid JSON but not worth a tree: empty {} / [] and arrays of bare numbers/literals
    // (e.g. "[1]" or "[1920, 1080]" read better inline).
    static bool Worth(JNode n) {
      if (n.Items.Count == 0) return false;
      if (n.Kind == 0) return true;
      foreach (JNode it in n.Items) if (it.Kind != 3) return true;
      return false;
    }

    // Plain text is printed exactly as read from the file: its own line breaks are kept, nothing is inserted.
    static void FlushText(StringBuilder cur, List<Line> outp, Cfg cfg) {
      string s = cur.ToString().TrimEnd();
      cur.Length = 0;
      if (s.Trim().Length == 0) return;
      outp.Add(new Line(0).Add(s, 0));
    }

    // ===================== JSON tree -> indented lines, no brackets =====================
    // Keys, strings and numbers are shown as written in the source (no unescaping, no number re-formatting,
    // duplicate keys are all kept). Empty strings are shown as "" so they are not mistaken for "no value".
    static string ScalarText(JNode n) {
      if (n.Kind == 2 && n.Raw.Length == 0) return "\"\"";
      if (n.Kind == 0) return "{}";
      if (n.Kind == 1) return "[]";
      return n.Raw;
    }

    static void Emit(JNode n, int lv, string label, List<Line> outp) {
      if ((n.Kind == 0 || n.Kind == 1) && n.Items.Count > 0) {
        int clv = lv;
        if (label != null) { outp.Add(new Line(lv).Add(label, 1).Add(":", 4)); clv = lv + 1; }
        for (int i = 0; i < n.Items.Count; i++)
          Emit(n.Items[i], clv, n.Kind == 0 ? n.Keys[i] : "#" + (i + 1), outp);
      } else {
        Line ln = new Line(lv);
        if (label != null) ln.Add(label, 1).Add(": ", 4);
        ln.Add(ScalarText(n), 2);
        outp.Add(ln);
      }
    }
  }

  // ===================== strict JSON scanner (keeps source text) =====================
  // Kind: 0 object, 1 array, 2 string (Raw = text between the quotes, still escaped), 3 number/true/false/null (Raw = token)
  class JNode {
    public int Kind, S, E;               // S..E = source slice [S, E)
    public string Raw;
    public List<string> Keys = new List<string>();   // objects: raw keys, parallel to Items (duplicates kept)
    public List<JNode> Items = new List<JNode>();
  }

  static class JP {
    static readonly Regex NumRe = new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?");

    // Try to parse one JSON value starting at s[i]. end = index just after it.
    public static bool TryParseAt(string s, int i, out JNode node, out int end) {
      int p = i; end = i;
      if (!Val(s, ref p, 0, out node)) { node = null; return false; }
      end = p;
      return true;
    }

    static void Ws(string s, ref int p) {
      while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\n' || s[p] == '\r')) p++;
    }

    static bool Val(string s, ref int p, int depth, out JNode n) {
      n = null;
      if (depth > 200) return false;
      Ws(s, ref p);
      if (p >= s.Length) return false;
      char c = s[p];
      JNode node = new JNode(); node.S = p;
      if (c == '{' || c == '[') {
        char close = c == '{' ? '}' : ']';
        node.Kind = c == '{' ? 0 : 1;
        p++; Ws(s, ref p);
        if (p < s.Length && s[p] == close) { p++; node.E = p; n = node; return true; }
        while (true) {
          Ws(s, ref p);
          if (p >= s.Length) return false;
          if (node.Kind == 0) {
            if (s[p] != '"') return false;
            string k;
            if (!Str(s, ref p, out k)) return false;
            Ws(s, ref p);
            if (p >= s.Length || s[p] != ':') return false;
            p++;
            node.Keys.Add(k);
          }
          JNode v;
          if (!Val(s, ref p, depth + 1, out v)) return false;
          node.Items.Add(v);
          Ws(s, ref p);
          if (p >= s.Length) return false;
          if (s[p] == ',') { p++; continue; }
          if (s[p] == close) { p++; break; }
          return false;
        }
        node.E = p; n = node; return true;
      }
      if (c == '"') {
        string raw;
        if (!Str(s, ref p, out raw)) return false;
        node.Kind = 2; node.Raw = raw; node.E = p; n = node; return true;
      }
      node.Kind = 3;
      Match m = NumRe.Match(s, p);
      if (m.Success && m.Length > 0) { node.Raw = m.Value; p += m.Length; node.E = p; n = node; return true; }
      string[] lits = { "true", "false", "null" };
      foreach (string l in lits)
        if (string.CompareOrdinal(s, p, l, 0, l.Length) == 0) { node.Raw = l; p += l.Length; node.E = p; n = node; return true; }
      return false;
    }

    // s[p] is the opening quote. raw = text between the quotes (escapes untouched).
    static bool Str(string s, ref int p, out string raw) {
      raw = null;
      int q = p + 1;
      while (q < s.Length) {
        char ch = s[q];
        if (ch == '"') { raw = s.Substring(p + 1, q - p - 1); p = q + 1; return true; }
        if (ch < ' ') return false;
        if (ch == '\\') {
          q++;
          if (q >= s.Length) return false;
          char e = s[q];
          if ("\"\\/bfnrt".IndexOf(e) >= 0) q++;
          else if (e == 'u') {
            if (q + 4 >= s.Length) return false;
            for (int k = 1; k <= 4; k++) if (Uri.IsHexDigit(s[q + k]) == false) return false;
            q += 5;
          } else return false;
        } else q++;
      }
      return false;
    }

    // JSON string unescape (used only for exiftool's own top-level strings, e.g. "\n" -> real newline)
    public static string Decode(string raw) {
      if (raw.IndexOf('\\') < 0) return raw;
      StringBuilder sb = new StringBuilder();
      for (int i = 0; i < raw.Length; i++) {
        char c = raw[i];
        if (c != '\\' || i + 1 >= raw.Length) { sb.Append(c); continue; }
        char e = raw[++i];
        switch (e) {
          case 'n': sb.Append('\n'); break;
          case 'r': sb.Append('\r'); break;
          case 't': sb.Append('\t'); break;
          case 'b': sb.Append('\b'); break;
          case 'f': sb.Append('\f'); break;
          case 'u': sb.Append((char)Convert.ToInt32(raw.Substring(i + 1, 4), 16)); i += 4; break;
          default: sb.Append(e); break;   // \" \\ \/
        }
      }
      return sb.ToString();
    }

    // Text of a top-level exiftool value: strings unescaped, numbers/literals as written, lists joined with ", ".
    public static string NodeText(string src, JNode n) {
      if (n.Kind == 2) return Decode(n.Raw);
      if (n.Kind == 3) return n.Raw;
      if (n.Kind == 1) {
        List<string> parts = new List<string>();
        foreach (JNode it in n.Items) parts.Add(NodeText(src, it));
        return string.Join(", ", parts.ToArray());
      }
      return src.Substring(n.S, n.E - n.S);
    }
  }
}
