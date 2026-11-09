using System.Diagnostics;
using System.Windows.Forms;

namespace WtClickerGUI
{
    /// <summary>
    /// 开机自启：注册任务计划程序任务（登录时以最高权限运行 exe --start --min [--web]）。
    /// 为什么不用 HKCU\...\Run：连点器必须管理员权限（UIPI），Run 键启动的进程
    /// 每次开机都会弹 UAC；schtasks /rl highest 由已提权进程注册，登录即提权零弹窗。
    /// GUI 程序必须交互会话（桌宠要渲染），故加 /it 仅在用户登录时运行。
    /// </summary>
    internal static class Autostart
    {
        public const string TaskName = "WtClickerFusion";

        /// <summary>任务执行体：本 exe + 自动开始连点 + 最小化（+ 远程监控）。</summary>
        static string Tr => "\\\"" + Application.ExecutablePath + "\\\" --start --min" + (Program.WebMode ? " --web" : "");

        public static bool Exists()
        {
            return Run("/query /tn \"" + TaskName + "\"") == 0;
        }

        /// <summary>注册成功返回 null；失败返回错误摘要（UI 提示）。</summary>
        public static string Enable()
        {
            int rc = Run("/create /f /tn \"" + TaskName + "\" /sc onlogon /rl highest /it /tr \"" + Tr + "\"");
            return rc == 0 ? null : "schtasks /create 退出码 " + rc + "（本程序必须以管理员身份运行才能注册）";
        }

        public static string Disable()
        {
            int rc = Run("/delete /f /tn \"" + TaskName + "\"");
            return rc == 0 ? null : "schtasks /delete 退出码 " + rc;
        }

        static int Run(string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
            catch { return -1; }
        }
    }
}
