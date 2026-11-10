"""验收：长按键盘不再让自定义角色连续触发动作（2026-11-10 修复）。

原理：真实键盘按住不放时，系统约每 30ms 连发一次 WM_KEYDOWN；低级键盘钩子的
KBDLLHOOKSTRUCT 没有 repeat 位，钩子看到的信号与下面「连续 keybd_event(keydown)」
完全等价。修复后 InputTapHook 只认按下沿（_held 集合），同一 VK 的重复消息全部吞掉。

用法：python tools/verify_long_press.py
要求：WtClickerFusion 正在运行，且「跟随真实键鼠」(petRealInput) 已开启。
判定：长按 ΔrealTaps==1；松开再按 +1；连打 3 个不同键 +3。
"""
import ctypes
import json
import sys
import time
import urllib.request

PORT = 48643
KEYEVENTF_KEYUP = 0x0002
VK_A, VK_B, VK_C, VK_D = 0x41, 0x42, 0x43, 0x44

user32 = ctypes.WinDLL("user32", use_last_error=True)
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def stats():
    with opener.open(f"http://127.0.0.1:{PORT}/stats", timeout=5) as f:
        return json.loads(f.read().decode("utf-8", "replace"))


def down(vk):
    user32.keybd_event(vk, 0, 0, 0)


def up(vk):
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)


def delta(label, action, expect):
    before = stats()["realTaps"]
    action()
    time.sleep(0.5)
    after = stats()["realTaps"]
    d = after - before
    ok = d == expect
    print(f"  [{'PASS' if ok else 'FAIL'}] {label}: ΔrealTaps={d} (预期 {expect})")
    return ok


def long_press(vk, repeats=60, gap=0.03):
    """模拟按住 vk 不放：连发 repeats 次 keydown，最后才 keyup。"""
    def run():
        for _ in range(repeats):
            down(vk)
            time.sleep(gap)
        up(vk)
    return run


def tap(vk):
    def run():
        down(vk)
        time.sleep(0.05)
        up(vk)
    return run


def type_seq(vks):
    def run():
        for vk in vks:
            down(vk)
            time.sleep(0.04)
            up(vk)
            time.sleep(0.04)
    return run


def main():
    s = stats()
    print("运行态:", json.dumps({k: s.get(k) for k in
          ("build", "precise", "running", "pet", "petRealInput", "realTaps")}, ensure_ascii=False))
    if not s.get("petRealInput"):
        print("FAIL: 未开启「跟随真实键鼠」，无法验收"); return 1
    print("验收项：")
    results = [
        delta("长按 A 约 1.8s（60 次重复 keydown）", long_press(VK_A), 1),
        delta("松开后再次按 A", tap(VK_A), 1),
        delta("快速连打 B/C/D 三个不同键", type_seq([VK_B, VK_C, VK_D]), 3),
        delta("长按 D 约 1.8s（再验一次不同键）", long_press(VK_D, repeats=60), 1),
    ]
    ok = all(results)
    print("RESULT:", "PASS —— 长按只触发一次，松开/换键仍逐次触发" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
