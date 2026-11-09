using System;
using System.Collections.Generic;

namespace WtClickerGUI
{
    /// <summary>
    /// 宝箱刷新倒计时读取（2026-10-30）：
    /// 应用内「总是展示宝箱」开启后，达到领取上限时桌宠旁会出现 mm:ss 倒计时气泡
    /// （主题色会随桌宠变化：奶白/蓝等，故用亮度掩码而非颜色签名）。
    /// 流程：浅色填充簇（55-115 x 22-58）→ 内缩取文字行 → 列分段 → 5 字形
    /// （数字 数字 冒号 数字 数字）→ 逐字形归一化 16x24 与内嵌模板 MAD 匹配。
    /// 模板由实机倒计时连拍 + 已知刷新时刻锚定自动采集（秒位两轮循环覆盖 0-9）。
    /// </summary>
    internal static class CountdownReader
    {
        const int GW = 16, GH = 24;
        internal static int GlyphW => GW;
        internal static int GlyphH => GH;
        internal static float TplAt(int g, int i) => _tpl[g * GW * GH + i];
        static readonly float[] _tpl = new float[11 * GW * GH];   // 0-9 + ':'
        static bool _loaded;

        static string TplB64()
        {
            return
                "H+Af8D74/Hz4HPAc4B7gH+AP4AfgB+AH4AfgB+AH4A/gH+Ae8Bz4HP/8P/gf8B/gA/8H///////8fwB/AH8AfwB/AH8AfwB/"
                + "AH8AfwB/AH8AfwB/AH8AfwB/AH8AfwB///D/+Pz8+H8APwAfAA8ADwAfADwAPAA8APAB8APAB4APAD4AfADwAP//////////"
                + "P/g/+Dh8ED8AHwAPAA8ADwAPABwP+A/4AHwAfgAPAA8ADwAPAA8AD/////x/+D/4AHgA+AH4AfgB+AO4B7gOOA44DjgcOBw4"
                + "ODhwOHA4/////3//P/8AOAA4ADgAOAA4P/8//z/8P/g4ADgAOAA4AD/wP/g//D/+AD8AHwAPAA8ADwAPAA8AH/////x/+D/4"
                + "H/wf/D+c/wj4APAA8ADwAPPw9/j//P/+8D/wH/AP8A/wD/AP8A/wH///P/wf+B/4//////////8ADwA+ADwAPAB8APwA+ADw"
                + "APAB8APAA8ADwA/AD8APAB8APwA+ADwAH/gf+D58/Dz4HPAc8Bx4HDw8Pnwf8B/wPnh8OPgc8A/gB+AH4A/gHvx8Pvwf+B/4"
                + "P/h/+Pn88PzwHPAP4A/AD+AP8A/wH/A/f/8//x+PDw8ADwAeABwAHP/8//h/4D/gB+AP8P//////+P/g/4AAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAGAB+Af+P//////8P/g";
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            byte[] raw = Convert.FromBase64String(TplB64());
            for (int g = 0; g < 11; g++)
                for (int i = 0; i < GW * GH; i++)
                {
                    int bit = (raw[g * 48 + i / 8] >> (7 - i % 8)) & 1;
                    _tpl[g * GW * GH + i] = bit;
                }
            _loaded = true;
        }

        /// <summary>倒计时气泡读取结果</summary>
        public class Countdown { public int Seconds; public int X0, Y0, X1, Y1; }

        /// <summary>在一帧中找所有倒计时气泡（帧内坐标）</summary>
        public static List<Countdown> Find(Frame f)
        {
            EnsureLoaded();
            var result = new List<Countdown>();
            if (f == null || f.Bits == null) return result;
            int w = f.W, h = f.H;
            var bits = f.Bits;

            // 浅色填充掩码（主题无关：三通道都亮）
            var cells = new HashSet<(int, int)>();
            for (int y = 0; y + 1 < h; y += 2)
            {
                int row = y * w * 4;
                for (int x = 0; x + 1 < w; x += 2)
                {
                    int i = row + x * 4;
                    int mn = bits[i];
                    if (bits[i + 1] < mn) mn = bits[i + 1];
                    if (bits[i + 2] < mn) mn = bits[i + 2];
                    if (mn > 180) cells.Add((x, y));
                }
            }

            var seen = new HashSet<(int, int)>();
            foreach (var p in cells)
            {
                if (seen.Contains(p)) continue;
                var comp = new List<(int, int)>();
                var stack = new Stack<(int, int)>();
                stack.Push(p); seen.Add(p);
                int x0 = p.Item1, y0 = p.Item2, x1 = p.Item1, y1 = p.Item2;
                while (stack.Count > 0)
                {
                    var c = stack.Pop(); comp.Add(c);
                    if (c.Item1 < x0) x0 = c.Item1;
                    if (c.Item1 > x1) x1 = c.Item1;
                    if (c.Item2 < y0) y0 = c.Item2;
                    if (c.Item2 > y1) y1 = c.Item2;
                    for (int dx = -2; dx <= 2; dx += 2)
                        for (int dy = -2; dy <= 2; dy += 2)
                        {
                            var n = (c.Item1 + dx, c.Item2 + dy);
                            if (cells.Contains(n) && !seen.Contains(n)) { seen.Add(n); stack.Push(n); }
                        }
                }
                if (comp.Count < 200) continue;
                int bw = x1 - x0, bh = y1 - y0;
                if (bw < 55 || bw > 115 || bh < 22 || bh > 58) continue;

                var glyphs = ExtractGlyphs(bits, w, x0, y0, x1, y1);
                if (glyphs == null) continue;

                // 逐字形识别
                int mm = 0, ss = 0;
                if (!MatchDigits(glyphs, ref mm, ref ss)) continue;
                result.Add(new Countdown { Seconds = mm * 60 + ss, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 });
            }
            return result;
        }

        /// <summary>气泡内缩后按列分段，要求 5 段（数字 数字 冒号 数字 数字）</summary>
        static List<bool[]> ExtractGlyphs(byte[] bits, int w, int x0, int y0, int x1, int y1)
        {
            int bh = y1 - y0, bw = x1 - x0;
            int ty0 = y0 + (int)(bh * 0.24), ty1 = y1 - (int)(bh * 0.26);
            int tx0 = x0 + (int)(bw * 0.12), tx1 = x1 - (int)(bw * 0.10);
            int rh = ty1 - ty0, rw = tx1 - tx0;
            if (rh <= 8 || rw <= 20) return null;

            var dark = new bool[rh, rw];
            var colSum = new int[rw];
            for (int y = 0; y < rh; y++)
            {
                int row = (ty0 + y) * w * 4;
                for (int x = 0; x < rw; x++)
                {
                    int i = row + (tx0 + x) * 4;
                    int lum = (bits[i] + bits[i + 1] + bits[i + 2]) / 3;
                    if (lum < 150) { dark[y, x] = true; colSum[x]++; }
                }
            }
            // 列分段
            var segs = new List<(int s, int e)>();
            bool inSeg = false; int st = 0;
            for (int x = 0; x < rw; x++)
            {
                if (colSum[x] > 0 && !inSeg) { inSeg = true; st = x; }
                else if (colSum[x] == 0 && inSeg) { inSeg = false; segs.Add((st, x)); }
            }
            if (inSeg) segs.Add((st, rw));
            segs.RemoveAll(g => g.e - g.s < 2);
            if (segs.Count != 5) return null;

            int[] ws = new int[5];
            for (int i = 0; i < 5; i++) ws[i] = segs[i].e - segs[i].s;
            int minW = int.MaxValue, minI = -1;
            for (int i = 0; i < 5; i++) if (ws[i] < minW) { minW = ws[i]; minI = i; }
            // 冒号须为最窄段且严格窄于相邻数字（10-31 修正：原「< 首位宽/2」在
            // 首位为 1 时（10:00–19:59，5px 窄字形）误杀全部气泡 → 检测漏报 8s
            // → 引擎误判气泡消失恢复连点。冒号合法性由 MatchDigits 的 ':' 模板兜底）
            if (minI != 2 || ws[2] >= Math.Min(ws[1], ws[3])) return null;

            var glyphs = new List<bool[]>();
            foreach (var (s, e) in segs)
            {
                int gy0 = int.MaxValue, gy1 = int.MinValue;
                for (int y = 0; y < rh; y++)
                    for (int x = s; x < e; x++)
                        if (dark[y, x]) { if (y < gy0) gy0 = y; if (y > gy1) gy1 = y; }
                if (gy0 > gy1) return null;
                glyphs.Add(Normalize(dark, s, e, gy0, gy1));
            }
            return glyphs;
        }

        /// <summary>字形裁剪 → 最近邻缩放至 16x24</summary>
        static bool[] Normalize(bool[,] dark, int xs, int xe, int ys, int ye)
        {
            int gw = xe - xs, gh = ye - ys + 1;
            var dst = new bool[GW * GH];
            for (int y = 0; y < GH; y++)
            {
                int sy = Math.Min(gh - 1, y * gh / GH);
                for (int x = 0; x < GW; x++)
                {
                    int sx = Math.Min(gw - 1, x * gw / GW);
                    dst[y * GW + x] = dark[ys + sy, xs + sx];
                }
            }
            return dst;
        }

        static bool MatchDigits(List<bool[]> glyphs, ref int mm, ref int ss)
        {
            int[] vals = new int[5];
            for (int i = 0; i < 5; i++)
            {
                int best = -1; double bestMad = double.MaxValue, second = double.MaxValue;
                for (int g = 0; g < 11; g++)
                {
                    double sum = 0;
                    var gl = glyphs[i];
                    for (int k = 0; k < GW * GH; k++)
                        sum += gl[k] != (_tpl[g * GW * GH + k] > 0.5) ? 1 : 0;
                    double mad = sum / (GW * GH);
                    if (mad < bestMad) { second = bestMad; bestMad = mad; best = g; }
                    else if (mad < second) second = mad;
                }
                if (best < 0) return false;
                vals[i] = best == 10 ? -1 : best;   // 10 = ':'
            }
            if (vals[2] != -1) return false;
            mm = vals[0] * 10 + vals[1];
            ss = vals[3] * 10 + vals[4];
            return mm <= 30 && ss <= 59;            // 上限 30:00
        }
    }
}
