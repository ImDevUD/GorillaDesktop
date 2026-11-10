// GorillaDesktop aktarma sunucusu.
// Yayıncı (role=pub) görüntü/ses mesajlarını gönderir; sunucu aynı kanaldaki izleyicilere (role=sub) dağıtır.
// Kanal = lobi + yayıncıdan türetilmiş rastgele görünümlü bir kimlik; içerik saklanmaz, sadece iletilir.
//
// Ortam değişkenleri:
//   SECRET       Moddaki "Anahtar" ile aynı olmalı (boşsa herkes bağlanabilir)
//   MAX_VIEWERS  Kanal başına en fazla izleyici (varsayılan 10)
//   PORT         Dinlenecek port (Render vb. bunu kendisi verir)

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var secret = Environment.GetEnvironmentVariable("SECRET") ?? "";
var maxViewers = int.TryParse(Environment.GetEnvironmentVariable("MAX_VIEWERS"), out var mv) ? mv : 10;
var channels = new ConcurrentDictionary<string, Channel>();
var validId = new Regex("^[a-zA-Z0-9]{8,64}$");

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/", () => $"GorillaDesktop relay OK - {channels.Count} kanal");

app.Map("/s/{id}", async (HttpContext ctx, string id) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest("websocket gerekli");
    if (secret.Length > 0 && ctx.Request.Query["key"] != secret) return Results.Unauthorized();
    if (!validId.IsMatch(id)) return Results.BadRequest("geçersiz kanal");

    var role = ctx.Request.Query["role"].ToString();
    var channel = channels.GetOrAdd(id, _ => new Channel());

    if (role == "pub")
    {
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await channel.RunPublisher(ws, ctx.RequestAborted);
    }
    else
    {
        if (channel.ViewerCount >= maxViewers) return Results.StatusCode(429);
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await channel.RunViewer(ws, ctx.RequestAborted);
    }

    // Boş kanalları temizle
    if (channel.IsEmpty) channels.TryRemove(new KeyValuePair<string, Channel>(id, channel));
    return Results.Empty;
});

app.Run();

// Mesaj türleri (ilk bayt): 1 = JPEG kare, 2 = ses, 4 = izleyici sayısı (sunucu -> yayıncı)
sealed class Channel
{
    private readonly object gate = new();
    private Viewer? publisher;
    private readonly List<Viewer> viewers = new();

    public int ViewerCount { get { lock (gate) return viewers.Count; } }
    public bool IsEmpty { get { lock (gate) return publisher == null && viewers.Count == 0; } }

    public async Task RunPublisher(WebSocket ws, CancellationToken ct)
    {
        var me = new Viewer(ws);
        Viewer? old;
        lock (gate) { old = publisher; publisher = me; }
        old?.Close(); // aynı kanala yeni yayıncı gelirse eskisi düşer
        _ = me.SendLoop(ct);
        NotifyCount();

        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (message.Length > 2 * 1024 * 1024) break; // çok büyük mesaj: bağlantıyı kes
                if (!result.EndOfMessage) continue;

                var data = message.ToArray();
                message.SetLength(0);
                Viewer[] targets;
                lock (gate) targets = viewers.ToArray();
                foreach (var v in targets) v.Enqueue(data);
            }
        }
        catch { }
        finally
        {
            lock (gate) if (publisher == me) publisher = null;
            me.Close();
        }
    }

    public async Task RunViewer(WebSocket ws, CancellationToken ct)
    {
        var me = new Viewer(ws);
        lock (gate) viewers.Add(me);
        NotifyCount();
        var send = me.SendLoop(ct);

        var buffer = new byte[1024];
        try
        {
            // İzleyici bir şey göndermez; sadece kapanmayı bekle
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
        finally
        {
            lock (gate) viewers.Remove(me);
            me.Close();
            NotifyCount();
        }
        await send;
    }

    // Yayıncıya kaç kişinin izlediğini söyle
    private void NotifyCount()
    {
        Viewer? pub;
        int count;
        lock (gate) { pub = publisher; count = viewers.Count; }
        pub?.Enqueue(new byte[] { 4, (byte)count });
    }
}

// Bir bağlantının gönderim kuyruğu. Yavaş izleyicide eski görüntü kareleri atılır (ses korunur),
// böylece bir kişinin yavaş interneti diğerlerini etkilemez.
sealed class Viewer(WebSocket ws)
{
    private readonly ConcurrentQueue<byte[]> queue = new();
    private readonly SemaphoreSlim signal = new(0);
    private long queuedBytes;
    private volatile bool closed;

    public void Enqueue(byte[] data)
    {
        if (closed) return;
        var size = Interlocked.Read(ref queuedBytes);
        if (data[0] == 1 && size > 1_500_000) return; // görüntü: geride kaldıysa bu kareyi atla
        if (size > 6_000_000) return;                  // her şey: çok geride
        Interlocked.Add(ref queuedBytes, data.Length);
        queue.Enqueue(data);
        signal.Release();
    }

    public async Task SendLoop(CancellationToken ct)
    {
        try
        {
            while (!closed && !ct.IsCancellationRequested)
            {
                await signal.WaitAsync(ct);
                if (!queue.TryDequeue(out var data)) continue;
                Interlocked.Add(ref queuedBytes, -data.Length);
                if (ws.State != WebSocketState.Open) break;
                await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
            }
        }
        catch { }
    }

    public void Close()
    {
        if (closed) return;
        closed = true;
        signal.Release();
        try { ws.Abort(); } catch { }
    }
}
