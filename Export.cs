using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace FeedCheckerGui;

public sealed record IssueRow(string Network, string Severity, string Title, string File, string Where, string Details);

/// <summary>Одна таблица для выгрузки: лист Excel или раздел CSV.</summary>
public sealed class Sheet
{
    public Sheet(string name, string[] headers, double[] widths)
    {
        Name = name;
        Headers = headers;
        Widths = widths;
    }

    public string Name { get; }
    public string[] Headers { get; }
    public double[] Widths { get; }
    /// <summary>Значения ячеек: string, целые, double. null — пустая ячейка.</summary>
    public List<object?[]> Rows { get; } = new();
}

/// <summary>Запись таблиц в .xlsx (без внешних библиотек) и .csv.</summary>
internal static class TableFile
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    // ---------------------------------------------------------------- csv

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string CsvCell(object? v) => v switch
    {
        null => "",
        string s => Csv(s),
        double d when double.IsNaN(d) || double.IsInfinity(d) => "",
        double d => d.ToString("0.##", CultureInfo.CurrentCulture),
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => Csv(v.ToString() ?? ""),
    };

    /// <summary>Если листов несколько, каждый раздел начинается со строки-заголовка, разделы отделены пустой строкой.</summary>
    public static void WriteCsv(string path, IReadOnlyList<Sheet> sheets)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true)); // BOM — чтобы Excel правильно показал кириллицу
        for (int s = 0; s < sheets.Count; s++)
        {
            var sheet = sheets[s];
            if (sheets.Count > 1)
            {
                if (s > 0) w.WriteLine();
                w.WriteLine(Csv("=== " + sheet.Name + " ==="));
            }
            w.WriteLine(string.Join(";", sheet.Headers.Select(Csv)));
            foreach (var row in sheet.Rows) w.WriteLine(string.Join(";", row.Select(CsvCell)));
        }
    }

    // ---------------------------------------------------------------- xlsx

    /// <summary>Убирает символы, недопустимые в XML 1.0, и экранирует служебные.</summary>
    private static string Xml(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); continue; }
            bool ok = c == 0x9 || c == 0xA || c == 0xD || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);
            if (!ok) continue;
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string ColName(int index)
    {
        string name = "";
        for (int n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    private static string SheetName(string name, int index)
    {
        var s = new string(name.Where(c => ":\\/?*[]".IndexOf(c) < 0).ToArray()).Trim();
        if (s.Length == 0) s = "Лист" + (index + 1);
        return s.Length > 31 ? s[..31] : s;
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    public static void WriteXlsx(string path, IReadOnlyList<Sheet> sheets)
    {
        const int maxDataRows = 1_048_575; // в листе Excel не больше 1 048 576 строк, одна занята заголовком
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var types = new StringBuilder(Decl +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            var book = new StringBuilder(Decl + $"<workbook xmlns=\"{Ns}\" xmlns:r=\"{RelNs}\"><sheets>");
            var rels = new StringBuilder(Decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

            for (int i = 0; i < sheets.Count; i++)
            {
                types.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                book.Append($"<sheet name=\"{Xml(SheetName(sheets[i].Name, i))}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            }

            types.Append("</Types>");
            book.Append("</sheets></workbook>");
            rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");

            AddEntry(zip, "[Content_Types].xml", types.ToString());
            AddEntry(zip, "_rels/.rels", Decl +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            AddEntry(zip, "xl/workbook.xml", book.ToString());
            AddEntry(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
            AddEntry(zip, "xl/styles.xml", Decl +
                $"<styleSheet xmlns=\"{Ns}\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
                "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                "</styleSheet>");

            for (int i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                int count = Math.Min(sheet.Rows.Count, maxDataRows);
                var entry = zip.CreateEntry($"xl/worksheets/sheet{i + 1}.xml", CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));

                w.Write(Decl + $"<worksheet xmlns=\"{Ns}\">");
                w.Write("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                w.Write("<cols>");
                for (int c = 0; c < sheet.Widths.Length; c++)
                    w.Write($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{sheet.Widths[c].ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
                w.Write("</cols><sheetData>");

                w.Write("<row r=\"1\">");
                for (int c = 0; c < sheet.Headers.Length; c++)
                    w.Write($"<c r=\"{ColName(c)}1\" t=\"inlineStr\" s=\"1\"><is><t xml:space=\"preserve\">{Xml(sheet.Headers[c])}</t></is></c>");
                w.Write("</row>");

                for (int r = 0; r < count; r++)
                {
                    int rowNo = r + 2;
                    w.Write($"<row r=\"{rowNo}\">");
                    var cells = sheet.Rows[r];
                    for (int c = 0; c < cells.Length; c++)
                    {
                        string cellRef = ColName(c) + rowNo;
                        switch (cells[c])
                        {
                            case null:
                                break;
                            case string s:
                                w.Write($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(s)}</t></is></c>");
                                break;
                            case int or long or short or byte:
                                w.Write($"<c r=\"{cellRef}\"><v>{Convert.ToInt64(cells[c], CultureInfo.InvariantCulture)}</v></c>");
                                break;
                            case double d:
                                if (!double.IsNaN(d) && !double.IsInfinity(d))
                                    w.Write($"<c r=\"{cellRef}\"><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                                break;
                            default:
                                w.Write($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(cells[c]?.ToString() ?? "")}</t></is></c>");
                                break;
                        }
                    }
                    w.Write("</row>");
                }

                w.Write("</sheetData>");
                if (sheet.Headers.Length > 0)
                    w.Write($"<autoFilter ref=\"A1:{ColName(sheet.Headers.Length - 1)}{count + 1}\"/>");
                w.Write("</worksheet>");
            }
        }

        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Выгрузка найденных ошибок и предупреждений: .xlsx, .csv, .txt или .json (по расширению файла).</summary>
public static class IssueExporter
{
    private static readonly string[] Headers = { "Сеть", "Серьёзность", "Проблема", "Файл", "Где (каталог / оффер / гео)", "Подробности" };
    private static readonly double[] Widths = { 22, 15, 44, 28, 40, 80 };

    public static IssueRow ToRow(string network, FeedIssue i) => new(
        network,
        i.Severity == IssueSeverity.Error ? "Ошибка" : "Предупреждение",
        IssueInfo.Title(i.Code), i.File, i.Where, i.Details);

    internal static Sheet ToSheet(string name, IEnumerable<IssueRow> rows)
    {
        var sheet = new Sheet(name, Headers, Widths);
        foreach (var r in rows) sheet.Rows.Add(new object?[] { r.Network, r.Severity, r.Title, r.File, r.Where, r.Details });
        return sheet;
    }

    public static void Write(string path, IReadOnlyList<IssueRow> rows)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".xlsx": TableFile.WriteXlsx(path, new[] { ToSheet("Ошибки", rows) }); break;
            case ".csv": TableFile.WriteCsv(path, new[] { ToSheet("Ошибки", rows) }); break;
            case ".json": File.WriteAllText(path, JsonSerializer.Serialize(rows, Json.Options), new UTF8Encoding(false)); break;
            default: WriteText(path, rows); break;
        }
    }

    private static void WriteText(string path, IReadOnlyList<IssueRow> rows)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine($"Ошибки и предупреждения: {rows.Count:N0}  ({DateTime.Now:dd.MM.yyyy HH:mm})");
        w.WriteLine();
        foreach (var r in rows)
        {
            string sev = r.Severity == "Ошибка" ? "ОШИБКА" : "предупр.";
            w.WriteLine($"[{sev}] {r.Network} | {r.Title} | {r.File} | {r.Where} | {r.Details}");
        }
    }
}

/// <summary>Выгрузка списка гео и полного отчёта по сети в те же форматы.</summary>
public static class ReportExporter
{
    // ---------------------------------------------------------------- гео

    private static Sheet GeoSheet(IEnumerable<GeoRow> geos)
    {
        var sheet = new Sheet("Гео", new[] { "Гео", "Офферов", "Уникальных товаров", "Файл" }, new double[] { 60, 14, 20, 40 });
        foreach (var g in geos) sheet.Rows.Add(new object?[] { g.Name, g.Offers, g.UniqueProducts, g.Files });
        return sheet;
    }

    /// <summary>.xlsx / .csv / .json — с цифрами; .txt — только названия, по одному в строке (для ручной сверки).</summary>
    public static void WriteGeo(string path, IReadOnlyList<GeoRow> geos)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".xlsx": TableFile.WriteXlsx(path, new[] { GeoSheet(geos) }); break;
            case ".csv": TableFile.WriteCsv(path, new[] { GeoSheet(geos) }); break;
            case ".json":
                File.WriteAllText(path, JsonSerializer.Serialize(
                    geos.Select(g => new { Name = g.Name, Offers = g.Offers, UniqueProducts = g.UniqueProducts, Files = g.Files }),
                    Json.Options), new UTF8Encoding(false));
                break;
            default:
                File.WriteAllLines(path, geos.Select(g => g.Name), new UTF8Encoding(true));
                break;
        }
    }

    // ---------------------------------------------------------------- отчёт

    private const int MaxIssueRows = 100_000;

    private static string GeoWord(GeoType t) => t switch
    {
        GeoType.Regions => "Регионы",
        GeoType.Shops => "Магазины",
        GeoType.Mixed => "Регионы и магазины",
        _ => "—",
    };

    private static string DateText(DateTime? utc) => utc is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    private static double? Finite(double? v) => v is { } d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;

    private static List<Sheet> ReportSheets(Sheet summary, Sheet cmp, Sheet? geoCmp, Sheet files, Sheet geo, Sheet problems)
    {
        var list = new List<Sheet> { summary, cmp };
        if (geoCmp is not null) list.Add(geoCmp);
        list.Add(files);
        list.Add(geo);
        list.Add(problems);
        return list;
    }

    private static IEnumerable<FeedIssue> AllIssues(NetworkCheckResult r)
    {
        IEnumerable<FeedIssue> all = r.Run is null ? Enumerable.Empty<FeedIssue>()
            : r.Run.Files.SelectMany(f => f.Issues.Items).Concat(r.Run.Summary.NetworkIssues);
        if (r.Comparison is not null) all = all.Concat(r.Comparison.VisibleIssues);
        return all.OrderBy(i => i.Severity).ThenBy(i => i.Code);
    }

    /// <summary>.xlsx — книга с листами «Сводка», «Сравнение», «Файлы», «Гео», «Проблемы»; .csv — те же разделы в одном файле;
    /// .json — то же в структурированном виде; .txt — готовый текст отчёта.</summary>
    public static void WriteReport(string path, string network, NetworkCheckResult r, Func<string> textReport)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".xlsx" && ext != ".csv" && ext != ".json")
        {
            File.WriteAllText(path, textReport(), new UTF8Encoding(true));
            return;
        }

        var run = r.Run ?? throw new InvalidOperationException("Нет результата проверки.");
        var s = run.Summary;
        var issues = AllIssues(r).Take(MaxIssueRows).Select(i => IssueExporter.ToRow(network, i)).ToList();
        long issuesTotal = run.Files.Sum(f => f.Issues.Totals.Values.Sum()) + s.NetworkIssues.Count + (r.Comparison?.Problems ?? 0);

        // сводка
        var summary = new Sheet("Сводка", new[] { "Показатель", "Значение" }, new double[] { 44, 60 });
        void Add(string name, object? value) => summary.Rows.Add(new object?[] { name, value });
        Add("Сеть", network);
        Add("Проверено", r.FinishedLocal.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture));
        if (r.Note is not null) Add("Примечание", r.Note);
        Add("Файлов", s.Files);
        Add("Валидных файлов", s.ValidFiles);
        Add("Тип гео", GeoWord(s.GeoType));
        Add("Количество гео", s.GeoCount);
        Add("Офферов (с дублями)", s.OffersWithDuplicates);
        Add("Уникальных товаров (без дублей)", s.UniqueProducts);
        Add("Среднее офферов на гео (с дублями)", Math.Round(s.AvgOffersPerGeoWithDuplicates, 1));
        Add("Среднее офферов на гео (без дублей)", Math.Round(s.AvgOffersPerGeoWithoutDuplicates, 1));
        if (s.MinGeo is not null) Add("Меньше всего офферов", $"{s.MinGeo.Name} — {s.MinGeo.Offers:N0}");
        if (s.MaxGeo is not null) Add("Больше всего офферов", $"{s.MaxGeo.Name} — {s.MaxGeo.Offers:N0}");
        Add("date_start", s.DateStartMax is null ? "—" : $"{s.DateStartMin:yyyy-MM-dd} … {s.DateStartMax:yyyy-MM-dd}");
        Add("Ошибок", s.Errors);
        Add("Предупреждений", s.Warnings);
        if (r.Comparison is { } c)
        {
            Add("Сравнение с проверкой от", c.Previous is null ? "первая проверка" : c.Previous.TimestampUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture));
            Add("Расхождений с историей и эталоном", c.Problems);
        }
        if (issuesTotal > issues.Count) Add("Проблем найдено / выгружено", $"{issuesTotal:N0} / {issues.Count:N0}");

        // сравнение
        var cmp = new Sheet("Сравнение",
            new[] { "Показатель", "Сейчас", "Прошлая проверка", "Изменение, %", "Среднее за период", "Эталон", "Статус" },
            new double[] { 38, 16, 18, 15, 18, 14, 50 });
        if (r.Comparison is not null)
            foreach (var m in r.Comparison.Metrics)
            {
                double? Rd(double? v) => v is null ? null : m.Decimal ? Math.Round(v.Value, 1) : v;
                cmp.Rows.Add(new object?[]
                {
                    m.Name, Rd(m.Current), Rd(m.Previous),
                    m.DeltaPercent is { } dp && double.IsInfinity(dp) ? "новое" : Finite(m.DeltaPercent) is { } dv ? Math.Round(dv, 1) : null,
                    Rd(m.Average), Rd(m.Expected), m.Status,
                });
            }

        // сравнение уникальных офферов по гео
        Sheet? geoCmp = null;
        if (r.Comparison is { } gc && gc.GeoRows.Count > 0)
        {
            geoCmp = new Sheet("Сравнение по гео",
                new[] { "Гео", "Уникальных сейчас", "Было", "Изменение", "Изменение, %", "Среднее за период", "Статус", "Впервые видели", "Последний раз", "Скрыто" },
                new double[] { 60, 18, 12, 12, 14, 18, 14, 16, 16, 10 });
            foreach (var g in gc.GeoRows)
                geoCmp.Rows.Add(new object?[]
                {
                    g.Name, g.Current, g.Previous, g.Delta,
                    g.DeltaPercent is { } dp2 ? Math.Round(dp2, 1) : null,
                    g.Average is { } av ? Math.Round(av, 1) : null,
                    g.Status, DateText(g.FirstSeenUtc), DateText(g.LastSeenUtc), g.Hidden ? "да" : "",
                });
        }

        // файлы
        var files = new Sheet("Файлы",
            new[] { "Файл", "Размер, МБ", "Время, с", "Каталогов", "Гео", "Офферов", "Уникальных товаров", "Статус" },
            new double[] { 40, 12, 11, 12, 10, 12, 20, 60 });
        foreach (var f in run.Files)
            files.Rows.Add(f.IsValid
                ? new object?[] { f.DisplayName, Math.Round(f.FileSizeBytes / 1024.0 / 1024.0, 1), Math.Round(f.Elapsed.TotalSeconds, 1),
                                  f.CatalogCount, f.Geos.Count, f.CatalogOfferRefs, f.UniqueProducts(), "OK" }
                : new object?[] { f.DisplayName, null, null, null, null, null, null, "Невалиден: " + f.InvalidReason });

        var geo = GeoSheet(s.Geos);
        var problems = IssueExporter.ToSheet("Проблемы", issues);

        switch (ext)
        {
            case ".xlsx": TableFile.WriteXlsx(path, ReportSheets(summary, cmp, geoCmp, files, geo, problems)); break;
            case ".csv": TableFile.WriteCsv(path, ReportSheets(summary, cmp, geoCmp, files, geo, problems)); break;
            default:
                var doc = new
                {
                    Network = network,
                    CheckedAt = r.FinishedLocal,
                    Note = r.Note,
                    Summary = summary.Rows.ToDictionary(x => (string)x[0]!, x => x[1]),
                    Comparison = r.Comparison?.Metrics.Select(m => new
                    {
                        m.Name, m.Current, m.Previous,
                        DeltaPercent = Finite(m.DeltaPercent), m.Average, m.Expected, m.Status,
                    }),
                    GeoComparison = r.Comparison?.GeoRows.Select(g => new
                    {
                        g.Name, g.Current, g.Previous, g.Delta, DeltaPercent = Finite(g.DeltaPercent), g.Average, g.Status,
                        FirstSeen = DateText(g.FirstSeenUtc), LastSeen = DateText(g.LastSeenUtc), g.Hidden,
                    }),
                    Files = run.Files.Select(f => new
                    {
                        Name = f.DisplayName, Valid = f.IsValid, f.InvalidReason,
                        SizeMb = Math.Round(f.FileSizeBytes / 1024.0 / 1024.0, 1),
                        Seconds = Math.Round(f.Elapsed.TotalSeconds, 1),
                        f.CatalogCount, Geo = f.Geos.Count, Offers = f.CatalogOfferRefs,
                        UniqueProducts = f.IsValid ? f.UniqueProducts() : 0,
                    }),
                    Geos = s.Geos.Select(g => new { g.Name, g.Offers, g.UniqueProducts, g.Files }),
                    Issues = issues,
                };
                File.WriteAllText(path, JsonSerializer.Serialize(doc, Json.Options), new UTF8Encoding(false));
                break;
        }
    }
}
