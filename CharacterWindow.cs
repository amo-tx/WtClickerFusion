using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WtClickerGUI
{
    /// <summary>
    /// 自定义角色窗口：WS_EX_LAYERED + UpdateLayeredWindow 逐像素 alpha，
    /// 能正确显示 PNG 的半透明边缘（TransparencyKey 会留锯齿/白边，不能用）。
    ///
    /// 设计要点：
    /// - 默认 WS_EX_TRANSPARENT：鼠标完全穿透，绝不干扰 WindowFromPoint 选空白点、
    ///   不抢真实鼠标、不影响 PostMessage 注入与 PrintWindow 抓帧。
    /// - 需要拖动时由主窗体把 AllowDrag 置 true，动态去掉穿透位。
    /// - 动画全部由 UI 线程 Timer 驱动；NotifyClick 可在引擎线程安全调用
    ///   （只用 Interlocked，绝不跨线程碰 UI）。
    /// </summary>
    internal sealed class CharacterWindow : Form
    {
        // ---------------- Win32 ----------------
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int GWL_EXSTYLE = -20;
        const byte AC_SRC_OVER = 0;
        const byte AC_SRC_ALPHA = 1;
        const uint ULW_ALPHA = 0x00000002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
        [StructLayout(LayoutKind.Sequential)]
        struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey,
            ref BLENDFUNCTION pblend, uint dwFlags);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        static IntPtr GetWindowLongPtr(IntPtr h, int i) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(h, i) : new IntPtr(GetWindowLong32(h, i));
        static IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v) =>
            IntPtr.Size == 8 ? SetWindowLongPtr64(h, i, v) : new IntPtr(SetWindowLong32(h, i, v.ToInt32()));

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        // ---------------- 状态 ----------------
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        readonly Random _rng = new Random();     // 只在 UI 线程 Tick 内使用
        CharacterFrames _frames;
        double _scale = 1.0;
        // ★ 同步模型：引擎每次成功注入就把序号 +1（Interlocked，永不丢拍）。
        // UI 线程每 tick 比较已处理序号，只要变了就立刻播放一次拍打。
        // 左右手由「点击序号奇偶」决定 —— 与官方角色「每次点击换一只手」严格同相，
        // 即使动画被下一次点击打断重播，也不会像旧的 pending 队列那样错相位、
        // 积压或丢拍（旧版 pending 上限 2，CPS 稍高就跟官方脱节）。
        long _clickSeq;               // 引擎线程累加
        long _lastHandled;            // UI 线程已处理到的序号
        string _clip = "idle";
        int _frameIdx;
        double _frenzyChance;         // 兴奋狂拍概率（来自 character.json）
        bool _frenzy;                 // 正在兴奋狂拍：不可被点击打断
        bool _frenzySecond;           // frenzy_l 已播完，正在播 frenzy_r
        int _frenzyClicks;            // 兴奋期间累计的点击数，结束后立刻补拍回到同步相位
        int _cooldown;                // 兴奋结束后的冷却 tick，防高 CPS 触发过密
        int _idleDivider;             // 平静打字待机的慢速循环分频
        bool _allowDrag;
        bool _dragging;
        Point _dragScreen;              // 拖动起点（屏幕物理坐标）
        int _dragPosX, _dragPosY;
        // 窗口位置/尺寸一律用 Win32 物理像素管理：PerMonitorV2 下 WinForms 的
        // Form.Location / SetBounds 会被 DPI 缩放，实测 150% 屏上位置偏到屏幕中间、
        // 尺寸缩成 2/3；Screen.PrimaryScreen.WorkingArea 同样可能给出虚拟化尺寸。
        int _posX = 200, _posY = 200;

        // ---- 预渲染缓存（2026-11-10 性能优化）----
        // 旧 Render 每 tick 都：new Bitmap(w,h) → Graphics 双三次缩放 → GetHbitmap()
        // 再复制一次 → 新建/销毁 2 个 DC。20fps 下每秒 60 次大位图分配，
        // 是桌宠窗口的常驻 CPU 与 GC 压力来源。现在按 scale 把每个素材帧
        // 预渲染成一张 32bppPArgb 的 HBITMAP 缓存起来，每 tick 只做
        // SelectObject + UpdateLayeredWindow；帧与位置都没变则整次调用直接跳过。
        readonly Dictionary<Bitmap, IntPtr> _hbitmapCache = new Dictionary<Bitmap, IntPtr>();
        int _cacheW, _cacheH;
        double _cacheScale = -1;
        bool _cacheEnabled;
        IntPtr _memDc = IntPtr.Zero;
        Bitmap _lastSrc;
        int _lastX = int.MinValue, _lastY = int.MinValue;
        bool _lastPainted;
        // 验收用计数（UI 线程写，HTTP /stats 线程读）
        long _renderCalls, _renderSkips;
        public long RenderCalls => Interlocked.Read(ref _renderCalls);
        public long RenderSkips => Interlocked.Read(ref _renderSkips);
        public int CachedFrames => _hbitmapCache.Count;

        public CharacterWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            MinimizeBox = MaximizeBox = false;
            ControlBox = false;
            BackColor = Color.Magenta;   // 分层窗口不用 GDI+ 绘制，实际由 UpdateLayeredWindow 覆盖
            AutoScaleMode = AutoScaleMode.None;   // 不做 WinForms 自动 DPI 缩放
            Size = new Size(200, 200);
            _timer.Interval = 100;
            _timer.Tick += Tick;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                if (!_allowDrag) cp.ExStyle |= WS_EX_TRANSPARENT;
                return cp;
            }
        }

        // 分层窗口不走 WM_PAINT，禁止背景擦除导致的闪烁
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        /// <summary>true = 允许鼠标按住拖动（同时关闭鼠标穿透）。</summary>
        public bool AllowDrag
        {
            get => _allowDrag;
            set
            {
                if (_allowDrag == value) return;
                _allowDrag = value;
                if (!IsHandleCreated) return;
                IntPtr ex = GetWindowLongPtr(Handle, GWL_EXSTYLE);
                long bits = ex.ToInt64();
                if (value) bits &= ~WS_EX_TRANSPARENT;
                else bits |= WS_EX_TRANSPARENT;
                SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(bits));
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_allowDrag && e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragScreen = Cursor.Position;
                _dragPosX = _posX;
                _dragPosY = _posY;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                _posX = _dragPosX + Cursor.Position.X - _dragScreen.X;
                _posY = _dragPosY + Cursor.Position.Y - _dragScreen.Y;
                Render(Current());
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            base.OnMouseUp(e);
        }

        /// <summary>载入角色并立即显示第一帧。只在 UI 线程调用。</summary>
        public void LoadCharacter(CharacterFrames frames, double scale)
        {
            InvalidateCache();               // 换角色：旧角色的 HBITMAP 必须全部丢弃
            _frames = frames;
            _scale = Math.Clamp(scale, 0.1, 3.0);
            _frenzyChance = Math.Clamp(frames?.FrenzyChance ?? 0.0, 0.0, 1.0);
            _clip = "idle";
            _frameIdx = 0;
            _frenzy = false;
            _frenzySecond = false;
            _frenzyClicks = 0;
            _cooldown = 0;
            _idleDivider = 0;
            // 切换角色不补播历史点击，从当前序号开始与官方同步
            _lastHandled = Interlocked.Read(ref _clickSeq);
            _timer.Interval = Math.Clamp(1000 / Math.Max(1, frames?.Fps ?? 8), 16, 500);
            // 尺寸/位置统一由 Render() 用 SetWindowPos 按物理像素设定；句柄未创建时
            // Render 会跳过，Show() 之后 Place() 再按屏幕尺寸定位一次。
            Render(Current());
            if (!_timer.Enabled) _timer.Start();
        }

        public void SetScale(double scale)
        {
            double next = Math.Clamp(scale, 0.1, 3.0);
            if (Math.Abs(next - _scale) < 1e-9) return;
            _scale = next;
            InvalidateCache();
            Render(Current());
        }

        /// <summary>把窗口放到屏幕预设位置（物理像素）。</summary>
        public void Place(string preset)
        {
            var first = Current();
            if (first == null) return;
            int w = Math.Max(1, (int)Math.Round(first.Width * _scale));
            int h = Math.Max(1, (int)Math.Round(first.Height * _scale));
            // PerMonitorV2 进程里 GetSystemMetrics 返回物理像素（2560×1600），
            // 不能信 Screen.PrimaryScreen.WorkingArea（可能给 150% 虚拟化尺寸）。
            int sw = Native.GetSystemMetrics(SM_CXSCREEN);
            int sh = Native.GetSystemMetrics(SM_CYSCREEN);
            const int m = 20, taskbar = 60;
            switch (preset)
            {
                case "左上": _posX = m; _posY = m; break;
                case "右上": _posX = sw - w - m; _posY = m; break;
                case "左下": _posX = m; _posY = sh - h - taskbar; break;
                case "居中": _posX = (sw - w) / 2; _posY = (sh - h) / 2; break;
                default: _posX = sw - w - m; _posY = sh - h - taskbar; break;   // 右下
            }
            Render(Current());
        }

        /// <summary>一次「tap」= 引擎注入成功 或 用户真实鼠标/键盘输入，两个来源共用序号。
        /// ★ 2026-11-09 用户反馈「真实鼠标和键盘点击没有随着一起动」：旧版只有
        ///   ClickerEngine.OnInjectedClick 会调这里。现由 MainForm 同时把
        ///   InputTapHook.OnTap 接进来；PostMessage 注入不经过低级钩子，
        ///   两个来源天然不重复。只做 Interlocked 累加，线程安全、不丢拍。
        /// UI 线程按序号差值驱动动画，左右手由序号奇偶决定，与官方逐次同步。</summary>
        public void NotifyClick() => Interlocked.Increment(ref _clickSeq);

        List<Bitmap> Clip(string name)
        {
            if (_frames == null) return null;
            if (name != "idle" && _frames.Clips.TryGetValue(name, out var c) && c.Count > 0) return c;
            return _frames.Clips.TryGetValue("idle", out var idle) && idle.Count > 0 ? idle : null;
        }

        bool HasClip(string name) =>
            _frames != null && _frames.Clips.TryGetValue(name, out var c) && c.Count > 0;

        void StartClip(string name) { _clip = name; _frameIdx = 0; }

        Bitmap Current()
        {
            var list = Clip(_clip);
            if (list == null || list.Count == 0) return null;
            return list[Math.Clamp(_frameIdx, 0, list.Count - 1)];
        }

        /// <summary>
        /// 动画状态机（只在 UI 线程跑）。
        /// ★ 同步源是 _clickSeq：引擎每次 PostMessage 注入成功就 +1。序号一变，
        ///   立刻播放一次拍打；用「序号奇偶」选 slap_l / slap_r，与官方角色
        ///   「每次点击拍一下、左右手交替」严格同相。不用 pending 队列，
        ///   因此高 CPS 不会积压/丢拍，动画被打断重播也不会错相位。
        /// - 兴奋狂拍按 frenzyChance 概率触发，进入后不可被点击打断，
        ///   必须 frenzy_l → frenzy_r 两套都完整播完才回平静；期间点击照常
        ///   计数，结束时立刻补一次拍打回到同步相位，避免长时间脱节。
        /// - 没有 frenzy 配置的其余 7 个 PetForge 角色同样改为「序号驱动」，
        ///   行为更简单、更跟手，帧序列本身不变。
        /// </summary>
        void Tick(object sender, EventArgs e)
        {
            if (_frames == null || !Visible) return;
            var idle = Clip("idle");
            if (idle == null) return;

            long seq = Interlocked.Read(ref _clickSeq);
            bool clicked = seq != _lastHandled;
            if (clicked) _lastHandled = seq;   // 单 tick 内多次注入合并为一次响应（≤1 帧延迟）

            if (_cooldown > 0) _cooldown--;

            if (_frenzy)
            {
                if (clicked) _frenzyClicks++;
                if (AdvanceClip())
                {
                    if (!_frenzySecond)
                    {
                        // 第一组拍完，紧接着把第二组也完整拍完
                        _frenzySecond = true;
                        StartClip("frenzy_r");
                    }
                    else
                    {
                        // 两组都拍完 => 这时才允许回到平静打字
                        _frenzy = false;
                        _frenzySecond = false;
                        _cooldown = Math.Max(30, 8 * Math.Max(1, _frames.Fps));
                        if (_frenzyClicks > 0) StartSlap(seq);   // 期间有点击，立刻对齐相位
                        else StartClip("idle");
                        _frenzyClicks = 0;
                    }
                }
            }
            else if (clicked)
            {
                bool frenzy = _frenzyChance > 0 && _cooldown == 0
                              && HasClip("frenzy_l") && HasClip("frenzy_r")
                              && _rng.NextDouble() < _frenzyChance;
                if (frenzy)
                {
                    _frenzy = true;
                    _frenzySecond = false;
                    _frenzyClicks = 0;
                    StartClip("frenzy_l");
                }
                else StartSlap(seq);
            }
            else if (_clip == "idle")
            {
                // 平静打字待机：2 tick 一帧的微动
                if (++_idleDivider >= 2)
                {
                    _idleDivider = 0;
                    _frameIdx = (_frameIdx + 1) % idle.Count;
                }
            }
            else
            {
                if (AdvanceClip()) StartClip("idle");
            }
            Render(Current());
        }

        /// <summary>推进当前 clip 一帧；返回 true 表示本 clip 已播完。</summary>
        bool AdvanceClip()
        {
            var cur = Clip(_clip);
            _frameIdx++;
            return cur == null || _frameIdx >= cur.Count;
        }

        /// <summary>
        /// 按点击序号奇偶选一组拍打序列并立即重播（可打断，保证跟手）。
        /// ★ 关键约束：StartClip 会把帧索引重置为 0，高 CPS 下用户看到的几乎
        ///   只有每组第 0 帧。因此素材里 slap_l[0] 与 slap_r[0] 必须是「不同手」
        ///   的两张原始帧（娜波摩 v4：F01 左手按键盘 / F00 右手按键盘），否则
        ///   无论序号怎么交替，画面永远停在同一个姿势 —— 这正是旧版
        ///   「只看到一只手、和官方不同步」的根因。
        /// </summary>
        void StartSlap(long seq) => StartClip((seq & 1L) == 0 ? "slap_l" : "slap_r");

        /// <summary>丢弃全部预渲染位图并强制下一帧重绘（换角色 / 改缩放）。</summary>
        void InvalidateCache()
        {
            foreach (var h in _hbitmapCache.Values)
                if (h != IntPtr.Zero) DeleteObject(h);
            _hbitmapCache.Clear();
            _cacheScale = -1;
            _cacheW = _cacheH = 0;
            _cacheEnabled = false;
            ForceRepaint();
        }

        /// <summary>窗口重新可见时缓存仍然有效，但必须重新推送一次画面。</summary>
        void ForceRepaint() { _lastPainted = false; _lastSrc = null; }

        void EnsureDevice(IntPtr screenDc)
        {
            // 内存 DC 不依赖屏幕 DC 的生命周期，可以长期复用；屏幕 DC 仍按帧
            // GetDC/ReleaseDC，避免长期持有在分辨率/显示模式切换后失效。
            if (_memDc == IntPtr.Zero) _memDc = CreateCompatibleDC(screenDc);
        }

        /// <summary>
        /// 一次性把当前角色的每帧按 scale 预渲染成 32bppPArgb HBITMAP。
        /// 之后每 tick 只做 SelectObject + UpdateLayeredWindow，不再有任何
        /// GDI+ 缩放或大位图分配。缓存以源 Bitmap 引用为 key（角色帧常驻不变）。
        /// </summary>
        void RebuildCache(int w, int h)
        {
            InvalidateCache();
            if (_frames == null || w <= 0 || h <= 0) return;
            int count = 0;
            foreach (var list in _frames.Clips.Values) count += list.Count;
            // 极端缩放（3.0x）单帧可达 13MB；设 96MB 上限，超了就退回逐帧渲染，
            // 宁可慢一点也不让桌宠窗口吃掉几百 MB 内存。
            _cacheEnabled = (long)count * w * h * 4 <= 96L * 1024 * 1024;
            _cacheW = w; _cacheH = h; _cacheScale = _scale;
            if (!_cacheEnabled) return;

            foreach (var list in _frames.Clips.Values)
                foreach (var bmp in list)
                {
                    if (bmp == null || _hbitmapCache.ContainsKey(bmp)) continue;
                    using (var canvas = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                    {
                        using (var g = Graphics.FromImage(canvas))
                        {
                            g.Clear(Color.Transparent);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(bmp, 0, 0, w, h);
                        }
                        _hbitmapCache[bmp] = canvas.GetHbitmap(Color.FromArgb(0));
                    }
                }
        }

        /// <summary>用缓存的 32bppPArgb（预乘 alpha）画布 + ULW_ALPHA 更新分层窗口。</summary>
        void Render(Bitmap src)
        {
            if (src == null || !IsHandleCreated) return;
            int w = Math.Max(1, (int)Math.Round(src.Width * _scale));
            int h = Math.Max(1, (int)Math.Round(src.Height * _scale));
            bool rebuild = w != _cacheW || h != _cacheH || _cacheScale < 0
                           || Math.Abs(_scale - _cacheScale) > 1e-9;
            if (rebuild) RebuildCache(w, h);
            bool moved = _posX != _lastX || _posY != _lastY;

            // 帧与位置都没变 → 屏幕内容完全一致，跳过 SetWindowPos、
            // 合成与 UpdateLayeredWindow。idle 每 2 tick 才换一帧、高 CPS 下
            // 反复重播同一张首帧，都会命中这里。
            if (!moved && _lastPainted && ReferenceEquals(src, _lastSrc))
            {
                Interlocked.Increment(ref _renderSkips);
                return;
            }

            IntPtr hbmp;
            bool temporary = false;
            if (!_cacheEnabled || !_hbitmapCache.TryGetValue(src, out hbmp))
            {
                // 缓存被内存上限禁用 / 素材刚换：退化为单次绘制，行为与旧版一致
                using (var canvas = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, 0, 0, w, h);
                    }
                    hbmp = canvas.GetHbitmap(Color.FromArgb(0));
                }
                temporary = true;
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                if (temporary && hbmp != IntPtr.Zero) DeleteObject(hbmp);
                return;
            }
            try
            {
                EnsureDevice(screenDc);
                // Win32 物理像素定位，绕开 WinForms 的 DPI 缩放（SetBounds 实测会偏）
                if (moved || rebuild || !_lastPainted)
                    SetWindowPos(Handle, IntPtr.Zero, _posX, _posY, w, h,
                        SWP_NOZORDER | SWP_NOACTIVATE);

                var oldBmp = SelectObject(_memDc, hbmp);
                var size = new SIZE { CX = w, CY = h };
                var ptSrc = new POINT { X = 0, Y = 0 };
                var ptDst = new POINT { X = _posX, Y = _posY };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA,
                };
                bool ok = UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size, _memDc,
                    ref ptSrc, 0, ref blend, ULW_ALPHA);
                if (oldBmp != IntPtr.Zero) SelectObject(_memDc, oldBmp);
                if (ok)
                {
                    _lastSrc = src; _lastX = _posX; _lastY = _posY; _lastPainted = true;
                    Interlocked.Increment(ref _renderCalls);
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (temporary && hbmp != IntPtr.Zero) DeleteObject(hbmp);
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { ForceRepaint(); Render(Current()); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                InvalidateCache();
                if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            }
            base.Dispose(disposing);
        }
    }
}
