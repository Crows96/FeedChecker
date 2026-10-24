
namespace FeedCheckerGui;

public enum IssueSeverity { Error, Warning }

public enum IssueCode
{
    // ошибки
    InvalidFeed,
    DuplicateCatalogId,
    CatalogOfferMissing,
    // предупреждения
    OfferFieldInvalid,
    OfferNotInCatalog,
    DuplicateOfferId,
    CatalogIdMissing,
    CatalogNoGeo,
    CatalogMixedGeo,
    EmptyCatalog,
    DuplicateGeoInFile,
    DuplicateGeoAcrossFiles,
    DateStartInvalid,
    DateStartMismatch,
    MixedGeoTypes,
    // сравнение с историей и эталоном
    FeedStale,
    CompareFilesChanged,
    CompareTotalChanged,
    CompareVsAverage,
    BaselineMismatch,
    CompareGeoRemoved,
    CompareGeoAdded,
    CompareGeoDrop,
}

public sealed record FeedIssue(IssueSeverity Severity, IssueCode Code, string File, string Where, string Details);

public static class IssueInfo
{
    public static IssueSeverity SeverityOf(IssueCode code) => code switch
    {
        IssueCode.InvalidFeed or IssueCode.DuplicateCatalogId or IssueCode.CatalogOfferMissing => IssueSeverity.Error,
        _ => IssueSeverity.Warning,
    };

    public static string Title(IssueCode code) => code switch
    {
        IssueCode.InvalidFeed => "Фид невалиден",
        IssueCode.DuplicateCatalogId => "Дубль id каталога",
        IssueCode.CatalogOfferMissing => "Id из каталога нет среди офферов",
        IssueCode.OfferFieldInvalid => "У оффера нет обязательного ключа или он пустой",
        IssueCode.OfferNotInCatalog => "Оффер не указан ни в одном каталоге",
        IssueCode.DuplicateOfferId => "Дубль id оффера",
        IssueCode.CatalogIdMissing => "У каталога нет id",
        IssueCode.CatalogNoGeo => "У каталога нет гео (target_regions/target_shops)",
        IssueCode.CatalogMixedGeo => "У каталога заданы и регионы, и магазины",
        IssueCode.EmptyCatalog => "Пустой каталог (нет офферов)",
        IssueCode.DuplicateGeoInFile => "Гео повторяется в нескольких каталогах файла",
        IssueCode.DuplicateGeoAcrossFiles => "Гео есть в нескольких файлах сети",
        IssueCode.DateStartInvalid => "date_start отсутствует или не читается",
        IssueCode.DateStartMismatch => "Разные date_start у каталогов",
        IssueCode.MixedGeoTypes => "В сети смешаны регионы и магазины",
        IssueCode.FeedStale => "Фид не обновился сегодня (date_start)",
        IssueCode.CompareFilesChanged => "Изменился состав файлов сети",
        IssueCode.CompareTotalChanged => "Показатель изменился относительно прошлой проверки",
        IssueCode.CompareVsAverage => "Показатель отклонился от среднего за период",
        IssueCode.BaselineMismatch => "Показатель не совпадает с эталоном",
        IssueCode.CompareGeoRemoved => "Гео пропало с прошлой проверки",
        IssueCode.CompareGeoAdded => "Гео появилось с прошлой проверки",
        IssueCode.CompareGeoDrop => "Резкое падение офферов у гео",
        _ => code.ToString(),
    };
}

/// <summary>Копит проблемы: счётчики считает точно, а сами записи хранит с ограничением на каждый тип.</summary>
public sealed class IssueCollector
{
    private readonly int _maxPerCode;
    private readonly Dictionary<IssueCode, int> _stored = new();

    public IssueCollector(int maxPerCode) => _maxPerCode = maxPerCode;

    public List<FeedIssue> Items { get; } = new();
    public Dictionary<IssueCode, long> Totals { get; } = new();

    public void Add(IssueCode code, string file, string where, string details)
    {
        Totals[code] = Totals.GetValueOrDefault(code) + 1;
        int s = _stored.GetValueOrDefault(code);
        if (s >= _maxPerCode) return;
        _stored[code] = s + 1;
        Items.Add(new FeedIssue(IssueInfo.SeverityOf(code), code, file, where, details));
    }

    /// <summary>Добавить к счётчику без записи (когда подробности по остальным не собирались).</summary>
    public void AddTotalOnly(IssueCode code, long count)
    {
        if (count > 0) Totals[code] = Totals.GetValueOrDefault(code) + count;
    }

    public long ErrorCount => Totals.Where(kv => IssueInfo.SeverityOf(kv.Key) == IssueSeverity.Error).Sum(kv => kv.Value);
    public long WarningCount => Totals.Where(kv => IssueInfo.SeverityOf(kv.Key) == IssueSeverity.Warning).Sum(kv => kv.Value);
}

public enum GeoType { None, Regions, Shops, Mixed }

public sealed class GeoStat
{
    /// <summary>Офферов в гео (с дублями: один товар в разных магазинах региона считается несколько раз).</summary>
    public long Offers;
    /// <summary>Хэши уникальных товаров гео.</summary>
    public HashSet<ulong> Products = new();
}

public sealed class FeedFileResult
{
    public FeedFileResult(string displayName, int maxIssuesPerCode)
    {
        DisplayName = displayName;
        Issues = new IssueCollector(maxIssuesPerCode);
    }

    public string DisplayName { get; }
    public bool IsValid { get; set; } = true;
    public string? InvalidReason { get; set; }
    public TimeSpan Elapsed { get; set; }
    public long FileSizeBytes { get; set; }

    public GeoType GeoType { get; set; }
    public int CatalogCount { get; set; }
    /// <summary>Сколько id указано в каталогах (с дублями).</summary>
    public long CatalogOfferRefs { get; set; }
    /// <summary>Сколько объектов в массиве offers.</summary>
    public long OfferObjects { get; set; }
    public long MissingOffers { get; set; }
    public long UnreferencedOffers { get; set; }

    public DateOnly? DateStartMin { get; set; }
    public DateOnly? DateStartMax { get; set; }

    public Dictionary<string, GeoStat> Geos { get; } = new(StringComparer.Ordinal);
    /// <summary>Офферы каталогов без гео.</summary>
    public long NoGeoOffers { get; set; }
    public HashSet<ulong> NoGeoProducts { get; } = new();

    public IssueCollector Issues { get; }

    /// <summary>Уникальные товары файла (без дублей), объединение по всем гео.</summary>
    public long UniqueProducts()
    {
        var all = new HashSet<ulong>(NoGeoProducts);
        foreach (var g in Geos.Values) all.UnionWith(g.Products);
        return all.Count;
    }
}

public enum Freshness { Unknown, Today, Stale, Future }

public static class FreshnessInfo
{
    /// <summary>Сравнение даты фида (по date_start) с сегодняшней датой. Время не учитывается.</summary>
    public static (Freshness Status, int Days) Evaluate(DateOnly? feedDate, DateOnly today)
    {
        if (feedDate is null) return (Freshness.Unknown, 0);
        int diff = today.DayNumber - feedDate.Value.DayNumber;
        if (diff == 0) return (Freshness.Today, 0);
        return diff > 0 ? (Freshness.Stale, diff) : (Freshness.Future, -diff);
    }

    public static string Text(Freshness f, int days) => f switch
    {
        Freshness.Today => "обновлён сегодня",
        Freshness.Stale => $"старый на {days} дн.",
        Freshness.Future => $"дата в будущем (+{days} дн.)",
        _ => "нет даты",
    };
}

public sealed class GeoRow
{
    public string Name { get; init; } = "";
    public long Offers { get; init; }
    public long UniqueProducts { get; init; }
    public string Files { get; init; } = "";
}

public sealed class NetworkSummary
{
    public string Name { get; init; } = "";
    public int Files { get; init; }
    public int ValidFiles { get; init; }
    public GeoType GeoType { get; init; }
    public int GeoCount { get; init; }
    public long OffersWithDuplicates { get; init; }
    public long UniqueProducts { get; init; }
    public double AvgOffersPerGeoWithDuplicates { get; init; }
    public double AvgOffersPerGeoWithoutDuplicates { get; init; }
    public GeoRow? MinGeo { get; init; }
    public GeoRow? MaxGeo { get; init; }
    public DateOnly? DateStartMin { get; init; }
    public DateOnly? DateStartMax { get; init; }
    public long Errors { get; init; }
    public long Warnings { get; init; }
    public List<GeoRow> Geos { get; init; } = new();
    /// <summary>Проблемы уровня сети (гео повторяется в разных файлах и т.п.).</summary>
    public List<FeedIssue> NetworkIssues { get; init; } = new();

    public static NetworkSummary Build(string name, IReadOnlyList<FeedFileResult> files)
    {
        var valid = files.Where(f => f.IsValid).ToList();
        var geos = new Dictionary<string, (long Offers, HashSet<ulong> Products, List<string> Files)>(StringComparer.Ordinal);
        var allProducts = new HashSet<ulong>();
        var issues = new List<FeedIssue>();
        long offers = 0;
        bool regions = false, shops = false;
        DateOnly? dMin = null, dMax = null;

        foreach (var f in valid)
        {
            offers += f.CatalogOfferRefs;
            if (f.GeoType is GeoType.Regions or GeoType.Mixed) regions = true;
            if (f.GeoType is GeoType.Shops or GeoType.Mixed) shops = true;
            if (f.DateStartMin is { } a && (dMin is null || a < dMin)) dMin = a;
            if (f.DateStartMax is { } b && (dMax is null || b > dMax)) dMax = b;
            allProducts.UnionWith(f.NoGeoProducts);

            foreach (var (geo, st) in f.Geos)
            {
                if (!geos.TryGetValue(geo, out var cur))
                {
                    cur = (0, new HashSet<ulong>(), new List<string>());
                    geos[geo] = cur;
                }
                else if (!cur.Files.Contains(f.DisplayName))
                {
                    issues.Add(new FeedIssue(IssueSeverity.Warning, IssueCode.DuplicateGeoAcrossFiles, f.DisplayName, geo,
                        "Это гео уже есть в файле: " + string.Join(", ", cur.Files)));
                }

                cur.Offers += st.Offers;
                cur.Products.UnionWith(st.Products);
                if (!cur.Files.Contains(f.DisplayName)) cur.Files.Add(f.DisplayName);
                geos[geo] = cur;
                allProducts.UnionWith(st.Products);
            }
        }

        var gt = regions && shops ? GeoType.Mixed : regions ? GeoType.Regions : shops ? GeoType.Shops : GeoType.None;
        if (gt == GeoType.Mixed)
            issues.Add(new FeedIssue(IssueSeverity.Warning, IssueCode.MixedGeoTypes, name, "", "В файлах сети используются и target_regions, и target_shops"));

        var rows = geos.Select(kv => new GeoRow
        {
            Name = kv.Key,
            Offers = kv.Value.Offers,
            UniqueProducts = kv.Value.Products.Count,
            Files = string.Join(", ", kv.Value.Files),
        }).OrderBy(r => r.Name, StringComparer.CurrentCulture).ToList();

        return new NetworkSummary
        {
            Name = name,
            Files = files.Count,
            ValidFiles = valid.Count,
            GeoType = gt,
            GeoCount = rows.Count,
            OffersWithDuplicates = offers,
            UniqueProducts = allProducts.Count,
            AvgOffersPerGeoWithDuplicates = rows.Count == 0 ? 0 : rows.Average(r => (double)r.Offers),
            AvgOffersPerGeoWithoutDuplicates = rows.Count == 0 ? 0 : rows.Average(r => (double)r.UniqueProducts),
            MinGeo = rows.Count == 0 ? null : rows.MinBy(r => r.Offers),
            MaxGeo = rows.Count == 0 ? null : rows.MaxBy(r => r.Offers),
            DateStartMin = dMin,
            DateStartMax = dMax,
            Errors = files.Sum(f => f.Issues.ErrorCount),
            Warnings = files.Sum(f => f.Issues.WarningCount) + issues.Count,
            Geos = rows,
            NetworkIssues = issues,
        };
    }
}
