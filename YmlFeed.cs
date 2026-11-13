using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;

namespace FeedCheckerGui;

/// <summary>
/// Проверка YML/XML-фидов (yml_catalog / Яндекс.Маркет). Только базовые проверки без географии:
/// валидность XML, офферы, ошибки в обязательных полях, дубли id, неизвестные категории, дата фида.
/// Результат — тот же FeedFileResult, что и у JSON, поэтому сводка, сравнение, история, выгрузки и Фронтир работают без изменений.
/// Читается потоково (XmlReader), в памяти — только id офферов и хэши названий.
/// </summary>
public sealed class YmlFeedAnalyzer
{
    private readonly AnalyzerOptions _opt;

    static YmlFeedAnalyzer()
    {
        // для фидов в windows-1251
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public YmlFeedAnalyzer(AnalyzerOptions opt) => _opt = opt;

    /// <summary>Файл начинается с «&lt;» (после BOM и пробелов) — это XML, а не JSON.</summary>
    public static bool LooksLikeXml(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[4096];
            int n = fs.Read(buf, 0, buf.Length);
            int i = 0;
            if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            while (i < n && buf[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
            return i < n && buf[i] == (byte)'<';
        }
        catch
        {
            return false;
        }
    }

    public FeedFileResult Analyze(string path, string displayName, IProgress<double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var res = new FeedFileResult(displayName, _opt.MaxIssuesPerCode) { Format = "YML", GeoType = GeoType.None };
        try
        {
            res.FileSizeBytes = new FileInfo(path).Length;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore, // в фидах есть <!DOCTYPE yml_catalog SYSTEM "shops.dtd">
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
            };
            using var r = XmlReader.Create(fs, settings);
            Parse(r, fs, res, progress, ct);
        }
        catch (XmlException ex)
        {
            MarkInvalid(res, $"Невалидный XML (строка {ex.LineNumber}, позиция {ex.LinePosition}): {ex.Message}");
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

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void Parse(XmlReader r, FileStream fs, FeedFileResult res, IProgress<double>? progress, CancellationToken ct)
    {
        r.MoveToContent();
        if (r.NodeType != XmlNodeType.Element) throw new FeedFormatException("В файле нет корневого элемента XML");
        if (!r.Name.Equals("yml_catalog", StringComparison.OrdinalIgnoreCase))
            throw new FeedFormatException($"Корневой элемент «{r.Name}», ожидался yml_catalog (YML-фид)");

        // дата фида (атрибут date) заменяет date_start: по ней работает проверка «свежести»
        string? dateText = r.GetAttribute("date");
        if (TryParseDate(dateText, out var feedDate))
        {
            res.DateStartMin = feedDate;
            res.DateStartMax = feedDate;
        }
        else
        {
            res.Issues.Add(IssueCode.YmlDateMissing, res.DisplayName, "yml_catalog",
                string.IsNullOrWhiteSpace(dateText) ? "У корневого элемента нет атрибута date" : $"Не удалось разобрать дату: «{dateText}»");
        }

        var categories = new HashSet<string>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var usedCategories = new Dictionary<string, (long Count, string Example)>(StringComparer.Ordinal);
        long offerNo = 0;
        long ticks = 0;

        r.Read();
        while (!r.EOF)
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (r.Name == "category")
                {
                    string? cid = r.GetAttribute("id");
                    if (!string.IsNullOrWhiteSpace(cid)) categories.Add(cid.Trim());
                    r.Skip();
                    continue;
                }

                if (r.Name == "offer")
                {
                    ct.ThrowIfCancellationRequested();
                    offerNo++;
                    ReadOffer(r, res, offerNo, seenIds, usedCategories);
                    if ((++ticks & 0x3FF) == 0 && progress is not null && fs.Length > 0)
                        progress.Report(Math.Min(fs.Position / (double)fs.Length, 0.999));
                    continue;
                }
            }

            r.Read();
        }

        if (res.OfferObjects == 0) throw new FeedFormatException("В фиде нет офферов (элементов offer)");

        res.CategoryCount = categories.Count;

        // категории, на которые ссылаются офферы, но которых нет в <categories>
        if (categories.Count > 0)
        {
            foreach (var (cid, (count, example)) in usedCategories)
            {
                if (categories.Contains(cid)) continue;
                res.Issues.Add(IssueCode.OfferUnknownCategory, res.DisplayName, cid,
                    $"Категории с таким id нет в categories; офферов с ней: {count:N0} (например, оффер id {example})");
            }
        }
    }

    /// <summary>Читает один &lt;offer&gt;. После вызова ридер стоит на узле сразу за элементом.</summary>
    private void ReadOffer(XmlReader r, FeedFileResult res, long offerNo, HashSet<string> seenIds,
        Dictionary<string, (long Count, string Example)> usedCategories)
    {
        res.OfferObjects++;
        string id = (r.GetAttribute("id") ?? "").Trim();
        string? name = null, model = null, url = null, price = null, categoryId = null;
        bool hasPictureKey = false, hasPicture = false;

        if (r.IsEmptyElement)
        {
            r.Read();
        }
        else
        {
            int depth = r.Depth;
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
            {
                if (r.NodeType == XmlNodeType.Element && r.Depth == depth + 1)
                {
                    switch (r.Name)
                    {
                        case "name": name = r.ReadElementContentAsString(); break;
                        case "model": model = r.ReadElementContentAsString(); break;
                        case "url": url = r.ReadElementContentAsString(); break;
                        case "price": price = r.ReadElementContentAsString(); break;
                        case "categoryId": categoryId = r.ReadElementContentAsString(); break;
                        case "picture":
                        {
                            hasPictureKey = true;
                            if (!string.IsNullOrWhiteSpace(r.ReadElementContentAsString())) hasPicture = true;
                            break;
                        }
                        default: r.Skip(); break;
                    }
                }
                else
                {
                    r.Read();
                }
            }

            r.Read(); // за </offer>
        }

        string title = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : (model ?? "").Trim();
        url = url?.Trim();

        // ---- обязательные поля (те же правила, что у JSON: название, id, картинка, цена; url не обязателен)
        var missing = new List<string>();
        var empty = new List<string>();
        var invalid = new List<string>();

        if (id.Length == 0) missing.Add("id (атрибут offer)");
        if (title.Length == 0)
        {
            if (name is null && model is null) missing.Add("name (или model)");
            else empty.Add("name/model");
        }

        if (price is null) missing.Add("price");
        else if (!decimal.TryParse(price.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var pv) || pv <= 0)
            invalid.Add($"price = «{Cut(price.Trim(), 40)}»");

        if (!hasPictureKey) missing.Add("picture");
        else if (!hasPicture) empty.Add("picture");

        bool fieldBad = missing.Count + empty.Count + invalid.Count > 0;
        bool duplicate = id.Length > 0 && !seenIds.Add(id);

        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            string cid = categoryId.Trim();
            usedCategories[cid] = usedCategories.TryGetValue(cid, out var cur) ? (cur.Count + 1, cur.Example) : (1, id.Length > 0 ? id : "#" + offerNo);
        }

        if (fieldBad || duplicate)
        {
            res.BadOffers++;
            res.ExcludedRefs++; // с ошибкой или повторный id — в итоговые числа не входит
        }
        else
        {
            res.CatalogOfferRefs++;
            res.NoGeoOffers++;
            res.NoGeoProducts.Add(IdHash.Full(Encoding.UTF8.GetBytes(title)));
        }

        if (!fieldBad && !duplicate) return;

        string where = id.Length > 0 ? $"{id} (оффер №{offerNo})" : $"(оффер №{offerNo} без id)";
        string context = title.Length > 0 ? "название: «" + Cut(title, 150) + "»" : "";

        if (duplicate)
            res.Issues.Add(IssueCode.DuplicateOfferId, res.DisplayName, where,
                "Оффер с таким id уже встречался в этом файле" + (context.Length > 0 ? "; " + context : ""), url ?? "");

        if (fieldBad)
        {
            var parts = new List<string>();
            if (missing.Count > 0) parts.Add("нет ключа: " + string.Join(", ", missing));
            if (empty.Count > 0) parts.Add("пусто: " + string.Join(", ", empty));
            if (invalid.Count > 0) parts.Add("некорректное значение: " + string.Join(", ", invalid));
            if (context.Length > 0) parts.Add(context);
            res.Issues.Add(IssueCode.OfferFieldInvalid, res.DisplayName, where, string.Join("; ", parts), url ?? "");
        }
    }

    /// <summary>Берёт дату из «2026-11-13 08:15» или «2026-11-12T10:56:21+07:00» — первые 10 символов.</summary>
    private static bool TryParseDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        return text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
