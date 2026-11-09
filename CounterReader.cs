using System;
using System.Collections.Generic;
using System.Linq;

namespace WtClickerGUI
{
    /// <summary>
    /// 桌宠「当前点击次数」胶囊读取 v5（2026-10-31 晚，蓝色皮肤 + 高带融合版）：
    /// 本机桌宠底栏结构 = [计数数字][✦圆钮][☰圆钮]，无用户名；其他玩家名牌只有用户名。
    /// v5 起因（实机落盘帧 ctr_fail_1.bin 确诊，两个独立致因）：
    ///   ① 用户切换桌宠皮肤后，✦ 圆钮由「奶油底+深色星隙」变为「黑圆钮+浅蓝星」，
    ///      旧 ✦ 模式要求「星列必现」失配；
    ///   ② 桌宠站到其他宠物上方/相邻时，胶囊行带与桌宠身体、名牌融成 190px+ 高带，
    ///      旧「行带高 24~70px」上限直接跳过 —— 两种情况都导致次数永远「未读取」。
    /// 算法：
    ///   ① 浅色掩码（三通道最小值 > 180，步长 2 网格）+ 行方向前缀和 →
    ///      任意行窗口的列光剖面 O(1)/列；
    ///   ② 行带 = 行光 ≥40 格的极大连续段（高 ≥24px，上限 640px，不再限 70px）；
    ///   ③ 高度 ≤70px 的带整带扫描；更高的带用滑窗（高 40/54/68px、步进 4px）
    ///      逐窗扫描 —— 胶囊行带被融合时仍能命中；
    ///   ④ ✦ 锚点统一为：全光段(≥40px) → 64px 内出现 ≥4 格黑隙(L≤0.15×峰值)
    ///      → 回到全光（旧主题经星列回光、新主题经黑钮回光，均匹配；
    ///      名牌误锚由 OCR 模板 MAD 拒绝兜底）；
    ///   ⑤ OCR：右起收集、大间隔截断、粘连拆分、模板匹配（16x24）；
    ///      字形竖直范围取「最大连续活动行段」抗悬垂描边污染，
    ///      高度过滤自适应（≥8px 且 ≥0.5×最高字形），最左字形失败可丢弃重试。
    /// </summary>
    internal static class CounterReader
    {
        public class CounterRead
        {
            public long Value;
            public int Digits;
            public int X0, Y0, X1, Y1;
        }

        const int GridStep = 2;          // 采样步长（px）
        const int BandMinRows = 12;      // 行带最小高（网格行，=24px）
        const int BandMaxRows = 320;     // 行带最大高（=640px）
        static readonly int[] WinRows = { 20, 27, 34 };   // 滑窗高度（网格行 = 40/54/68px）

        public static List<CounterRead> Find(Frame f)
        {
            var result = new List<CounterRead>();
            if (f == null || f.Bits == null) return result;
            CountdownReader.EnsureLoaded();
            int w = f.W, h = f.H;
            var bits = f.Bits;
            int gh = (h + GridStep - 1) / GridStep, gw = (w + GridStep - 1) / GridStep;

            // ---- 浅色掩码 + 行前缀和：S[iy+1, ix] = 网格行 0..iy 中列 ix 的浅色格数
            var S = new int[gh + 1, gw];
            var rowL = new int[gh];
            for (int iy = 0; iy < gh; iy++)
            {
                int y = iy * GridStep, row = y * w * 4, cnt = 0;
                for (int ix = 0; ix < gw; ix++)
                {
                    // 网格列 ix 对应像素 x=ix*2，字节偏移 = x*4 = ix*8
                    //（曾误写 ix*4 导致只扫每行左半屏且坐标错位 —— v5 首版全灭的根因）
                    int i = row + ix * 8;
                    int mn = bits[i];
                    if (bits[i + 1] < mn) mn = bits[i + 1];
                    if (bits[i + 2] < mn) mn = bits[i + 2];
                    if (mn > 180) { cnt++; S[iy + 1, ix] = S[iy, ix] + 1; }
                    else S[iy + 1, ix] = S[iy, ix];
                }
                rowL[iy] = cnt;
            }

            // ---- 行带：行光 ≥40 格的极大连续段
            var bands = new List<(int g0, int g1)>();
            int bs = -1;
            for (int iy = 0; iy < gh; iy++)
            {
                if (rowL[iy] >= 40)
                {
                    if (bs < 0) bs = iy;
                }
                else if (bs >= 0)
                {
                    if (iy - bs >= BandMinRows) bands.Add((bs, iy - 1));
                    bs = -1;
                }
            }
            if (bs >= 0 && gh - bs >= BandMinRows) bands.Add((bs, gh - 1));

            bool dbg = Environment.GetEnvironmentVariable("CTR_DEBUG") == "1";
            if (dbg) Console.Error.WriteLine($"[CTR] bands=" +
                string.Join(";", bands.Select(b => $"[{b.g0 * 2},{b.g1 * 2}]h{(b.g1 - b.g0 + 1) * 2}")));

            foreach (var (g0, g1) in bands)
            {
                int rows = g1 - g0 + 1;
                if (rows > BandMaxRows) continue;

                // 窗口列表：矮带整带；高带滑窗
                var wins = new List<(int wy0, int wy1)>();
                if (rows <= 35)
                {
                    wins.Add((g0, g1));
                }
                else
                {
                    foreach (int wh in WinRows)
                        for (int wy0 = g0; wy0 + wh - 1 <= g1; wy0 += 2)
                            wins.Add((wy0, wy0 + wh - 1));
                }

                foreach (var (wy0, wy1) in wins)
                    ScanWindow(f, S, gw, wy0, wy1, result, dbg);
            }

            // ---- 去重：60px 内的同位置读数只留一个
            var dedup = new List<CounterRead>();
            foreach (var r in result)
                if (!dedup.Any(d => Math.Abs(d.X0 - r.X0) < 60 && Math.Abs(d.Y0 - r.Y0) < 60))
                    dedup.Add(r);
            return dedup;
        }

        /// <summary>对一个行窗口做列光剖面（前缀和 O(1)/列）+ ✦/黑钮锚点扫描 + OCR</summary>
        static void ScanWindow(Frame f, int[,] S, int gw, int wy0, int wy1,
            List<CounterRead> result, bool dbg)
        {
            int by0 = wy0 * GridStep, by1 = wy1 * GridStep;
            int nc = gw;
            var L = new int[nc];
            var xs = new int[nc];
            int maxL = 0;
            for (int ix = 0; ix < nc; ix++)
            {
                xs[ix] = ix * GridStep;
                L[ix] = S[wy1 + 1, ix] - S[wy0, ix];
                if (L[ix] > maxL) maxL = L[ix];
            }
            if (maxL < 12) return;

            // 调试：CTR_WIN=by0,by1 指定窗口打印列光剖面
            string dbgWin = Environment.GetEnvironmentVariable("CTR_WIN");
            if (dbgWin == $"{by0},{by1}")
                Console.Error.WriteLine("[CTR] profile " + string.Join(",",
                    Enumerable.Range(0, nc).Where(ix => L[ix] > 0).Select(ix => $"{xs[ix]}:{L[ix]}")));

            double thFull = 0.5 * maxL, thGap = 0.45 * maxL, thLeft = 0.35 * maxL;

            int i2 = 0;
            while (i2 < nc)
            {
                if (L[i2] < thFull) { i2++; continue; }
                int j = i2;
                while (j < nc && L[j] >= thFull) j++;
                if (xs[j - 1] - xs[i2] >= 40)
                {
                    // 统一锚点：全光段结束后 80px 内，≥4 格显著暗列 + 回到全光
                    // （旧主题 = 黑隙→星列→☰圆钮，回光最远 ~50+28=78px；
                    //   新主题 = 黑圆钮→☰区，均匹配）。
                    // 黑隙阈值取 0.45×maxL：新皮肤黑钮内嵌浅蓝星（列光 5~12），
                    // 旧阈值 0.15×maxL 会被星形像素打断。名牌误锚由 OCR MAD 兜底。
                    int k = j, gapcnt = 0; bool hit = false;
                    while (k < nc && xs[k] - xs[j] < 80)
                    {
                        if (L[k] <= thGap) gapcnt++;
                        else if (L[k] >= thFull && gapcnt >= 4) { hit = true; break; }
                        k++;
                    }
                    if (hit)
                    {
                        if (dbg) Console.Error.WriteLine(
                            $"[CTR] win[{by0},{by1}] 锚点 capRight={xs[j - 1]} gapcnt={gapcnt} maxL={maxL}");
                        // 左缘：低阈值扫描，容 2 格（4px）缺口
                        int p = j - 1, miss = 0, iLeft = j - 1;
                        while (p > 0)
                        {
                            p--;
                            if (L[p] >= thLeft) { iLeft = p; miss = 0; }
                            else if (++miss > 2) break;
                        }
                        int capW = xs[j - 1] - xs[iLeft];
                        if (capW >= 60 && capW <= 500)
                            TryOcr(f, xs[iLeft], xs[j - 1], by0, by1, result);
                    }
                }
                i2 = j;
            }

            // ---- 黑圆钮锚点（2026-10-31 23:10，合并名牌带场景）：本机胶囊挤进联机
            //      名牌长带时，按钮列光被圆钮内浅色星星垫高（实测列光 15 > 阈值 10.8），
            //      「暗隙→回光」锚点失配。改找圆钮本体：实心近黑圆盘 = 连续 ≥12 网格列、
            //      每列窗口内 mn<90 的暗行 ≥13。数字笔画（宽 2~4 列、暗行 ~10）与
            //      ☰ 细横条、名牌文字都达不到；宠物深色块由 OCR 模板 MAD 兜底剔除。
            //      OCR 右缘强制止于圆钮左缘，星星像素不进段列表（根治 22:17 帧的
            //      「圆钮左缘细段截断从右收集」问题）。
            //      始终执行（不因轻跑锚点命中而跳过）：合并名牌带里轻跑锚点会命中
            //      但 capW 超限被拒（实测 capW=630>500），真正能读出数字的是本锚点。
            //      DminRows=10（像素，逐行统计）：圆钮是「黑环+橙星心」，环的每列
            //      连续暗行被星形截断成两段 13~15px（实测）；若按网格步长2采样只剩
            //      6~7 个样本永远不达标。数字竖笔画 ~18px 虽达标但仅宽 2~4px
            //      （1~2 网格列），被 DminCols=12 排除；整列黑背景区由 DmaxCols 兜底。
            {
                const int DminRows = 10, DminCols = 12, DmaxCols = 26;
                var darkRun = new int[nc];
                var fbits = f.Bits;
                int fw = f.W, fh = f.H;
                for (int ix = 0; ix < nc; ix++)
                {
                    int px = ix * GridStep;
                    if (px >= fw) { darkRun[ix] = 0; continue; }
                    int best = 0, cur = 0;
                    for (int py = by0; py <= by1; py++)
                    {
                        if (py >= fh) break;
                        int i = (py * fw + px) * 4;
                        int mn = fbits[i];
                        if (fbits[i + 1] < mn) mn = fbits[i + 1];
                        if (fbits[i + 2] < mn) mn = fbits[i + 2];
                        if (mn < 90) { cur++; if (cur > best) best = cur; }
                        else cur = 0;
                    }
                    darkRun[ix] = best;
                }
                int runCols = 0;
                for (int ix = 0; ix <= nc; ix++)
                {
                    bool on = ix < nc && darkRun[ix] >= DminRows;
                    if (on) { runCols++; continue; }
                    if (runCols >= DminCols && runCols <= DmaxCols)
                    {
                        int circleL = (ix - runCols) * GridStep, circleR = (ix - 1) * GridStep;
                        if (circleL - 190 >= 0)
                        {
                            if (dbg) Console.Error.WriteLine(
                                $"[CTR] win[{by0},{by1}] 黑圆钮锚点 x[{circleL},{circleR}] 暗行={darkRun[ix - runCols + runCols / 2]}");
                            TryOcr(f, circleL - 190, circleR + 2, by0, by1, result, circleL - 4);
                        }
                    }
                    runCols = 0;
                }
            }
        }

        /// <summary>对 [capLeft,capRight]×[by0,by1] 的数字胶囊 OCR：行活动分组 + 右起收集、大间隔截断、粘连拆分、模板匹配。
        /// tx1Override &gt; 0 时强制 OCR 右缘（黑圆钮锚点用：止于圆钮左缘，星形像素不进段列表）。
        /// v6 行分组（2026-11-01 实机失败帧 dist/ctr_fail_1.bin 确诊）：桌宠移动后身体/头发悬垂进
        /// OCR 窗口顶部，其暗像素恰好垫高数字间隙列的 colSum → 「3+4」「1+3」粘成 w29/w34 宽段，
        /// 粘连拆分切缝错位 → MAD 全拒 → 未读取。数字与悬垂污染之间有 ≥6 行净空带，按行活动度
        /// 分组后组内重切分即可干净分离；自下而上尝试（宠物在胶囊上方，数字组在最下），命中即返回。</summary>
        static void TryOcr(Frame f, int capLeft, int capRight, int by0, int by1, List<CounterRead> result,
            int tx1Override = -1)
        {
            int w = f.W;
            var bits = f.Bits;
            int bh = by1 - by0;
            int capW = capRight - capLeft;
            int tx0 = capLeft + (int)(capW * 0.03);
            int tx1 = tx1Override > 0 ? tx1Override : capRight - (int)(capW * 0.02);
            // 温和竖直裁剪（悬垂描边污染主要由行分组兜底）；蓝皮肤数字
            // RGB≈(21,26,37)、暖皮肤 ≈(74,52,35)，lum<135 均可靠检出
            int ty0 = by0 + (int)(bh * 0.20), ty1 = by1 - (int)(bh * 0.12);
            int rh = ty1 - ty0, rw = tx1 - tx0;
            if (rh < 14 || rw < 24) return;

            var dark = new bool[rh, rw];
            for (int y = 0; y < rh; y++)
            {
                int row = (ty0 + y) * w * 4;
                for (int x = 0; x < rw; x++)
                {
                    int i = row + (tx0 + x) * 4;
                    int lum = (bits[i] + bits[i + 1] + bits[i + 2]) / 3;
                    dark[y, x] = lum < 135;
                }
            }
            // 水平边框线剔除：整行暗像素占比过高的行是胶囊描边弧线（会把所有
            // 字形粘连成一个大段 → 无法切分）。v4 靠更紧的竖直裁剪避开，v5 裁剪
            // 放宽后改为此行级过滤（数字行的暗像素只占宽度 ~10-20%，不会误伤）。
            for (int y = 0; y < rh; y++)
            {
                int dc = 0;
                for (int x = 0; x < rw; x++) if (dark[y, x]) dc++;
                if (dc > rw * 0.6)
                    for (int x = 0; x < rw; x++) dark[y, x] = false;
            }

            // ---- v6 行活动分组：数字组与悬垂污染组之间有净空行带
            double digitW = 0.42 * rh;   // 粘连拆分估宽仍按完整窗口高（与 v5 一致：
                                         // 单数字 w~12 < 1.5×估宽不拆，双数字 w29/34 拆 2）
            var groups = new List<(int g0, int g1)>();
            {
                var rowAct = new int[rh];
                for (int y = 0; y < rh; y++)
                {
                    int c = 0;
                    for (int x = 0; x < rw; x++) if (dark[y, x]) c++;
                    rowAct[y] = c;
                }
                int s = -1;
                for (int y = 0; y < rh; y++)
                {
                    if (rowAct[y] >= 2) { if (s < 0) s = y; }
                    else if (s >= 0) { if (y - s >= 6) groups.Add((s, y - 1)); s = -1; }
                }
                if (s >= 0 && rh - s >= 6) groups.Add((s, rh - 1));
            }
            for (int gi = groups.Count - 1; gi >= 0; gi--)      // 自下而上
            {
                int g0 = groups[gi].g0, g1 = groups[gi].g1;
                var dark2 = new bool[g1 - g0 + 1, rw];
                for (int y = g0; y <= g1; y++)
                    for (int x = 0; x < rw; x++) dark2[y - g0, x] = dark[y, x];
                if (OcrBand(dark2, tx0, ty0 + g0, digitW, capLeft, capRight, by0, by1, result))
                    return;
            }
        }

        /// <summary>在给定行组（dark 第一维 = 组内行）内切分并匹配数字。返回是否产出读数。</summary>
        static bool OcrBand(bool[,] dark, int tx0, int ty0, double digitW,
            int capLeft, int capRight, int by0, int by1, List<CounterRead> result)
        {
            int rh = dark.GetLength(0), rw = dark.GetLength(1);
            var colSum = new int[rw];
            for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                    if (dark[y, x]) colSum[x]++;
            var segs = new List<(int s, int e)>();
            bool inSeg = false; int st = 0;
            for (int x = 0; x < rw; x++)
            {
                if (colSum[x] > 0 && !inSeg) { inSeg = true; st = x; }
                else if (colSum[x] == 0 && inSeg) { inSeg = false; segs.Add((st, x)); }
            }
            if (inSeg) segs.Add((st, rw));
            segs.RemoveAll(g => g.e - g.s < 3);
            if (segs.Count == 0) return false;
            bool dbg = Environment.GetEnvironmentVariable("CTR_DEBUG") == "1";

            // 非字形段预过滤（2026-10-31 22:xx 奶油皮肤实测教训）：✦ 星形按钮中心
            // 只有 1 行暗像素，但它排在段列表最右——「从右往左收集」从它起步后
            // 立即被 0.9×rh 大间隔截断，数字段全部落选 → 整读失败（次数永久过期）。
            // 组内行高≈数字实高，字形应占组高 ≥40%；阈值下限 4 行。
            segs.RemoveAll(g =>
            {
                int act = Math.Max(2, (g.e - g.s) / 8);
                int bestLen = 0, curLen = 0;
                for (int y = 0; y < rh; y++)
                {
                    int c = 0;
                    for (int x = g.s; x < g.e; x++) if (dark[y, x]) c++;
                    if (c >= act) { curLen++; if (curLen > bestLen) bestLen = curLen; }
                    else curLen = 0;
                }
                bool rej = bestLen < Math.Max(4, rh * 0.4);
                if (dbg && rej)
                    Console.Error.WriteLine($"[CTR]   非字形段 x[{g.s + tx0},{g.e + tx0}] 最大连续活动行={bestLen}<{Math.Max(4, (int)(rh * 0.4))} 剔除");
                return rej;
            });
            if (segs.Count == 0) return false;
            if (dbg) Console.Error.WriteLine($"[CTR]   OCR band ty[{ty0},{ty0 + rh}] rh={rh} 段=" +
                string.Join(",", segs.Select(g => $"({g.s + tx0},{g.e + tx0})w{g.e - g.s}")));

            // 从右往左收集：遇到大间隔（用户名与计数的布局间隔）即停
            var picked = new List<(int s, int e)>();
            for (int gi = segs.Count - 1; gi >= 0; gi--)
            {
                if (picked.Count > 0)
                {
                    int gapPx = segs[gi + 1].s - segs[gi].e;
                    if (gapPx > 0.9 * rh) break;
                }
                picked.Insert(0, segs[gi]);
            }
            if (picked.Count == 0) return false;

            // 粘连拆分：宽段按估计字宽均分
            var glyphs = new List<(int s, int e)>();
            foreach (var (s, e) in picked)
            {
                int gw2 = e - s;
                int parts = Math.Max(1, (int)Math.Round(gw2 / digitW));
                if (parts <= 1) { glyphs.Add((s, e)); continue; }
                for (int q = 0; q < parts; q++)
                    glyphs.Add((s + (int)Math.Round(gw2 * q / (double)parts),
                                s + (int)Math.Round(gw2 * (q + 1) / (double)parts)));
            }
            if (glyphs.Count == 0 || glyphs.Count > 8) return false;

            // ---- 每字形竖直范围 = 最大连续活动行段（行内暗像素 ≥ max(2, 段宽/8) 视为活动）。
            //      悬垂的桌宠耳朵/尾巴描边与数字仅个别行相连时，孤立碎行被排除。
            int n = glyphs.Count;
            var gy0s = new int[n]; var gy1s = new int[n];
            int maxH = 0;
            for (int gi = 0; gi < n; gi++)
            {
                var (s, e) = glyphs[gi];
                int act = Math.Max(2, (e - s) / 8);
                int bestLen = 0, bestS = -1, curS = -1, curLen = 0;
                for (int y = 0; y < rh; y++)
                {
                    int c = 0;
                    for (int x = s; x < e; x++) if (dark[y, x]) c++;
                    if (c >= act)
                    {
                        if (curS < 0) { curS = y; curLen = 1; } else curLen++;
                        if (curLen > bestLen) { bestLen = curLen; bestS = curS; }
                    }
                    else curS = -1;
                }
                if (bestS < 0) { gy0s[gi] = -1; gy1s[gi] = -1; continue; }
                gy0s[gi] = bestS; gy1s[gi] = bestS + bestLen - 1;
                if (bestLen > maxH) maxH = bestLen;
            }
            // 高度过滤：≥8px 且 ≥0.5×最高字形（胶囊圆角边框、碎描边被剔除）
            int minH = Math.Max(8, maxH / 2);

            long value = 0; int nDigits = 0; double maxMad = 0;
            int GW = CountdownReader.GlyphW, GH = CountdownReader.GlyphH;
            for (int gi = 0; gi < n; gi++)
            {
                if (gy0s[gi] < 0) { if (gi == 0) continue; return false; }
                int gh2 = gy1s[gi] - gy0s[gi] + 1;
                if (gh2 < minH)
                {
                    if (dbg) Console.Error.WriteLine($"[CTR]   字形{gi} 过扁 h={gh2}<{minH} 丢弃");
                    if (gi == 0) continue;          // 最左（污染侧）可丢弃
                    return false;
                }
                var gl = Normalize(dark, glyphs[gi].s, glyphs[gi].e, gy0s[gi], gy1s[gi]);
                int best = -1; double bMad = 9, sec = 9;
                for (int g = 0; g < 11; g++)
                {
                    double sum = 0;
                    for (int kk = 0; kk < GW * GH; kk++)
                        sum += gl[kk] != (CountdownReader.TplAt(g, kk) > 0.5f) ? 1 : 0;
                    double mad = sum / (GW * GH);
                    if (mad < bMad) { sec = bMad; bMad = mad; best = g; }
                    else if (mad < sec) sec = mad;
                }
                if (best < 0 || best == 10 || bMad > 0.30 || sec - bMad < 0.015)
                {
                    if (dbg) Console.Error.WriteLine($"[CTR]   模板匹配失败 gi={gi} best={best} bMad={bMad:F3} sec={sec:F3}");
                    if (gi == 0) continue;          // 最左字形失败=边缘污染，丢弃重试
                    return false;                    // 中间字形失败=不可信，整读放弃
                }
                value = value * 10 + best;
                if (bMad > maxMad) maxMad = bMad;
                nDigits++;
            }
            if (nDigits == 0) return false;
            // 短读数假阳性（宠物装饰凑出 1 位假「1」等）不在此过滤：MAD 分布与
            // 真字形重叠（真 0.198~0.266 / 假 0.242~0.299），无法用阈值分离。
            // 引擎侧已有两道防线：重新捕获只采信 ≥2 位且优先位数最多；锁定后按
            // 位置距离(150px)就近选取。且余数结转后 1~2 位是合法状态（1005→3），
            // 这里拒绝会造成冷却期长时间「未读取」。
            if (dbg) Console.Error.WriteLine($"[CTR]   ✓ 读出 {value}（{nDigits} 位）maxMad={maxMad:F3}");
            result.Add(new CounterRead { Value = value, Digits = nDigits, X0 = capLeft, Y0 = by0, X1 = capRight, Y1 = by1 });
            return true;
        }

        /// <summary>字形裁剪 → 最近邻缩放至 16x24</summary>
        static bool[] Normalize(bool[,] dark, int xs, int xe, int ys, int ye)
        {
            int GW = CountdownReader.GlyphW, GH = CountdownReader.GlyphH;
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
    }
}
