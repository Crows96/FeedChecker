using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FeedCheckerGui;

// ===================== Потоковое чтение JSON =====================
/// <summary>
/// Потоковый читатель JSON поверх Utf8JsonReader. В памяти держит только небольшой буфер
/// (растёт лишь если один токен длиннее буфера), поэтому подходит для файлов на гигабайты.
/// Токены отдаются по одному; значение текущего токена доступно через ValueBytes / GetString().
/// </summary>
public sealed class JsonTokenStream
{
    private readonly Stream _stream;
    private readonly CancellationToken _ct;
    private byte[] _buffer = new byte[128 * 1024];
    private int _start, _end;
    private bool _eof;
    private long _consumedBefore;
    private JsonReaderState _state;
    private byte[] _value = new byte[512];
    private int _valueLen;

    public JsonTokenStream(Stream stream, CancellationToken ct = default)
    {
        _stream = stream;
        _ct = ct;
        Length = stream.CanSeek ? stream.Length : -1;

        // Пропускаем UTF-8 BOM, если он есть (Utf8JsonReader его не принимает).
        int n;
        while (_end < 3 && (n = _stream.Read(_buffer, _end, _buffer.Length - _end)) > 0) _end += n;
        if (_end >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF) _start = 3;
    }

    /// <summary>Размер потока, либо -1 если неизвестен.</summary>
    public long Length { get; }

    public JsonTokenType TokenType { get; private set; }

    /// <summary>Сколько байт уже прочитано (для прогресса).</summary>
    public long BytesConsumed => _consumedBefore + _start;

    /// <summary>Байты значения текущего токена (имя свойства, строка без кавычек и escape, число).</summary>
    public ReadOnlySpan<byte> ValueBytes => new(_value, 0, _valueLen);

    public string GetString() => Encoding.UTF8.GetString(_value, 0, _valueLen);

    public bool NameIs(ReadOnlySpan<byte> name) =>
        TokenType == JsonTokenType.PropertyName && ValueBytes.SequenceEqual(name);

    public bool Read()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(_buffer, _start, _end - _start), _eof, _state);
            if (reader.Read())
            {
                Capture(ref reader);
                _state = reader.CurrentState;
                _start += (int)reader.BytesConsumed;
                return true;
            }

            _state = reader.CurrentState;
            _start += (int)reader.BytesConsumed;
            if (_eof) return false;
            Fill();
        }
    }

    /// <summary>Если текущий токен — начало объекта/массива, пропускает его целиком.</summary>
    public void SkipValue()
    {
        if (TokenType != JsonTokenType.StartObject && TokenType != JsonTokenType.StartArray) return;
        int depth = 1;
        while (depth > 0)
        {
            if (!Read()) throw new JsonException("Неожиданный конец данных");
            if (TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
            else if (TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
        }
    }

    private void Fill()
    {
        _ct.ThrowIfCancellationRequested();
        int remaining = _end - _start;
        if (_start > 0)
        {
            if (remaining > 0) Buffer.BlockCopy(_buffer, _start, _buffer, 0, remaining);
            _consumedBefore += _start;
            _start = 0;
            _end = remaining;
        }

        if (_end == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
        int n = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (n == 0) _eof = true; else _end += n;
    }

    private void Capture(ref Utf8JsonReader reader)
    {
        TokenType = reader.TokenType;
        switch (TokenType)
        {
            case JsonTokenType.PropertyName:
            case JsonTokenType.String:
                if (reader.ValueIsEscaped) SetValue(Encoding.UTF8.GetBytes(reader.GetString()!));
                else SetValue(reader.ValueSpan);
                break;
            case JsonTokenType.Number:
                SetValue(reader.ValueSpan);
                break;
            default:
                _valueLen = 0;
                break;
        }
    }

    private void SetValue(ReadOnlySpan<byte> src)
    {
        if (src.Length > _value.Length) _value = new byte[Math.Max(src.Length, _value.Length * 2)];
        src.CopyTo(_value);
        _valueLen = src.Length;
    }
}

/// <summary>64-битный FNV-1a по байтам. Нужен, чтобы хранить миллионы id как числа, а не как строки.</summary>
public static class IdHash
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Full(ReadOnlySpan<byte> bytes)
    {
        ulong h = Offset;
        foreach (var b in bytes) { h ^= b; h *= Prime; }
        return h;
    }

    /// <summary>Хэш «товара»: часть id до первого '_' (id оффера имеет вид "{товар}_{магазин}").</summary>
    public static ulong Product(ReadOnlySpan<byte> offerId)
    {
        int i = offerId.IndexOf((byte)'_');
        return Full(i < 0 ? offerId : offerId[..i]);
    }
}

// ===================== Анализ одного файла =====================
public sealed class AnalyzerOptions
{
    /// <summary>Сколько записей каждого типа проблем хранить (счётчики при этом точные).</summary>
    public int MaxIssuesPerCode { get; set; } = 20000;
    /// <summary>Для скольких id из каталога/офферов собирать подробности во втором проходе.</summary>
    public int DetailLimit { get; set; } = 5000;
}

internal sealed class FeedFormatException : Exception
{
    public FeedFormatException(string message) : base(message) { }
}

/// <summary>
/// Анализ одного файла фида за один потоковый проход. В памяти остаются только хэши id и счётчики.
/// Если фид невалиден (битый JSON, нет catalogs/offers), результат помечается невалидным и остальные проверки не выполняются.
/// </summary>
public sealed class FeedAnalyzer
{
    private readonly AnalyzerOptions _opt;

    public FeedAnalyzer(AnalyzerOptions? options = null) => _opt = options ?? new AnalyzerOptions();

    private sealed class Ctx
    {
        public Ctx(FeedFileResult res) => Res = res;
        public FeedFileResult Res { get; }
        /// <summary>Хэш id → биты: 1 = указан в каталоге, 2 = есть объект оффера. Порядок catalogs/offers в файле не важен.</summary>
        public Dictionary<ulong, byte> State { get; } = new();
        public HashSet<string> CatalogIds { get; } = new(StringComparer.Ordinal);
        public byte[] IdBuf = new byte[256];
        public int IdLen;
        public bool SawCatalogs, SawOffers, AnyRegions, AnyShops;
        public long Ticks;
        public double LastReported;
        public IProgress<double>? Progress;

        public void SetId(ReadOnlySpan<byte> id)
        {
            if (id.Length > IdBuf.Length) IdBuf = new byte[Math.Max(id.Length, IdBuf.Length * 2)];
            id.CopyTo(IdBuf);
            IdLen = id.Length;
        }

        public ReadOnlySpan<byte> Id => new(IdBuf, 0, IdLen);
    }

    public FeedFileResult Analyze(string path, string displayName, IProgress<double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var res = new FeedFileResult(displayName, _opt.MaxIssuesPerCode);
        var ctx = new Ctx(res) { Progress = progress };
        try
        {
            res.FileSizeBytes = new FileInfo(path).Length;
            using var fs = OpenRead(path);
            var js = new JsonTokenStream(fs, ct);
            ParseRoot(js, ctx);
            Finish(ctx, path, ct);
        }
        catch (JsonException ex)
        {
            MarkInvalid(res, "Невалидный JSON: " + ex.Message);
        }
        catch (FeedFormatException ex)
        {
            MarkInvalid(res, ex.Message);
        }
        catch (IOException ex)
        {
            MarkInvalid(res, "Ошибка чтения файла: " + ex.Message);
        }

        res.Elapsed = sw.Elapsed;
        progress?.Report(1.0);
        return res;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);

    private static void MarkInvalid(FeedFileResult res, string reason)
    {
        res.IsValid = false;
        res.InvalidReason = reason;
        res.Geos.Clear();
        res.NoGeoProducts.Clear();
        res.Issues.Items.Clear();
        res.Issues.Totals.Clear();
        res.Issues.Add(IssueCode.InvalidFeed, res.DisplayName, "", reason);
    }

    // ---------------------------------------------------------------- разбор корня

    private void ParseRoot(JsonTokenStream js, Ctx c)
    {
        if (!js.Read() || js.TokenType != JsonTokenType.StartObject)
            throw new FeedFormatException("Корень JSON не является объектом");

        while (js.Read() && js.TokenType != JsonTokenType.EndObject)
        {
            if (js.NameIs("catalogs"u8))
            {
                js.Read();
                if (js.TokenType != JsonTokenType.StartArray) throw new FeedFormatException("\"catalogs\" не является массивом");
                c.SawCatalogs = true;
                while (js.Read() && js.TokenType != JsonTokenType.EndArray)
                {
                    if (js.TokenType != JsonTokenType.StartObject) { js.SkipValue(); continue; }
                    ReadCatalog(js, c);
                }
            }
            else if (js.NameIs("offers"u8))
            {
                js.Read();
                if (js.TokenType != JsonTokenType.StartArray) throw new FeedFormatException("\"offers\" не является массивом");
                c.SawOffers = true;
                while (js.Read() && js.TokenType != JsonTokenType.EndArray)
                {
                    if (js.TokenType != JsonTokenType.StartObject) { js.SkipValue(); continue; }
                    ReadOffer(js, c);
                }
            }
            else
            {
                js.Read();
                js.SkipValue();
            }
        }
    }

    private static void Tick(JsonTokenStream js, Ctx c)
    {
        if ((++c.Ticks & 0x7FF) != 0 || c.Progress is null || js.Length <= 0) return;
        double f = js.BytesConsumed / (double)js.Length;
        if (f - c.LastReported < 0.002) return;
        c.LastReported = f;
        c.Progress.Report(Math.Min(f, 0.999));
    }

    // ---------------------------------------------------------------- каталоги

    private void ReadCatalog(JsonTokenStream js, Ctx c)
    {
        var res = c.Res;
        string? id = null, dateStart = null;
        bool hasShops = false, hasRegions = false;
        var shops = new List<string>();
        var regions = new List<string>();
        var products = new HashSet<ulong>();
        long offersCount = 0;

        while (js.Read() && js.TokenType != JsonTokenType.EndObject)
        {
            if (js.NameIs("id"u8))
            {
                js.Read();
                if (js.TokenType == JsonTokenType.String || js.TokenType == JsonTokenType.Number) id = js.GetString();
                else js.SkipValue();
            }
            else if (js.NameIs("date_start"u8))
            {
                js.Read();
                if (js.TokenType == JsonTokenType.String) dateStart = js.GetString();
                else js.SkipValue();
            }
            else if (js.NameIs("offers"u8))
            {
                js.Read();
                if (js.TokenType != JsonTokenType.StartArray) { js.SkipValue(); continue; }
                while (js.Read() && js.TokenType != JsonTokenType.EndArray)
                {
                    if (js.TokenType != JsonTokenType.String) { js.SkipValue(); continue; }
                    var b = js.ValueBytes;
                    ref byte st = ref CollectionsMarshal.GetValueRefOrAddDefault(c.State, IdHash.Full(b), out _);
                    st |= 1;
                    products.Add(IdHash.Product(b));
                    offersCount++;
                    Tick(js, c);
                }
            }
            else if (js.NameIs("target_shops"u8))
            {
                hasShops = true;
                ReadStringArray(js, shops);
            }
            else if (js.NameIs("target_regions"u8))
            {
                hasRegions = true;
                ReadStringArray(js, regions);
            }
            else
            {
                js.Read();
                js.SkipValue();
            }
        }

        res.CatalogCount++;
        res.CatalogOfferRefs += offersCount;
        string where = string.IsNullOrWhiteSpace(id) ? $"(каталог №{res.CatalogCount} без id)" : id!;

        if (string.IsNullOrWhiteSpace(id))
            res.Issues.Add(IssueCode.CatalogIdMissing, res.DisplayName, where, "У каталога нет id");
        else if (!c.CatalogIds.Add(id!))
            res.Issues.Add(IssueCode.DuplicateCatalogId, res.DisplayName, where, "Каталог с таким id уже встречался в этом файле");

        if (offersCount == 0)
            res.Issues.Add(IssueCode.EmptyCatalog, res.DisplayName, where, "В каталоге нет офферов");

        DateOnly? catalogDate = null;
        // date_start: сравниваем только дату, как она записана в фиде
        if (dateStart is not null && DateTimeOffset.TryParse(dateStart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            var d = DateOnly.FromDateTime(dto.DateTime);
            catalogDate = d;
            if (res.DateStartMin is null || d < res.DateStartMin) res.DateStartMin = d;
            if (res.DateStartMax is null || d > res.DateStartMax) res.DateStartMax = d;
        }
        else
        {
            res.Issues.Add(IssueCode.DateStartInvalid, res.DisplayName, where,
                dateStart is null ? "Нет date_start" : "Не удалось прочитать date_start: " + dateStart);
        }

        // география
        List<string> geos;
        if (hasShops && hasRegions)
        {
            res.Issues.Add(IssueCode.CatalogMixedGeo, res.DisplayName, where, "Указаны и target_shops, и target_regions");
            geos = shops.Concat(regions).ToList();
            c.AnyShops = c.AnyRegions = true;
        }
        else if (hasShops) { geos = shops; c.AnyShops = true; }
        else if (hasRegions) { geos = regions; c.AnyRegions = true; }
        else geos = new List<string>();

        if (geos.Count == 0)
        {
            if (!hasShops && !hasRegions || (hasShops && hasRegions && geos.Count == 0))
                res.Issues.Add(IssueCode.CatalogNoGeo, res.DisplayName, where, "Нет ни target_shops, ни target_regions");
            else
                res.Issues.Add(IssueCode.CatalogNoGeo, res.DisplayName, where, "Список гео пуст");
            res.NoGeoOffers += offersCount;
            res.NoGeoProducts.UnionWith(products);
            return;
        }

        for (int i = 0; i < geos.Count; i++)
        {
            string geo = geos[i];
            if (res.Geos.TryGetValue(geo, out var gs))
            {
                res.Issues.Add(IssueCode.DuplicateGeoInFile, res.DisplayName, geo, "Гео уже встречалось в другом каталоге этого файла (каталог " + where + ")");
                gs.Offers += offersCount;
                if (catalogDate is { } cd && (gs.DateStart is null || cd > gs.DateStart)) gs.DateStart = cd;
                gs.Products.UnionWith(products);
            }
            else
            {
                res.Geos[geo] = new GeoStat
                {
                    Offers = offersCount,
                    DateStart = catalogDate,
                    // для единственного гео переиспользуем набор, для нескольких — копируем
                    Products = geos.Count == 1 ? products : new HashSet<ulong>(products),
                };
            }
        }
    }

    private static void ReadStringArray(JsonTokenStream js, List<string> into)
    {
        js.Read();
        if (js.TokenType != JsonTokenType.StartArray) { js.SkipValue(); return; }
        while (js.Read() && js.TokenType != JsonTokenType.EndArray)
        {
            if (js.TokenType == JsonTokenType.String)
            {
                var s = js.GetString();
                if (!string.IsNullOrWhiteSpace(s)) into.Add(s);
            }
            else js.SkipValue();
        }
    }

    // ---------------------------------------------------------------- офферы

    private static readonly string[] KeyNames = { "description", "id", "image", "url", "price_new" };

    private static int KeyIndex(ReadOnlySpan<byte> n)
    {
        if (n.SequenceEqual("description"u8)) return 0;
        if (n.SequenceEqual("id"u8)) return 1;
        if (n.SequenceEqual("image"u8)) return 2;
        if (n.SequenceEqual("url"u8)) return 3;
        if (n.SequenceEqual("price_new"u8)) return 4;
        return -1;
    }

    private static bool HasData(ReadOnlySpan<byte> s)
    {
        foreach (var b in s) if (b > 0x20) return true;
        return false;
    }

    private void ReadOffer(JsonTokenStream js, Ctx c)
    {
        var res = c.Res;
        // состояние ключа: 0 — нет ключа, 1 — пусто, 2 — есть данные
        Span<byte> st = stackalloc byte[5];
        st.Clear();
        c.IdLen = 0;

        while (js.Read() && js.TokenType != JsonTokenType.EndObject)
        {
            int k = KeyIndex(js.ValueBytes);
            js.Read();
            if (k < 0) { js.SkipValue(); continue; }

            byte s = 1;
            switch (js.TokenType)
            {
                case JsonTokenType.String:
                    if (HasData(js.ValueBytes)) s = 2;
                    break;
                case JsonTokenType.Number:
                    s = 2;
                    break;
                default:
                    js.SkipValue();
                    break;
            }

            st[k] = s;
            if (k == 1 && s == 2) c.SetId(js.ValueBytes);
        }

        res.OfferObjects++;
        Tick(js, c);

        bool duplicate = false;
        if (st[1] == 2)
        {
            ref byte v = ref CollectionsMarshal.GetValueRefOrAddDefault(c.State, IdHash.Full(c.Id), out _);
            if ((v & 2) != 0) duplicate = true;
            v |= 2;
        }

        bool allOk = st[0] == 2 && st[1] == 2 && st[2] == 2 && st[3] == 2 && st[4] == 2;
        if (allOk && !duplicate) return;

        string where = st[1] == 2 ? Encoding.UTF8.GetString(c.Id) : $"(оффер №{res.OfferObjects} без id)";
        if (duplicate)
            res.Issues.Add(IssueCode.DuplicateOfferId, res.DisplayName, where, "Оффер с таким id уже встречался в этом файле");

        if (!allOk)
        {
            var missing = new List<string>();
            var empty = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                if (st[i] == 0) missing.Add(KeyNames[i]);
                else if (st[i] == 1) empty.Add(KeyNames[i]);
            }

            var parts = new List<string>();
            if (missing.Count > 0) parts.Add("нет ключа: " + string.Join(", ", missing));
            if (empty.Count > 0) parts.Add("пусто: " + string.Join(", ", empty));
            res.Issues.Add(IssueCode.OfferFieldInvalid, res.DisplayName, where, string.Join("; ", parts));
        }
    }

    // ---------------------------------------------------------------- итоги и детализация

    private void Finish(Ctx c, string path, CancellationToken ct)
    {
        var res = c.Res;
        if (!c.SawCatalogs) throw new FeedFormatException("В фиде нет массива \"catalogs\"");
        if (!c.SawOffers) throw new FeedFormatException("В фиде нет массива \"offers\"");

        res.GeoType = c.AnyRegions && c.AnyShops ? GeoType.Mixed
            : c.AnyRegions ? GeoType.Regions
            : c.AnyShops ? GeoType.Shops
            : GeoType.None;

        if (res.DateStartMin is not null && res.DateStartMin != res.DateStartMax)
            res.Issues.Add(IssueCode.DateStartMismatch, res.DisplayName, "",
                $"date_start от {res.DateStartMin:yyyy-MM-dd} до {res.DateStartMax:yyyy-MM-dd}");

        var missingSet = new HashSet<ulong>();
        var unrefSet = new HashSet<ulong>();
        long missing = 0, unref = 0;
        foreach (var kv in c.State)
        {
            if (kv.Value == 1) { missing++; if (missingSet.Count < _opt.DetailLimit) missingSet.Add(kv.Key); }
            else if (kv.Value == 2) { unref++; if (unrefSet.Count < _opt.DetailLimit) unrefSet.Add(kv.Key); }
        }

        c.State.Clear();
        res.MissingOffers = missing;
        res.UnreferencedOffers = unref;

        if (missing > 0 || unref > 0)
            DetailPass(path, res, missingSet, unrefSet, missing, unref, ct);
    }

    /// <summary>
    /// Второй проход, только если найдены расхождения: по хэшам находит сами id (и каталог), чтобы показать их в отчёте.
    /// </summary>
    private void DetailPass(string path, FeedFileResult res, HashSet<ulong> missingSet, HashSet<ulong> unrefSet,
        long missingTotal, long unrefTotal, CancellationToken ct)
    {
        int foundMissing = 0, foundUnref = 0;
        using (var fs = OpenRead(path))
        {
            var js = new JsonTokenStream(fs, ct);
            js.Read(); // StartObject
            while (js.Read() && js.TokenType != JsonTokenType.EndObject)
            {
                if (js.NameIs("catalogs"u8) && missingSet.Count > 0)
                {
                    js.Read();
                    while (js.Read() && js.TokenType != JsonTokenType.EndArray)
                    {
                        if (js.TokenType != JsonTokenType.StartObject) { js.SkipValue(); continue; }
                        string? id = null;
                        var pending = new List<string>();
                        while (js.Read() && js.TokenType != JsonTokenType.EndObject)
                        {
                            if (js.NameIs("id"u8))
                            {
                                js.Read();
                                if (js.TokenType is JsonTokenType.String or JsonTokenType.Number) id = js.GetString(); else js.SkipValue();
                            }
                            else if (js.NameIs("offers"u8))
                            {
                                js.Read();
                                if (js.TokenType != JsonTokenType.StartArray) { js.SkipValue(); continue; }
                                while (js.Read() && js.TokenType != JsonTokenType.EndArray)
                                {
                                    if (js.TokenType != JsonTokenType.String) { js.SkipValue(); continue; }
                                    if (foundMissing + pending.Count < _opt.DetailLimit && missingSet.Contains(IdHash.Full(js.ValueBytes)))
                                        pending.Add(js.GetString());
                                }
                            }
                            else { js.Read(); js.SkipValue(); }
                        }

                        foreach (var offerId in pending)
                        {
                            res.Issues.Add(IssueCode.CatalogOfferMissing, res.DisplayName,
                                string.IsNullOrWhiteSpace(id) ? "(каталог без id)" : id!,
                                "Id из каталога нет среди офферов: " + offerId);
                            foundMissing++;
                        }
                    }
                }
                else if (js.NameIs("offers"u8) && unrefSet.Count > 0)
                {
                    js.Read();
                    while (js.Read() && js.TokenType != JsonTokenType.EndArray)
                    {
                        if (js.TokenType != JsonTokenType.StartObject) { js.SkipValue(); continue; }
                        string? offerId = null;
                        while (js.Read() && js.TokenType != JsonTokenType.EndObject)
                        {
                            if (js.NameIs("id"u8))
                            {
                                js.Read();
                                if (js.TokenType is JsonTokenType.String or JsonTokenType.Number)
                                {
                                    if (foundUnref < _opt.DetailLimit && unrefSet.Contains(IdHash.Full(js.ValueBytes))) offerId = js.GetString();
                                }
                                else js.SkipValue();
                            }
                            else { js.Read(); js.SkipValue(); }
                        }

                        if (offerId is not null)
                        {
                            res.Issues.Add(IssueCode.OfferNotInCatalog, res.DisplayName, offerId,
                                "Оффер есть в offers, но не указан ни в одном каталоге");
                            foundUnref++;
                        }
                    }
                }
                else
                {
                    js.Read();
                    js.SkipValue();
                }
            }
        }

        res.Issues.AddTotalOnly(IssueCode.CatalogOfferMissing, missingTotal - foundMissing);
        res.Issues.AddTotalOnly(IssueCode.OfferNotInCatalog, unrefTotal - foundUnref);
    }
}

// ===================== Скачивание по ссылке =====================
/// <summary>Скачивает фид по ссылке в файл кэша потоком (без загрузки в память).</summary>
public static class FeedDownloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FeedChecker/1.0");
        return client;
    }

    public static string CachePathFor(string url, string cacheDir)
    {
        string name = "feed";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var last = Path.GetFileNameWithoutExtension(uri.AbsolutePath.TrimEnd('/'));
            if (!string.IsNullOrWhiteSpace(last)) name = last;
        }

        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..8];
        return Path.Combine(cacheDir, $"{name}_{hash}.json");
    }

    public static async Task<string> DownloadAsync(string url, string cacheDir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDir);
        string target = CachePathFor(url, cacheDir);
        string temp = target + ".part";
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total is > 0) progress?.Report(Math.Min(1.0, done / (double)total.Value));
                }
            }

            File.Move(temp, target, overwrite: true);
            progress?.Report(1.0);
            return target;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* игнорируем */ }
            throw;
        }
    }
}

// ===================== Запуск по списку источников =====================

public sealed record FeedSource(string Value, string? Label = null)
{
    public bool IsUrl => Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public bool IsS3 => Value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Адрес S3-папки (оканчивается на «/» или указывает на корень бакета).</summary>
    public bool IsS3Folder => IsS3 && S3Path.TryParse(Value, out _, out var key) && (key.Length == 0 || key.EndsWith('/'));

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Label)) return Label!;
            if (IsS3)
            {
                if (IsS3Folder || !S3Path.TryParse(Value, out _, out var key)) return Value;
                int slash = key.LastIndexOf('/');
                return slash < 0 ? key : key[(slash + 1)..];
            }
            if (IsUrl && Uri.TryCreate(Value, UriKind.Absolute, out var u))
            {
                var last = Path.GetFileName(u.AbsolutePath.TrimEnd('/'));
                return string.IsNullOrEmpty(last) ? u.Host : last;
            }
            return Path.GetFileName(Value);
        }
    }
}

public sealed class RunOptions
{
    public string CacheDir { get; set; } = Path.Combine(Path.GetTempPath(), "FeedChecker", "cache");
    public bool DeleteDownloaded { get; set; } = true;
    public AnalyzerOptions Analyzer { get; set; } = new();
    /// <summary>Возвращает клиент S3 для бакета (или null, если для него нет ключей). Нужен, если есть источники s3://…</summary>
    public Func<string, S3Client?>? S3ClientFor { get; set; }
}

public sealed record RunProgress(int SourceIndex, int SourceCount, string SourceName, string Phase, double Overall);

public sealed class RunResult
{
    public List<FeedFileResult> Files { get; } = new();
    public NetworkSummary Summary { get; set; } = null!;
    public bool Cancelled { get; set; }
}

/// <summary>Последовательно проверяет список источников (файлы, ссылки, S3-папки) как одну сеть.</summary>
public sealed class FeedRunner
{
    public RunResult Run(IReadOnlyList<FeedSource> sources, string networkName, RunOptions opt,
        IProgress<RunProgress>? progress, Action<int, FeedFileResult>? onFileDone, CancellationToken ct)
    {
        var result = new RunResult();
        var analyzer = new FeedAnalyzer(opt.Analyzer);

        // S3-папки раскрываем в список файлов; ошибка листинга становится «невалидным источником»
        var work = new List<(FeedSource Src, string? Error)>();
        foreach (var s in sources)
        {
            if (!s.IsS3Folder) { work.Add((s, null)); continue; }
            try
            {
                if (!S3Path.TryParse(s.Value, out var folderBucket, out _))
                    throw new InvalidOperationException("Неверный адрес S3: " + s.Value);
                var folderClient = opt.S3ClientFor?.Invoke(folderBucket)
                    ?? throw new InvalidOperationException($"Для бакета «{folderBucket}» не заданы ключи доступа (Настройки → S3).");
                var files = folderClient.ListFeedSourcesAsync(s.Value, ct).GetAwaiter().GetResult();
                if (files.Count == 0) throw new InvalidOperationException("В S3-папке нет файлов .json: " + s.Value);
                foreach (var f in files) work.Add((f, null));
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
            catch (Exception ex) when (ex is S3Exception or InvalidOperationException or HttpRequestException)
            {
                work.Add((s, ex.Message));
            }
        }

        for (int i = 0; i < work.Count && !result.Cancelled; i++)
        {
            if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
            var (src, listError) = work[i];
            string name = src.DisplayName;
            string? downloaded = null;

            void Report(string phase, double fraction) =>
                progress?.Report(new RunProgress(i, work.Count, name, phase, (i + Math.Clamp(fraction, 0, 1)) / work.Count));

            if (listError is not null)
            {
                var bad = new FeedFileResult(name, opt.Analyzer.MaxIssuesPerCode) { IsValid = false, InvalidReason = listError };
                bad.Issues.Add(IssueCode.InvalidFeed, name, "", listError);
                result.Files.Add(bad);
                onFileDone?.Invoke(i, bad);
                continue;
            }

            try
            {
                string path = src.Value;
                double analysisStart = 0, analysisSpan = 1;
                if (src.IsUrl)
                {
                    Report("Скачивание", 0);
                    var dl = new Progress<double>(p => Report("Скачивание", p * 0.5));
                    downloaded = FeedDownloader.DownloadAsync(src.Value, opt.CacheDir, dl, ct).GetAwaiter().GetResult();
                    path = downloaded;
                    analysisStart = 0.5; analysisSpan = 0.5;
                }
                else if (src.IsS3)
                {
                    if (!S3Path.TryParse(src.Value, out var bucket, out var key))
                        throw new InvalidOperationException("Неверный адрес S3: " + src.Value);
                    var s3Client = opt.S3ClientFor?.Invoke(bucket)
                        ?? throw new InvalidOperationException($"Для бакета «{bucket}» не заданы ключи доступа (Настройки → S3).");
                    Report("Скачивание", 0);
                    var dl = new Progress<double>(p => Report("Скачивание", p * 0.5));
                    downloaded = s3Client.DownloadAsync(bucket, key, opt.CacheDir, dl, ct).GetAwaiter().GetResult();
                    path = downloaded;
                    analysisStart = 0.5; analysisSpan = 0.5;
                }

                Report("Проверка", analysisStart);
                var ap = new Progress<double>(p => Report("Проверка", analysisStart + p * analysisSpan));
                var fileResult = analyzer.Analyze(path, name, ap, ct);
                result.Files.Add(fileResult);
                onFileDone?.Invoke(i, fileResult);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or S3Exception)
            {
                var failed = new FeedFileResult(name, opt.Analyzer.MaxIssuesPerCode) { IsValid = false, InvalidReason = ex.Message };
                failed.Issues.Add(IssueCode.InvalidFeed, name, "", (src.IsUrl || src.IsS3 ? "Не удалось скачать: " : "Не удалось открыть: ") + ex.Message);
                result.Files.Add(failed);
                onFileDone?.Invoke(i, failed);
            }
            finally
            {
                if (downloaded is not null && opt.DeleteDownloaded)
                {
                    try { File.Delete(downloaded); } catch { /* файл может быть занят — не критично */ }
                }
            }
        }

        result.Summary = NetworkSummary.Build(networkName, result.Files);
        progress?.Report(new RunProgress(work.Count, work.Count, "", result.Cancelled ? "Остановлено" : "Готово", result.Cancelled ? 0 : 1));
        return result;
    }
}
