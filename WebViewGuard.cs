using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace WtClickerGUI
{
    /// <summary>
    /// WebView2 进程树资源治理（2026-11-10）。
    ///
    /// 实测基线：融合桌宠 WuwaTappo.exe 拉起的 msedgewebview2 树共 13 个进程、
    /// 1213 MB 工作集，其中 7 个 renderer 的 CPU 增量为 0（Chromium 的站点隔离 /
    /// spare renderer 常驻），白占约 600 MB；gpu-process 常驻 3D 引擎。
    ///
    /// 曾尝试由 FusionHost 通过 WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS 注入
    /// renderer-process-limit / disable-gpu 等开关，A/B 实测本机 WebView2
    /// 完全忽略（子进程命令行无该开关、GPU 进程与空闲 renderer 数量不变）。
    /// 因此本类是唯一实际生效的手段：按自适应节拍快照进程树，只对本程序
    /// 启动的那棵 msedgewebview2 树动手：
    ///   1) 对「空闲」或「工作集 ≥ 48MB」的进程 EmptyWorkingSet，把闲置物理页
    ///      换出；活跃的小进程不动，避免刚换出的热页立刻被换入造成周期性抖动；
    ///   2) 只有 CPU 增量 &lt; 20ms 的空闲进程降为 BelowNormal，且只在空闲状态
    ///      翻转时改一次优先级；
    ///   3) 前 3 轮 10s 快速收敛，稳态拉长到 30s，进一步压低治理器自身开销。
    ///
    /// EmptyWorkingSet 只是把物理页换出到页面文件，不释放提交内存、不终止进程，
    /// Windows 会在进程真正访问时自动换入，因此不会破坏桌宠或精确状态桥。
    /// 所有操作都按「父进程链属于 PetProcess」严格限定，绝不碰系统小组件、
    /// 浏览器或用户其它程序的 WebView2 进程；任何异常就地吞掉。
    /// </summary>
    internal static class WebViewGuard
    {
        const uint TH32CS_SNAPPROCESS = 0x00000002;
        const int FastPeriodMs = 10000;       // 启动后前 3 轮：快速把工作集压下来
        const int SlowPeriodMs = 30000;       // 稳态：低水位已建立，拉长间隔省 CPU
        const int TrimMinMB = 48;             // 活跃进程只有超过这个工作集才值得换出
        const int SelfTrimMinMB = 256;        // 宿主进程启动期冷页过多时瘦身一次
        const double IdleCpuSeconds = 0.02;   // 一个周期里 CPU 时间增量 < 20ms 视为空闲

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);
        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EmptyWorkingSet(IntPtr hProcess);

        static System.Threading.Timer _timer;
        static int _rootPid;
        static int _busy;
        static readonly Dictionary<int, double> _lastCpu = new Dictionary<int, double>();
        static readonly HashSet<int> _idlePids = new HashSet<int>();

        /// <summary>config.ini 的 webviewGuard 可关闭（默认开）。</summary>
        public static volatile bool Enabled = true;
        /// <summary>本周期治理的 WebView2 进程数 / 工作集合计(MB) / 累计修剪次数 / 累计周期数 / 因活跃而跳过修剪次数。</summary>
        public static int Procs, WsMB, Trimmed, Cycles, Skipped, SelfTrimmed;

        /// <summary>桌宠进程 PID；RestartPet 重新拉起时会用新 PID 重置状态。</summary>
        public static void Start(int rootPid)
        {
            _rootPid = rootPid;
            _lastCpu.Clear();
            _idlePids.Clear();
            Procs = WsMB = Trimmed = Cycles = Skipped = SelfTrimmed = 0;
            if (_timer == null) _timer = new System.Threading.Timer(Cycle, null, FastPeriodMs, FastPeriodMs);
            else _timer.Change(FastPeriodMs, FastPeriodMs);
        }

        public static void Stop()
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        static Dictionary<int, (int Parent, string Name)> Snapshot()
        {
            var map = new Dictionary<int, (int, string)>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (!Process32FirstW(snap, ref e)) return map;
                do
                {
                    map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile ?? "");
                } while (Process32NextW(snap, ref e));
            }
            finally { CloseHandle(snap); }
            return map;
        }

        static void Cycle(object state)
        {
            if (!Enabled || _rootPid <= 0) return;
            if (Interlocked.Exchange(ref _busy, 1) != 0) return;   // 防止周期重叠
            try
            {
                var entries = Snapshot();
                if (entries.Count == 0) return;

                var children = new Dictionary<int, List<int>>();
                foreach (var kv in entries)
                {
                    if (!children.TryGetValue(kv.Value.Parent, out var list))
                    {
                        list = new List<int>();
                        children[kv.Value.Parent] = list;
                    }
                    list.Add(kv.Key);
                }

                // 只从本程序拉起的桌宠 PID 向下 BFS，绝不误伤其它 WebView2 树
                var queue = new Queue<int>();
                var seen = new HashSet<int> { _rootPid };
                var targets = new List<int>();
                queue.Enqueue(_rootPid);
                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    if (!children.TryGetValue(cur, out var kids)) continue;
                    foreach (int k in kids)
                    {
                        if (!seen.Add(k)) continue;
                        queue.Enqueue(k);
                        if (entries[k].Name.StartsWith("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                            targets.Add(k);
                    }
                }

                int ws = 0, live = 0;
                var alive = new HashSet<int>();
                foreach (int pid in targets)
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        double cpu = p.TotalProcessorTime.TotalSeconds;
                        bool hasHistory = _lastCpu.TryGetValue(pid, out double prev);
                        double delta = hasHistory ? Math.Max(0, cpu - prev) : 0;
                        bool idle = hasHistory && delta < IdleCpuSeconds;
                        _lastCpu[pid] = cpu;
                        alive.Add(pid);
                        live++;
                        int wsMB = 0;
                        try { wsMB = (int)(p.WorkingSet64 / (1024 * 1024)); ws += wsMB; } catch { }

                        // 选择性瘦身：空闲进程照常换出；活跃进程只有工作集已经偏大
                        // 才 trim，避免把正在用的热页换出后立刻触发硬缺页回换
                        // （旧版每 20s 全量 trim 带来的周期性 CPU/IO 尖峰）。
                        if (idle || wsMB >= TrimMinMB)
                        {
                            try { if (EmptyWorkingSet(p.Handle)) Trimmed++; } catch { }
                        }
                        else Skipped++;

                        // 只降空闲进程的优先级，且仅在空闲状态翻转时写一次，
                        // 活跃 renderer / gpu / browser 保持 Normal 不影响响应。
                        bool wasIdle = _idlePids.Contains(pid);
                        if (idle != wasIdle)
                        {
                            try
                            {
                                p.PriorityClass = idle ? ProcessPriorityClass.BelowNormal
                                                       : ProcessPriorityClass.Normal;
                                if (idle) _idlePids.Add(pid); else _idlePids.Remove(pid);
                            }
                            catch { }
                        }
                    }
                    catch { /* 进程已退出或无权限，跳过 */ }
                }
                foreach (int k in new List<int>(_lastCpu.Keys))
                    if (!alive.Contains(k)) _lastCpu.Remove(k);
                foreach (int k in new List<int>(_idlePids))
                    if (!alive.Contains(k)) _idlePids.Remove(k);

                Procs = live;
                WsMB = ws;
                Cycles++;

                // 宿主自己是压缩单文件（内嵌 78MB payload + 16MB 角色素材），
                // 启动后 .NET 加载/解压会留下一大堆再也不会访问的冷页，工作集
                // 实测能到 450MB。前 4 轮若仍超 256MB 就对自身做一次
                // EmptyWorkingSet（只换出冷页、不释放提交内存），稳态不再碰，
                // 避免影响连点主循环的响应。
                if (Cycles <= 4)
                {
                    try
                    {
                        using var self = Process.GetCurrentProcess();
                        if (self.WorkingSet64 / (1024 * 1024) >= SelfTrimMinMB &&
                            EmptyWorkingSet(self.Handle)) SelfTrimmed++;
                    }
                    catch { }
                }

                // 前 3 轮快速收敛，之后拉长到 30s：稳态内存已低，没必要每 10s
                // 枚举一次全系统进程树。
                int next = Cycles < 3 ? FastPeriodMs : SlowPeriodMs;
                try { _timer?.Change(next, next); } catch { }
            }
            catch { /* 治理器异常绝不影响连点主流程 */ }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }
    }
}
