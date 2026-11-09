using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace WtClickerGUI
{
    /// <summary>一次扫描得到的可见元素块</summary>
    internal class Blob
    {
        public double Cx, Cy;      // 中心（屏幕坐标）
        public int N;              // 命中采样点数
        public int X0, Y0, X1, Y1; // 包围盒（屏幕坐标）

        public double Dist(Blob o)
        {
            double dx = Cx - o.Cx, dy = Cy - o.Cy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public override string ToString()
            => $"中心({Cx:F0},{Cy:F0}) 范围({X0},{Y0})-({X1},{Y1})";
    }

    /// <summary>一次 PrintWindow 抓取的完整帧（BGRA 位图）</summary>
    internal class Frame
    {
        public byte[] Bits;
        public int W, H;
        public int Left, Top;      // 窗口原点（屏幕坐标）
    }

    internal static class Scanner
    {
        /// <summary>找 WuwaTappo 的 DesktopCatInput（新版仍存在，可计数但不再参与命中测试）</summary>
        public static IntPtr FindInputHwnd()
        {
            var pids = GetPids();
            if (pids.Count == 0) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            Native.EnumProc cb = (h, l) =>
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pids.Contains(pid) && Native.ClassName(h) == "DesktopCatInput")
                {
                    found = h; return false;
                }
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            return found;
        }

        /// <summary>找主窗 'Wuwa Tappo'（视觉扫描与注入的数据源）</summary>
        public static IntPtr FindMainWindow()
        {
            var pids = GetPids();
            if (pids.Count == 0) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            Native.EnumProc cb = (h, l) =>
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pids.Contains(pid) && Native.ClassName(h) == "Tauri Window"
                    && Native.Title(h) == "Wuwa Tappo")
                {
                    found = h; return false;
                }
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            return found;
        }

        /// <summary>主窗下找 WebView2 的 Chrome_RenderWidgetHostHWND（网页 UI 点击通道）</summary>
        public static IntPtr FindRwhHwnd(IntPtr mainHwnd)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumProc cb = (h, l) =>
            {
                if (Native.ClassName(h) == "Chrome_RenderWidgetHostHWND") { found = h; return false; }
                return true;
            };
            Native.EnumChildWindows(mainHwnd, cb, IntPtr.Zero);
            return found;
        }

        static HashSet<uint> GetPids()
        {
            var set = new HashSet<uint>();
            try
            {
                foreach (var p in Process.GetProcessesByName("WuwaTappo"))
                    set.Add((uint)p.Id);
            }
            catch { }
            return set;
        }

        /// <summary>
        /// 抓一帧主窗画面（PrintWindow，PW_RENDERFULLCONTENT）。
        /// 合成背景为纯黑；失败返回 null。
        /// </summary>
        public static Frame CaptureFrame(IntPtr mainHwnd)
        {
            if (mainHwnd == IntPtr.Zero) return null;
            if (!Native.GetWindowRect(mainHwnd, out Native.RECT r)) return null;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;

            IntPtr wdc = Native.GetWindowDC(mainHwnd);
            IntPtr mdc = Native.CreateCompatibleDC(wdc);
            IntPtr bmp = Native.CreateCompatibleBitmap(wdc, w, h);
            IntPtr old = Native.SelectObject(mdc, bmp);
            try
            {
                if (!Native.PrintWindow(mainHwnd, mdc, Native.PW_RENDERFULLCONTENT))
                    return null;
                var bi = new Native.BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0
                };
                var bits = new byte[w * h * 4];
                Native.GetDIBits(mdc, bmp, 0, h, bits, ref bi, 0);
                return new Frame { Bits = bits, W = w, H = h, Left = r.Left, Top = r.Top };
            }
            finally
            {
                Native.SelectObject(mdc, old);
                Native.DeleteObject(bmp);
                Native.DeleteDC(mdc);
                Native.ReleaseDC(mainHwnd, wdc);
            }
        }

        /// <summary>
        /// 可见元素采样点：背景纯黑，max(R,G,B)>28 = 桌宠/UI 像素。返回屏幕坐标。
        /// </summary>
        public static List<Native.POINT> VisPoints(Frame f, int step = 12)
        {
            var pts = new List<Native.POINT>();
            var bits = f.Bits;
            int w = f.W, h = f.H;
            for (int y = 0; y < h; y += step)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x += step)
                {
                    int i = row + x * 4;
                    int mx = bits[i];
                    if (bits[i + 1] > mx) mx = bits[i + 1];
                    if (bits[i + 2] > mx) mx = bits[i + 2];
                    if (mx > 28) pts.Add(new Native.POINT(f.Left + x, f.Top + y));
                }
            }
            return pts;
        }

        /// <summary>
        /// 命中点聚块（网格连通聚类）。
        /// 注意：邻域偏移步长必须等于采样步长 step（此前用固定 +8 与 12px 网格错位，
        /// 导致所有块碎裂成 3~4 个假块——宝箱检测与基线跟踪双双失效的根源）。
        /// </summary>
        public static List<Blob> BlobsFromPoints(List<Native.POINT> pts, int step, int gap)
        {
            var cells = new HashSet<(int, int)>();
            foreach (var p in pts) cells.Add((p.X, p.Y));
            var seen = new HashSet<(int, int)>();
            var blobs = new List<Blob>();
            foreach (var p in pts)
            {
                var key = (p.X, p.Y);
                if (seen.Contains(key)) continue;
                var comp = new List<(int, int)>();
                var stack = new Stack<(int, int)>();
                stack.Push(key); seen.Add(key);
                while (stack.Count > 0)
                {
                    var c = stack.Pop(); comp.Add(c);
                    for (int dx = -gap; dx <= gap; dx += step)
                        for (int dy = -gap; dy <= gap; dy += step)
                        {
                            var n = (c.Item1 + dx, c.Item2 + dy);
                            if (cells.Contains(n) && !seen.Contains(n))
                            {
                                seen.Add(n); stack.Push(n);
                            }
                        }
                }
                if (comp.Count == 0) continue;
                int sx = 0, sy = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
                foreach (var c in comp)
                {
                    sx += c.Item1; sy += c.Item2;
                    if (c.Item1 < x0) x0 = c.Item1;
                    if (c.Item1 > x1) x1 = c.Item1;
                    if (c.Item2 < y0) y0 = c.Item2;
                    if (c.Item2 > y1) y1 = c.Item2;
                }
                blobs.Add(new Blob
                {
                    Cx = (double)sx / comp.Count,
                    Cy = (double)sy / comp.Count,
                    N = comp.Count,
                    X0 = x0, Y0 = y0, X1 = x1, Y1 = y1
                });
            }
            blobs.Sort((a, b) => b.N.CompareTo(a.N));
            return blobs;
        }

        /// <summary>便捷：抓帧 + 聚块</summary>
        public static (Frame frame, List<Blob> blobs) ScanFrame(IntPtr mainHwnd, int step = 12)
        {
            var f = CaptureFrame(mainHwnd);
            if (f == null) return (null, new List<Blob>());
            return (f, BlobsFromPoints(VisPoints(f, step), step, step * 4));
        }
    }
}
