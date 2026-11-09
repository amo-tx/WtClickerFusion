using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows.Forms;

namespace WtClickerGUI
{
    internal static class Program
    {
        /// <summary>精确状态桥（127.0.0.1:48643），由打了补丁的融合桌宠推送状态。</summary>
        public static FusionBridge Bridge;
        public static bool FusionLaunched;
        /// <summary>--web：桥监听 0.0.0.0，开放只读监控面板（GET / 与 /stats）供远程访问。</summary>
        public static bool WebMode;
        /// <summary>--cdp：桌宠 WebView2 开 9223 调试端口（实机诊断）。</summary>
        public static bool CdpMode;
        /// <summary>MainForm 创建前产生的启动日志，构造后回放进日志框。</summary>
        public static readonly List<string> StartupLog = new();

        static void StartLog(string s) { StartupLog.Add(s); }

        static bool IsElevated()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        [STAThread]
        static int Main(string[] args)
        {
            // 注意：SetProcessDPIAware 会锁定进程 DPI 感知，使后面 ApplicationConfiguration
            // 的 PerMonitorV2 失效 → 窗口在 150% 屏上被缩成 2/3、文字拥挤截断。
            // 只有 --selftest（无头抓帧需要真实物理坐标）才允许调用。
            if (args.Length > 0 && args[0] == "--selftest")
            {
                try { Native.SetProcessDPIAware(); } catch { }
                return SelfTest.Run();
            }

            // 官方 WuwaTappo.exe / 内嵌 payload 的 manifest 都是 requireAdministrator
            //（高完整性）。Windows UIPI 会静默丢弃「中完整性 → 高完整性」窗口的
            // PostMessage，普通权限下点击注入会完全失效（次数不涨、宝箱不开），
            // 且没有任何报错。因此本程序必须与桌宠同等级：非管理员时自我提权重启。
            if (!IsElevated())
            {
                string extra = string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = extra,
                    });
                    return 0;       // 提权实例已启动，本实例静默退出
                }
                catch
                {
                    MessageBox.Show(
                        "桌宠以管理员权限运行，本程序也必须提权才能向它注入点击；否则 Windows 会静默拦截消息，点击完全无效。\n\n请右键 WtClickerFusion.exe →「以管理员身份运行」。",
                        "需要管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
            }

            ApplicationConfiguration.Initialize();

            StartLog("● 权限检查通过：管理员（高完整性），PostMessage 注入不会被 UIPI 拦截");
            WebMode = args.Contains("--web");
            CdpMode = args.Contains("--cdp");
            bool startArg = args.Contains("--start");
            bool minArg = args.Contains("--min");
            if (WebMode) StartLog("● 远程监控模式（--web）：桥监听 0.0.0.0:48643，状态上报仍限本机；浏览器访问 http://<本机IP>:48643/ 查看监控面板");

            // ---- 融合版：先起本地桥，再拉起内嵌的「打了精确状态补丁」的桌宠
            Bridge = new FusionBridge();
            Bridge.PublicWeb = WebMode;
            try
            {
                Bridge.Start();
                StartLog($"● 精确状态桥已监听 {(WebMode ? "0.0.0.0" : "127.0.0.1")}:{FusionBridge.Port}");
            }
            catch (Exception ex)
            {
                StartLog("⚠️ 精确状态桥启动失败（端口被占用？）：" + ex.Message + " —— 回退 OCR 模式");
            }
            FusionLaunched = FusionHost.Launch(StartLog);

            var form = new MainForm();
            form.AttachFusion(Bridge, FusionLaunched);
            if (minArg) form.WindowState = FormWindowState.Minimized;
            if (startArg)
                form.AutoStart();       // 启动即自动开始连点（免点击验证 / 挂机直达）
            Application.Run(form);
            Bridge?.Dispose();
            return 0;
        }

        /// <summary>
        /// 无头自检：抓一帧 → 依次跑 次数胶囊 / 倒计时气泡 / 宝箱检测，打印结果。
        /// 用法：WtClickerGUI.exe --selftest
        /// </summary>
        internal static class SelfTest
        {
            public static int Run()
            {
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
                var main = Scanner.FindMainWindow();
                if (main == IntPtr.Zero) { Console.WriteLine("FAIL: 未找到 Wuwa Tappo 主窗"); return 1; }
                var (frame, blobs) = Scanner.ScanFrame(main);
                if (frame == null) { Console.WriteLine("FAIL: 抓帧失败"); return 1; }
                Console.WriteLine($"frame {frame.W}x{frame.H} @({frame.Left},{frame.Top}), blobs={blobs.Count}");

                var counters = CounterReader.Find(frame);
                Console.WriteLine($"counter reads: {counters.Count}");
                foreach (var c in counters)
                    Console.WriteLine($"  value={c.Value} digits={c.Digits} rect=({c.X0},{c.Y0})-({c.X1},{c.Y1})");

                var cds = CountdownReader.Find(frame);
                Console.WriteLine($"countdown reads: {cds.Count}");
                foreach (var c in cds)
                    Console.WriteLine($"  seconds={c.Seconds} ({c.Seconds / 60}:{c.Seconds % 60:D2}) rect=({c.X0},{c.Y0})-({c.X1},{c.Y1})");

                var chests = ChestDetector.Find(frame.Bits, frame.W, frame.H);
                Console.WriteLine($"chest matches: {chests.Count}");
                foreach (var m in chests)
                    Console.WriteLine($"  ({m.Cx:F0},{m.Cy:F0}) MAD={m.Mad:F1}");

                Console.WriteLine("SELFTEST OK");
                return 0;
            }
        }
    }
}
