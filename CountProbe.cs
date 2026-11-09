using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace WtClickerGUI
{
    /// <summary>
    /// 计数验证探针：监控 %APPDATA%\com.kuro.wuwatappo.cn\cumulative_click*.dat 的
    /// SHA256 哈希变化（文件为混淆二进制，无法直接读数，但哈希变化 = 计数增长）。
    /// 逆向依据：2026-10-29 实测，PostMessage 注入后该文件哈希立即变化。
    /// </summary>
    internal class CountProbe
    {
        readonly Dictionary<string, byte[]> _hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        readonly string _dir;

        public bool Available { get; private set; }
        public int Changes { get; private set; }

        public CountProbe()
        {
            try
            {
                _dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "com.kuro.wuwatappo.cn");
                Available = Directory.Exists(_dir) && Rescan() > 0;
            }
            catch { Available = false; }
        }

        int Rescan()
        {
            int n = 0;
            try
            {
                foreach (var f in Directory.GetFiles(_dir, "cumulative_click*.dat"))
                {
                    byte[] h = HashOf(f);
                    if (h == null) continue;
                    _hashes[f] = h;
                    n++;
                }
            }
            catch { }
            return n;
        }

        static byte[] HashOf(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var sha = SHA256.Create();
                return sha.ComputeHash(fs);
            }
            catch { return null; }
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>轮询：任一计数文件哈希变化返回 true（计数确实增长了）</summary>
        public bool PollChanged()
        {
            if (!Available) return false;
            bool changed = false;
            try
            {
                // 重新扫描（文件可能在首次计数时才创建）
                foreach (var f in Directory.GetFiles(_dir, "cumulative_click*.dat"))
                {
                    byte[] h = HashOf(f);
                    if (h == null) continue;
                    if (_hashes.TryGetValue(f, out var old))
                    {
                        if (!Same(h, old)) { changed = true; _hashes[f] = h; }
                    }
                    else
                    {
                        changed = true;      // 新出现的计数文件也算变化
                        _hashes[f] = h;
                    }
                }
            }
            catch { }
            if (changed) Changes++;
            return changed;
        }
    }
}
