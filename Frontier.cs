using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FeedCheckerGui;

// ===================== Фронтир: данные, сессия, клиент, сравнение =====================

/// <summary>Один каталог, как его видит Фронтир (ответ catalogs.get).</summary>
public sealed class FrontierCatalog
{
    public string Uuid { get; set; } = "";
    public DateOnly? DateStart { get; set; }
    public DateOnly? DateEnd { get; set; }
    public string Conditions { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>publications.active: то же число, что показывает колонка «офферы» на странице Фронтира.</summary>
    public long ActiveOffers { get; set; }
    public long InactiveOffers { get; set; }
    public List<string> GeoNames { get; } = new();
}

public class FrontierException : Exception
{
    public FrontierException(string message) : base(message) { }
}

/// <summary>Сервер не принял сессию: нужно войти заново.</summary>
public sealed class FrontierAuthException : FrontierException
{
    public FrontierAuthException(string message) : base(message) { }
}

/// <summary>Разбор ссылки на Фронтир: берём ID ретейлера и (если есть) фильтр has_source_url.</summary>
internal static class FrontierUrl
{
    public static bool TryParse(string text, out int retailerId, out bool? onlyFromFeed)
    {
        retailerId = 0;
        onlyFromFeed = null;
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out retailerId))
            return retailerId > 0;

        retailerId = 0;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            string key = Uri.UnescapeDataString(kv[0]);
            string val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            if (key == "retailer_ids")
            {
                string first = val.Split(',')[0].Trim();
                if (int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out int id)) retailerId = id;
            }
            else if (key == "has_source_url")
            {
                if (val.Equals("true", StringComparison.OrdinalIgnoreCase)) onlyFromFeed = true;
                else if (val.Equals("false", StringComparison.OrdinalIgnoreCase)) onlyFromFeed = false;
            }
        }
        return retailerId > 0;
    }
}

/// <summary>Cookie входа во Фронтир: хранятся на этом компьютере, зашифрованные средствами Windows (как ключи S3).</summary>
internal static class FrontierSession
{
    private static string FilePath => Path.Combine(AppStorage.Dir, "frontier.session");
    public static string WebViewDir => Path.Combine(AppStorage.Dir, "webview");

    public static bool Exists => File.Exists(FilePath);

    public static DateTime? SavedAtLocal => Exists ? File.GetLastWriteTime(FilePath) : null;

    public static void Save(string cookieHeader)
    {
        Directory.CreateDirectory(AppStorage.Dir);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(cookieHeader), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, data);
    }

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            string s = Encoding.UTF8.GetString(bytes);
            return s.Length == 0 ? null : s;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* не критично */ }
        try { if (Directory.Exists(WebViewDir)) Directory.Delete(WebViewDir, recursive: true); } catch { /* профиль браузера может быть занят */ }
    }
}

/// <summary>Читает каталоги из Фронтира так же, как его страница: по веб-сокету /ws/, метод catalogs.get.</summary>
public static class FrontierClient
{
    private const int PageLimit = 100;
    private const int FallbackLimit = 20; // столько запрашивает сама страница

    private static Uri SocketUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            throw new FrontierException("Неверный адрес Фронтира в настройках: " + baseUrl);
        return new Uri((u.Scheme == Uri.UriSchemeHttps ? "wss://" : "ws://") + u.Authority + "/ws/");
    }

    private static async Task<ClientWebSocket> ConnectAsync(string baseUrl, string cookie, CancellationToken ct)
    {
        var uri = SocketUri(baseUrl);
        var ws = new ClientWebSocket();
        try
        {
            ws.Options.SetRequestHeader("Cookie", cookie);
            ws.Options.SetRequestHeader("Origin", new Uri(baseUrl.Trim()).GetLeftPart(UriPartial.Authority));
            ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) FeedChecker");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            await ws.ConnectAsync(uri, cts.Token);
            return ws;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new FrontierException("Фронтир не ответил при подключении за 30 секунд. Проверьте, включён ли VPN.");
        }
        catch (WebSocketException ex)
        {
            ws.Dispose();
            string text = ex.Message + " " + ex.InnerException?.Message;
            if (text.Contains("401") || text.Contains("403") || text.Contains("302") || text.Contains("301"))
                throw new FrontierAuthException("Фронтир не принял сессию (" + ex.Message + ").");
            throw new FrontierException("Не удалось подключиться к Фронтиру: " + ex.Message +
                                        (ex.InnerException is null ? "" : " — " + ex.InnerException.Message));
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private static Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);

    private static async Task<string> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new FrontierException("Фронтир закрыл соединение" +
                                            (string.IsNullOrEmpty(r.CloseStatusDescription) ? "." : ": " + r.CloseStatusDescription));
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>Простой JSON-RPC: запрос {"id","method","params"}, ответ {"id","result"}; на ping сервера отвечаем тем же seq.</summary>
    private sealed class Rpc
    {
        private readonly ClientWebSocket _ws;
        private int _nextId = 10;

        public Rpc(ClientWebSocket ws) => _ws = ws;

        public async Task<JsonElement> CallAsync(string method, Dictionary<string, object?> parameters, CancellationToken ct)
        {
            int id = ++_nextId;
            string request = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await SendAsync(_ws, request, cts.Token);
                while (true)
                {
                    string text = await ReceiveAsync(_ws, cts.Token);
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        if (m.GetString() == "ping" && root.TryGetProperty("id", out var pid) && root.TryGetProperty("params", out var pp))
                            await SendAsync(_ws, "{\"id\":" + pid.GetRawText() + ",\"result\":" + pp.GetRawText() + "}", cts.Token);
                        continue; // прочие сообщения сервера нам не нужны
                    }

                    bool mine = !root.TryGetProperty("id", out var rid) ||
                                (rid.ValueKind == JsonValueKind.Number && rid.TryGetInt32(out int rv) && rv == id);
                    if (!mine) continue;

                    if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                    {
                        string raw = err.GetRawText();
                        throw new FrontierException("Фронтир вернул ошибку: " + (raw.Length > 300 ? raw[..300] + "…" : raw));
                    }
                    if (root.TryGetProperty("result", out var result)) return result.Clone();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new FrontierException($"Фронтир не ответил на запрос {method} за 2 минуты.");
            }
        }
    }

    /// <summary>Только проверка, что соединение и вход работают.</summary>
    public static async Task TestAsync(string baseUrl, string cookie, CancellationToken ct)
    {
        using var ws = await ConnectAsync(baseUrl, cookie, ct);
    }

    /// <summary>Все каталоги ретейлера постранично (limit/skip). onlyFromFeed — фильтр has_source_url.</summary>
    public static async Task<List<FrontierCatalog>> LoadAsync(string baseUrl, string cookie, int retailerId,
        bool onlyFromFeed, CancellationToken ct)
    {
        using var ws = await ConnectAsync(baseUrl, cookie, ct);
        var rpc = new Rpc(ws);
        var list = new List<FrontierCatalog>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int limit = PageLimit, skip = 0;

        for (int page = 0; page < 2000; page++)
        {
            var prm = new Dictionary<string, object?>
            {
                ["retailer_ids"] = new[] { retailerId },
                ["limit"] = limit,
                ["skip"] = skip,
            };
            if (onlyFromFeed) prm["has_source_url"] = "true"; // страница шлёт именно строку "true"

            JsonElement result;
            try
            {
                result = await rpc.CallAsync("catalogs.get", prm, ct);
            }
            catch (FrontierException) when (page == 0 && limit != FallbackLimit)
            {
                limit = FallbackLimit; // сервер мог не принять большой limit — пробуем как на странице
                continue;
            }

            if (!result.TryGetProperty("catalogs", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new FrontierException("Неожиданный ответ Фронтира: в ответе нет списка catalogs.");

            int n = 0, added = 0;
            foreach (var el in arr.EnumerateArray())
            {
                n++;
                var c = ParseCatalog(el);
                if (c.Uuid.Length == 0 || seen.Add(c.Uuid))
                {
                    list.Add(c);
                    added++;
                }
            }

            if (n < limit || added == 0) break;
            skip += n;
        }

        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;

    private static DateOnly? Date(JsonElement e, string name)
    {
        string s = Str(e, name);
        if (s.Length == 0) return null;
        return DateOnly.TryParse(s.Length >= 10 ? s[..10] : s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static FrontierCatalog ParseCatalog(JsonElement e)
    {
        bool isActive = true;
        if (e.TryGetProperty("is_active", out var ia) && ia.ValueKind == JsonValueKind.False) isActive = false;

        var c = new FrontierCatalog
        {
            Uuid = Str(e, "catalog_uuid"),
            DateStart = Date(e, "date_start"),
            DateEnd = Date(e, "date_end"),
            Conditions = Str(e, "conditions"),
            SourceUrl = Str(e, "source_url"),
            IsActive = isActive,
        };

        if (e.TryGetProperty("publications", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            c.ActiveOffers = Num(p, "active");
            c.InactiveOffers = Num(p, "inactive");
        }

        if (e.TryGetProperty("geo_locations", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in g.EnumerateArray())
            {
                string name = Str(x, "name");
                if (name.Length > 0) c.GeoNames.Add(name);
            }
        }
        return c;
    }
}

// ---------------------- результат сравнения ----------------------

public enum FrontierState { Updated, Partial, NotUpdated, NoMatch, Failed }

public sealed class FrontierRow
{
    public string Name { get; init; } = "";
    /// <summary>В каком файле (JSON) нашлось это гео; у каталогов, которых нет в JSON, пусто.</summary>
    public string Files { get; init; } = "";
    public long? JsonOffers { get; init; }
    public long? FrontierOffers { get; init; }
    public DateOnly? JsonStart { get; init; }
    public DateOnly? FrontierStart { get; init; }
    public DateOnly? FrontierEnd { get; init; }
    /// <summary>OK или список замечаний через «; ».</summary>
    public string Status { get; init; } = "OK";
    public string Details { get; init; } = "";

    public int Rank => Status switch
    {
        "OK" => 9,
        var s when s.StartsWith("Нет во Фронтире") => 0,
        var s when s.StartsWith("Пропал") => 0,
        var s when s.StartsWith("Период старый") => 1,
        var s when s.StartsWith("Нет в JSON") => 3,
        _ => 2,
    };
}

public sealed class FrontierComparison
{
    public int RetailerId { get; init; }
    public FrontierState State { get; set; }
    public string Message { get; set; } = "";
    public int FrontierCatalogs { get; set; }
    public int JsonGeos { get; set; }
    public int Fresh { get; set; }
    public DateTime LoadedLocal { get; set; } = DateTime.Now;
    public List<FrontierRow> Rows { get; } = new();
    public IssueCollector Issues { get; } = new(2000);

    public long Problems => Issues.Totals.Values.Sum();

    public static string StateText(FrontierState s) => s switch
    {
        FrontierState.Updated => "обновлён",
        FrontierState.Partial => "обновлён частично",
        FrontierState.NotUpdated => "не обновлён",
        FrontierState.NoMatch => "не удалось сопоставить",
        _ => "не загрузился",
    };
}

// ------------------------------------------------------------ история каталогов Фронтира

/// <summary>Каталог во Фронтире на момент проверки (для истории).</summary>
public sealed class FrontierCatalogRec
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public DateOnly? Start { get; set; }
    public DateOnly? End { get; set; }
    public long Offers { get; set; }
}

/// <summary>Снимок каталогов сети во Фронтире за одну проверку.</summary>
public sealed class FrontierRunRecord
{
    public string NetworkId { get; set; } = "";
    public int RetailerId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int Total { get; set; }
    public int Fresh { get; set; }
    public List<FrontierCatalogRec> Catalogs { get; set; } = new();
}

/// <summary>История лежит в %AppData%\FeedChecker\frontier-history\&lt;id сети&gt;\ГГГГММДД-ЧЧММСС-ммм.json, по файлу на проверку.</summary>
public static class FrontierHistory
{
    private static string DirFor(string networkId) =>
        Path.Combine(AppStorage.Dir, "frontier-history",
            new string(networkId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()));

    /// <summary>Все снимки сети по возрастанию времени. Битые файлы пропускаются.</summary>
    public static List<FrontierRunRecord> Load(string networkId)
    {
        var list = new List<FrontierRunRecord>();
        string dir = DirFor(networkId);
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<FrontierRunRecord>(File.ReadAllText(file, Encoding.UTF8), Json.Options);
                if (rec is not null) list.Add(rec);
            }
            catch { /* повреждённый снимок пропускаем */ }
        }
        return list.OrderBy(r => r.TimestampUtc).ToList();
    }

    public static FrontierRunRecord Snapshot(string networkId, int retailerId, IReadOnlyList<FrontierCatalog> all, FrontierComparison cmp) => new()
    {
        NetworkId = networkId,
        RetailerId = retailerId,
        TimestampUtc = DateTime.UtcNow,
        Total = cmp.FrontierCatalogs,
        Fresh = cmp.Fresh,
        Catalogs = all.Where(c => c.IsActive).Select(c => new FrontierCatalogRec
        {
            Uuid = c.Uuid,
            Name = FrontierComparer.NameOf(c),
            Start = c.DateStart,
            End = c.DateEnd,
            Offers = c.ActiveOffers,
        }).ToList(),
    };

    public static void Save(FrontierRunRecord rec) =>
        Json.WriteAtomic(Path.Combine(DirFor(rec.NetworkId), rec.TimestampUtc.ToString("yyyyMMdd-HHmmss-fff") + ".json"), rec);

    public static void Purge(string networkId, int keepDays)
    {
        string dir = DirFor(networkId);
        if (!Directory.Exists(dir)) return;
        var limit = DateTime.UtcNow.AddDays(-keepDays);
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<FrontierRunRecord>(File.ReadAllText(file, Encoding.UTF8), Json.Options);
                if (rec is not null && rec.TimestampUtc < limit) File.Delete(file);
            }
            catch { /* не критично */ }
        }
    }
}

public static class FrontierComparer
{
    public static string NameOf(FrontierCatalog c)
    {
        string name = c.GeoNames.Count > 0 ? string.Join(", ", c.GeoNames) : Snippet(c.Conditions);
        return name.Length == 0 ? c.Uuid : name;
    }

    /// <summary>
    /// Сверка по каталогам: сколько активных каталогов сейчас во Фронтире, у скольких из них период актуален
    /// (date_start не раньше date_start в JSON) и что изменилось с прошлых проверок (past — снимки по возрастанию времени).
    /// Гео из JSON с каталогами Фронтира не сопоставляются. tolerancePercent оставлен для совместимости и не используется.
    /// </summary>
    public static FrontierComparison Compare(int retailerId, NetworkSummary summary, IReadOnlyList<FrontierCatalog> all,
        double tolerancePercent, int periodDays, IReadOnlyList<FrontierRunRecord>? past = null)
    {
        periodDays = Math.Max(1, periodDays);
        past ??= Array.Empty<FrontierRunRecord>();
        string net = summary.Name;
        var res = new FrontierComparison { RetailerId = retailerId, JsonGeos = summary.GeoCount };
        var issues = res.Issues;
        var catalogs = all.Where(c => c.IsActive).ToList();
        res.FrontierCatalogs = catalogs.Count;

        DateOnly? exp = summary.DateStartMax;
        DateOnly? expEnd = exp?.AddDays(periodDays - 1);
        string expectedText = exp is { } ds
            ? $"JSON date_start {ds:yyyy-MM-dd}, ожидаемый период {ds:yyyy-MM-dd} – {expEnd:yyyy-MM-dd}"
            : "в JSON нет date_start";
        var today = DateOnly.FromDateTime(DateTime.Now);

        // прошлые снимки: uuid → каталог (для «с какой проверки период не менялся»)
        var pastMaps = past
            .Select(p => (Time: p.TimestampUtc, Map: p.Catalogs.Where(x => x.Uuid.Length > 0)
                .GroupBy(x => x.Uuid).ToDictionary(g => g.Key, g => g.First())))
            .ToList();

        DateTime? SameSince(FrontierCatalog c)
        {
            DateTime? since = null;
            for (int i = pastMaps.Count - 1; i >= 0; i--)
            {
                if (pastMaps[i].Map.TryGetValue(c.Uuid, out var r) && r.Start == c.DateStart && r.End == c.DateEnd) since = pastMaps[i].Time;
                else break;
            }
            return since;
        }

        if (catalogs.Count == 0)
        {
            res.State = FrontierState.NotUpdated;
            res.Message = "Фронтир не обновлён: у ретейлера нет активных каталогов (с учётом фильтра). " + expectedText;
            issues.Add(IssueCode.FrontierNotUpdated, net, "Фронтир", res.Message);
            return res;
        }

        var rows = new List<FrontierRow>();
        var stale = new List<FrontierCatalog>();
        int fresh = 0, endMismatch = 0;
        string? endExample = null;
        DateOnly? minStart = null, maxEnd = null;

        foreach (var c in catalogs)
        {
            string name = NameOf(c);
            if (c.DateStart is { } cs && (minStart is null || cs < minStart)) minStart = cs;
            if (c.DateEnd is { } ce0 && (maxEnd is null || ce0 > maxEnd)) maxEnd = ce0;

            string status = "OK", details = "";
            bool isFresh = true;
            if (exp is { } e)
            {
                if (c.DateStart is null)
                {
                    isFresh = false;
                    status = "Нет даты во Фронтире";
                    details = "У каталога во Фронтире нет даты начала";
                }
                else if (c.DateStart < e)
                {
                    isFresh = false;
                    status = "Период старый";
                    details = $"во Фронтире {Range(c)}, в JSON date_start {e:yyyy-MM-dd}";
                    if (c.Uuid.Length > 0 && SameSince(c) is { } since)
                        details += $"; тот же период с проверки от {since.ToLocalTime():dd.MM.yyyy HH:mm}";
                }
                else if (c.DateStart == e && c.DateEnd is { } ce && ce != expEnd)
                {
                    status = "Другой период";
                    details = $"период во Фронтире {Range(c)}, ожидалось {e:yyyy-MM-dd} – {expEnd:yyyy-MM-dd}";
                    endMismatch++;
                    endExample ??= $"{name} ({Range(c)})";
                }
            }

            if (status != "OK" && c.Uuid.Length > 0) details += (details.Length > 0 ? "; " : "") + "id каталога " + c.Uuid;
            if (isFresh) fresh++; else stale.Add(c);

            rows.Add(new FrontierRow
            {
                Name = name,
                FrontierOffers = c.ActiveOffers,
                JsonStart = exp,
                FrontierStart = c.DateStart,
                FrontierEnd = c.DateEnd,
                Status = status,
                Details = details,
            });
        }

        res.Fresh = fresh;
        int total = catalogs.Count;
        int staleCount = stale.Count;
        string period = minStart is { } mn && maxEnd is { } mx ? $"{mn:yyyy-MM-dd} – {mx:yyyy-MM-dd}" : "период неизвестен";

        // ---- сравнение с прошлой проверкой
        string historyText = "";
        if (past.Count > 0)
        {
            var prev = past[^1];
            string prevTime = prev.TimestampUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            var curIds = catalogs.Select(c => c.Uuid).Where(u => u.Length > 0).ToHashSet();
            var prevIds = prev.Catalogs.Select(c => c.Uuid).Where(u => u.Length > 0).ToHashSet();
            // каталог «действует», если срок не истёк на сегодня (или срока нет)
            bool Valid(DateOnly? end) => end is null || end >= today;
            int prevValid = prev.Catalogs.Count(c => Valid(c.End));
            int curValid = catalogs.Count(c => Valid(c.DateEnd));

            var gone = prev.Catalogs.Where(c => c.Uuid.Length > 0 && !curIds.Contains(c.Uuid)).ToList();
            var goneEarly = gone.Where(c => Valid(c.End)).ToList(); // пропали, хотя срок ещё не истёк
            int added = curIds.Count(u => !prevIds.Contains(u));

            historyText = $" С прошлой проверки ({prevTime}): было {prev.Total:N0}, стало {total:N0}; " +
                          $"действующих было {prevValid:N0}, стало {curValid:N0}; " +
                          $"пропало до окончания срока {goneEarly.Count:N0}, появилось {added:N0}.";

            foreach (var g in goneEarly)
            {
                string gp = (g.Start?.ToString("yyyy-MM-dd") ?? "?") + " – " + (g.End?.ToString("yyyy-MM-dd") ?? "?");
                string d = $"был при проверке от {prevTime}: период {gp}, офферов {g.Offers:N0}; сейчас во Фронтире его нет; id каталога {g.Uuid}";
                rows.Add(new FrontierRow
                {
                    Name = g.Name,
                    FrontierOffers = null,
                    JsonStart = exp,
                    FrontierStart = g.Start,
                    FrontierEnd = g.End,
                    Status = "Пропал с прошлой проверки",
                    Details = d,
                });
                issues.Add(IssueCode.FrontierCatalogGone, net, g.Name, d);
            }

            if (curValid < prevValid)
            {
                var recent = past.Skip(Math.Max(0, past.Count - 5)).ToList();
                issues.Add(IssueCode.FrontierCatalogsFewer, net, "Фронтир",
                    $"Действующих каталогов стало меньше: при проверке от {prevTime} было {prevValid:N0} (с учётом истёкших к сегодня), сейчас {curValid:N0}. " +
                    $"Пропало до окончания срока: {goneEarly.Count:N0}, появилось новых: {added:N0}. " +
                    $"Всего каталогов в последних {recent.Count} проверках: {string.Join(", ", recent.Select(p => p.Total.ToString("N0")))}; сейчас {total:N0}");
            }
        }

        if (staleCount == 0)
        {
            res.State = FrontierState.Updated;
            res.Message = $"Фронтир обновлён: каталогов {total:N0}, у всех актуальный период (во Фронтире {period}). {expectedText}.{historyText}";
        }
        else
        {
            string groups = string.Join("; ", stale
                .GroupBy(Range)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Key}: {g.Count():N0}"));
            if (fresh == 0)
            {
                res.State = FrontierState.NotUpdated;
                res.Message = $"Фронтир не обновлён: у всех каталогов ({total:N0}) период старый (во Фронтире {groups}). {expectedText}.{historyText}";
            }
            else
            {
                res.State = FrontierState.Partial;
                res.Message = $"Фронтир обновлён частично: актуальный период у {fresh:N0} из {total:N0} каталогов, " +
                              $"не обновлено {staleCount:N0} (период во Фронтире у них: {groups}). {expectedText}.{historyText}";
            }
            issues.Add(IssueCode.FrontierNotUpdated, net, "Фронтир", res.Message);

            // при частичном обновлении перечисляем каталоги, которые не обновились; если не обновилось ничего — хватит одного сообщения
            if (res.State == FrontierState.Partial)
                foreach (var r in rows.Where(x => x.Status is "Период старый" or "Нет даты во Фронтире"))
                    issues.Add(IssueCode.FrontierPeriodMismatch, net, r.Name, r.Details);
        }

        if (endMismatch > 0)
            issues.Add(IssueCode.FrontierPeriodMismatch, net, "Период каталогов",
                $"У {endMismatch:N0} каталогов с актуальным началом период заканчивается не {expEnd:yyyy-MM-dd}; например: {endExample}");

        res.Rows.AddRange(rows.OrderBy(r => r.Rank).ThenBy(r => r.Name, StringComparer.CurrentCulture));
        return res;
    }

    private static string Range(FrontierCatalog c) =>
        (c.DateStart?.ToString("yyyy-MM-dd") ?? "?") + " – " + (c.DateEnd?.ToString("yyyy-MM-dd") ?? "?");

    private static string Snippet(string s)
    {
        s = s.Trim();
        return s.Length <= 80 ? s : s[..80] + "…";
    }
}

/// <summary>Запуск сверки сети с Фронтиром: сессия → загрузка каталогов → сравнение. Ошибки превращаются в понятное сообщение.</summary>
public static class FrontierChecker
{
    public static FrontierComparison Run(NetworkConfig net, int retailerId, NetworkSummary summary, AppSettings settings, CancellationToken ct)
    {
        try
        {
            string? cookie = FrontierSession.Load();
            if (cookie is null)
                return Failed(net, retailerId, "Нет сохранённого входа во Фронтир. Откройте «Настройки → Фронтир» и войдите.");

            var catalogs = FrontierClient.LoadAsync(settings.FrontierBaseUrl, cookie, retailerId, net.FrontierOnlyFromFeed, ct)
                .GetAwaiter().GetResult();
            List<FrontierRunRecord> past;
            try { past = FrontierHistory.Load(net.Id); }
            catch { past = new List<FrontierRunRecord>(); }

            var cmp = FrontierComparer.Compare(retailerId, summary, catalogs, settings.FrontierTolerancePercent, settings.FrontierPeriodDays, past);

            // историю пополняем только успешной загрузкой; сбой записи на работу проверки не влияет
            try
            {
                FrontierHistory.Save(FrontierHistory.Snapshot(net.Id, retailerId, catalogs, cmp));
                FrontierHistory.Purge(net.Id, settings.KeepHistoryDays);
            }
            catch { /* история — удобство, а не обязательная часть проверки */ }

            return cmp;
        }
        catch (OperationCanceledException)
        {
            return Failed(net, retailerId, "Загрузка из Фронтира остановлена.");
        }
        catch (FrontierAuthException ex)
        {
            return Failed(net, retailerId, ex.Message + " Войдите заново: «Настройки → Фронтир».");
        }
        catch (Exception ex)
        {
            return Failed(net, retailerId, "Не удалось получить данные Фронтира: " + ex.Message);
        }
    }

    private static FrontierComparison Failed(NetworkConfig net, int retailerId, string message)
    {
        var res = new FrontierComparison { RetailerId = retailerId, State = FrontierState.Failed, Message = message };
        res.Issues.Add(IssueCode.FrontierLoadFailed, net.Name, "Фронтир", message);
        return res;
    }
}
