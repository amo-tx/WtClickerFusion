using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WtClickerGUI
{
    /// <summary>
    /// 单文件版资源引导：把编进 exe 的 characters/ 释放到
    /// %LOCALAPPDATA%\WtClickerFusion\assets\characters\，双击 exe 就能用全部角色。
    ///
    /// 为什么需要它：csproj 原先把 characters/ 作为外部 Content 复制到发布目录，
    /// 于是「一个 exe」什么都带不上。这里改为同时以 EmbeddedResource 编入，
    /// LogicalName 用 WtAsset/ 前缀 + RecursiveDir 保留原始相对路径（若不给
    /// LogicalName，MSBuild 会把目录分隔符压成 '.'，中文目录名与文件名将无法还原）。
    ///
    /// 外部 characters/ 优先（方便继续用 PetForge 往 exe 旁边加角色），
    /// 只有外部不存在时才用这里释放出来的内嵌副本，见 MainForm.ResolveCharacterRoot。
    /// </summary>
    internal static class EmbeddedAssets
    {
        const string Prefix = "WtAsset/";

        /// <summary>内嵌角色释放后的目录；失败时为 null。</summary>
        public static string CharactersDir;
        /// <summary>释放失败原因；成功为 null。</summary>
        public static string Error;
        /// <summary>内嵌资源文件数。</summary>
        public static int FileCount;
        /// <summary>出厂默认配置落盘位置（单文件 exe 旁不放任何额外文件）。</summary>
        public static string DefaultConfigPath;

        /// <summary>
        /// UI 出厂默认是「不显示自定义角色」，全靠 config.ini 打开。单文件版旁边
        /// 没有配置文件，所以首次运行必须写一份，否则双击后桌宠角色不会出现。
        /// </summary>
        static readonly string[] FactoryConfig =
        {
            "maxCps=10", "autoChest=True", "humanize=True", "pacedBubble=True",
            "channel=0", "petName=娜波摩", "petEnabled=True", "petScale=0.6",
            "petPos=0", "petDrag=False", "petRealInput=True", "webviewGuard=True",
        };

        public static void Ensure()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appDir = Path.Combine(local, "WtClickerFusion");
                string root = Path.Combine(appDir, "assets");
                CharactersDir = Path.Combine(root, "characters");
                DefaultConfigPath = Path.Combine(appDir, "config.ini");

                // 已有用户配置绝不覆盖
                if (!File.Exists(DefaultConfigPath))
                {
                    try
                    {
                        Directory.CreateDirectory(appDir);
                        File.WriteAllLines(DefaultConfigPath, FactoryConfig, new UTF8Encoding(false));
                    }
                    catch { /* 只读环境时 LoadConfig 会用 UI 默认值，不影响主流程 */ }
                }

                var asm = Assembly.GetExecutingAssembly();
                string[] names = asm.GetManifestResourceNames()
                    .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
                    .ToArray();
                if (names.Length == 0)
                {
                    Error = "本 exe 未内嵌角色素材";
                    return;
                }
                FileCount = names.Length;

                // ★ 2026-11-10 性能：原来每次启动都把全部内嵌素材流读出来算
                //   SHA256（16MB PNG），只为判断缓存是否过期。资源是编译进同一
                //   个模块的 EmbeddedResource，ModuleVersionId 随模块内容变化，
                //   所以「MVID 相同 + 释放目录文件齐全」就足以判定缓存有效，
                //   正常启动一次资源流都不用碰，冷启动明显变快。
                string buildId = asm.ManifestModule.ModuleVersionId.ToString("N");
                string stamp = Path.Combine(root, ".stamp");
                string cachedStamp = "";
                try { if (File.Exists(stamp)) cachedStamp = File.ReadAllText(stamp); } catch { }
                if (cachedStamp.StartsWith(buildId + ":", StringComparison.Ordinal) &&
                    Directory.Exists(CharactersDir))
                {
                    try
                    {
                        int onDisk = Directory.GetFiles(CharactersDir, "*", SearchOption.AllDirectories).Length;
                        if (onDisk >= names.Length) return;   // 命中：跳过 SHA256 与全部解压
                    }
                    catch { /* 目录被外部改动则走下面的完整重建 */ }
                }

                // 未命中才计算内容签名（资源名 + 每个资源内容的 SHA256）。
                // ★ 2026-11-10：早期实现只拼「长度」，重新抠图后若某帧压缩后大小
                //   恰好不变，旧缓存不会覆盖，用户会一直看到旧素材，故必须用内容哈希。
                var sb = new StringBuilder();
                foreach (string n in names.OrderBy(x => x, StringComparer.Ordinal))
                {
                    using var s = asm.GetManifestResourceStream(n);
                    string hash = s == null ? "0" : Convert.ToHexString(SHA256.HashData(s));
                    sb.Append(n).Append(':').Append(hash).Append('|');
                }
                string sig = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 16);

                Directory.CreateDirectory(CharactersDir);
                foreach (string n in names)
                {
                    string rel = n.Substring(Prefix.Length).Replace('\\', '/');
                    string dst = Path.Combine(CharactersDir,
                        rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    using var src = asm.GetManifestResourceStream(n);
                    if (src == null) continue;
                    using var outFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
                    src.CopyTo(outFs);
                }
                // 旧版 stamp 是纯 16 位 sig；加上 buildId 前缀后首次升级会自然
                // 失配并重建一次，之后一直走快速短路。
                File.WriteAllText(stamp, buildId + ":" + sig);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
        }
    }
}
