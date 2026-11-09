using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace WtClickerGUI
{
    /// <summary>
    /// 融合宿主：从单文件 exe 的嵌入资源释放「打了精确状态桥补丁的 WuwaTappo 1.0.4」，
    /// 拉起它并跟踪进程。原官方客户端不会被修改或覆盖。
    /// </summary>
    internal static class FusionHost
    {
        const string PetExeName = "WuwaTappo.exe";
        public static Process PetProcess;
        public static string RuntimeDir;
        public static string Status = "未启动";

        /// <summary>是否已有官方/其他实例在跑（此时不能重复启动，桥接会失效）。</summary>
        public static bool PetAlreadyRunning()
        {
            try { return Process.GetProcessesByName("WuwaTappo").Length > 0; }
            catch { return false; }
        }

        /// <summary>释放 payload 到 %LOCALAPPDATA%\WtClickerFusion\runtime\&lt;digest&gt;\ 并启动。</summary>
        public static bool Launch(Action<string> log)
        {
            try
            {
                if (PetAlreadyRunning())
                {
                    Status = "已有桌宠在运行（官方版无精确桥，已回退 OCR）";
                    log?.Invoke("⚠️ 检测到已有 WuwaTappo 进程在运行：融合版不会重复启动桌宠，精确桥不可用，自动回退 OCR。若需精确模式，请先退出正在运行的桌宠再重启本程序。");
                    return false;
                }

                var asm = Assembly.GetExecutingAssembly();
                string resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("payload.exe", StringComparison.OrdinalIgnoreCase));
                if (resName == null)
                {
                    Status = "未内嵌 payload.exe";
                    log?.Invoke("❌ 本 exe 未内嵌桌宠 payload，精确桥不可用，将回退 OCR 模式。");
                    return false;
                }

                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WtClickerFusion", "runtime");
                using var src = asm.GetManifestResourceStream(resName);
                if (src == null) return false;
                string digest;
                using (var sha = SHA256.Create())
                    digest = Convert.ToHexString(sha.ComputeHash(src)).Substring(0, 12).ToLowerInvariant();
                src.Position = 0;

                RuntimeDir = Path.Combine(root, digest);
                Directory.CreateDirectory(RuntimeDir);
                string exe = Path.Combine(RuntimeDir, PetExeName);
                bool needWrite = !File.Exists(exe) || new FileInfo(exe).Length != src.Length;
                if (needWrite)
                {
                    using var dst = new FileStream(exe, FileMode.Create, FileAccess.Write, FileShare.None);
                    src.CopyTo(dst);
                }

                // CDP 调试端口：无条件注入（只听回环，无风险），实机诊断用。
                // 显式写 psi.EnvironmentVariables——提权 ShellExecute 会重建环境块、
                // 进程级 SetEnvironmentVariable 实测传不到子进程，只有这条路可靠。
                var psi = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = RuntimeDir,
                    UseShellExecute = false,
                };
                psi.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = "--remote-debugging-port=9223";
                PetProcess = Process.Start(psi);
                Status = "融合桌宠已启动";
                log?.Invoke($"✅ 已启动带精确状态桥的桌宠（{exe}），等待本地状态上报…");
                return PetProcess != null;
            }
            catch (Exception ex)
            {
                Status = "启动失败：" + ex.Message;
                log?.Invoke("❌ 融合桌宠启动失败：" + ex.Message + " —— 将回退 OCR 模式");
                return false;
            }
        }

        /// <summary>等待桌宠把第一帧精确状态推上来（最多 timeoutMs）。</summary>
        public static bool WaitForBridge(FusionBridge bridge, int timeoutMs, Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (bridge.Received > 0) return true;
                Thread.Sleep(100);
            }
            log?.Invoke("⏳ 未收到桌宠的精确状态上报，已回退 OCR 模式（桌宠可能仍在登录/启动中）。");
            return false;
        }

        /// <summary>
        /// 计数冻结自愈：杀掉桌宠进程树（含 WebView2）并按原路径重新拉起。
        /// 登录态存于 WebView2 用户数据目录，重启后自动恢复在线（实测）。
        /// 由引擎看门狗在计数冻结 ≥3 分钟时调用，冷却由调用方控制。
        /// </summary>
        public static bool RestartPet(Action<string> log)
        {
            try
            {
                log?.Invoke("⟳ 计数冻结自愈：重启桌宠进程树（登录态保留）…");
                try { PetProcess?.Kill(true); } catch { }
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(PetExeName.Replace(".exe", "")))
                    try { p.Kill(true); } catch { }
                Thread.Sleep(3000);
                bool ok = Launch(log);
                log?.Invoke(ok ? "✅ 桌宠已重启，等待桥重新接入（计数应恢复增长）"
                               : "❌ 桌宠重启失败，将回退 OCR 模式");
                return ok;
            }
            catch (Exception ex)
            {
                log?.Invoke("❌ 桌宠重启异常：" + ex.Message);
                return false;
            }
        }
    }
}
