using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace WtClickerGUI
{
    /// <summary>
    /// 全局低级键鼠钩子：把「用户真实物理鼠标左键 / 键盘按键」转成一次桌宠拍打。
    ///
    /// 背景（2026-11-09 用户反馈「当我真实鼠标和键盘点击没有随着一起动」）：
    /// CharacterWindow 的动画序号原先只由 ClickerEngine.OnInjectedClick 驱动，
    /// 也就是只在程序自己 PostMessage 注入时 +1。用户手动点桌宠、敲键盘时
    /// 桌宠计数照常增长（官方角色也在拍），但自定义角色纹丝不动。
    ///
    /// ★ 为什么不会和注入重复计数：
    ///   PostMessage 把 WM_LBUTTONDOWN 直接投递到目标窗口的线程消息队列，
    ///   不经过系统硬件输入队列，因此不会触发 WH_MOUSE_LL / WH_KEYBOARD_LL。
    ///   真实键鼠（以及 SendInput 模拟输入）必然经过低级钩子。
    ///   两个来源都只做 Interlocked 累加同一个点击序号，角色按序号奇偶
    ///   左右手交替，与官方桌宠「每次 tap 换一只手」严格同步。
    ///
    /// ★ 2026-11-10 修复「长按某个键，角色一直拍」：只认按键的**按下沿**。
    ///   系统自动重复会连发 WM_KEYDOWN，旧版每 90ms 放行一次等于长按=连拍；
    ///   现在用 _held 记住当前按住的 VK，KEYUP 才释放，重复消息全部吞掉。
    ///   打字时每个键都是新的按下沿，仍逐次驱动角色；鼠标不受影响。
    ///
    /// ★ 钩子要求安装线程有消息循环：本类在 MainForm 构造（UI 线程）里安装，
    ///   WinForms 的 Application.Run 会持续泵消息。回调只做 Interlocked，
    ///   绝不跨线程碰 UI；异常就地兜住，避免未捕获异常终止进程。
    /// </summary>
    internal sealed class InputTapHook : IDisposable
    {
        const int WH_MOUSE_LL = 14;
        const int WH_KEYBOARD_LL = 13;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_SYSKEYUP = 0x0105;

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string lpModuleName);

        // 强引用防 GC：委托被回收后回调地址失效会直接崩溃
        readonly HookProc _mouseProc;
        readonly HookProc _keyProc;
        IntPtr _hMouse, _hKey;

        // ★ 2026-11-10 修复「长按键盘按键，自定义角色会一直触发动作」：
        //   旧实现是「同一键每 90ms 放行一次」，但系统自动重复约 30ms 一次，
        //   长按不放就会每 90ms Fire() 一次，角色持续拍打。
        //   低级键盘钩子的 KBDLLHOOKSTRUCT 没有 WM_KEYDOWN 的 repeat 位，
        //   只能自己记「哪些键当前处于按下状态」：只有按下沿算一次 tap，
        //   KEYUP 才释放，按住期间的重复 WM_KEYDOWN 全部忽略。
        //   钩子回调在安装的 UI 线程执行，_held 不需要加锁。
        readonly HashSet<int> _held = new HashSet<int>();

        /// <summary>开关（UI 复选框控制）。关闭后真实输入不再驱动角色。</summary>
        public volatile bool Enabled = true;
        /// <summary>每次真实 tap 触发；订阅方必须线程安全（只做 Interlocked）。</summary>
        public Action OnTap;
        /// <summary>累计捕获的真实 tap 数，供 /stats 与验收脚本读取。</summary>
        long _taps;
        public long TapCount => Interlocked.Read(ref _taps);
        public bool Installed => _hMouse != IntPtr.Zero && _hKey != IntPtr.Zero;

        public InputTapHook()
        {
            _mouseProc = OnMouse;
            _keyProc = OnKey;
        }

        /// <summary>安装全局低级钩子（0 = 所有线程）。返回是否两个钩子都成功。</summary>
        public bool Install()
        {
            if (Installed) return true;
            IntPtr hMod = GetModuleHandleW(null);
            _hMouse = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, hMod, 0);
            _hKey = SetWindowsHookExW(WH_KEYBOARD_LL, _keyProc, hMod, 0);
            if (_hMouse == IntPtr.Zero && _hKey != IntPtr.Zero) { UnhookWindowsHookEx(_hKey); _hKey = IntPtr.Zero; }
            if (_hKey == IntPtr.Zero && _hMouse != IntPtr.Zero) { UnhookWindowsHookEx(_hMouse); _hMouse = IntPtr.Zero; }
            return Installed;
        }

        IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Enabled && wParam.ToInt32() == WM_LBUTTONDOWN) Fire();
            return CallNextHookEx(_hMouse, nCode, wParam, lParam);
        }

        IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                // KBDLLHOOKSTRUCT 第一个字段是 vkCode(DWORD)，KEYDOWN/KEYUP 结构相同
                int vk = Marshal.ReadInt32(lParam);
                if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    // 即使开关关闭也要释放状态，否则「关着按住、开着松开」会残留幽灵按下态
                    _held.Remove(vk);
                }
                else if (Enabled && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN))
                {
                    // Add 返回 false = 该键已按住（自动重复），直接吞掉，不触发拍打
                    if (_held.Add(vk)) Fire();
                }
            }
            return CallNextHookEx(_hKey, nCode, wParam, lParam);
        }

        void Fire()
        {
            Interlocked.Increment(ref _taps);
            try { OnTap?.Invoke(); }
            catch { /* 角色窗口异常绝不影响系统输入 */ }
        }

        public void Dispose()
        {
            Enabled = false;
            _held.Clear();
            if (_hMouse != IntPtr.Zero) { UnhookWindowsHookEx(_hMouse); _hMouse = IntPtr.Zero; }
            if (_hKey != IntPtr.Zero) { UnhookWindowsHookEx(_hKey); _hKey = IntPtr.Zero; }
        }
    }
}
