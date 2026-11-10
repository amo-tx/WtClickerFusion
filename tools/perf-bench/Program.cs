using System;
using System.Diagnostics;
using System.Reflection;
using WtClickerGUI;

/// <summary>
/// 宝箱检测微基准：直接链接主程序 ChestDetector.cs，量化「每 0.2s 扫一帧」的真实 CPU 成本。
///
/// 三个场景：
///   no-match  最常见：画面有橙色 UI 簇但没有宝箱，滑窗会跑满整个搜索范围（最坏情况）
///   match     把模板贴进帧里，验证优化后仍能命中且 MAD 不变
///   empty     纯黑帧，橙色签名预筛直接返回
///
/// 同时打印若干固定位置的 MAD 黄金值，用于证明早停优化不改变计算结果。
/// </summary>
internal static class Bench
{
    const int W = 1200, H = 800;
    const int Cx = 400, Cy = 300;

    static byte[] MakeFrame(bool orange, bool withTemplate)
    {
        var bits = new byte[W * H * 4];
        var rnd = new Random(20261110);
        // 伪随机低对比背景（模拟桌宠窗口里的角色/场景，不是纯黑）
        for (int i = 0; i < bits.Length; i += 4)
        {
            byte v = (byte)rnd.Next(0, 45);
            bits[i] = v; bits[i + 1] = v; bits[i + 2] = v; bits[i + 3] = 255;
        }
        if (withTemplate)
        {
            // 反射取模板 BGR，贴成一块「真宝箱」
            var t = typeof(ChestDetector);
            int[] tb = (int[])t.GetField("_tB", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            int[] tg = (int[])t.GetField("_tG", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            int[] tr = (int[])t.GetField("_tR", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            const int TW = 100, TH = 78;
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                {
                    int k = y * TW + x;
                    int i = ((Cy + y) * W + (Cx + x)) * 4;
                    bits[i] = (byte)tb[k]; bits[i + 1] = (byte)tg[k]; bits[i + 2] = (byte)tr[k];
                }
        }
        else if (orange)
        {
            // 100x78 的米橙色块：满足 cream 签名但不匹配模板，触发完整滑窗
            for (int y = 0; y < 78; y++)
                for (int x = 0; x < 100; x++)
                {
                    int i = ((Cy + y) * W + (Cx + x)) * 4;
                    bits[i] = 110; bits[i + 1] = 175; bits[i + 2] = 230;
                }
        }
        return bits;
    }

    static (double Ms, int Count, double Mad) Run(string name, byte[] bits, int rounds)
    {
        // 预热（JIT / 模板解码）
        for (int i = 0; i < 2; i++) ChestDetector.Find(bits, W, H);
        var sw = Stopwatch.StartNew();
        int count = 0; double mad = -1;
        for (int i = 0; i < rounds; i++)
        {
            var r = ChestDetector.Find(bits, W, H);
            count = r.Count;
            if (r.Count > 0) mad = r[0].Mad;
        }
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds / rounds;
        Console.WriteLine($"{name,-10} {ms,10:F3} ms/frame   matches={count}  mad={(mad < 0 ? "n/a" : mad.ToString("F4"))}");
        return (ms, count, mad);
    }

    public static void Main(string[] args)
    {
        int rounds = args.Length > 0 ? int.Parse(args[0]) : 12;
        Console.WriteLine($"PerfBench  frame={W}x{H}  rounds={rounds}  .NET {Environment.Version}");
        ChestDetector.EnsureLoaded();

        // 黄金值：优化前后必须逐位一致（早停只影响「被判为不可能更优」的调用，不改 MAD 定义）
        var probe = MakeFrame(true, false);
        Console.WriteLine("MAD golden:");
        foreach (var (px, py) in new[] { (Cx, Cy), (Cx - 34, Cy - 34), (Cx + 10, Cy + 7), (0, 0) })
            Console.WriteLine($"  ({px},{py}) = {ChestDetector.MadAtPublic(probe, W, px, py):F6}");

        var noMatch = MakeFrame(true, false);
        var match = MakeFrame(false, true);
        var empty = MakeFrame(false, false);

        Run("no-match", noMatch, rounds);
        Run("match", match, rounds);
        Run("empty", empty, rounds);
    }
}
