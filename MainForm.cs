using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WtClickerGUI
{
    /// <summary>
    /// 主窗体（2026-10-30 UI 第二版）：
    /// - 可拉伸窗口（MinimumSize 限制，所有区域 Dock/Anchor 随窗口缩放）；
    /// - 奶油暖色主题匹配 WuwaTappo 桌宠风格；卡片式布局，行距宽松不再拥挤；
    /// - 状态卡：当前次数 / 距下一宝箱 / 刷新倒计时 / 本次连点；
    /// - 日志深色控制台风，按级别着色，并同步落盘 WtClicker.log 便于排查。
    /// </summary>
    internal class MainForm : Form
    {
        readonly ClickerEngine _engine = new ClickerEngine();
        readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();

        HeaderBar _header;
        StatCard _cardCount, _cardNeed, _cardCd, _cardRate, _cardStat;
        NumericUpDown _numCps;
        Label _tipCps;
        ComboBox _cmbChannel;
        CheckBox _chkChest, _chkHuman, _chkPaced, _chkBoot;
        bool _bootReady;   // 初始同步任务状态期间屏蔽 CheckedChanged
        // ---- 自定义角色窗口（PetForge characters/ 角色库）
        CharacterLibrary _charLib;
        CharacterWindow _petWin;
        ComboBox _cmbPet, _cmbPetPos;
        CheckBox _chkPet, _chkPetDrag, _chkPetRealInput;
        InputTapHook _inputHook;      // 真实物理键鼠 → 角色拍打（与注入并列）
        NumericUpDown _numPetScale;
        bool _petReady;    // 初始载入 config 期间屏蔽角色事件
        bool _lastForceLocked;   // 强制更新锁状态翻转检测
        readonly List<string> _logTail = new();   // 监控面板 /stats 的日志尾（最近 80 行）
        RoundedButton _btnStart, _btnTest;
        Label _lblLive;
        RichTextBox _rtbLog;
        FusionBridge _bridge;
        bool _fusionLaunched;

        /// <summary>接入精确状态桥：桌宠每秒推来的服务器时钟状态直接喂给引擎。
        /// ApplyPrecise 内部加锁、不触碰 UI，因此可直接在线程池线程调用。</summary>
        public void AttachFusion(FusionBridge bridge, bool launched)
        {
            _bridge = bridge;
            _fusionLaunched = launched;
            if (bridge != null)
            {
                bridge.OnState += s =>
                {
                    // 强制更新锁状态翻转：这是「注入无效/计数冻结」的头号根因，必须醒目留痕
                    if (s.ForceLocked != _lastForceLocked)
                    {
                        _lastForceLocked = s.ForceLocked;
                        AppendLog(s.ForceLocked
                            ? "⛔ 桌宠强制更新锁生效（isForceUpdateLocked=true）：所有点击被桌宠静默丢弃，计数不再增长。请查看桌宠是否弹出更新提示，或重启融合程序。"
                            : "✅ 强制更新锁已解除，点击计数恢复", s.ForceLocked ? 3 : 1);
                    }
                    _engine.ApplyPrecise(s);
                };
                // 只读监控数据源（GET /stats 与 GET / 面板）
                bridge.StatsProvider = BuildStatsJson;
                bridge.LogTail = () => { lock (_logTail) return _logTail.ToArray(); };
            }
            foreach (var line in Program.StartupLog) AppendLog(line);
            if (!launched)
                AppendLog("● 融合桌宠未启动：本次以原 OCR 模式运行（原有识别与调度逻辑完整保留）");
        }

        // ---- 主题色
        static readonly Color Ink = Color.FromArgb(74, 52, 35);        // 深棕（主文字）
        static readonly Color InkSoft = Color.FromArgb(156, 139, 117); // 浅棕（次要文字）
        static readonly Color Accent = Color.FromArgb(185, 106, 60);   // 焦糖橙（主按钮）
        static readonly Color AccentDark = Color.FromArgb(158, 87, 45);
        static readonly Color Border_ = Color.FromArgb(232, 220, 198); // 卡片描边
        static readonly Color PageBg = Color.FromArgb(246, 241, 231);  // 页面底色

        public MainForm()
        {
            // DPI 缩放策略：.NET 8 下 AutoScaleMode.Dpi 在构造期 DeviceDpi 仍为 96，
            // Load 后不再触发，导致 150% 屏窗口不放大（实测 windowDPI=144 而物理 860）。
            // 改为 OnLoad 里按真实 DeviceDpi 手动 Scale —— 见 OnLoad override。
            // 标题带构建时间（取 exe 自身修改时间，发布即变，杜绝「跑的是旧版」的困惑）
            DateTime built = System.IO.File.GetLastWriteTime(Application.ExecutablePath);
            Text = $"WuwaTappo 融合版 · 精确桥 + 连点调度  [构建 {built:MM-dd HH:mm}]";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = PageBg;
            Size = new Size(860, 966);          // +74：新增自定义角色两行设置
            MinimumSize = new Size(860, 966);
            Padding = new Padding(14, 10, 14, 12);
            Font = new Font("Microsoft YaHei UI", 9F);

            // ================= 头部（Dock=Top，最后添加保证最顶） =================
            _header = new HeaderBar { Dock = DockStyle.Top, Height = 96 };

            // ================= 状态卡行（TableLayoutPanel 五等分，随窗口拉伸） =================
            var statTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top, Height = 90, ColumnCount = 5, RowCount = 1,
                Margin = new Padding(0), Padding = new Padding(0)
            };
            for (int i = 0; i < 5; i++)
                statTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
            statTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _cardCount = NewCard("当前次数（精确桥 / OCR）");
            _cardNeed = NewCard("距下一宝箱");
            _cardCd = NewCard("双槽倒计时（服务器时钟）");
            _cardRate = NewCard("实时点击速率");
            _cardStat = NewCard("本次连点 · 宝箱");
            statTable.Controls.Add(_cardCount, 0, 0);
            statTable.Controls.Add(_cardNeed, 1, 0);
            statTable.Controls.Add(_cardCd, 2, 0);
            statTable.Controls.Add(_cardRate, 3, 0);
            statTable.Controls.Add(_cardStat, 4, 0);

            // ================= 设置卡（Dock=Top，行距宽松） =================
            var cfg = new RoundedPanel
            {
                Dock = DockStyle.Top, Height = 360,
                BackColor = Color.White, BorderColor = Border_, Padding = new Padding(18, 14, 18, 10)
            };
            cfg.Controls.Add(MkLabel("最快速率上限", 18, 22, cfg, Ink, 9.5F, bold: true));
            _numCps = new NumericUpDown
            {
                Location = new Point(120, 17),
                Size = new Size(110, 28),
                Minimum = 1, Maximum = 30, Value = 10,
                DecimalPlaces = 1, Increment = 0.5M,
                BorderStyle = BorderStyle.FixedSingle
            };
            _tipCps = MkLabel("10.0 CPS（上限）", 244, 22, cfg, Accent, 10.5F, bold: true);
            _numCps.ValueChanged += (s, e) => { _tipCps.Text = CpsText(); ApplyConfig(); };
            cfg.Controls.Add(_numCps);
            // 注意：不要用 Anchor.Right —— OnLoad 的 Scale() 会把 anchor 右对齐的
            // AutoSize label 推出窗口外。固定 x 即可。

            cfg.Controls.Add(MkLabel(
                "融合调度：桌宠内部服务器时钟直接给出次数与左右双槽剩余时间（不再靠 OCR 猜）；按缺口÷剩余时间精准补量到 2000，到量即停，宝箱由 Helper 状态机领取避免重复",
                18, 62, cfg, InkSoft, 8.5F));

            cfg.Controls.Add(MkLabel("注入通道", 18, 108, cfg, Ink, 9.5F, bold: true));
            _cmbChannel = new ComboBox
            {
                Location = new Point(120, 102), Size = new Size(320, 28),
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _cmbChannel.Items.AddRange(new object[]
            {
                "自动（DesktopCatInput / 主窗，均已验证计数）",
                "DesktopCatInput",
                "主窗 Tauri Window"
            });
            _cmbChannel.SelectedIndex = 0;
            _cmbChannel.SelectedIndexChanged += (s, e) => ApplyConfig();
            cfg.Controls.Add(_cmbChannel);

            _chkChest = NewCheck("宝箱自动领取（精确桥模式交给 Helper 状态机领；OCR 回退时由本程序秒开 —— 关闭则两边都不领）", 18, 150, cfg);
            _chkChest.CheckedChanged += (s, e) => ApplyConfig();
            _chkHuman = NewCheck("拟人化节奏（抖动 + 微/长休息，防「点击数异常」检测；宝箱紧迫时自动跳过长休息）", 18, 182, cfg);
            _chkHuman.CheckedChanged += (s, e) => ApplyConfig();
            _chkPaced = NewCheck("气泡期精准补量（按剩余冷却把缺口摊成匀速，到点刚好攒满不浪费；关闭则全速攒到目标值）", 18, 214, cfg);
            _chkPaced.Checked = true;
            _chkPaced.CheckedChanged += (s, e) => ApplyConfig();
            // 注意：此前 cfg 高度 214 恰好把 y=214 的 _chkPaced 裁在底边外（视觉上看不到但功能在），
            // 面板加高到 286 后该复选框可见，并新增开机自启开关。
            _chkBoot = NewCheck("开机自启（注册任务计划：登录 Windows 即自动开始连点并最小化，无 UAC 弹窗）", 18, 246, cfg);
            _chkBoot.Checked = false;
            _chkBoot.CheckedChanged += (s, e) =>
            {
                if (!_bootReady) return;
                var msg = _chkBoot.Checked ? Autostart.Enable() : Autostart.Disable();
                if (msg != null) { AppendLog("⚠️ 开机自启设置失败：" + msg, 2); }
                else AppendLog(_chkBoot.Checked
                    ? "✅ 开机自启已注册（任务 WtClickerFusion：下次登录自动开始连点并最小化）"
                    : "开机自启已移除", _chkBoot.Checked ? 1 : 4);
            };

            // ================= 自定义角色（PetForge 格式角色库） =================
            _charLib = new CharacterLibrary(ResolveCharacterRoot());
            cfg.Controls.Add(MkLabel("自定义角色", 18, 282, cfg, Ink, 9.5F, bold: true));
            _cmbPet = new ComboBox
            {
                Location = new Point(120, 276), Size = new Size(320, 28),
                FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbPet.Items.Add("（不显示自定义角色）");
            foreach (var d in _charLib.Items) _cmbPet.Items.Add(d.Name);
            _cmbPet.SelectedIndex = 0;
            _cmbPet.SelectedIndexChanged += (s, e) => { if (_petReady) ApplyPet(); };
            cfg.Controls.Add(_cmbPet);
            cfg.Controls.Add(MkLabel(_charLib.Items.Count > 0
                ? $"已加载 {_charLib.Items.Count} 个角色 · characters/ 可直接用 PetForge 继续导入"
                : "未找到 characters/ 角色目录", 452, 282, cfg, InkSoft, 8.5F));

            _chkPet = NewCheck("显示", 18, 312, cfg);
            _chkPet.Checked = false;                 // 默认关闭，绝不干扰 OCR / 注入
            _chkPet.CheckedChanged += (s, e) => { if (_petReady) ApplyPet(); };
            _chkPetDrag = NewCheck("允许拖动", 100, 312, cfg);
            _chkPetDrag.Checked = false;             // 默认鼠标穿透
            _chkPetDrag.CheckedChanged += (s, e) =>
            { if (_petReady && _petWin != null) _petWin.AllowDrag = _chkPetDrag.Checked; };

            cfg.Controls.Add(MkLabel("缩放", 210, 316, cfg, Ink, 9F));
            _numPetScale = new NumericUpDown
            {
                Location = new Point(250, 310), Size = new Size(68, 26),
                Minimum = 0.2M, Maximum = 2.0M, Increment = 0.1M,
                DecimalPlaces = 2, Value = 0.6M, BorderStyle = BorderStyle.FixedSingle
            };
            _numPetScale.ValueChanged += (s, e) => { if (_petReady) ApplyPet(); };
            cfg.Controls.Add(_numPetScale);

            cfg.Controls.Add(MkLabel("位置", 332, 316, cfg, Ink, 9F));
            _cmbPetPos = new ComboBox
            {
                Location = new Point(370, 310), Size = new Size(96, 26),
                FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbPetPos.Items.AddRange(new object[] { "右下", "左下", "右上", "左上", "居中" });
            _cmbPetPos.SelectedIndex = 0;
            _cmbPetPos.SelectedIndexChanged += (s, e) =>
            { if (_petReady && _petWin != null) _petWin.Place(_cmbPetPos.Text); };
            cfg.Controls.Add(_cmbPetPos);
            // ★ 用户反馈「当我真实鼠标和键盘点击没有随着一起动」：
            //   原先只有引擎注入回调驱动角色；这里加全局低级键鼠钩子，
            //   真实鼠标左键 / 键盘按键也累加同一个点击序号。
            _chkPetRealInput = NewCheck("跟随真实键鼠", 478, 312, cfg);
            _chkPetRealInput.Checked = true;
            _chkPetRealInput.CheckedChanged += (s, e) =>
            {
                if (_inputHook != null) _inputHook.Enabled = _chkPetRealInput.Checked;
                AppendLog(_chkPetRealInput.Checked
                    ? "✅ 自定义角色已跟随真实鼠标/键盘输入（与官方桌宠同步）"
                    : "自定义角色已停止跟随真实输入（仅注入点击驱动）", 4);
            };
            cfg.Controls.Add(MkLabel("默认鼠标穿透 · 真实键鼠也拍打", 610, 316, cfg, InkSoft, 8.25F));

            // ================= 按钮行（Dock=Top） =================
            var btnPanel = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = PageBg };
            _btnStart = new RoundedButton
            {
                Text = "开始连点",
                Location = new Point(2, 8),
                Size = new Size(190, 48),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Base = Accent, Hover = AccentDark, Press = Color.FromArgb(136, 73, 36)
            };
            _btnStart.Click += BtnStart_Click;
            btnPanel.Controls.Add(_btnStart);

            _btnTest = new RoundedButton
            {
                Text = "测试一次",
                Location = new Point(206, 8),
                Size = new Size(120, 48),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Base = Color.FromArgb(240, 233, 219), Hover = Color.FromArgb(230, 220, 200),
                Press = Color.FromArgb(220, 208, 184),
                ForeColor = Ink
            };
            _btnTest.Click += BtnTest_Click;
            btnPanel.Controls.Add(_btnTest);

            _lblLive = new Label
            {
                Text = "待机中 — 启动 Wuwa Tappo 后点「测试一次」确认注入有效，再「开始连点」",
                Location = new Point(2, 64), Size = new Size(btnPanel.Width - 4, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = InkSoft, Font = new Font("Microsoft YaHei UI", 9F),
                AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft
            };
            btnPanel.Controls.Add(_lblLive);

            // ================= 日志（Dock=Fill 填满剩余空间） =================
            var logCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(43, 36, 30), BorderColor = Border_,
                Padding = new Padding(12)
            };
            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(43, 36, 30),
                ForeColor = Color.FromArgb(232, 220, 198),
                Font = new Font("Consolas", 9.25F),
                ReadOnly = true,
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            logCard.Controls.Add(_rtbLog);

            // Dock 加载顺序：Fill 最先加，Top 依次加（后加的在更外侧）
            Controls.Add(logCard);
            Controls.Add(btnPanel);
            Controls.Add(cfg);
            Controls.Add(statTable);
            Controls.Add(_header);

            // ---- 引擎日志回调（按内容分级着色，marshal 到 UI 线程；同步落盘便于排查）
            _engine.Log = s => BeginInvoke(() => AppendLog(s));

            // ---- UI 轮询：状态卡 300ms；空闲时每 tick 调 ReadIdleVision
            //      （引擎内部节流 0.5s 实扫一帧读「次数+倒计时」，待机也实时跟随），
            //      WuwaTappo 在线状态共用同一帧
            _uiTimer.Interval = 100;   // 0.1s 刷新：卡片与状态行跟手，数据源引擎 0.5s 持续重建
            _uiTimer.Tick += (s, e) =>
            {
                RefreshCards();
                int blobs = _engine.ReadIdleVision();   // 引擎运行时返回 -2，由引擎自己读
                if (blobs == -1)
                {
                    _header.SetStatus("WuwaTappo 未运行", false);
                    return;
                }
                if (blobs >= 0) _header.SetStatus("WuwaTappo 运行中", blobs > 0);
            };
            _uiTimer.Start();

            // 注入成功 → 自定义角色左右手交替拍打（NotifyClick 只做 Interlocked，线程安全）
            _engine.OnInjectedClick = () => _petWin?.NotifyClick();
            FormClosing += (s, e) =>
            {
                _uiTimer.Stop(); SaveConfig(); _engine.Shutdown();
                _inputHook?.Dispose();
                _petWin?.Dispose(); _charLib?.DisposeAll();
            };

            LoadConfig(); ApplyConfig();
            // ★ 全局键鼠钩子放在 LoadConfig 之后安装，避免复选框默认值覆盖用户配置。
            //   PostMessage 注入直接投递到窗口消息队列、不经过低级钩子，
            //   因此与 OnInjectedClick 天然不重复；两者共用 _clickSeq，
            //   角色按序号奇偶左右手交替，与官方桌宠逐次同步。
            _inputHook = new InputTapHook();
            _inputHook.OnTap = () => _petWin?.NotifyClick();
            _inputHook.Enabled = _chkPetRealInput.Checked;
            bool hookOk = _inputHook.Install();
            AppendLog(hookOk
                ? "✅ 已接入全局键鼠输入：真实鼠标点击 / 键盘按键也会让自定义角色拍打（与官方桌宠逐次同步）"
                : "⚠️ 全局键鼠钩子安装失败（权限不足？）：自定义角色仅跟随注入点击",
                hookOk ? 1 : 2);
            // 开机自启复选框与任务计划实际状态同步（勾选状态以系统为准，不落 config）；
            // 先赋值再解除屏蔽：赋值触发的 CheckedChanged 在屏蔽期内被忽略，不会重复注册
            _chkBoot.Checked = Autostart.Exists();
            _bootReady = true;
            if (_chkBoot.Checked)
                AppendLog("✅ 开机自启已生效（任务计划 WtClickerFusion）：登录 Windows 自动开始连点并最小化");
            AppendLog($"程序构建时间 {System.IO.File.GetLastWriteTime(Application.ExecutablePath):yyyy-MM-dd HH:mm:ss}（若与最新发布不符说明运行的是旧 exe）。", 4);
            AppendLog("① 启动 Wuwa Tappo；② 点「测试一次」确认注入通道有效；③ 点「开始连点」。", 4);
            AppendLog("点击落在屏幕空白点——不触碰桌宠、不抢鼠标。计数是否生效自动验证。", 4);
            AppendLog("自动流程：识别桌宠下方「当前次数」→ 每攒 1000 立即点开宝箱；", 4);
            AppendLog("连续领 2 个后，后续宝箱变为 30 分钟倒计时气泡 → 气泡 = 门槛已攒满在排队，", 4);
            AppendLog("期间自动暂停点击防计数溢出，气泡消失宝箱弹出即自动点击。", 4);
            AppendLog("建议：挂机前关闭应用内「游戏模式」，开启「总是展示宝箱」。", 4);
        }

        // ---------------------------------------------------------------- DPI 缩放
        // 布局按 96dpi 设计（860x820）。窗口实际 DPI 更高时整体等比放大：
        // Scale() 缩放全部子控件位置/尺寸；字体为 pt 指定，GDI 会按窗口 DPI 自动
        // 渲染成对应物理大小，与放大后的布局框匹配 —— 文字不再拥挤截断。
        const float DesignDpi = 96f;
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            float factor = DeviceDpi / DesignDpi;
            if (factor > 1.01f || factor < 0.99f)
            {
                MinimumSize = Size.Empty;
                Scale(new SizeF(factor, factor));
                Size = new Size((int)(860 * factor), (int)(966 * factor));
                MinimumSize = new Size((int)(860 * factor), (int)(966 * factor));
            }
            // 角色窗口必须在 DPI 缩放完成后创建：Place() 用 Screen 物理像素定位
            _petReady = true;
            ApplyPet();
            if (_charLib.Error != null) AppendLog("⚠️ 角色库：" + _charLib.Error, 2);
        }

        // ---------------------------------------------------------------- UI 构建辅助
        StatCard NewCard(string caption)
        {
            return new StatCard
            {
                Caption = caption,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 10, 0)
            };
        }

        static Label MkLabel(string text, int x, int y, Control parent, Color color, float size, bool bold = false)
        {
            var l = new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = true,
                ForeColor = color,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
            };
            parent.Controls.Add(l);
            return l;
        }

        static CheckBox NewCheck(string text, int x, int y, Control parent)
        {
            var c = new CheckBox
            {
                Text = text, Location = new Point(x, y), AutoSize = true,
                ForeColor = Ink, Checked = true,
                Font = new Font("Microsoft YaHei UI", 8.75F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            parent.Controls.Add(c);
            return c;
        }

        string CpsText() => $"{_numCps.Value:0.#} CPS（上限）";

        // ---------------------------------------------------------------- 状态刷新
        void RefreshCards()
        {
            // 次数：读到过就一直显示（引擎 0.5s 持续刷新）；新鲜度只影响颜色，
            // 不再因 10s 窗口回退成「—」（日志证明引擎在读数时 UI 曾误判过期）
            long cv = _engine.CountVal;
            bool fresh = _engine.CountFresh;
            if (cv >= 0)
            {
                _cardCount.Value = cv.ToString("N0");
                _cardCount.ValueColor = fresh ? Ink : InkSoft;
            }
            else if (_engine.WantRun)
            {
                // 引擎已启动但 OCR 尚未首次读到（如启动瞬间/桌宠被遮挡）——如实说明而非「—」
                _cardCount.Value = "读取中…";
                _cardCount.ValueColor = InkSoft;
            }
            else
            {
                _cardCount.Value = "—";
                _cardCount.ValueColor = InkSoft;
            }

            int queued = _engine.QueuedBubbles;
            if (queued > 0)
            {
                _cardNeed.Value = $"{queued} 个排队 ✓";
                _cardNeed.ValueColor = Color.FromArgb(96, 150, 90);
            }
            else
            {
                int need = _engine.NeededClicks;
                _cardNeed.Value = need < 0 ? "—" : need == 0 ? "已攒够 ✓" : $"还需 {need:N0} 次";
                _cardNeed.ValueColor = need == 0 ? Color.FromArgb(96, 150, 90) : Ink;
            }

            double cd = _engine.CdRemain;
            if (_engine.PreciseActive)
            {
                string L = TimeSpan.FromSeconds(_engine.LeftRemSec).ToString(@"mm\:ss");
                string R = TimeSpan.FromSeconds(_engine.RightRemSec).ToString(@"mm\:ss");
                _cardCd.Caption = "双槽倒计时（精确桥）";
                _cardCd.Value = $"{L} / {R}";
                _cardCd.ValueColor = Color.FromArgb(96, 150, 90);
            }
            else
            {
                _cardCd.Caption = "宝箱倒计时（OCR 回退）";
                _cardCd.Value = cd < 0 ? "无" : TimeSpan.FromSeconds(cd).ToString(@"mm\:ss");
                _cardCd.ValueColor = cd < 0 ? InkSoft : Color.FromArgb(196, 106, 72);
            }

            _cardStat.Value = $"{_engine.Clicks:N0} · {_engine.Chests}";

            // 实时点击速率：4s 滑窗真实值（暂停/宝箱点击期间趋 0，全速期≈上限）
            double acps = _engine.ActualCps;
            _cardRate.Value = acps < 0.05 ? "0 /s" : $"{acps:F1} /s";
            _cardRate.ValueColor = acps < 0.05 ? InkSoft : Color.FromArgb(96, 150, 90);

            // 状态行：引擎运行时显示与心跳日志同格式的实时快照（引擎 ~0.4-0.5s 重建，
            // 此处 300ms 同步），待机/未启动时回退到桌宠检测状态文本
            string live = _engine.StatusLine;
            if (string.IsNullOrEmpty(live)) live = _engine.LiveDesc;
            if (!string.IsNullOrEmpty(live)) _lblLive.Text = live;
            _header.Running = _engine.WantRun;
            _header.Invalidate();
        }

        // ---------------------------------------------------------------- 日志
        void AppendLog(string s, int forcedLevel = -1)
        {
            // 监控面板 /stats 的日志尾（带时间戳原文，最近 80 行）
            lock (_logTail)
            {
                _logTail.Add($"[{DateTime.Now:HH:mm:ss}] {s}");
                if (_logTail.Count > 80) _logTail.RemoveRange(0, _logTail.Count - 80);
            }
            // 所有日志（含 UI 侧如「▶ 已开始」）统一落盘，便于事后排查
            try { System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "WtClicker.log"),
                $"[{DateTime.Now:HH:mm:ss}] {s}\r\n"); } catch { }
            int level = forcedLevel >= 0 ? forcedLevel : Classify(s);
            Color c = level switch
            {
                0 => Color.FromArgb(232, 220, 198),   // 普通
                1 => Color.FromArgb(143, 209, 143),   // 成功
                2 => Color.FromArgb(240, 179, 87),    // 警告
                3 => Color.FromArgb(242, 126, 126),   // 错误
                _ => Color.FromArgb(167, 155, 138),   // 暗淡提示
            };
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.SelectionColor = Color.FromArgb(122, 110, 96);
            _rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] ");
            _rtbLog.SelectionColor = c;
            _rtbLog.AppendText(s + "\n");
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.ScrollToCaret();

            // 长时间挂机保护：日志框超过 600 行时裁掉最老的 300 行（文件里是全量的，仅省 UI 内存）
            if (_rtbLog.Lines.Length > 600)
            {
                var all = _rtbLog.Lines;
                _rtbLog.Clear();
                _rtbLog.Lines = all[^300..];
                _rtbLog.SelectionStart = _rtbLog.TextLength;
                _rtbLog.ScrollToCaret();
            }
        }

        static int Classify(string s)
        {
            if (s.StartsWith("✅")) return 1;            if (s.StartsWith("⚠️") || s.StartsWith("⏱")) return 2;
            if (s.Contains("失败") || s.Contains("异常")) return 3;
            if (s.StartsWith("▶") || s.StartsWith("⏸")) return 4;
            return 0;
        }

        // ---------------------------------------------------------------- 配置与按钮
        void ApplyConfig()
        {
            _engine.Cps = (double)_numCps.Value;   // 仅为上限；实际速率由引擎按倒计时自主调节
            _engine.AutoChest = _chkChest.Checked;
            // 融合版：同一开关同时控制「精确桥模式下让 Helper 领箱」与补丁侧
            // 自动领箱标志（autoClaim 随每次状态响应下发给补丁写 localStorage）。
            _engine.DelegateChestClaim = _chkChest.Checked;
            if (_bridge != null) _bridge.AutoClaim = _chkChest.Checked;
            _engine.Humanize = _chkHuman.Checked;
            _engine.PacedBubble = _chkPaced.Checked;
            _engine.Channel = _cmbChannel.SelectedIndex;
        }

        // ---------------------------------------------------------------- 自定义角色窗口
        /// <summary>按当前 UI 选择创建/隐藏角色窗口。只在 UI 线程调用。</summary>
        void ApplyPet()
        {
            try
            {
                int idx = _cmbPet.SelectedIndex;
                var def = idx > 0 && idx <= _charLib.Items.Count ? _charLib.Items[idx - 1] : null;
                if (!_chkPet.Checked || def == null)
                {
                    if (_petWin != null) _petWin.Hide();
                    return;
                }
                var frames = _charLib.Load(def);
                if (frames == null)
                {
                    AppendLog("⚠️ 角色「" + def.Name + "」没有可用帧", 2);
                    return;
                }
                if (_petWin == null)
                {
                    _petWin = new CharacterWindow();
                    _petWin.AllowDrag = _chkPetDrag.Checked;   // Show() 前设置，CreateParams 才会生效
                }
                else _petWin.AllowDrag = _chkPetDrag.Checked;

                // character.json 的 scale 是素材自带基准；UI 数值框是用户倍率
                _petWin.LoadCharacter(frames, def.Scale * (double)_numPetScale.Value);
                if (!_petWin.Visible) _petWin.Show();
                _petWin.Place(_cmbPetPos.Text);
            }
            catch (Exception ex)
            {
                AppendLog("⚠️ 自定义角色窗口初始化失败：" + ex.Message, 2);
            }
        }

        // ---------------------------------------------------------------- 监控面板数据（GET /stats）
        /// <summary>构建只读统计 JSON：给本地/远程监控页轮询。只读，无任何控制端点。</summary>
        string BuildStatsJson()
        {
            var e = _engine;
            var st = _bridge?.Latest;
            bool precise = e.PreciseActive;
            var obj = new
            {
                ok = 1,
                time = DateTime.Now.ToString("HH:mm:ss"),
                build = System.IO.File.GetLastWriteTime(Application.ExecutablePath).ToString("MM-dd HH:mm"),
                online = st?.Online ?? false,
                clockReady = st?.ClockReady ?? false,
                precise,
                clicks = e.CountVal,
                fresh = e.CountFresh,
                leftMs = precise ? e.LeftRemSec * 1000 : -1.0,
                rightMs = precise ? e.RightRemSec * 1000 : -1.0,
                min = st?.Minimum ?? 0L,
                cpsMax = e.Cps,
                planCps = e.PlanCps,
                actualCps = e.ActualCps,
                paused = e.Paused,
                running = e.WantRun,
                injected = e.Clicks,
                chests = e.Chests,
                queued = e.QueuedBubbles,
                needed = e.NeededClicks,
                strategy = string.IsNullOrEmpty(e.StatusLine) ? (e.LiveDesc ?? "") : e.StatusLine,
                received = _bridge?.Received ?? 0L,
                locked = st?.ForceLocked ?? false,
                localClicks = st?.LocalClicks ?? 0L,
                serverTotal = st?.ServerTotal ?? 0L,
                serverSpend = st?.ServerSpend ?? 0L,
                serverConsume = st?.ServerConsume ?? 0L,
                petLoggedIn = st?.LoggedIn ?? false,
                frozen = e.CountFrozen,
                uptimeSec = (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds,
                autostart = _chkBoot?.Checked ?? false,
                web = Program.WebMode,
                pet = _petWin != null && _petWin.Visible ? _cmbPet.Text : "",
                realTaps = _inputHook?.TapCount ?? 0L,     // 真实键鼠捕获数（验收用）
                petRealInput = _chkPetRealInput?.Checked ?? false,
                log = _bridge?.LogTail?.Invoke() ?? Array.Empty<string>(),
            };
            return System.Text.Json.JsonSerializer.Serialize(obj);
        }

        // ---------------------------------------------------------------- 资源路径与配置持久化
        /// <summary>外部 characters/ 优先（方便继续用 PetForge 往 exe 旁边加角色）；
        /// 单文件版回退到 EmbeddedAssets 从 exe 内嵌资源释放出来的目录。</summary>
        static string ResolveCharacterRoot()
        {
            string external = System.IO.Path.Combine(AppContext.BaseDirectory, "characters");
            try
            {
                if (System.IO.Directory.Exists(external) &&
                    System.IO.Directory.GetDirectories(external).Length > 0)
                    return external;
            }
            catch { }
            return EmbeddedAssets.CharactersDir ?? external;
        }

        /// <summary>旧发布目录已有 config.ini 时沿用，避免本机设置丢失；
        /// 单文件 exe 放在只读目录时自动落到 %LOCALAPPDATA%（SaveConfig 已有容错）。</summary>
        string ConfigPath
        {
            get
            {
                string legacy = System.IO.Path.Combine(AppContext.BaseDirectory, "config.ini");
                if (System.IO.File.Exists(legacy)) return legacy;
                return EmbeddedAssets.DefaultConfigPath
                    ?? System.IO.Path.Combine(AppContext.BaseDirectory, "config.ini");
            }
        }

        void SaveConfig()
        {
            try
            {
                System.IO.File.WriteAllLines(ConfigPath, new[]
                {
                    "maxCps=" + ((double)_numCps.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "autoChest=" + _chkChest.Checked,
                    "humanize=" + _chkHuman.Checked,
                    "pacedBubble=" + _chkPaced.Checked,
                    "channel=" + _cmbChannel.SelectedIndex,
                    "petName=" + (_cmbPet.SelectedIndex > 0 ? _cmbPet.Text : ""),
                    "petEnabled=" + _chkPet.Checked,
                    "petScale=" + _numPetScale.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "petPos=" + _cmbPetPos.SelectedIndex,
                    "petDrag=" + _chkPetDrag.Checked,
                    "petRealInput=" + _chkPetRealInput.Checked,
                });
            }
            catch { /* 落盘失败不影响运行 */ }
        }

        void LoadConfig()
        {
            try
            {
                if (!System.IO.File.Exists(ConfigPath)) return;
                foreach (var line in System.IO.File.ReadAllLines(ConfigPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
                    switch (k)
                    {
                        case "maxCps" when double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out double mc):
                            _numCps.Value = Math.Clamp((decimal)mc, _numCps.Minimum, _numCps.Maximum); break;
                        case "cpsX2" when int.TryParse(v, out int cps):   // 旧版滑条配置迁移：值/2 = CPS
                            _numCps.Value = Math.Clamp(cps / 2.0m, _numCps.Minimum, _numCps.Maximum); break;
                        case "autoChest" when bool.TryParse(v, out bool ac): _chkChest.Checked = ac; break;
                        case "humanize" when bool.TryParse(v, out bool hu): _chkHuman.Checked = hu; break;
                        case "pacedBubble" when bool.TryParse(v, out bool pb): _chkPaced.Checked = pb; break;
                        case "channel" when int.TryParse(v, out int ch):
                            _cmbChannel.SelectedIndex = Math.Clamp(ch, 0, _cmbChannel.Items.Count - 1); break;
                        case "petName":
                            // Items 在 LoadConfig 调用前已按 characters/ 目录填充
                            int pi = _cmbPet.Items.IndexOf(v);
                            if (pi > 0) _cmbPet.SelectedIndex = pi;
                            break;
                        case "petEnabled" when bool.TryParse(v, out bool pe): _chkPet.Checked = pe; break;
                        case "petScale" when decimal.TryParse(v, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out decimal ps):
                            _numPetScale.Value = Math.Clamp(ps, _numPetScale.Minimum, _numPetScale.Maximum); break;
                        case "petPos" when int.TryParse(v, out int pp):
                            _cmbPetPos.SelectedIndex = Math.Clamp(pp, 0, _cmbPetPos.Items.Count - 1); break;
                        case "petDrag" when bool.TryParse(v, out bool pd): _chkPetDrag.Checked = pd; break;
                        case "petRealInput" when bool.TryParse(v, out bool pri): _chkPetRealInput.Checked = pri; break;
                    }
                }
            }
            catch { /* 配置损坏则用默认值 */ }
        }

        void BtnStart_Click(object sender, EventArgs e)
        {
            ApplyConfig();
            if (!_engine.WantRun)
            {
                _engine.WantRun = true;
                if (!_engine.Started) { _engine.Start(); _engine.Started = true; }
                _btnStart.Text = "停止";
                _btnStart.Base = Color.FromArgb(169, 84, 74);
                _btnStart.Hover = Color.FromArgb(150, 70, 60);
                _btnStart.Press = Color.FromArgb(130, 60, 52);
                AppendLog($"▶ 已开始：最快速率 {_engine.Cps:0.#} CPS（实际速率由调度自动决定），通道={_cmbChannel.Text}，" +
                          $"自动宝箱={(_engine.AutoChest ? "开" : "关")}");
            }
            else
            {
                _engine.WantRun = false;
                _btnStart.Text = "开始连点";
                _btnStart.Base = Accent; _btnStart.Hover = AccentDark;
                _btnStart.Press = Color.FromArgb(136, 73, 36);
                AppendLog("⏸ 已停止");
            }
        }

        /// <summary>命令行 --start：窗体加载后自动开始连点。</summary>
        public void AutoStart()
        {
            Shown += (s, e) => BtnStart_Click(this, EventArgs.Empty);
        }

        void BtnTest_Click(object sender, EventArgs e)
        {
            ApplyConfig();
            AppendLog("— 测试一次…", 4);
            AppendLog(_engine.TestClick());
        }
    }

    // ================================================================= 自绘控件
    /// <summary>头部栏：标题 + 副标题 + 右侧 WuwaTappo 检测状态（圆点）</summary>
    internal class HeaderBar : Panel
    {
        public string StatusText = "检测中…";
        public bool Ok;
        public bool Running;

        public HeaderBar()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var bg = new SolidBrush(Color.FromArgb(251, 247, 239));
                g.FillRectangle(bg, ClientRectangle);
                using var line = new Pen(Color.FromArgb(234, 223, 200));
                g.DrawLine(line, 0, Height - 1, Width, Height - 1);

                g.DrawString("WuwaTappo 连点器", new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
                    new SolidBrush(Color.FromArgb(107, 66, 38)), new PointF(8, 16));
                g.DrawString("次数识别 · 倒计时智能调度 · 宝箱立即开启 · 不占用鼠标",
                    new Font("Microsoft YaHei UI", 9F),
                    new SolidBrush(Color.FromArgb(156, 139, 117)), new PointF(10, 56));

                // 右侧状态：圆点 + 文字
                var f = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
                float tw = g.MeasureString(StatusText, f).Width;
                int dotX = Math.Max(220, Width - 28 - (int)tw);
                using var dot = new SolidBrush(Ok ? Color.FromArgb(106, 176, 96) : Color.FromArgb(205, 120, 100));
                g.FillEllipse(dot, dotX, 32, 12, 12);
                if (Running)
                {
                    using var ring = new Pen(Color.FromArgb(185, 106, 60), 2F);
                    g.DrawEllipse(ring, dotX - 4, 28, 20, 20);
                }
                g.DrawString(StatusText, f, new SolidBrush(Color.FromArgb(107, 66, 38)),
                    new PointF(dotX + 18, 28));
            };
        }

        public void SetStatus(string text, bool ok)
        {
            if (StatusText == text && Ok == ok) return;
            StatusText = text; Ok = ok;
            Invalidate();
        }
    }

    /// <summary>状态卡：白色圆角底 + 描边，上标题下数值（Dock=Fill 随窗口缩放）</summary>
    internal class StatCard : Panel
    {
        public string Caption = "";
        string _value = "—";
        Color _valueColor = Color.FromArgb(74, 52, 35);
        // 必须是属性并在变化时 Invalidate：UI 定时器每 100ms 只改字段不重绘的话，
        // 卡片会停留在旧画面上（曾致卡片数字滞后状态行十几秒、启动后长时间不显示）
        public string Value
        {
            get => _value;
            set { if (_value != value) { _value = value; Invalidate(); } }
        }
        public Color ValueColor
        {
            get => _valueColor;
            set { if (_valueColor != value) { _valueColor = value; Invalidate(); } }
        }

        public StatCard()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                using var path = Gfx.Round(r, 12);
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var p = new Pen(Color.FromArgb(232, 220, 198))) g.DrawPath(p, path);

                g.DrawString(Caption, new Font("Microsoft YaHei UI", 8.25F),
                    new SolidBrush(Color.FromArgb(156, 139, 117)), new PointF(14, 10));
                var f = new Font("Microsoft YaHei UI", 13.5F, FontStyle.Bold);
                var sz = g.MeasureString(Value, f);
                g.DrawString(Value, f, new SolidBrush(ValueColor),
                    new PointF(14, Height - sz.Height - 9));
            };
        }
    }

    /// <summary>白色圆角面板</summary>
    internal class RoundedPanel : Panel
    {
        public Color BorderColor = Color.FromArgb(232, 220, 198);
        public RoundedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Gfx.Round(new Rectangle(0, 0, Width - 1, Height - 1), 12);
                using var p = new Pen(BorderColor);
                g.DrawPath(p, path);
            };
        }
    }

    /// <summary>圆角按钮（焦糖橙主按钮 / 米色次按钮）</summary>
    internal class RoundedButton : Control
    {
        public Color Base = Color.FromArgb(185, 106, 60);
        public Color Hover = Color.FromArgb(158, 87, 45);
        public Color Press = Color.FromArgb(136, 73, 36);
        bool _hot, _down;

        public RoundedButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Gfx.Round(r, 12);
            Color c = !Enabled ? Color.FromArgb(210, 202, 188) : _down ? Press : _hot ? Hover : Base;
            using (var b = new SolidBrush(c)) g.FillPath(b, path);
            var textColor = Enabled ? (ForeColor == Color.FromArgb(0, 0, 0) ? Color.White : ForeColor)
                                    : Color.FromArgb(150, 140, 125);
            var f = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            var sz = g.MeasureString(Text, f);
            g.DrawString(Text, f, new SolidBrush(textColor),
                new PointF((Width - sz.Width) / 2f, (Height - sz.Height) / 2f));
        }
    }

    static class Gfx
    {
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
