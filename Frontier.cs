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

public static class FrontierComparer
{
    public static FrontierComparison Compare(int retailerId, NetworkSummary summary, IReadOnlyList<FrontierCatalog> all,
        double tolerancePercent, int periodDays)
    {
        periodDays = Math.Max(1, periodDays);
        string net = summary.Name;
        var res = new FrontierComparison { RetailerId = retailerId, JsonGeos = summary.GeoCount };
        var issues = res.Issues;
        var catalogs = all.Where(c => c.IsActive).ToList();
        res.FrontierCatalogs = catalogs.Count;

        string expectedText = summary.DateStartMax is { } ds
            ? $"JSON date_start {ds:yyyy-MM-dd}, ожидаемый период {ds:yyyy-MM-dd} – {ds.AddDays(periodDays - 1):yyyy-MM-dd}"
            : "в JSON нет date_start";

        if (catalogs.Count == 0)
        {
            res.State = FrontierState.NotUpdated;
            res.Message = "Фронтир не обновлён: у ретейлера нет активных каталогов (с учётом фильтра). " + expectedText;
            issues.Add(IssueCode.FrontierNotUpdated, net, "Фронтир", res.Message);
            return res;
        }

        // индексы Фронтира
        var byGeo = new Dictionary<string, List<FrontierCatalog>>(StringComparer.Ordinal);
        var conditions = new Dictionary<FrontierCatalog, string>();
        foreach (var c in catalogs)
        {
            foreach (var n in c.GeoNames.Select(Norm).Where(x => x.Length > 0).Distinct())
            {
                if (!byGeo.TryGetValue(n, out var l)) byGeo[n] = l = new List<FrontierCatalog>();
                l.Add(c);
            }
            conditions[c] = Norm(c.Conditions);
        }

        // у магазинов адрес лежит в тексте условий каталога, а не в названии гео Фронтира
        bool useConditions = summary.GeoType is GeoType.Shops or GeoType.Mixed;

        var used = new HashSet<FrontierCatalog>();
        var rows = new List<FrontierRow>();
        var pendingStale = new List<(string Geo, string Details)>();
        int matched = 0, fresh = 0, stale = 0, missing = 0;
        DateOnly? minStart = null, maxEnd = null;

        foreach (var g in summary.Geos)
        {
            DateOnly? expStart = g.DateStart ?? summary.DateStartMax;
            string key = Norm(g.Name);
            List<FrontierCatalog> cands = byGeo.TryGetValue(key, out var found) ? found : new List<FrontierCatalog>();
            if (cands.Count == 0 && useConditions && key.Length >= 5)
                cands = catalogs.Where(x => ContainsToken(conditions[x], key)).ToList();

            if (cands.Count == 0)
            {
                missing++;
                rows.Add(new FrontierRow
                {
                    Name = g.Name, Files = g.Files, JsonOffers = g.Offers, JsonStart = expStart, Status = "Нет во Фронтире",
                    Details = "Каталог этого гео не найден среди каталогов Фронтира",
                });
                issues.Add(IssueCode.FrontierGeoMissing, net, g.Name, $"В JSON офферов: {g.Offers:N0}, во Фронтире каталога нет");
                continue;
            }

            matched++;
            foreach (var c in cands) used.Add(c);
            var best = cands
                .OrderByDescending(x => expStart is not null && x.DateStart == expStart)
                .ThenByDescending(x => x.DateStart ?? DateOnly.MinValue)
                .ThenByDescending(x => x.ActiveOffers)
                .First();

            if (best.DateStart is { } bs && (minStart is null || bs < minStart)) minStart = bs;
            if (best.DateEnd is { } be && (maxEnd is null || be > maxEnd)) maxEnd = be;

            var notes = new List<string>();
            var details = new List<string>();
            bool isFresh = true;

            if (expStart is { } exp)
            {
                if (best.DateStart is null)
                {
                    isFresh = false;
                    notes.Add("Нет даты во Фронтире");
                }
                else if (best.DateStart < exp)
                {
                    isFresh = false;
                    stale++;
                    notes.Add("Период старый");
                    string d = $"во Фронтире {Range(best)}, в JSON date_start {exp:yyyy-MM-dd}";
                    details.Add(d);
                    pendingStale.Add((g.Name, d));
                }
                else
                {
                    DateOnly expEnd = exp.AddDays(periodDays - 1);
                    if (best.DateStart > exp)
                    {
                        notes.Add("Другой период");
                        string d = $"во Фронтире {Range(best)}, в JSON date_start {exp:yyyy-MM-dd}: Фронтир новее JSON";
                        details.Add(d);
                        issues.Add(IssueCode.FrontierPeriodMismatch, net, g.Name, d);
                    }
                    else if (best.DateEnd is { } e2 && e2 != expEnd)
                    {
                        notes.Add("Другой период");
                        string d = $"период во Фронтире {Range(best)}, ожидалось {exp:yyyy-MM-dd} – {expEnd:yyyy-MM-dd}";
                        details.Add(d);
                        issues.Add(IssueCode.FrontierPeriodMismatch, net, g.Name, d);
                    }
                }
            }

            if (isFresh) fresh++;

            // число офферов сравниваем только у каталогов с актуальным периодом, иначе сравнение бессмысленно
            if (isFresh)
            {
                long j = g.Offers, f = best.ActiveOffers;
                double diffPct = j == 0 ? (f == 0 ? 0 : 100) : Math.Abs(f - j) * 100.0 / j;
                if (diffPct > tolerancePercent)
                {
                    notes.Add("Число офферов отличается");
                    string d = $"в JSON {j:N0}, во Фронтире {f:N0} (разница {f - j:+#,0;-#,0}, {diffPct:0.##}%); допуск {tolerancePercent:0.##}%";
                    details.Add(d);
                    issues.Add(IssueCode.FrontierOffersMismatch, net, g.Name, d);
                }
            }

            if (cands.Count > 1) details.Add($"каталогов у гео во Фронтире: {cands.Count}, взят самый свежий");

            rows.Add(new FrontierRow
            {
                Name = g.Name,
                Files = g.Files,
                JsonOffers = g.Offers,
                FrontierOffers = best.ActiveOffers,
                JsonStart = expStart,
                FrontierStart = best.DateStart,
                FrontierEnd = best.DateEnd,
                Status = notes.Count == 0 ? "OK" : string.Join("; ", notes),
                Details = string.Join("; ", details),
            });
        }

        // каталоги Фронтира, которым не нашлось гео в JSON
        foreach (var c in catalogs.Where(x => !used.Contains(x)).ToList())
        {
            string name = c.GeoNames.Count > 0 ? string.Join(", ", c.GeoNames) : Snippet(c.Conditions);
            rows.Add(new FrontierRow
            {
                Name = name, FrontierOffers = c.ActiveOffers, FrontierStart = c.DateStart, FrontierEnd = c.DateEnd,
                Status = "Нет в JSON", Details = "Каталог есть во Фронтире, но такого гео нет в JSON",
            });
            issues.Add(IssueCode.FrontierGeoExtra, net, name,
                $"Каталог есть во Фронтире (период {Range(c)}, офферов {c.ActiveOffers:N0}), но в JSON такого гео нет");
        }

        res.Fresh = fresh;
        int total = summary.GeoCount;
        string period = minStart is { } mn && maxEnd is { } mx ? $"{mn:yyyy-MM-dd} – {mx:yyyy-MM-dd}" : "период неизвестен";

        if (matched == 0)
        {
            res.State = FrontierState.NoMatch;
            res.Message = $"Не удалось сопоставить ни одно гео из JSON ({total:N0}) с каталогами Фронтира ({catalogs.Count:N0}). " +
                          "Проверьте ID ретейлера и фильтр «только каталоги из фида».";
        }
        else if (fresh == total)
        {
            res.State = FrontierState.Updated;
            res.Message = $"Фронтир обновлён: у всех гео ({total:N0}) актуальный период. {expectedText}.";
        }
        else if (fresh == 0)
        {
            res.State = FrontierState.NotUpdated;
            res.Message = $"Фронтир не обновлён: у всех сопоставленных гео ({matched:N0} из {total:N0}) период старый или не совпадает " +
                          $"(во Фронтире {period}). {expectedText}.";
            issues.Add(IssueCode.FrontierNotUpdated, net, "Фронтир", res.Message);
        }
        else
        {
            res.State = FrontierState.Partial;
            res.Message = $"Фронтир обновлён частично: актуальный период у {fresh:N0} из {total:N0} гео " +
                          $"(старый период: {stale:N0}, не найдено во Фронтире: {missing:N0}). {expectedText}.";
            issues.Add(IssueCode.FrontierNotUpdated, net, "Фронтир", res.Message);
        }

        // если обновилось только часть, подробно перечисляем, у кого период старый; если не обновилось ничего — хватит одного сообщения
        if (res.State == FrontierState.Partial)
            foreach (var (geo, d) in pendingStale)
                issues.Add(IssueCode.FrontierPeriodMismatch, net, geo, d);

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

    /// <summary>Нижний регистр, ё как е, лишние пробелы схлопнуты.</summary>
    private static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char ch in s.Trim().ToLowerInvariant())
        {
            char c = ch == 'ё' ? 'е' : ch;
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>Подстрока найдена как целое «слово»: «…, 4» не совпадает с «…, 41» и «…, 36» с «…, 36б».</summary>
    private static bool ContainsToken(string haystack, string needle)
    {
        int i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
            int end = i + needle.Length;
            bool rightOk = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (leftOk && rightOk) return true;
            i++;
        }
        return false;
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
            return FrontierComparer.Compare(retailerId, summary, catalogs, settings.FrontierTolerancePercent, settings.FrontierPeriodDays);
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
