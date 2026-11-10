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
    /// 因此本类是唯一实际生效的手段：每 20 秒快照一次进程树，只对本程序
    /// 启动的那棵 msedgewebview2 树动手：
    ///   1) 每轮对全树 EmptyWorkingSet，把闲置物理页换出，让工作集长期保持低水位；
    ///   2) 只有 CPU 增量 &lt; 20ms 的空闲进程降为 BelowNormal，
    ///      活跃 renderer / gpu / 主 browser 进程保持 Normal，不影响桌宠响应与精确桥。
    ///
    /// EmptyWorkingSet 只是把物理页换出到页面文件，不释放提交内存、不终止进程，
    /// Windows 会在进程真正访问时自动换入，因此不会破坏桌宠或精确状态桥。
    /// 所有操作都按「父进程链属于 PetProcess」严格限定，绝不碰系统小组件、
    /// 浏览器或用户其它程序的 WebView2 进程；任何异常就地吞掉。
    /// </summary>
    internal static class WebViewGuard
    {
        const uint TH32CS_SNAPPROCESS = 0x00000002;
        const int PeriodMs = 20000;
        const double IdleCpuSeconds = 0.02;   // 20 秒周期里 CPU 时间增量 < 20ms 视为空闲

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

        /// <summary>config.ini 的 webviewGuard 可关闭（默认开）。</summary>
        public static volatile bool Enabled = true;
        /// <summary>本周期治理的 WebView2 进程数 / 工作集合计(MB) / 累计修剪次数 / 累计周期数。</summary>
        public static int Procs, WsMB, Trimmed, Cycles;

        /// <summary>桌宠进程 PID；RestartPet 重新拉起时会用新 PID 重置状态。</summary>
        public static void Start(int rootPid)
        {
            _rootPid = rootPid;
            _lastCpu.Clear();
            Procs = WsMB = Trimmed = Cycles = 0;
            if (_timer == null) _timer = new System.Threading.Timer(Cycle, null, PeriodMs, PeriodMs);
            else _timer.Change(PeriodMs, PeriodMs);
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
                        try { ws += (int)(p.WorkingSet64 / (1024 * 1024)); } catch { }

                        // 每轮全量瘦身：EmptyWorkingSet 只换出闲置物理页，活跃进程
                        // 按需自动换回；实测精确桥/计数/动画均不受影响，内存长期低水位。
                        try { if (EmptyWorkingSet(p.Handle)) Trimmed++; } catch { }
                        // 只降空闲进程的优先级；活跃 renderer / gpu / browser 保持 Normal，
                        // 避免高负载时拖慢桌宠的点击响应与状态推送。
                        try { p.PriorityClass = idle ? ProcessPriorityClass.BelowNormal
                                                     : ProcessPriorityClass.Normal; } catch { }
                    }
                    catch { /* 进程已退出或无权限，跳过 */ }
                }
                foreach (int k in new List<int>(_lastCpu.Keys))
                    if (!alive.Contains(k)) _lastCpu.Remove(k);

                Procs = live;
                WsMB = ws;
                Cycles++;
            }
            catch { /* 治理器异常绝不影响连点主流程 */ }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }
    }
}
