using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace WtClickerGUI
{
    /// <summary>
    /// 原生桌宠（WuwaTappo）观察器：只读它的**窗口事实**，不碰它的进程与设置。
    ///
    /// ★ 2026-11-11 用户反馈两点：
    ///   1) 「自定义角色和原生桌宠之间的遮挡关系按照联机桌宠的处理方式处理」；
    ///   2) 「wuwatappo 有一个游戏模式，自定义角色行为和原生角色保持一致」。
    ///
    /// 实测结论（Python + Win32 探针，非推测）：
    /// - 原生把角色画在**一个全屏透明置顶窗**里（class `Tauri Window`、title `Wuwa Tappo`、
    ///   2560×1600、WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_NOACTIVATE），
    ///   自机角色与联机角色都在这一张画布上，层级由它自己按脚底 Y 排序 —— 所以
    ///   外部窗口想「表现得像联机桌宠」，等价做法就是：拿自己脚底 Y 跟它的角色比，
    ///   更低（更靠前）就压在它上面，更高就沉到它下面，让它的角色自然遮住我们重叠的部分。
    /// - 游戏模式（托盘菜单 gameMode / 快捷键 F10）与前台全屏检测，
    ///   表现都是**隐藏 `DesktopCatInput` 全屏输入层**：角色照常显示最上层，
    ///   但完全不吃鼠标（点击直接落到后面的游戏上）。所以我们的角色在游戏模式下
    ///   也强制整窗 WS_EX_TRANSPARENT、暂停拖动，退出游戏模式再还原。
    /// - 只有 PrintWindow(PW_RENDERFULLCONTENT) 能读到那张画布的像素（44ms/次，
    ///   自动降采样到 1/4 再扫），据此得到原生角色的真实脚底 Y。
    /// </summary>
    internal static class PetDepth
    {
        const string PetProcessName = "WuwaTappo";
        const string RenderWndClass = "Tauri Window";
        const string RenderWndTitle = "Wuwa Tappo";
        const string InputWndClass = "DesktopCatInput";

        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOPMOST = 0x00000008;
        const long WS_EX_LAYERED = 0x00080000;
        const uint PW_RENDERFULLCONTENT = 0x00000002;
        const int HALFTONE = 4;
        const uint SRCCOPY = 0x00CC0020;
        const int DIB_RGB_COLORS = 0;

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc lpEnumFunc, IntPtr lParam);
        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr hdcDest, int xd, int yd, int wd, int hd,
            IntPtr hdcSrc, int xs, int ys, int ws, int hs, uint rop);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi,
            int usage, out IntPtr bits, IntPtr hSection, int offset);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight;
            public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public BITMAPINFOHEADER h; public uint c0, c1, c2; }

        static long GetExStyle(IntPtr h) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(h, GWL_EXSTYLE).ToInt64() : GetWindowLong32(h, GWL_EXSTYLE);

        static int _pid = -1;
        static long _pidStamp;

        /// <summary>缓存的进程 id（进程名解析不便宜，8 秒刷一次）。</summary>
        static int PetPid()
        {
            long now = Environment.TickCount64;
            if (_pid > 0 && now - _pidStamp < 8000) return _pid;
            _pidStamp = now;
            _pid = -1;
            try
            {
                var ps = Process.GetProcessesByName(PetProcessName);
                if (ps.Length > 0) _pid = ps[0].Id;
                foreach (var p in ps) p.Dispose();
            }
            catch { }
            return _pid;
        }

        static string ClassOf(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static string TitleOf(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>枚举某个进程的所有顶层窗口。</summary>
        static List<IntPtr> WindowsOf(int pid, string requireClass = null)
        {
            var list = new List<IntPtr>();
            if (pid <= 0) return list;
            EnumWindows((h, _) =>
            {
                uint p; GetWindowThreadProcessId(h, out p);
                if (p != (uint)pid) return true;
                if (requireClass != null && ClassOf(h) != requireClass) return true;
                list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        /// <summary>原生角色的绘制画布（全屏透明置顶层）。取不到返回 IntPtr.Zero。</summary>
        public static IntPtr FindRenderWindow()
        {
            int pid = PetPid();
            foreach (var h in WindowsOf(pid))
            {
                if (ClassOf(h) != RenderWndClass) continue;
                if (TitleOf(h) != RenderWndTitle) continue;
                long ex = GetExStyle(h);
                if ((ex & WS_EX_LAYERED) == 0) continue;
                return h;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 原生是否处在「游戏模式」：它把全屏输入捕获层 `DesktopCatInput` 藏起来了。
        /// 这一步在两种情况下都会发生 —— 托盘菜单/ F10 打开游戏模式，或它探测到
        /// 前台是全屏应用。角色可视部分照常置顶，只是不再吃鼠标。
        /// </summary>
        public static bool NativeGameMode()
        {
            int pid = PetPid();
            if (pid <= 0) return false;
            var inputs = WindowsOf(pid, InputWndClass);
            if (inputs.Count == 0) return false;          // 该版本没有输入层：当作非游戏模式
            bool anyVisible = false;
            foreach (var h in inputs) if (IsWindowVisible(h)) anyVisible = true;
            return !anyVisible;
        }

        // ---------------- 角色脚底 Y（画布像素 → 屏幕像素） ----------------
        static Rectangle _lastBounds;
        static long _lastBoundsAt;           // ★ 不能用 long.MinValue 当初值：now - long.MinValue 会溢出成负数
        static int _lastFeet = -1;
        static long _captureCount;
        static byte[] _buf;
        /// <summary>最近一次抓帧失败的原因（诊断用，/stats 暴露）。</summary>
        public static string LastError = "";

        /// <summary>本进程读到的原生角色脚底 Y（屏幕物理像素），-1 表示未知。</summary>
        public static int NativeFeetY => _lastFeet;
        public static Rectangle NativeBounds => _lastBounds;
        public static long CaptureCount => _captureCount;

        /// <summary>
        /// 用 PrintWindow 抓原生画布（1/4 降采样）并求角色可见像素的最低行。
        /// 只关心「脚底」，所以按行统计像素数，取最后一个超过阈值的行，
        /// 避免一颗浮尘/一个通知角标把脚底拖到屏幕底部。
        /// </summary>
        public static bool TryMeasureFeet(IntPtr renderWnd, bool force)
        {
            if (renderWnd == IntPtr.Zero) { LastError = "render window not found"; _lastFeet = -1; return false; }
            long now = Environment.TickCount64;
            if (!force && _lastBoundsAt != 0 && now - _lastBoundsAt < 1200) return _lastFeet > 0;
            _lastBoundsAt = now;

            RECT wr;
            if (!GetWindowRect(renderWnd, out wr)) { LastError = "GetWindowRect failed"; _lastFeet = -1; return false; }
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w <= 0 || h <= 0) { LastError = "empty rect " + w + "x" + h; _lastFeet = -1; return false; }

            const int DIV = 4;
            int sw = Math.Max(1, w / DIV), sh = Math.Max(1, h / DIV);

            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;
            IntPtr fullDc = IntPtr.Zero, fullBmp = IntPtr.Zero, smallDc = IntPtr.Zero, smallBmp = IntPtr.Zero;
            try
            {
                fullDc = CreateCompatibleDC(screenDc);
                fullBmp = CreateCompatibleBitmap(screenDc, w, h);
                if (fullDc == IntPtr.Zero || fullBmp == IntPtr.Zero) { LastError = "compatible dc/bmp failed"; return false; }
                SelectObject(fullDc, fullBmp);
                if (!PrintWindow(renderWnd, fullDc, PW_RENDERFULLCONTENT)) { LastError = "PrintWindow returned false"; _lastFeet = -1; return false; }

                var bmi = new BITMAPINFO();
                bmi.h.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.h.biWidth = sw;
                bmi.h.biHeight = -sh;              // 负数 = 自上而下
                bmi.h.biPlanes = 1;
                bmi.h.biBitCount = 32;
                bmi.h.biCompression = 0;           // BI_RGB

                IntPtr bits;
                smallDc = CreateCompatibleDC(screenDc);
                smallBmp = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
                if (smallDc == IntPtr.Zero || smallBmp == IntPtr.Zero || bits == IntPtr.Zero) { LastError = "dib section failed"; return false; }
                SelectObject(smallDc, smallBmp);

                SetStretchBltMode(smallDc, HALFTONE);
                StretchBlt(smallDc, 0, 0, sw, sh, fullDc, 0, 0, w, h, SRCCOPY);

                // 逐行扫：透明区域经 PrintWindow 会退化成黑色（实测 bbox 与角色轮廓一致）
                int rowThreshold = Math.Max(4, sw / 40);
                int lowestRow = -1, leftMost = int.MaxValue, rightMost = -1;
                int need = sw * sh * 4;
                if (_buf == null || _buf.Length < need) _buf = new byte[need];   // 复用，避免每 1.2s 一次 1MB 分配
                Marshal.Copy(bits, _buf, 0, need);
                var px = _buf;
                for (int y = sh - 1; y >= 0 && lowestRow < 0; y--)
                {
                    int baseIdx = y * sw * 4;
                    int count = 0, minX = int.MaxValue, maxX = -1;
                    for (int x = 0; x < sw; x++)
                    {
                        int i = baseIdx + x * 4;
                        if (px[i] > 10 || px[i + 1] > 10 || px[i + 2] > 10)
                        {
                            count++;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                        }
                    }
                    if (count >= rowThreshold) { lowestRow = y; leftMost = minX; rightMost = maxX; }
                }

                if (lowestRow < 0) { LastError = "no sprite pixels in capture"; _lastFeet = -1; _lastBounds = Rectangle.Empty; return false; }
                _lastFeet = wr.Top + (int)((lowestRow + 1L) * h / sh);
                _lastBounds = new Rectangle(wr.Left + leftMost * DIV, wr.Top, Math.Max(1, (rightMost - leftMost + 1) * DIV), 1);
                _captureCount++;
                LastError = "";
                return true;
            }
            catch (Exception ex) { LastError = "exception: " + ex.GetType().Name + " " + ex.Message; _lastFeet = -1; return false; }
            finally
            {
                if (smallBmp != IntPtr.Zero) DeleteObject(smallBmp);
                if (smallDc != IntPtr.Zero) DeleteDC(smallDc);
                if (fullBmp != IntPtr.Zero) DeleteObject(fullBmp);
                if (fullDc != IntPtr.Zero) DeleteDC(fullDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
