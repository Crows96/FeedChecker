namespace FeedCheckerGui;

// ===================== Сравнение с историей и эталоном =====================

public sealed class CompareSettings
{
    public double NetworkTolerancePercent { get; init; } = 10;
    public double GeoDropPercent { get; init; } = 30;
    public int KeepDays { get; init; } = 14;
    public DateOnly Today { get; init; }
    public int TimezoneHours { get; init; } = 3;
    /// <summary>Сколько дней пропавшее гео остаётся в списке «Пропало».</summary>
    public int GeoHistoryDays { get; init; } = 30;
}

public sealed class MetricRow
{
    public string Name { get; init; } = "";
    public bool Decimal { get; init; }
    public double Current { get; init; }
    public double? Previous { get; init; }
    public double? DeltaPercent { get; init; }
    public double? Average { get; init; }
    public double? Expected { get; init; }
    public string Status { get; init; } = "";
    public bool HasProblem { get; init; }
}

/// <summary>Одна строка вкладки «Сравнение по гео»: уникальные офферы магазина/региона сейчас и раньше.</summary>
public sealed class GeoCompareRow
{
    public string Name { get; init; } = "";
    public long? Current { get; init; }
    /// <summary>Сколько было раньше: в прошлой проверке или (для пропавших/вернувшихся) в последней, где гео видели.</summary>
    public long? Previous { get; init; }
    public long? Delta { get; init; }
    public double? DeltaPercent { get; init; }
    /// <summary>Среднее за период (считается, если в истории не меньше двух проверок с этим гео).</summary>
    public double? Average { get; init; }
    /// <summary>OK, Падение, Рост, Новое, Вернулось, Пропало или «нет истории».</summary>
    public string Status { get; init; } = "";
    public DateTime? FirstSeenUtc { get; init; }
    public DateTime? LastSeenUtc { get; init; }
    /// <summary>Гео скрыто пользователем: не попадает в предупреждения и счётчики.</summary>
    public bool Hidden { get; set; }

    public static int StatusOrder(string status) => status switch
    {
        "Пропало" => 0,
        "Падение" => 1,
        "Новое" => 2,
        "Вернулось" => 3,
        "Рост" => 4,
        "OK" => 5,
        _ => 6,
    };
}

public sealed class ComparisonResult
{
    public List<GeoCompareRow> GeoRows { get; } = new();
    public RunRecord? Previous { get; set; }
    /// <summary>По скольким проверкам посчитано среднее.</summary>
    public int AverageCount { get; set; }
    public int KeepDays { get; set; }
    public List<MetricRow> Metrics { get; } = new();
    public IssueCollector Issues { get; } = new(2000);
    /// <summary>Названия гео, скрытых пользователем.</summary>
    public HashSet<string> HiddenGeos { get; } = new(StringComparer.Ordinal);

    private static bool IsGeoCode(IssueCode code) =>
        code is IssueCode.CompareGeoRemoved or IssueCode.CompareGeoAdded or IssueCode.CompareGeoDrop;

    private bool IsHiddenIssue(FeedIssue i) => IsGeoCode(i.Code) && HiddenGeos.Contains(i.Where);

    /// <summary>Проблемы без тех, что относятся к скрытым гео.</summary>
    public IEnumerable<FeedIssue> VisibleIssues => Issues.Items.Where(i => !IsHiddenIssue(i));

    public long Problems => Issues.Totals.Values.Sum() - Issues.Items.Count(IsHiddenIssue);

    /// <summary>Скрыть или вернуть гео: обновляет и строки сравнения, и список скрытых.</summary>
    public void SetHidden(IReadOnlyCollection<string> names, bool hidden)
    {
        foreach (var n in names)
        {
            if (hidden) HiddenGeos.Add(n); else HiddenGeos.Remove(n);
        }
        foreach (var row in GeoRows)
            if (names.Contains(row.Name)) row.Hidden = hidden;
    }
}

public static class RunComparer
{
    public static ComparisonResult Compare(RunRecord cur, IReadOnlyList<RunRecord> history, Baseline? baseline, CompareSettings s,
        GeoRegistry? registry = null)
    {
        var res = new ComparisonResult { KeepDays = s.KeepDays };
        var issues = res.Issues;
        string net = cur.NetworkName;
        double tol = s.NetworkTolerancePercent;

        var past = history.Where(h => h.TimestampUtc < cur.TimestampUtc).OrderBy(h => h.TimestampUtc).ToList();
        var prev = past.LastOrDefault();
        res.Previous = prev;

        // Запись «обновлялась», если date_start изменился с прошлой проверки. Неизменённые не идут в среднее.
        cur.Updated = prev is null || cur.DateStartMax != prev.DateStartMax;
        var avgSet = past.Where(h => h.Updated && h.TimestampUtc >= cur.TimestampUtc.AddDays(-s.KeepDays)).ToList();
        res.AverageCount = avgSet.Count;

        // свежесть по date_start
        var (fresh, days) = FreshnessInfo.Evaluate(cur.DateStartMax, s.Today);
        if (fresh is Freshness.Stale or Freshness.Future)
            issues.Add(IssueCode.FeedStale, net, cur.DateStartMax?.ToString("yyyy-MM-dd") ?? "",
                $"date_start {cur.DateStartMax:yyyy-MM-dd}, сегодня {s.Today:yyyy-MM-dd}: {FreshnessInfo.Text(fresh, days)}");

        // состав файлов
        if (prev is not null && prev.Files != cur.Files)
            issues.Add(IssueCode.CompareFilesChanged, net, "Файлов в сети",
                $"В прошлой проверке было файлов: {prev.Files}, сейчас: {cur.Files}");

        var bl = baseline is { IsEmpty: false } ? baseline : null;

        void Metric(string name, bool dec, double now, Func<RunRecord, double> sel, double? expected)
        {
            double? p = prev is null ? null : sel(prev);
            double? a = avgSet.Count == 0 ? null : avgSet.Average(sel);
            double? delta = Pct(now, p);
            var notes = new List<string>();

            if (delta is not null && Math.Abs(delta.Value) > tol)
            {
                notes.Add("изменилось к прошлой проверке");
                issues.Add(IssueCode.CompareTotalChanged, net, name,
                    $"было {Fmt(p!.Value, dec)}, стало {Fmt(now, dec)} ({PctText(delta.Value)}); допуск {tol:0.#}%");
            }

            double? da = Pct(now, a);
            if (avgSet.Count >= 2 && da is not null && Math.Abs(da.Value) > tol)
            {
                notes.Add($"отклонение от среднего ({PctText(da.Value)})");
                issues.Add(IssueCode.CompareVsAverage, net, name,
                    $"среднее за {s.KeepDays} дн. по {avgSet.Count} проверкам: {Fmt(a!.Value, dec)}, сейчас {Fmt(now, dec)} ({PctText(da.Value)}); допуск {tol:0.#}%");
            }

            if (expected is not null && bl is not null)
            {
                double? de = Pct(now, expected);
                if (de is null || Math.Abs(de.Value) > bl.TolerancePercent)
                {
                    notes.Add("не совпадает с эталоном");
                    issues.Add(IssueCode.BaselineMismatch, net, name,
                        $"эталон {Fmt(expected.Value, dec)}, сейчас {Fmt(now, dec)} ({(de is null ? "—" : PctText(de.Value))}); допуск {bl.TolerancePercent:0.#}%");
                }
            }

            string status = notes.Count > 0 ? string.Join("; ", notes)
                : prev is null && expected is null ? "нет истории"
                : "OK";
            res.Metrics.Add(new MetricRow
            {
                Name = name,
                Decimal = dec,
                Current = now,
                Previous = p,
                DeltaPercent = delta,
                Average = avgSet.Count >= 2 ? a : null,
                Expected = expected,
                Status = status,
                HasProblem = notes.Count > 0,
            });
        }

        Metric("Количество гео", false, cur.GeoCount, r => r.GeoCount, bl?.GeoCount);
        Metric("Офферов (с дублями)", false, cur.OffersWithDuplicates, r => r.OffersWithDuplicates, null);
        Metric("Уникальных товаров (без дублей)", false, cur.UniqueProducts, r => r.UniqueProducts, bl?.UniqueProducts);
        Metric("Среднее на гео (с дублями)", true, cur.AvgWithDuplicates, r => r.AvgWithDuplicates, null);
        Metric("Среднее на гео (без дублей)", true, cur.AvgWithoutDuplicates, r => r.AvgWithoutDuplicates, bl?.AvgUniquePerGeo);

        // ---- гео: сравнение с реестром (помнит не только прошлую проверку, а весь срок GeoHistoryDays)
        var reg = registry ?? GeoRegistry.FromHistory(past);
        DateOnly Local(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(s.TimezoneHours));

        var prevByName = prev is null
            ? new Dictionary<string, GeoRecord>(StringComparer.Ordinal)
            : prev.Geos.GroupBy(g => g.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var avgByName = new Dictionary<string, double>(StringComparer.Ordinal);
        if (avgSet.Count >= 2)
        {
            var acc = new Dictionary<string, (double Sum, int Count)>(StringComparer.Ordinal);
            foreach (var rec in avgSet)
                foreach (var g in rec.Geos)
                {
                    acc.TryGetValue(g.Name, out var a0);
                    acc[g.Name] = (a0.Sum + g.UniqueProducts, a0.Count + 1);
                }
            foreach (var (name, a1) in acc)
                if (a1.Count >= 2) avgByName[name] = a1.Sum / a1.Count;
        }

        GeoCompareRow MakeRow(string name, long? now, long? before, string status, GeoEntry? e, DateTime? firstSeen, DateTime? lastSeen)
        {
            double? pct = Pct(now ?? 0, before);
            return new GeoCompareRow
            {
                Name = name,
                Current = now,
                Previous = before,
                Delta = before is null ? null : (now ?? 0) - before.Value,
                DeltaPercent = pct is { } p && !double.IsInfinity(p) ? p : null,
                Average = avgByName.TryGetValue(name, out var av) ? av : null,
                Status = status,
                FirstSeenUtc = firstSeen,
                LastSeenUtc = lastSeen,
                Hidden = e?.Hidden == true,
            };
        }

        var firstRunUtc = past.FirstOrDefault()?.TimestampUtc; // гео из самой первой проверки сети «новыми» не считаются
        var rows = new List<GeoCompareRow>();
        var curNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in cur.Geos)
        {
            curNames.Add(g.Name);
            reg.Entries.TryGetValue(g.Name, out var e);
            long? before = prevByName.TryGetValue(g.Name, out var pg) ? pg.UniqueProducts : e?.LastUnique;
            double? pct = Pct(g.UniqueProducts, before);

            string status;
            if (prev is null && e is null) status = "нет истории";
            // новое: раньше не встречалось, либо появилось сегодня уже после самой первой проверки сети (держится при повторных запусках в тот же день)
            else if (e is null || (Local(e.FirstSeenUtc) == s.Today && firstRunUtc is { } fr && e.FirstSeenUtc > fr)) status = "Новое";
            else if ((prev is not null && e.LastSeenUtc < prev.TimestampUtc) || (e.ReturnedUtc is { } ret && Local(ret) == s.Today)) status = "Вернулось";
            else if (pct is { } down && down < 0 && down <= -s.GeoDropPercent) status = "Падение";
            else if (pct is { } up && up > 0 && up >= s.GeoDropPercent) status = "Рост";
            else status = "OK";

            rows.Add(MakeRow(g.Name, g.UniqueProducts, before, status, e, e?.FirstSeenUtc ?? cur.TimestampUtc, cur.TimestampUtc));

            if (status == "Новое")
                issues.Add(IssueCode.CompareGeoAdded, net, g.Name, $"Новое гео: раньше не встречалось, уникальных офферов: {g.UniqueProducts:N0}");
            else if (status == "Падение" && pct is { } dropPct)
                issues.Add(IssueCode.CompareGeoDrop, net, g.Name, $"было {before:N0}, стало {g.UniqueProducts:N0} (−{Math.Abs(dropPct):0}%)");
        }

        foreach (var (name, e) in reg.Entries)
        {
            if (curNames.Contains(name) || e.LastSeenUtc >= cur.TimestampUtc) continue;
            double daysAgo = (cur.TimestampUtc - e.LastSeenUtc).TotalDays;
            if (daysAgo > s.GeoHistoryDays) continue; // слишком давно — уже не показываем (реестр сам его забудет)

            rows.Add(MakeRow(name, null, e.LastUnique, "Пропало", e, e.FirstSeenUtc, e.LastSeenUtc));
            issues.Add(IssueCode.CompareGeoRemoved, net, name,
                $"Нет в этой проверке. Последний раз было {Local(e.LastSeenUtc):dd.MM.yyyy} ({Math.Floor(daysAgo):0} дн. назад), уникальных офферов: {e.LastUnique:N0}");
        }

        res.GeoRows.AddRange(rows
            .OrderBy(x => GeoCompareRow.StatusOrder(x.Status))
            .ThenBy(x => x.DeltaPercent ?? 0)
            .ThenBy(x => x.Name, StringComparer.CurrentCulture));
        foreach (var row in res.GeoRows)
            if (row.Hidden) res.HiddenGeos.Add(row.Name);

        return res;
    }

    /// <summary>Изменение в процентах относительно базы; null — базы нет; бесконечность — база 0, а значение нет.</summary>
    private static double? Pct(double now, double? basis)
    {
        if (basis is null) return null;
        if (basis.Value == 0) return now == 0 ? 0 : double.PositiveInfinity;
        return (now - basis.Value) / Math.Abs(basis.Value) * 100.0;
    }

    public static string PctText(double v) =>
        double.IsInfinity(v) ? "новое" : v.ToString("+0.0;-0.0;0.0") + "%";

    public static string Fmt(double v, bool dec) => dec ? v.ToString("N1") : v.ToString("N0");
}

// ===================== Проверка одной сети: запуск, сравнение, сохранение в историю =====================

public sealed class NetworkCheckResult
{
    public NetworkConfig Network { get; init; } = null!;
    public RunResult? Run { get; set; }
    public RunRecord? Record { get; set; }
    public ComparisonResult? Comparison { get; set; }
    public bool SavedToHistory { get; set; }
    /// <summary>Результат сверки с Фронтиром (null — для сети она не включена).</summary>
    public FrontierComparison? Frontier { get; set; }
    public DateTime FinishedLocal { get; set; } = DateTime.Now;
    /// <summary>Пояснение для пользователя, если сравнение/сохранение пропущено или проверка не удалась.</summary>
    public string? Note { get; set; }

    public static NetworkCheckResult Failure(NetworkConfig net, string message) =>
        new() { Network = net, Note = "Ошибка при проверке: " + message };
}

public static class NetworkChecker
{
    public static NetworkCheckResult Check(NetworkConfig net, AppSettings settings, HistoryStore history,
        IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var result = new NetworkCheckResult { Network = net };
        if (net.Sources.Count == 0)
        {
            result.Note = "В сети нет источников — добавьте файлы или ссылки.";
            return result;
        }

        var options = new RunOptions
        {
            CacheDir = settings.CacheDir,
            DeleteDownloaded = settings.DeleteDownloaded,
            S3ClientFor = bucket => settings.FindS3(bucket) is { } conn ? S3Client.FromSettings(conn) : null,
        };
        var sources = net.Sources.Select(v => new FeedSource(v)).ToList();
        var run = new FeedRunner().Run(sources, net.Name, options, progress, null, ct);
        result.Run = run;
        result.FinishedLocal = DateTime.Now;

        if (run.Cancelled)
        {
            result.Note = "Проверка остановлена — результат неполный, в историю не сохранён.";
            return result;
        }

        if (run.Files.Any(f => !f.IsValid))
        {
            result.Note = "Есть невалидные или недоступные файлы: сравнение и запись в историю пропущены, иначе итоги по сети были бы неполными.";
            return result;
        }

        try
        {
            var record = RunRecord.From(net, run.Summary, run.Files, DateTime.UtcNow);
            var past = history.Load(net.Id);
            var registry = history.LoadGeoRegistry(net.Id, past);
            var cmp = RunComparer.Compare(record, past, net.Baseline, new CompareSettings
            {
                NetworkTolerancePercent = settings.NetworkTolerancePercent,
                GeoDropPercent = settings.GeoDropPercent,
                KeepDays = settings.KeepHistoryDays,
                Today = settings.Today(),
                TimezoneHours = settings.TimezoneHours,
                GeoHistoryDays = settings.GeoHistoryDays,
            }, registry);
            registry.Apply(record, past.LastOrDefault()?.TimestampUtc, settings.GeoHistoryDays);
            history.SaveGeoRegistry(net.Id, registry);
            history.Save(record);
            history.Purge(net.Id, settings.KeepHistoryDays);
            result.Record = record;
            result.Comparison = cmp;
            result.SavedToHistory = true;
        }
        catch (Exception ex)
        {
            result.Note = "Проверка выполнена, но историю сохранить не удалось: " + ex.Message;
        }

        if (net.FrontierRetailerId is int frontierId && !ct.IsCancellationRequested)
            result.Frontier = FrontierChecker.Run(net, frontierId, run.Summary, settings, ct);

        return result;
    }
}
