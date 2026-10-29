using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace FeedCheckerGui;

// ===================== Поиск товара по названию в нужном гео =====================

public sealed class ProductQuery
{
    /// <summary>Название (или часть названия) товара.</summary>
    public string Text { get; init; } = "";
    /// <summary>false — все слова запроса должны встретиться в названии (в любом порядке); true — запрос как фраза целиком.</summary>
    public bool WholePhrase { get; init; }
    /// <summary>Гео: часть названия региона или адреса магазина (target_regions / target_shops, либо гео из id оффера). Пусто — любое.</summary>
    public string Geo { get; init; } = "";
    public bool GeoExact { get; init; }
    public int MaxResults { get; init; } = 5000;
}

public sealed class ProductHit
{
    public string Network { get; set; } = "";
    /// <summary>Имя файла (источника) как в списке файлов проверки.</summary>
    public string FileName { get; set; } = "";
    /// <summary>Полный путь, ссылка или адрес S3 — чтобы открыть именно этот файл.</summary>
    public string Source { get; set; } = "";
    public string Geo { get; set; } = "";
    /// <summary>true — гео взято из id оффера (вид «{товар}_{гео}»), а не из каталога.</summary>
    public bool GeoFromId { get; set; }
    public string Name { get; set; } = "";
    public decimal? Price { get; set; }
    public decimal? OldPrice { get; set; }
    public bool PriceFrom { get; set; }
    public string OfferId { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Barcode { get; set; } = "";
}

public sealed class ProductSearchResult
{
    public List<ProductHit> Hits { get; } = new();
    public bool Truncated { get; set; }
    public bool Cancelled { get; set; }
    public int FilesScanned { get; set; }
    public List<string> Errors { get; } = new();
    public TimeSpan Elapsed { get; set; }
}

public sealed record ProductSearchTarget(string Network, IReadOnlyList<string> Sources);

public static class ProductSearcher
{
    /// <summary>Нижний регистр, ё как е: так сравниваем названия и гео.</summary>
    public static string Norm(string s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLowerInvariant().Replace('ё', 'е');

    // ---------------------------------------------------------------- вспомогательные типы

    private sealed class ByteBuf
    {
        private byte[] _b = new byte[128];
        private int _n;
        public int Length => _n;
        public void Clear() => _n = 0;

        public void Set(ReadOnlySpan<byte> s)
        {
            if (s.Length > _b.Length) _b = new byte[Math.Max(s.Length, _b.Length * 2)];
            s.CopyTo(_b);
            _n = s.Length;
        }

        public ReadOnlySpan<byte> Span => new(_b, 0, _n);
        public override string ToString() => Encoding.UTF8.GetString(_b, 0, _n);
    }

    private sealed class OfferReader
    {
        public readonly ByteBuf Id = new(), Name = new(), AltName = new(), Url = new(), Barcode = new();
        public decimal? Price, OldPrice;
        public bool PriceFrom;

        private static void ReadText(JsonTokenStream js, ByteBuf buf)
        {
            if (js.TokenType is JsonTokenType.String or JsonTokenType.Number) buf.Set(js.ValueBytes);
            else js.SkipValue();
        }

        private static decimal? ReadPrice(JsonTokenStream js)
        {
            if (js.TokenType is JsonTokenType.Number or JsonTokenType.String &&
                Utf8Parser.TryParse(js.ValueBytes, out decimal d, out _)) return d;
            js.SkipValue();
            return null;
        }

        /// <summary>Читает объект оффера (текущий токен — StartObject) до его конца.</summary>
        public void Read(JsonTokenStream js)
        {
            Id.Clear(); Name.Clear(); AltName.Clear(); Url.Clear(); Barcode.Clear();
            Price = OldPrice = null;
            PriceFrom = false;
            decimal? plainPrice = null;

            while (js.Read() && js.TokenType != JsonTokenType.EndObject)
            {
                if (js.TokenType != JsonTokenType.PropertyName) { js.SkipValue(); continue; }

                int kind =
                    js.NameIs("id"u8) ? 1 :
                    js.NameIs("description"u8) ? 2 :
                    js.NameIs("name"u8) || js.NameIs("title"u8) ? 3 :
                    js.NameIs("price_new"u8) ? 4 :
                    js.NameIs("price_old"u8) ? 5 :
                    js.NameIs("price"u8) ? 6 :
                    js.NameIs("price_is_from"u8) ? 7 :
                    js.NameIs("url"u8) ? 8 :
                    js.NameIs("barcode"u8) ? 9 : 0;

                js.Read();
                switch (kind)
                {
                    case 1: ReadText(js, Id); break;
                    case 2: ReadText(js, Name); break;
                    case 3: ReadText(js, AltName); break;
                    case 4: Price = ReadPrice(js); break;
                    case 5: OldPrice = ReadPrice(js); break;
                    case 6: plainPrice = ReadPrice(js); break;
                    case 7:
                        if (js.TokenType == JsonTokenType.True) PriceFrom = true;
                        else js.SkipValue();
                        break;
                    case 8: ReadText(js, Url); break;
                    case 9: ReadText(js, Barcode); break;
                    default: js.SkipValue(); break;
                }
            }

            Price ??= plainPrice;
        }

        public string NameText() => (Name.Length > 0 ? Name : AltName).ToString();
    }

    private sealed class CatalogData
    {
        public string Id = "";
        public readonly List<string> Geos = new();
        public readonly List<ulong> Ids = new();
    }

    private static void ReadCatalog(JsonTokenStream js, CatalogData c)
    {
        c.Id = "";
        c.Geos.Clear();
        c.Ids.Clear();
        while (js.Read() && js.TokenType != JsonTokenType.EndObject)
        {
            if (js.TokenType != JsonTokenType.PropertyName) { js.SkipValue(); continue; }
            if (js.NameIs("id"u8))
            {
                js.Read();
                if (js.TokenType is JsonTokenType.String or JsonTokenType.Number) c.Id = js.GetString();
                else js.SkipValue();
            }
            else if (js.NameIs("offers"u8))
            {
                js.Read();
                if (js.TokenType != JsonTokenType.StartArray) { js.SkipValue(); continue; }
                while (js.Read() && js.TokenType != JsonTokenType.EndArray)
                {
                    if (js.TokenType is JsonTokenType.String or JsonTokenType.Number) c.Ids.Add(IdHash.Full(js.ValueBytes));
                    else js.SkipValue();
                }
            }
            else if (js.NameIs("target_regions"u8) || js.NameIs("target_shops"u8))
            {
                js.Read();
                if (js.TokenType == JsonTokenType.StartArray)
                {
                    while (js.Read() && js.TokenType != JsonTokenType.EndArray)
                    {
                        if (js.TokenType == JsonTokenType.String) c.Geos.Add(js.GetString());
                        else js.SkipValue();
                    }
                }
                else if (js.TokenType == JsonTokenType.String) c.Geos.Add(js.GetString());
                else js.SkipValue();
            }
            else
            {
                js.Read();
                js.SkipValue();
            }
        }
    }

    /// <summary>Проходит по объектам массива catalogs или offers; onObject получает читатель на StartObject и должен дочитать объект. false — остановиться.</summary>
    private static void ScanRoot(string path, bool catalogs, Func<JsonTokenStream, bool> onObject, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        var js = new JsonTokenStream(fs, ct);
        if (!js.Read() || js.TokenType != JsonTokenType.StartObject) throw new JsonException("Корень JSON не является объектом");

        while (js.Read() && js.TokenType != JsonTokenType.EndObject)
        {
            bool wanted = catalogs ? js.NameIs("catalogs"u8) : js.NameIs("offers"u8);
            if (!wanted) { js.Read(); js.SkipValue(); continue; }

            js.Read();
            if (js.TokenType != JsonTokenType.StartArray) { js.SkipValue(); continue; }
            while (js.Read() && js.TokenType != JsonTokenType.EndArray)
            {
                if (js.TokenType != JsonTokenType.StartObject) { js.SkipValue(); continue; }
                if (!onObject(js)) return;
            }
            return; // нужный массив пройден, остаток файла не нужен
        }
    }

    // ---------------------------------------------------------------- поиск в одном файле

    private sealed class Matcher
    {
        private readonly string[] _words;
        private readonly string _phrase;
        private readonly bool _whole;
        private readonly string _geo;
        private readonly bool _geoExact;

        public Matcher(ProductQuery q)
        {
            _phrase = Norm(q.Text);
            _words = _phrase.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            _whole = q.WholePhrase;
            _geo = Norm(q.Geo);
            _geoExact = q.GeoExact;
        }

        public bool HasGeo => _geo.Length > 0;

        public bool NameOk(string normName)
        {
            if (_whole) return normName.Contains(_phrase, StringComparison.Ordinal);
            foreach (var w in _words)
                if (!normName.Contains(w, StringComparison.Ordinal)) return false;
            return true;
        }

        public bool GeoOk(string geo)
        {
            string n = Norm(geo);
            return _geoExact ? n == _geo : n.Contains(_geo, StringComparison.Ordinal);
        }
    }

    private static string GeoFromOfferId(string id)
    {
        int i = id.IndexOf('_');
        return i < 0 || i == id.Length - 1 ? "" : id[(i + 1)..];
    }

    private static string JoinGeos(List<string> geos) =>
        geos.Count <= 8 ? string.Join("; ", geos) : string.Join("; ", geos.Take(8)) + $"; … (ещё {geos.Count - 8})";

    private static ProductHit NewHit(OfferReader o, string name, string network, string fileName, string source) => new()
    {
        Network = network,
        FileName = fileName,
        Source = source,
        Name = name,
        Price = o.Price,
        OldPrice = o.OldPrice,
        PriceFrom = o.PriceFrom,
        OfferId = o.Id.ToString(),
        Url = o.Url.ToString(),
        Barcode = o.Barcode.ToString(),
    };

    /// <summary>
    /// Ищет в одном локальном файле. Если гео указано: сначала каталоги (какие офферы относятся к подходящим гео), затем офферы.
    /// Если гео не указано: сначала офферы с подходящим названием, затем каталоги — чтобы подписать гео.
    /// </summary>
    private static bool ScanFile(string path, string network, string fileName, string source, Matcher m, int room,
        List<ProductHit> hits, CancellationToken ct)
    {
        bool truncated = false;
        var offer = new OfferReader();
        var cd = new CatalogData();

        if (m.HasGeo)
        {
            var allowed = new Dictionary<ulong, int>();
            var catalogs = new List<(string Id, List<string> Geos)>();
            ScanRoot(path, true, js =>
            {
                ReadCatalog(js, cd);
                var matched = cd.Geos.Where(m.GeoOk).ToList();
                if (matched.Count == 0) return true;
                int idx = catalogs.Count;
                catalogs.Add((cd.Id, matched));
                foreach (var h in cd.Ids) allowed.TryAdd(h, idx);
                return true;
            }, ct);

            ScanRoot(path, false, js =>
            {
                offer.Read(js);
                string name = offer.NameText();
                if (name.Length == 0 || !m.NameOk(Norm(name))) return true;

                ProductHit? hit = null;
                if (allowed.TryGetValue(IdHash.Full(offer.Id.Span), out int idx))
                {
                    hit = NewHit(offer, name, network, fileName, source);
                    hit.Geo = JoinGeos(catalogs[idx].Geos);
                    hit.CatalogId = catalogs[idx].Id;
                }
                else
                {
                    // каталога с этим гео нет (или оффер не указан в каталоге): пробуем гео из id вида «{товар}_{гео}»
                    string idGeo = GeoFromOfferId(offer.Id.ToString());
                    if (idGeo.Length > 0 && m.GeoOk(idGeo))
                    {
                        hit = NewHit(offer, name, network, fileName, source);
                        hit.Geo = idGeo;
                        hit.GeoFromId = true;
                    }
                }

                if (hit is null) return true;
                hits.Add(hit);
                if (hits.Count >= room) { truncated = true; return false; }
                return true;
            }, ct);

            return truncated;
        }

        var byHash = new Dictionary<ulong, ProductHit>();
        ScanRoot(path, false, js =>
        {
            offer.Read(js);
            string name = offer.NameText();
            if (name.Length == 0 || !m.NameOk(Norm(name))) return true;
            var hit = NewHit(offer, name, network, fileName, source);
            hits.Add(hit);
            byHash.TryAdd(IdHash.Full(offer.Id.Span), hit);
            if (hits.Count >= room) { truncated = true; return false; }
            return true;
        }, ct);

        if (byHash.Count == 0) return truncated;

        var geoByHit = new Dictionary<ProductHit, List<string>>();
        var catByHit = new Dictionary<ProductHit, List<string>>();
        ScanRoot(path, true, js =>
        {
            ReadCatalog(js, cd);
            foreach (var h in cd.Ids)
            {
                if (!byHash.TryGetValue(h, out var hit)) continue;
                if (!catByHit.TryGetValue(hit, out var cl)) catByHit[hit] = cl = new List<string>();
                if (!cl.Contains(cd.Id)) cl.Add(cd.Id);
                if (!geoByHit.TryGetValue(hit, out var gl)) geoByHit[hit] = gl = new List<string>();
                foreach (var g in cd.Geos) if (!gl.Contains(g)) gl.Add(g);
            }
            return true;
        }, ct);

        foreach (var hit in hits)
        {
            if (hit.Geo.Length > 0) continue;
            if (geoByHit.TryGetValue(hit, out var gl) && gl.Count > 0)
            {
                hit.Geo = JoinGeos(gl);
                hit.CatalogId = string.Join("; ", catByHit[hit]);
            }
            else
            {
                hit.Geo = GeoFromOfferId(hit.OfferId);
                hit.GeoFromId = hit.Geo.Length > 0;
            }
        }
        return truncated;
    }

    // ---------------------------------------------------------------- поиск по сетям

    public static ProductSearchResult Search(IReadOnlyList<ProductSearchTarget> targets, ProductQuery query, AppSettings settings,
        IProgress<string>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new ProductSearchResult();
        var matcher = new Matcher(query);
        var opt = new RunOptions
        {
            CacheDir = settings.CacheDir,
            DeleteDownloaded = settings.DeleteDownloaded,
            S3ClientFor = bucket => settings.FindS3(bucket) is { } conn ? S3Client.FromSettings(conn) : null,
        };

        try
        {
            foreach (var target in targets)
            {
                var files = ExpandSources(target, opt, result, ct);
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (result.Hits.Count >= query.MaxResults) { result.Truncated = true; return Finish(result, sw); }

                    var src = files[i];
                    string name = src.DisplayName;
                    string label = $"{target.Network}: файл {i + 1} из {files.Count} — {name}";
                    string? downloaded = null;
                    try
                    {
                        string path = src.Value;
                        if (src.IsUrl)
                        {
                            progress?.Report(label + " (скачивание)");
                            downloaded = FeedDownloader.DownloadAsync(src.Value, opt.CacheDir, null, ct).GetAwaiter().GetResult();
                            path = downloaded;
                        }
                        else if (src.IsS3)
                        {
                            if (!S3Path.TryParse(src.Value, out var bucket, out var key))
                                throw new InvalidOperationException("Неверный адрес S3: " + src.Value);
                            var client = opt.S3ClientFor?.Invoke(bucket)
                                ?? throw new InvalidOperationException($"Для бакета «{bucket}» не заданы ключи доступа (Настройки → S3).");
                            progress?.Report(label + " (скачивание)");
                            downloaded = client.DownloadAsync(bucket, key, opt.CacheDir, null, ct).GetAwaiter().GetResult();
                            path = downloaded;
                        }
                        else if (!File.Exists(path))
                        {
                            throw new FileNotFoundException("Файл не найден: " + path);
                        }

                        progress?.Report(label + " (поиск)");
                        int room = query.MaxResults - result.Hits.Count;
                        var fileHits = new List<ProductHit>();
                        if (ScanFile(path, target.Network, name, src.Value, matcher, room, fileHits, ct)) result.Truncated = true;
                        result.Hits.AddRange(fileHits);
                        result.FilesScanned++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                                   or InvalidOperationException or HttpRequestException or S3Exception)
                    {
                        result.Errors.Add($"{target.Network} / {name}: {ex.Message}");
                    }
                    finally
                    {
                        if (downloaded is not null && opt.DeleteDownloaded)
                        {
                            try { File.Delete(downloaded); } catch { /* не критично */ }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }

        return Finish(result, sw);
    }

    private static ProductSearchResult Finish(ProductSearchResult r, System.Diagnostics.Stopwatch sw)
    {
        r.Elapsed = sw.Elapsed;
        return r;
    }

    /// <summary>S3-папки раскрываем в файлы, как при обычной проверке.</summary>
    private static List<FeedSource> ExpandSources(ProductSearchTarget target, RunOptions opt, ProductSearchResult result, CancellationToken ct)
    {
        var list = new List<FeedSource>();
        foreach (var value in target.Sources)
        {
            var s = new FeedSource(value);
            if (!s.IsS3Folder) { list.Add(s); continue; }
            try
            {
                if (!S3Path.TryParse(s.Value, out var bucket, out _))
                    throw new InvalidOperationException("Неверный адрес S3: " + s.Value);
                var client = opt.S3ClientFor?.Invoke(bucket)
                    ?? throw new InvalidOperationException($"Для бакета «{bucket}» не заданы ключи доступа (Настройки → S3).");
                var files = client.ListFeedSourcesAsync(s.Value, ct).GetAwaiter().GetResult();
                if (files.Count == 0) throw new InvalidOperationException("В S3-папке нет файлов .json: " + s.Value);
                list.AddRange(files);
            }
            catch (Exception ex) when (ex is S3Exception or InvalidOperationException or HttpRequestException)
            {
                result.Errors.Add($"{target.Network} / {s.Value}: {ex.Message}");
            }
        }
        return list;
    }
}

// ---------------------------------------------------------------- выгрузка результатов поиска

public static class ProductSearchExporter
{
    public static readonly string[] Headers =
    {
        "Сеть", "Файл JSON", "Гео", "Откуда гео", "Товар", "Цена", "Старая цена", "Цена от",
        "ID оффера", "ID каталога", "Ссылка", "Штрихкод", "Источник (полный путь)",
    };

    private static readonly double[] Widths = { 20, 28, 46, 14, 56, 10, 12, 9, 40, 34, 46, 16, 70 };

    private static object?[] Row(ProductHit h) => new object?[]
    {
        h.Network, h.FileName, h.Geo, h.GeoFromId ? "id оффера" : "каталог", h.Name,
        h.Price is { } p ? (double)p : null, h.OldPrice is { } o ? (double)o : null, h.PriceFrom ? "да" : "",
        h.OfferId, h.CatalogId, h.Url, h.Barcode, h.Source,
    };

    public static void Write(string path, ProductQuery query, string whereText, ProductSearchResult result)
    {
        var data = new Sheet("Результаты", Headers, Widths);
        foreach (var h in result.Hits) data.Rows.Add(Row(h));

        var info = new Sheet("Запрос", new[] { "Параметр", "Значение" }, new double[] { 28, 90 });
        info.Rows.Add(new object?[] { "Дата поиска", DateTime.Now.ToString("dd.MM.yyyy HH:mm") });
        info.Rows.Add(new object?[] { "Где искали", whereText });
        info.Rows.Add(new object?[] { "Название товара", query.Text });
        info.Rows.Add(new object?[] { "Как искали", query.WholePhrase ? "фраза целиком" : "все слова в любом порядке" });
        info.Rows.Add(new object?[] { "Гео", query.Geo.Length == 0 ? "любое" : query.Geo + (query.GeoExact ? " (точное совпадение)" : " (часть названия)") });
        info.Rows.Add(new object?[] { "Найдено", result.Hits.Count });
        info.Rows.Add(new object?[] { "Просмотрено файлов", result.FilesScanned });
        if (result.Truncated) info.Rows.Add(new object?[] { "Внимание", $"достигнут предел результатов ({query.MaxResults}); есть и другие совпадения" });
        if (result.Cancelled) info.Rows.Add(new object?[] { "Внимание", "поиск остановлен, список неполный" });
        foreach (var e in result.Errors) info.Rows.Add(new object?[] { "Ошибка", e });

        var sheets = new[] { data, info };
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".xlsx": TableFile.WriteXlsx(path, sheets); break;
            case ".csv": TableFile.WriteCsv(path, sheets); break;
            case ".json":
                File.WriteAllText(path, JsonSerializer.Serialize(new { query, where = whereText, result.Truncated, result.FilesScanned, result.Errors, hits = result.Hits },
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                    new UTF8Encoding(false));
                break;
            default:
                using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
                {
                    w.WriteLine($"Поиск товара ({DateTime.Now:dd.MM.yyyy HH:mm})");
                    w.WriteLine($"Где: {whereText}; название: {query.Text}; гео: {(query.Geo.Length == 0 ? "любое" : query.Geo)}");
                    w.WriteLine($"Найдено: {result.Hits.Count}, файлов просмотрено: {result.FilesScanned}");
                    w.WriteLine();
                    foreach (var h in result.Hits)
                        w.WriteLine($"{h.Network} | {h.FileName} | {h.Geo} | {h.Name} | {(h.Price is { } p ? p.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "—")}{(h.PriceFrom ? " (от)" : "")} | {h.OfferId}");
                }
                break;
        }
    }
}
