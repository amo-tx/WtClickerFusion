using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WtClickerGUI
{
    /// <summary>
    /// PetForge 的 character.json 定义（只读解析，不依赖 PetForge 的 Python 代码）。
    /// 支持三种素材布局：根目录 PNG、frames/ 子目录、frame_*.png 大图；
    /// 动画名 idle / slap_l / slap_r / key / happy / drag；outfits 里 default=true
    /// 的装扮会按 offset 叠加到每一帧。
    /// </summary>
    internal sealed class CharacterDef
    {
        public string Name;
        public string Dir;
        public string Source;
        public double Scale = 1.0;
        public int Fps = 8;
        public bool Tappo;
        /// <summary>每次点击触发「兴奋狂拍」的概率（0~1）；0 = 不用兴奋动画。</summary>
        public double FrenzyChance;
        public readonly Dictionary<string, List<string>> Animations =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public readonly List<OutfitDef> Outfits = new List<OutfitDef>();
        public override string ToString() => Name;
    }

    internal sealed class OutfitDef
    {
        public string Name, Image;
        public int Ox, Oy;
        public bool Default;
    }

    /// <summary>解码并归一化后的角色帧；所有动作帧统一到同一画布，避免动画抖动。</summary>
    internal sealed class CharacterFrames : IDisposable
    {
        public readonly Dictionary<string, List<Bitmap>> Clips =
            new Dictionary<string, List<Bitmap>>(StringComparer.OrdinalIgnoreCase);
        public int Width, Height, Fps;
        public double FrenzyChance;

        public void Dispose()
        {
            foreach (var list in Clips.Values)
                foreach (var b in list) b.Dispose();
            Clips.Clear();
        }
    }

    internal sealed class CharacterLibrary
    {
        public readonly string Root;
        public readonly List<CharacterDef> Items = new List<CharacterDef>();
        public string Error;
        readonly Dictionary<string, CharacterFrames> _cache =
            new Dictionary<string, CharacterFrames>(StringComparer.OrdinalIgnoreCase);

        public CharacterLibrary(string root)
        {
            Root = root;
            try
            {
                if (!Directory.Exists(root))
                {
                    Error = "未找到角色目录 " + root;
                    return;
                }
                foreach (var dir in Directory.GetDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var jsonPath = Path.Combine(dir, "character.json");
                    if (!File.Exists(jsonPath)) continue;
                    try
                    {
                        var def = Parse(dir, jsonPath);
                        if (def != null && def.Animations.Count > 0) Items.Add(def);
                    }
                    catch (Exception ex)
                    {
                        Error = (Error == null ? "" : Error + "; ") + Path.GetFileName(dir) + ": " + ex.Message;
                    }
                }
            }
            catch (Exception ex) { Error = ex.Message; }
        }

        static CharacterDef Parse(string dir, string jsonPath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath, System.Text.Encoding.UTF8));
            var r = doc.RootElement;
            var def = new CharacterDef
            {
                Dir = dir,
                Name = GetString(r, "name") ?? Path.GetFileName(dir),
                Source = GetString(r, "source"),
                Scale = GetDouble(r, "scale", 1.0),
                Fps = Math.Clamp(GetInt(r, "fps", 8), 1, 60),
                Tappo = GetBool(r, "tappo", false),
                FrenzyChance = Math.Clamp(GetDouble(r, "frenzyChance", 0.0), 0.0, 1.0),
            };

            if (r.TryGetProperty("animations", out var anims) && anims.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in anims.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Array) continue;
                    var list = new List<string>();
                    foreach (var e in p.Value.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String)
                        {
                            var rel = e.GetString().Replace('/', Path.DirectorySeparatorChar);
                            if (File.Exists(Path.Combine(dir, rel))) list.Add(rel);
                        }
                    if (list.Count > 0) def.Animations[p.Name] = list;
                }
            }

            // 没有 animations 字段时，按 PetForge 的「动作_编号.png」约定兜底扫描
            if (def.Animations.Count == 0)
            {
                foreach (var f in Directory.GetFiles(dir, "*.png").OrderBy(p => p))
                {
                    var stem = Path.GetFileNameWithoutExtension(f);
                    int u = stem.LastIndexOf('_');
                    var key = u > 0 && int.TryParse(stem[(u + 1)..], out _) ? stem[..u] : "idle";
                    if (!def.Animations.TryGetValue(key, out var list))
                        def.Animations[key] = list = new List<string>();
                    list.Add(Path.GetFileName(f));
                }
            }

            if (r.TryGetProperty("outfits", out var outs) && outs.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in outs.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object) continue;
                    var img = GetString(o, "image");
                    if (img == null) continue;
                    img = img.Replace('/', Path.DirectorySeparatorChar);
                    var od = new OutfitDef
                    {
                        Name = GetString(o, "name") ?? Path.GetFileNameWithoutExtension(img),
                        Image = img,
                        Default = GetBool(o, "default", false),
                    };
                    if (o.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.Array)
                    {
                        var arr = off.EnumerateArray().ToArray();
                        if (arr.Length > 0 && arr[0].TryGetInt32(out int x)) od.Ox = x;
                        if (arr.Length > 1 && arr[1].TryGetInt32(out int y)) od.Oy = y;
                    }
                    def.Outfits.Add(od);
                }
            }

            // tappo 角色缺左右拍打帧时，用 happy / key / idle 兜底，保证点击永远有反馈
            if (def.Tappo)
            {
                if (!def.Animations.ContainsKey("slap_l"))
                    def.Animations["slap_l"] = Pick(def, "happy", "key", "idle");
                if (!def.Animations.ContainsKey("slap_r"))
                    def.Animations["slap_r"] = Pick(def, "key", "happy", "idle");
            }
            if (!def.Animations.ContainsKey("idle") && def.Animations.Count > 0)
                def.Animations["idle"] = def.Animations.Values.First();
            return def;
        }

        static List<string> Pick(CharacterDef d, params string[] names)
        {
            foreach (var n in names)
                if (d.Animations.TryGetValue(n, out var v) && v.Count > 0) return new List<string>(v);
            return new List<string>();
        }

        static string GetString(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int GetInt(JsonElement e, string key, int dft) =>
            e.TryGetProperty(key, out var v) && v.TryGetInt32(out int i) ? i : dft;
        static double GetDouble(JsonElement e, string key, double dft) =>
            e.TryGetProperty(key, out var v) && v.TryGetDouble(out double d) ? d : dft;
        static bool GetBool(JsonElement e, string key, bool dft)
        {
            if (!e.TryGetProperty(key, out var v)) return dft;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => dft,
            };
        }

        /// <summary>载入（并缓存）一个角色的全部帧。线程不安全，只在 UI 线程调用。</summary>
        public CharacterFrames Load(CharacterDef def)
        {
            if (_cache.TryGetValue(def.Name, out var cached)) return cached;

            var raw = new Dictionary<string, List<Bitmap>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in def.Animations)
            {
                var list = new List<Bitmap>();
                foreach (var rel in kv.Value)
                {
                    try
                    {
                        using var fs = new FileStream(Path.Combine(def.Dir, rel), FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var img = Image.FromStream(fs);
                        list.Add(new Bitmap(img));      // 复制一份，避免锁住文件
                    }
                    catch { /* 单帧损坏不影响整个角色 */ }
                }
                if (list.Count > 0) raw[kv.Key] = list;
            }
            if (raw.Count == 0) return null;

            int mw = raw.Values.SelectMany(l => l).Max(b => b.Width);
            int mh = raw.Values.SelectMany(l => l).Max(b => b.Height);

            // 默认装扮：按 character.json 的 offset 叠加（PetForge 用左上角对齐）
            var overlays = new List<(Bitmap Bmp, int Ox, int Oy)>();
            foreach (var o in def.Outfits.Where(o => o.Default))
            {
                var p = Path.Combine(def.Dir, o.Image);
                if (!File.Exists(p)) continue;
                try
                {
                    using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var img = Image.FromStream(fs);
                    overlays.Add((new Bitmap(img), o.Ox, o.Oy));
                }
                catch { }
            }

            var frames = new CharacterFrames
            {
                Width = mw, Height = mh, Fps = def.Fps, FrenzyChance = def.FrenzyChance,
            };
            foreach (var kv in raw)
            {
                var list = new List<Bitmap>();
                foreach (var b in kv.Value)
                {
                    var canvas = new Bitmap(mw, mh, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(b, (mw - b.Width) / 2f, (mh - b.Height) / 2f, b.Width, b.Height);
                        foreach (var ov in overlays)
                            g.DrawImage(ov.Bmp, ov.Ox, ov.Oy, ov.Bmp.Width, ov.Bmp.Height);
                    }
                    b.Dispose();
                    list.Add(canvas);
                }
                frames.Clips[kv.Key] = list;
            }
            foreach (var ov in overlays) ov.Bmp.Dispose();
            _cache[def.Name] = frames;
            return frames;
        }

        /// <summary>当前常驻的角色帧缓存数（验收用）。</summary>
        public int CachedCount => _cache.Count;

        /// <summary>
        /// 只保留正在显示的角色帧。旧实现把每一次切换过的角色都永久留在
        /// _cache 里：5 个角色 × 18 帧 × 581×619×4 ≈ 130MB+ 解码位图常驻，
        /// 切回时却几乎不省时间（用户切换是低频操作）。
        /// ★ 必须在 CharacterWindow.LoadCharacter 之后调用：那时窗口的 _frames
        ///   已指向新角色，且窗口用的是自己预渲染的 HBITMAP 副本，旧 Bitmap
        ///   不再被引用，释放安全。
        /// </summary>
        public int ReleaseExcept(string keepName)
        {
            int freed = 0;
            foreach (var kv in _cache.ToArray())
            {
                if (string.Equals(kv.Key, keepName, StringComparison.OrdinalIgnoreCase)) continue;
                kv.Value.Dispose();
                _cache.Remove(kv.Key);
                freed++;
            }
            return freed;
        }

        public void DisposeAll()
        {
            foreach (var f in _cache.Values) f.Dispose();
            _cache.Clear();
        }
    }
}
