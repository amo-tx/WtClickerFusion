using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace WtClickerGUI
{
    /// <summary>
    /// 桌宠内部精确状态（由 wt-fusion-bridge.js 每秒 POST，单位已换算）。
    /// c=R.totalClickCount / l,r=Nt.value,Ht.value（服务器时钟剩余毫秒）/ m=Ns 门槛 /
    /// q=Qn.value 时钟已同步 / o=联机状态。
    /// </summary>
    public sealed class FusionState
    {
        public long Clicks;
        public double LeftRemMs;
        public double RightRemMs;
        public long Minimum;
        public bool ClockReady;
        public bool Online;
        /// <summary>桌宠强制更新锁（Ie()）：true 时 pet-clap 被静默丢弃，点击全部不计。</summary>
        public bool ForceLocked;
        /// <summary>桌宠本地未上报点击数（区分「事件没进来」vs「上报失败」）。</summary>
        public long LocalClicks;
        /// <summary>桌宠视角的服务器总数/消费记账（totalClickCount=total−consume−spend+local）。</summary>
        public long ServerTotal;
        public long ServerSpend;
        public long ServerConsume;
        public bool LoggedIn;
    }

    /// <summary>
    /// 本地回环 HTTP（127.0.0.1:48643）。补丁用 text/plain POST（CORS 简单请求，
    /// 不触发 preflight）；响应里带 autoClaim，用于同步 Helper 的自动领箱开关。
    /// 只接受本机连接，不监听外网。
    /// </summary>
    internal sealed class FusionBridge : IDisposable
    {
        public const int Port = 48643;
        TcpListener _listener;
        volatile bool _running = true;
        long _received;
        FusionState _latest;
        DateTime _startedAt = DateTime.Now;

        /// <summary>远程监控模式（--web）：监听 0.0.0.0，允许外网访问只读面板；
        /// 桌宠状态上报 POST 仍只接受本机回环，防外部伪造状态。</summary>
        public bool PublicWeb { get; set; }

        /// <summary>UI/引擎侧注入的只读统计快照（/stats 数据源），返回 JSON 文本。</summary>
        public Func<string> StatsProvider;

        /// <summary>UI/引擎侧注入的日志环形缓冲（最近若干行，含时间戳）。</summary>
        public Func<string[]> LogTail;

        /// <summary>UI「自动领取宝箱」开关：随每次响应下发给补丁写 localStorage。</summary>
        public volatile bool AutoClaim = true;
        public long Received => Interlocked.Read(ref _received);
        public FusionState Latest => Volatile.Read(ref _latest);
        public event Action<FusionState> OnState;

        public void Start()
        {
            // 127.0.0.1（默认，仅本机）/ 0.0.0.0（--web，供 Azure VM 等远程只读监控）
            _listener = new TcpListener(PublicWeb ? IPAddress.Any : IPAddress.Loopback, Port);
            _listener.Start();
            new Thread(Serve) { IsBackground = true, Name = "FusionBridge" }.Start();
        }

        void Serve()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => Reply(client));
                }
                catch { if (_running) Thread.Sleep(50); }
            }
        }

        static string Header(string status, string type, int length, string origin)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(" Response\r\n");
            sb.Append("Content-Type: ").Append(type).Append("\r\n");
            sb.Append("Content-Length: ").Append(length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\nConnection: close\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            if (!string.IsNullOrEmpty(origin))
            {
                sb.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\nVary: Origin\r\n");
                sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
                sb.Append("Access-Control-Allow-Private-Network: true\r\n");
            }
            sb.Append("\r\n");
            return sb.ToString();
        }

        void Reply(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                try
                {
                    var stream = client.GetStream();
                    // 必须按原始字节解析：StreamReader 会预读缓冲，把 POST body 一起
                    // 吞进内部缓冲区，之后再从 NetworkStream 读就永远等不到数据
                    // （实测症状：POST 无响应、received 恒为 0）。
                    var acc = new MemoryStream();
                    var buf = new byte[4096];
                    int headEnd = -1;
                    while (acc.Length < 16384)
                    {
                        int n = stream.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        acc.Write(buf, 0, n);
                        var a = acc.ToArray();
                        for (int i = 0; i + 3 < a.Length; i++)
                            if (a[i] == 13 && a[i + 1] == 10 && a[i + 2] == 13 && a[i + 3] == 10)
                            { headEnd = i + 4; break; }
                        if (headEnd >= 0) break;
                    }
                    if (headEnd < 0) return;
                    var all = acc.ToArray();
                    var lines = Encoding.ASCII.GetString(all, 0, headEnd).Split('\n');
                    var parts = lines[0].Trim().Split(' ');
                    if (parts.Length < 2) return;

                    string origin = "";
                    int contentLength = 0;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var l = lines[i].Trim();
                        if (l.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))
                            origin = l.Substring(7).Trim();
                        else if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(l.Substring(15).Trim(), out contentLength);
                    }

                    string body = "{\"ok\":1,\"autoClaim\":" + (AutoClaim ? "true" : "false") + "}";
                    string path = parts[1].Split('?')[0];
                    bool localLoopback = IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint).Address);
                    string contentType = "application/json; charset=utf-8";

                    if (parts[0] == "OPTIONS")
                    {
                        body = "{}";
                    }
                    else if (parts[0] == "POST" && path == "/state" && contentLength > 0 && contentLength < 8192)
                    {
                        if (!localLoopback)
                        {
                            var deny = Encoding.UTF8.GetBytes("{\"ok\":0,\"err\":\"remote state forbidden\"}");
                            var dh = Encoding.ASCII.GetBytes(Header("403 Forbidden", contentType, deny.Length, null));
                            stream.Write(dh, 0, dh.Length); stream.Write(deny, 0, deny.Length); stream.Flush();
                            return;
                        }
                        var bodyBuf = new byte[contentLength];
                        int have = all.Length - headEnd;
                        int got = Math.Min(have, contentLength);
                        if (got > 0) Array.Copy(all, headEnd, bodyBuf, 0, got);
                        while (got < contentLength)
                        {
                            int n = stream.Read(bodyBuf, got, contentLength - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        var state = Parse(Encoding.UTF8.GetString(bodyBuf, 0, got));
                        if (state != null)
                        {
                            Volatile.Write(ref _latest, state);
                            Interlocked.Increment(ref _received);
                            OnState?.Invoke(state);
                        }
                    }
                    else if (parts[0] == "GET" && path == "/health")
                    {
                        body = "{\"ok\":1,\"protocol\":1,\"received\":" + Received + "}";
                    }
                    else if (parts[0] == "GET" && path == "/stats")
                    {
                        body = StatsProvider?.Invoke()
                            ?? "{\"ok\":1,\"received\":" + Received + ",\"uptimeSec\":" + (long)(DateTime.Now - _startedAt).TotalSeconds + "}";
                    }
                    else if (parts[0] == "GET" && (path == "/" || path == "/index.html"))
                    {
                        // 只读可视化监控页（本机与 --web 远程皆可访问；无任何控制能力）
                        var html = Encoding.UTF8.GetBytes(DashboardPage());
                        var hh = Encoding.ASCII.GetBytes(Header("200 OK", "text/html; charset=utf-8", html.Length, null));
                        stream.Write(hh, 0, hh.Length); stream.Write(html, 0, html.Length); stream.Flush();
                        return;
                    }

                    var bytes = Encoding.UTF8.GetBytes(body);
                    var head = Encoding.ASCII.GetBytes(Header("200 OK", "application/json; charset=utf-8", bytes.Length, origin));
                    stream.Write(head, 0, head.Length);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                catch { }
            }
        }

        static FusionState Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                double Num(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                bool Flag(string k) => r.TryGetProperty(k, out var v) &&
                    (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "online"));
                return new FusionState
                {
                    Clicks = (long)Num("c"),
                    LeftRemMs = Num("l"),
                    RightRemMs = Num("r"),
                    Minimum = (long)Num("m"),
                    ClockReady = Flag("q"),
                    Online = Flag("o"),
                    ForceLocked = r.TryGetProperty("k", out var kv) && (kv.ValueKind == JsonValueKind.True ||
                        (kv.ValueKind == JsonValueKind.String && kv.GetString() == "online")),
                    LocalClicks = (long)Num("n"),
                    ServerTotal = (long)Num("t"),
                    ServerSpend = (long)Num("sp"),
                    ServerConsume = (long)Num("sc"),
                    LoggedIn = Flag("li"),
                };
            }
            catch { return null; }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        /// <summary>只读监控面板（GET /）。深色、自适应、1s 轮询 /stats；无任何控制端点。</summary>
        internal static string DashboardPage() =>
"""
<!doctype html><html lang="zh"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>WuwaTappo 融合监控</title><style>
:root{--bg:#17120d;--card:#221a12;--ink:#f0e6d6;--soft:#a8967c;--acc:#e09a5f;--ok:#7ec96f;--warn:#e0b05f;--bad:#e07a5f;--line:#3a2f21}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--ink);font:15px/1.5 "Microsoft YaHei UI",system-ui,sans-serif;padding:24px;max-width:980px;margin:0 auto}
h1{font-size:20px;color:var(--acc);display:flex;align-items:center;gap:10px}
.dot{width:10px;height:10px;border-radius:50%;background:#666}.dot.on{background:var(--ok);box-shadow:0 0 8px var(--ok)}
.sub{color:var(--soft);font-size:12.5px;margin:4px 0 18px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:12px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px 16px}
.cap{color:var(--soft);font-size:12px}.val{font-size:26px;font-weight:700;margin-top:4px;font-variant-numeric:tabular-nums}
.val.ok{color:var(--ok)}.val.warn{color:var(--warn)}.val.bad{color:var(--bad)}
.bar{height:8px;background:#33281b;border-radius:6px;margin-top:8px;overflow:hidden}
.bar>i{display:block;height:100%;background:var(--acc);border-radius:6px;transition:width 1s linear}
#strat{margin:14px 0;padding:12px 16px;background:var(--card);border:1px solid var(--line);border-radius:12px;font-size:13.5px;color:var(--soft)}
#log{margin-top:14px;background:#14100b;border:1px solid var(--line);border-radius:12px;padding:12px 14px;height:240px;overflow:auto;font:12px/1.55 Consolas,monospace;color:#c9b99b;white-space:pre-wrap}
.off{opacity:.35;filter:grayscale(1)}
</style></head><body>
<h1><span class="dot" id="dot"></span>WuwaTappo 融合监控 <span id="clock" style="font-size:13px;color:var(--soft);font-weight:400"></span></h1>
<div class="sub" id="sub">只读监控 · 数据来自精确状态桥（1s 刷新） · 构建加载中…</div>
<div class="grid" id="g">
 <div class="card"><div class="cap">当前次数（可花余额）</div><div class="val" id="clicks">—</div><div class="cap" id="fresh"></div></div>
 <div class="card"><div class="cap">左槽到期</div><div class="val" id="lms">—</div><div class="bar"><i id="lbar"></i></div></div>
 <div class="card"><div class="cap">右槽到期</div><div class="val" id="rms">—</div><div class="bar"><i id="rbar"></i></div></div>
 <div class="card"><div class="cap">点击速率（计划 / 实际）</div><div class="val" id="cps">—</div><div class="cap" id="cpscap">上限 —</div></div>
 <div class="card"><div class="cap">本次注入 / 宝箱</div><div class="val" id="inj">—</div><div class="cap" id="bridge">桥心跳 —</div></div>
 <div class="card"><div class="cap">运行状态</div><div class="val" id="st">—</div><div class="cap" id="misc"></div></div>
</div>
<div id="frozenbar" style="display:none;margin:14px 0;padding:12px 16px;background:#3a2d15;border:1px solid #e0b05f;border-radius:12px;color:#ffd9a0;font-size:13.5px">⏸ 计数冻结：注入在发但桌宠不计数，已停手止损；看门狗将自动重启桌宠自愈（约 3 分钟）。</div>
<div id="lockbar" style="display:none;margin:14px 0;padding:12px 16px;background:#3a1d15;border:1px solid #e07a5f;border-radius:12px;color:#ffb09a;font-size:13.5px">⛔ 桌宠强制更新锁生效：所有点击被静默丢弃（注入无效）。请 RDP 上去查看桌宠的更新提示并处理。</div>
<div id="strat">策略加载中…</div>
<div id="log"></div>
<script>
const $=id=>document.getElementById(id);const W=1800;
let lastOk=true;
function fmt(s){if(s<0)return"—";s=Math.max(0,s);const m=Math.floor(s/60);return m+":"+String(Math.floor(s%60)).padStart(2,"0")}
function num(n){return n.toLocaleString("zh-CN")}
async function tick(){
 try{
  const r=await fetch("/stats",{cache:"no-store"});if(!r.ok)throw 0;const s=await r.json();
  $("dot").className="dot on";
  $("clock").textContent=s.time;
  $("sub").textContent="只读监控 · 数据来自精确状态桥（1s 刷新） · 构建 "+s.build+(s.precise?" · 精确桥模式":" · OCR 回退模式");
  $("clicks").textContent=num(s.clicks);$("clicks").className="val"+(s.fresh?"":" off");
  $("fresh").textContent=s.fresh?"读数新鲜":"读数过期";
  const f=v=>v>=0;
  for(const[id,b,ms]of[["lms","lbar",s.leftMs],["rms","rbar",s.rightMs]]){
   $(id).textContent=ms>0?fmt(ms/1000):(ms===0?"已到期":"—");
   $(id).className="val "+(ms===0?"warn":ms<0?"":"");
   $(b).style.width=(f(ms)&&ms>0?Math.min(100,ms/W*100):0)+"%";
  }
  $("cps").textContent=s.planCps.toFixed(2)+" / "+s.actualCps.toFixed(2);
  $("cpscap").textContent="上限 "+s.cpsMax.toFixed(1)+" CPS";
  $("inj").textContent=num(s.injected)+" / "+num(s.chests);
  $("bridge").textContent="桥心跳 "+num(s.received)+" · 在线 "+(s.online?"是":"否")+(s.clockReady?" · 时钟已同步":" · 时钟未同步");
  const st=$("st");
  if(!s.running){st.textContent="未启动";st.className="val"}
  else if(s.paused){st.textContent="精准待命";st.className="val ok"}
  else{st.textContent="连点中";st.className="val"}
  $("misc").textContent=(s.paused?"扣款已备好等领取":"补量中")+" · 排队气泡 "+s.queued+" · 需再攒 "+num(Math.max(0,s.needed))
    +" · 桌宠[登录"+(s.petLoggedIn?"✓":"✗")+(s.locked?" 更新锁⛔":"")+" 本地未报"+num(s.localClicks)+" 服务器总数"+num(s.serverTotal)+"]";
  $("lockbar").style.display=s.locked?"block":"none";
  $("frozenbar").style.display=s.frozen?"block":"none";
  $("strat").textContent="策略："+s.strategy+(s.localClicks>0?" · 本地未上报 "+s.localClicks+" 次":"");
  $("log").textContent=(s.log||[]).join("\n");$("log").scrollTop=1e9;
  lastOk=true;
 }catch(e){
  if(lastOk){lastOk=false;$("dot").className="dot";$("sub").textContent="桥连接断开——程序未运行或网络不可达，自动重试中…"}
 }
}
tick();setInterval(tick,1000);
</script></body></html>
""";
    }
}
