using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeedCheckerGui;

/// <summary>
/// Сохраняет результаты проверок на диск (по файлу на сеть: папка results рядом с настройками), чтобы после перезапуска
/// программа показывала то же, что и до закрытия: сводку, сравнение, ошибки, гео и сверку с Фронтиром.
/// Тяжёлые данные (множества товаров по гео) не сохраняются — они нужны только во время проверки.
/// </summary>
internal static class ResultStore
{
    private const int FormatVersion = 1;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opt = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static string Dir => Path.Combine(AppStorage.Dir, "results");

    private static string PathFor(string networkId) =>
        Path.Combine(Dir, new string(networkId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()) + ".json");

    // ------------------------------------------------------------ DTO

    private sealed class Wrapper
    {
        public int Version { get; set; }
        public ResultDto? Result { get; set; }
    }

    private sealed class IssuesDto
    {
        public List<FeedIssue> Items { get; set; } = new();
        public Dictionary<IssueCode, long> Totals { get; set; } = new();

        public static IssuesDto From(IssueCollector c) => new()
        {
            Items = c.Items.ToList(),
            Totals = new Dictionary<IssueCode, long>(c.Totals),
        };

        public void FillTo(IssueCollector c)
        {
            c.Items.AddRange(Items);
            foreach (var (code, n) in Totals) c.Totals[code] = n;
        }
    }

    private sealed class FileDto
    {
        public string DisplayName { get; set; } = "";
        public bool IsValid { get; set; } = true;
        public string? InvalidReason { get; set; }
        public TimeSpan Elapsed { get; set; }
        public long FileSizeBytes { get; set; }
        public GeoType GeoType { get; set; }
        public int CatalogCount { get; set; }
        public long CatalogOfferRefs { get; set; }
        public long OfferObjects { get; set; }
        public long MissingOffers { get; set; }
        public long UnreferencedOffers { get; set; }
        public DateOnly? DateStartMin { get; set; }
        public DateOnly? DateStartMax { get; set; }
        public long NoGeoOffers { get; set; }
        public long UniqueProducts { get; set; }
        public int GeoCount { get; set; }
        public IssuesDto Issues { get; set; } = new();
    }

    private sealed class RunDto
    {
        public bool Cancelled { get; set; }
        public NetworkSummary Summary { get; set; } = null!;
        public List<FileDto> Files { get; set; } = new();
    }

    private sealed class ComparisonDto
    {
        public List<GeoCompareRow> GeoRows { get; set; } = new();
        public RunRecord? Previous { get; set; }
        public int AverageCount { get; set; }
        public int KeepDays { get; set; }
        public List<MetricRow> Metrics { get; set; } = new();
        public IssuesDto Issues { get; set; } = new();
        public List<string> HiddenGeos { get; set; } = new();
    }

    private sealed class FrontierDto
    {
        public int RetailerId { get; set; }
        public FrontierState State { get; set; }
        public string Message { get; set; } = "";
        public int FrontierCatalogs { get; set; }
        public int JsonGeos { get; set; }
        public int Fresh { get; set; }
        public DateTime LoadedLocal { get; set; }
        public List<FrontierRow> Rows { get; set; } = new();
        public IssuesDto Issues { get; set; } = new();
    }

    private sealed class ResultDto
    {
        public string NetworkId { get; set; } = "";
        public DateTime FinishedLocal { get; set; }
        public string? Note { get; set; }
        public bool SavedToHistory { get; set; }
        public RunRecord? Record { get; set; }
        public RunDto? Run { get; set; }
        public ComparisonDto? Comparison { get; set; }
        public FrontierDto? Frontier { get; set; }
    }

    // ------------------------------------------------------------ объект → DTO

    private static ResultDto ToDto(NetworkCheckResult r) => new()
    {
        NetworkId = r.Network.Id,
        FinishedLocal = r.FinishedLocal,
        Note = r.Note,
        SavedToHistory = r.SavedToHistory,
        Record = r.Record,
        Run = r.Run is null ? null : new RunDto
        {
            Cancelled = r.Run.Cancelled,
            Summary = r.Run.Summary,
            Files = r.Run.Files.Select(f => new FileDto
            {
                DisplayName = f.DisplayName,
                IsValid = f.IsValid,
                InvalidReason = f.InvalidReason,
                Elapsed = f.Elapsed,
                FileSizeBytes = f.FileSizeBytes,
                GeoType = f.GeoType,
                CatalogCount = f.CatalogCount,
                CatalogOfferRefs = f.CatalogOfferRefs,
                OfferObjects = f.OfferObjects,
                MissingOffers = f.MissingOffers,
                UnreferencedOffers = f.UnreferencedOffers,
                DateStartMin = f.DateStartMin,
                DateStartMax = f.DateStartMax,
                NoGeoOffers = f.NoGeoOffers,
                UniqueProducts = f.IsValid ? f.UniqueProducts() : 0,
                GeoCount = f.GeoCount,
                Issues = IssuesDto.From(f.Issues),
            }).ToList(),
        },
        Comparison = r.Comparison is null ? null : new ComparisonDto
        {
            GeoRows = r.Comparison.GeoRows.ToList(),
            Previous = r.Comparison.Previous,
            AverageCount = r.Comparison.AverageCount,
            KeepDays = r.Comparison.KeepDays,
            Metrics = r.Comparison.Metrics.ToList(),
            Issues = IssuesDto.From(r.Comparison.Issues),
            HiddenGeos = r.Comparison.HiddenGeos.ToList(),
        },
        Frontier = r.Frontier is null ? null : new FrontierDto
        {
            RetailerId = r.Frontier.RetailerId,
            State = r.Frontier.State,
            Message = r.Frontier.Message,
            FrontierCatalogs = r.Frontier.FrontierCatalogs,
            JsonGeos = r.Frontier.JsonGeos,
            Fresh = r.Frontier.Fresh,
            LoadedLocal = r.Frontier.LoadedLocal,
            Rows = r.Frontier.Rows.ToList(),
            Issues = IssuesDto.From(r.Frontier.Issues),
        },
    };

    // ------------------------------------------------------------ DTO → объект

    private static NetworkCheckResult FromDto(ResultDto d, NetworkConfig net)
    {
        var r = new NetworkCheckResult
        {
            Network = net,
            Record = d.Record,
            SavedToHistory = d.SavedToHistory,
            FinishedLocal = d.FinishedLocal,
            Note = d.Note,
        };

        if (d.Run is { } rd && rd.Summary is not null)
        {
            var run = new RunResult { Summary = rd.Summary, Cancelled = rd.Cancelled };
            foreach (var fd in rd.Files)
            {
                var f = new FeedFileResult(fd.DisplayName, int.MaxValue)
                {
                    IsValid = fd.IsValid,
                    InvalidReason = fd.InvalidReason,
                    Elapsed = fd.Elapsed,
                    FileSizeBytes = fd.FileSizeBytes,
                    GeoType = fd.GeoType,
                    CatalogCount = fd.CatalogCount,
                    CatalogOfferRefs = fd.CatalogOfferRefs,
                    OfferObjects = fd.OfferObjects,
                    MissingOffers = fd.MissingOffers,
                    UnreferencedOffers = fd.UnreferencedOffers,
                    DateStartMin = fd.DateStartMin,
                    DateStartMax = fd.DateStartMax,
                    NoGeoOffers = fd.NoGeoOffers,
                    UniqueProductsOverride = fd.UniqueProducts,
                    GeoCountOverride = fd.GeoCount,
                };
                fd.Issues.FillTo(f.Issues);
                run.Files.Add(f);
            }
            r.Run = run;
        }

        if (d.Comparison is { } cd)
        {
            var c = new ComparisonResult { KeepDays = cd.KeepDays, AverageCount = cd.AverageCount, Previous = cd.Previous };
            c.GeoRows.AddRange(cd.GeoRows);
            c.Metrics.AddRange(cd.Metrics);
            cd.Issues.FillTo(c.Issues);
            foreach (var g in cd.HiddenGeos) c.HiddenGeos.Add(g);
            r.Comparison = c;
        }

        if (d.Frontier is { } fd2)
        {
            var f = new FrontierComparison
            {
                RetailerId = fd2.RetailerId,
                State = fd2.State,
                Message = fd2.Message,
                FrontierCatalogs = fd2.FrontierCatalogs,
                JsonGeos = fd2.JsonGeos,
                Fresh = fd2.Fresh,
                LoadedLocal = fd2.LoadedLocal,
            };
            f.Rows.AddRange(fd2.Rows);
            fd2.Issues.FillTo(f.Issues);
            r.Frontier = f;
        }

        return r;
    }

    // ------------------------------------------------------------ публичные операции

    /// <summary>Записать результат в фоне (сериализация больших сетей занимает заметное время).</summary>
    public static void SaveAsync(NetworkCheckResult r) => Task.Run(() => Save(r));

    public static void Save(NetworkCheckResult r)
    {
        try
        {
            string json = JsonSerializer.Serialize(new Wrapper { Version = FormatVersion, Result = ToDto(r) }, Opt);
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                string path = PathFor(r.Network.Id);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch
        {
            // сохранение результата — удобство, а не обязательная часть проверки: не мешаем работе
        }
    }

    public static void Delete(string networkId)
    {
        try
        {
            lock (Gate)
            {
                string path = PathFor(networkId);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch
        {
            // не критично
        }
    }

    /// <summary>Загрузить сохранённые результаты для существующих сетей. Повреждённые файлы пропускаются.</summary>
    public static Dictionary<string, NetworkCheckResult> LoadAll(IReadOnlyList<NetworkConfig> networks)
    {
        var map = new Dictionary<string, NetworkCheckResult>();
        foreach (var net in networks)
        {
            try
            {
                string path = PathFor(net.Id);
                if (!File.Exists(path)) continue;
                var w = JsonSerializer.Deserialize<Wrapper>(File.ReadAllText(path, Encoding.UTF8), Opt);
                if (w?.Result is null || w.Version != FormatVersion) continue;
                map[net.Id] = FromDto(w.Result, net);
            }
            catch
            {
                // файл от другой версии или повреждён — просто не показываем старый результат
            }
        }
        return map;
    }
}
