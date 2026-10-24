using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeedCheckerGui;

// ===================== Настройки, сети, эталоны =====================

/// <summary>Эталон сети: всё необязательно. Пустое поле — по нему не сравниваем.</summary>
public sealed class Baseline
{
    public int? GeoCount { get; set; }
    public long? UniqueProducts { get; set; }
    public double? AvgUniquePerGeo { get; set; }
    public double TolerancePercent { get; set; } = 5;

    [JsonIgnore]
    public bool IsEmpty => GeoCount is null && UniqueProducts is null && AvgUniquePerGeo is null;
}

public sealed class NetworkConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Галочка «проверять» в списке сетей.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Файлы или ссылки на фиды.</summary>
    public List<string> Sources { get; set; } = new();
    public Baseline Baseline { get; set; } = new();

    public NetworkConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Sources = new List<string>(Sources),
        Baseline = new Baseline
        {
            GeoCount = Baseline.GeoCount,
            UniqueProducts = Baseline.UniqueProducts,
            AvgUniquePerGeo = Baseline.AvgUniquePerGeo,
            TolerancePercent = Baseline.TolerancePercent,
        },
    };
}

public sealed class AppSettings
{
    public string CacheDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FeedChecker", "cache");
    public string HistoryDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeedChecker", "history");
    public bool DeleteDownloaded { get; set; } = true;
    public int KeepHistoryDays { get; set; } = 14;
    /// <summary>Допустимое изменение показателей сети относительно прошлой проверки/среднего, %.</summary>
    public double NetworkTolerancePercent { get; set; } = 10;
    /// <summary>Падение числа офферов у одного гео, начиная с которого предупреждаем, %.</summary>
    public double GeoDropPercent { get; set; } = 30;
    /// <summary>Часовой пояс для «сегодня» (UTC+N). 3 — Москва.</summary>
    public int TimezoneHours { get; set; } = 3;

    public string? LastExportDir { get; set; }
    public int WindowWidth { get; set; } = 1250;
    public int WindowHeight { get; set; } = 780;
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public bool Maximized { get; set; }
    public int SplitterDistance { get; set; } = 560;

    public DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(TimezoneHours));

    public void Normalize()
    {
        var def = new AppSettings();
        if (string.IsNullOrWhiteSpace(CacheDir)) CacheDir = def.CacheDir;
        if (string.IsNullOrWhiteSpace(HistoryDir)) HistoryDir = def.HistoryDir;
        KeepHistoryDays = Math.Clamp(KeepHistoryDays, 1, 365);
        NetworkTolerancePercent = Math.Clamp(NetworkTolerancePercent, 0, 100);
        GeoDropPercent = Math.Clamp(GeoDropPercent, 0, 100);
        TimezoneHours = Math.Clamp(TimezoneHours, -12, 14);
        WindowWidth = Math.Clamp(WindowWidth, 600, 5000);
        WindowHeight = Math.Clamp(WindowHeight, 400, 3000);
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица читается в файле как есть
        Converters = { new JsonStringEnumConverter() },
    };

    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Настройки и список сетей лежат в %AppData%\FeedChecker (settings.json, networks.json).</summary>
public sealed class AppStorage
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeedChecker");

    private static string SettingsPath => Path.Combine(Dir, "settings.json");
    private static string NetworksPath => Path.Combine(Dir, "networks.json");

    public AppSettings Settings { get; private set; } = new();
    public List<NetworkConfig> Networks { get; private set; } = new();
    public string? Warning { get; private set; }

    public static AppStorage Load()
    {
        var st = new AppStorage();
        st.Settings = st.Read<AppSettings>(SettingsPath) ?? new AppSettings();
        st.Networks = st.Read<List<NetworkConfig>>(NetworksPath) ?? new List<NetworkConfig>();
        st.Settings.Normalize();
        foreach (var n in st.Networks)
        {
            n.Sources ??= new List<string>();
            n.Baseline ??= new Baseline();
            if (string.IsNullOrWhiteSpace(n.Id)) n.Id = Guid.NewGuid().ToString("N");
        }
        return st;
    }

    private T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Json.Options);
        }
        catch (Exception ex)
        {
            Warning += $"Не удалось прочитать {Path.GetFileName(path)} ({ex.Message}). Файл сохранён как .bad, взяты значения по умолчанию. ";
            try { File.Move(path, path + ".bad", overwrite: true); } catch { /* не критично */ }
            return null;
        }
    }

    /// <summary>Сохраняет настройки и сети. Возвращает текст ошибки или null.</summary>
    public string? Save()
    {
        try
        {
            Json.WriteAtomic(SettingsPath, Settings);
            Json.WriteAtomic(NetworksPath, Networks);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

// ===================== История проверок =====================

public sealed class GeoRecord
{
    public string Name { get; set; } = "";
    public long Offers { get; set; }
    public long UniqueProducts { get; set; }
}

/// <summary>Итог одной проверки сети — то, что хранится в истории и с чем сравниваем.</summary>
public sealed class RunRecord
{
    public string NetworkId { get; set; } = "";
    public string NetworkName { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public GeoType GeoType { get; set; }
    public int Files { get; set; }
    public int GeoCount { get; set; }
    public long OffersWithDuplicates { get; set; }
    public long UniqueProducts { get; set; }
    public double AvgWithDuplicates { get; set; }
    public double AvgWithoutDuplicates { get; set; }
    public DateOnly? DateStartMin { get; set; }
    public DateOnly? DateStartMax { get; set; }
    /// <summary>false — date_start не изменился с прошлой проверки (такая запись не идёт в среднее).</summary>
    public bool Updated { get; set; } = true;
    public long Errors { get; set; }
    public long Warnings { get; set; }
    public List<string> FileNames { get; set; } = new();
    public List<GeoRecord> Geos { get; set; } = new();

    public static RunRecord From(NetworkConfig net, NetworkSummary s, IReadOnlyList<FeedFileResult> files, DateTime utcNow) => new()
    {
        NetworkId = net.Id,
        NetworkName = net.Name,
        TimestampUtc = utcNow,
        GeoType = s.GeoType,
        Files = s.Files,
        GeoCount = s.GeoCount,
        OffersWithDuplicates = s.OffersWithDuplicates,
        UniqueProducts = s.UniqueProducts,
        AvgWithDuplicates = s.AvgOffersPerGeoWithDuplicates,
        AvgWithoutDuplicates = s.AvgOffersPerGeoWithoutDuplicates,
        DateStartMin = s.DateStartMin,
        DateStartMax = s.DateStartMax,
        Errors = s.Errors,
        Warnings = s.Warnings,
        FileNames = files.Select(f => f.DisplayName).ToList(),
        Geos = s.Geos.Select(g => new GeoRecord { Name = g.Name, Offers = g.Offers, UniqueProducts = g.UniqueProducts }).ToList(),
    };
}

/// <summary>История лежит в папке сети: &lt;HistoryDir&gt;\&lt;id сети&gt;\ГГГГММДД-ЧЧММСС-ммм.json, по файлу на проверку.</summary>
public sealed class HistoryStore
{
    private readonly string _root;

    public HistoryStore(string root) => _root = root;

    private string DirFor(string networkId) => Path.Combine(_root, networkId);

    /// <summary>Все записи сети по возрастанию времени. Битые файлы пропускаются.</summary>
    public List<RunRecord> Load(string networkId)
    {
        var list = new List<RunRecord>();
        string dir = DirFor(networkId);
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file, Encoding.UTF8), Json.Options);
                if (rec is not null) list.Add(rec);
            }
            catch { /* повреждённую запись пропускаем */ }
        }
        return list.OrderBy(r => r.TimestampUtc).ToList();
    }

    public void Save(RunRecord record)
    {
        string path = Path.Combine(DirFor(record.NetworkId), record.TimestampUtc.ToString("yyyyMMdd-HHmmss-fff") + ".json");
        Json.WriteAtomic(path, record);
    }

    /// <summary>Удаляет записи старше keepDays дней. Возвращает, сколько удалено.</summary>
    public int Purge(string networkId, int keepDays)
    {
        int removed = 0;
        string dir = DirFor(networkId);
        if (!Directory.Exists(dir)) return 0;
        var limit = DateTime.UtcNow.AddDays(-keepDays);
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file, Encoding.UTF8), Json.Options);
                if (rec is not null && rec.TimestampUtc < limit) { File.Delete(file); removed++; }
            }
            catch { /* не критично */ }
        }
        return removed;
    }

    public void DeleteAll(string networkId)
    {
        try { if (Directory.Exists(DirFor(networkId))) Directory.Delete(DirFor(networkId), recursive: true); }
        catch { /* не критично */ }
    }
}
