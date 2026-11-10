using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace WtClickerGUI
{
    internal enum ThemeKind { Light, Dark }

    /// <summary>
    /// 一套完整 UI 色板。GDI+ 的 Brush/Pen 在 Freeze() 里一次创建、整个进程复用，
    /// 避免自绘控件每次 Paint 都 new（100ms 定时器下会持续泄漏句柄）。
    /// </summary>
    internal sealed class ThemePalette
    {
        // ---- 语义色 ----
        public Color PageBg { get; init; }      // 窗体 / 按钮行 / 状态卡行底色
        public Color HeaderBg { get; init; }    // 头部栏
        public Color CardBg { get; init; }      // 状态卡
        public Color PanelBg { get; init; }     // 设置卡圆角面板
        public Color InputBg { get; init; }     // ComboBox / NumericUpDown
        public Color LogBg { get; init; }       // 日志控制台（两套主题都保持深色）
        public Color LogFore { get; init; }
        public Color Ink { get; init; }         // 主文字
        public Color InkSoft { get; init; }     // 次要文字
        public Color Border { get; init; }      // 卡片 / 面板描边
        public Color Accent { get; init; }      // 主按钮 / 强调
        public Color AccentHover { get; init; }
        public Color AccentPress { get; init; }
        public Color SubBtn { get; init; }      // 次按钮
        public Color SubBtnHover { get; init; }
        public Color SubBtnPress { get; init; }
        public Color SubBtnInk { get; init; }
        public Color Good { get; init; }        // 成功 / 正常值
        public Color Warn { get; init; }        // 倒计时紧张
        public Color Stop { get; init; }        // 运行态「停止」按钮
        public Color StopHover { get; init; }
        public Color StopPress { get; init; }
        public Color Ring { get; init; }        // 运行态状态环
        public Color Disabled { get; init; }
        public Color DisabledInk { get; init; }
        public Color Timestamp { get; init; }   // 日志时间戳

        // ---- 复用 GDI 对象 ----
        public SolidBrush HeaderBgBrush, InkBrush, SubBrush, OkBrush, BadBrush;
        public SolidBrush CardBrush, CaptionBrush, RingBrush;
        public Pen LinePen, CardPen, RingPen, BorderPen;

        public void Freeze()
        {
            HeaderBgBrush = new SolidBrush(HeaderBg);
            InkBrush = new SolidBrush(Ink);
            SubBrush = new SolidBrush(InkSoft);
            OkBrush = new SolidBrush(Good);
            BadBrush = new SolidBrush(Color.FromArgb(214, 108, 96));
            CardBrush = new SolidBrush(CardBg);
            CaptionBrush = new SolidBrush(InkSoft);
            RingBrush = new SolidBrush(Ring);
            LinePen = new Pen(Border);
            CardPen = new Pen(Border);
            RingPen = new Pen(Ring, 2F);
            BorderPen = new Pen(Border);
        }
    }

    /// <summary>
    /// 全局 UI 主题。所有自绘控件 Paint 时读 UiTheme.P，标准控件由
    /// MainForm.ApplyTheme() 遍历换色。切换即时生效，配置落盘在 MainForm 里做。
    /// </summary>
    internal static class UiTheme
    {
        public static ThemeKind Current { get; private set; } = ThemeKind.Light;
        public static ThemePalette P { get; private set; }

        /// <summary>浅色基准色板：MainForm 用它的色值反查控件原本的语义角色。</summary>
        public static ThemePalette Light { get; }
        static ThemePalette DarkPal { get; }

        static UiTheme()
        {
            Light = new ThemePalette
            {
                PageBg = Color.FromArgb(246, 241, 231),
                HeaderBg = Color.FromArgb(251, 247, 239),
                CardBg = Color.White,
                PanelBg = Color.White,
                InputBg = Color.White,
                LogBg = Color.FromArgb(43, 36, 30),
                LogFore = Color.FromArgb(232, 220, 198),
                Ink = Color.FromArgb(74, 52, 35),
                InkSoft = Color.FromArgb(156, 139, 117),
                Border = Color.FromArgb(232, 220, 198),
                Accent = Color.FromArgb(185, 106, 60),
                AccentHover = Color.FromArgb(158, 87, 45),
                AccentPress = Color.FromArgb(136, 73, 36),
                SubBtn = Color.FromArgb(240, 233, 219),
                SubBtnHover = Color.FromArgb(230, 220, 200),
                SubBtnPress = Color.FromArgb(220, 208, 184),
                SubBtnInk = Color.FromArgb(74, 52, 35),
                Good = Color.FromArgb(96, 150, 90),
                Warn = Color.FromArgb(196, 106, 72),
                Stop = Color.FromArgb(169, 84, 74),
                StopHover = Color.FromArgb(150, 70, 60),
                StopPress = Color.FromArgb(130, 60, 52),
                Ring = Color.FromArgb(185, 106, 60),
                Disabled = Color.FromArgb(210, 202, 188),
                DisabledInk = Color.FromArgb(150, 140, 125),
                Timestamp = Color.FromArgb(150, 140, 125),
            };
            DarkPal = new ThemePalette
            {
                PageBg = Color.FromArgb(18, 20, 24),
                HeaderBg = Color.FromArgb(26, 29, 34),
                CardBg = Color.FromArgb(32, 36, 42),
                PanelBg = Color.FromArgb(28, 32, 38),
                InputBg = Color.FromArgb(40, 45, 53),
                LogBg = Color.FromArgb(12, 14, 17),
                LogFore = Color.FromArgb(214, 221, 230),
                Ink = Color.FromArgb(232, 237, 243),
                InkSoft = Color.FromArgb(150, 160, 174),
                Border = Color.FromArgb(60, 67, 77),
                Accent = Color.FromArgb(226, 138, 84),
                AccentHover = Color.FromArgb(243, 158, 102),
                AccentPress = Color.FromArgb(190, 106, 58),
                SubBtn = Color.FromArgb(48, 54, 63),
                SubBtnHover = Color.FromArgb(64, 72, 83),
                SubBtnPress = Color.FromArgb(38, 43, 51),
                SubBtnInk = Color.FromArgb(226, 231, 238),
                Good = Color.FromArgb(122, 196, 116),
                Warn = Color.FromArgb(236, 146, 102),
                Stop = Color.FromArgb(196, 88, 78),
                StopHover = Color.FromArgb(216, 104, 92),
                StopPress = Color.FromArgb(162, 68, 60),
                Ring = Color.FromArgb(226, 138, 84),
                Disabled = Color.FromArgb(52, 57, 65),
                DisabledInk = Color.FromArgb(112, 120, 132),
                Timestamp = Color.FromArgb(132, 141, 154),
            };
            Light.Freeze();
            DarkPal.Freeze();
            P = Light;
        }

        public static Color PageBg => P.PageBg;
        public static Color Ink => P.Ink;
        public static Color InkSoft => P.InkSoft;
        public static Color Accent => P.Accent;
        public static Color Border => P.Border;
        public static Color Good => P.Good;
        public static Color Warn => P.Warn;
        public static Color LogBg => P.LogBg;
        public static Color LogFore => P.LogFore;
        public static Color Timestamp => P.Timestamp;

        /// <summary>切换主题；返回是否真的变化了。</summary>
        public static bool Set(ThemeKind kind)
        {
            if (kind == Current) return false;
            Current = kind;
            P = kind == ThemeKind.Dark ? DarkPal : Light;
            return true;
        }

        public static void Toggle() => Set(Current == ThemeKind.Light ? ThemeKind.Dark : ThemeKind.Light);

        /// <summary>
        /// 把控件创建时写死的「浅色基准前景色」映射到当前主题的语义色。
        /// 控件原始色保存在 MainForm._baseFg，因此来回切换不会累积偏差。
        /// </summary>
        public static Color MapFg(Color baseColor)
        {
            if (baseColor == Light.Ink) return P.Ink;
            if (baseColor == Light.InkSoft) return P.InkSoft;
            if (baseColor == Light.Accent) return P.Accent;
            if (baseColor == Light.Border) return P.LogFore;
            if (baseColor == Light.LogFore) return P.LogFore;
            return baseColor;
        }

        // ---- 深色标题栏（DWM）。20 = Win11 / Win10 20H1+，19 = Win10 1903~1909。
        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_FRAMECHANGED：强制 DWM 重算非客户区，
        // 否则运行中切换主题时标题栏底色会变黑但标题文字仍停留在浅色配色。
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static void ApplyTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int v = Current == ThemeKind.Dark ? 1 : 0;
            try
            {
                DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int));
                DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int));
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0027);
            }
            catch { /* 老系统 / 无 DWM 时忽略，客户区主题照常生效 */ }
        }
    }
}
