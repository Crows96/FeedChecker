using System.Data;
using System.Diagnostics;

namespace FeedCheckerGui;

/// <summary>Окно «Поиск товара»: название + (необязательно) гео → список найденных офферов с ценой и файлом, где они лежат.</summary>
internal sealed class ProductSearchForm : Form
{
    private const string AllChecked = "Все отмеченные галочкой сети";
    private const string LocalItem = "Локальные файлы…";

    private readonly IReadOnlyList<NetworkConfig> _networks;
    private readonly AppSettings _settings;
    private readonly Func<string> _exportDir;
    private readonly Action<string?> _rememberExportDir;

    private readonly ComboBox _where = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly TextBox _text = new() { Width = 330 };
    private readonly CheckBox _phrase = new() { Text = "искать фразу целиком (иначе — все слова в любом порядке)", AutoSize = true };
    private readonly TextBox _geo = new() { Width = 330 };
    private readonly CheckBox _geoExact = new() { Text = "гео точно совпадает (иначе — часть названия или адреса)", AutoSize = true };
    private readonly Label _localCaption = new() { Text = "Локальные файлы:", AutoSize = true, Margin = new Padding(3, 7, 12, 3), Visible = false };
    private readonly FlowLayoutPanel _localPanel = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0), Visible = false };
    private readonly Button _pickFiles = new() { Text = "Выбрать файлы…", AutoSize = true };
    private readonly Button _pickFolder = new() { Text = "Папка…", AutoSize = true };
    private readonly Button _clearFiles = new() { Text = "Очистить", AutoSize = true };
    private readonly Label _localInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 7, 3, 3) };
    private readonly ToolTip _tip = new();
    private readonly List<string> _localFiles = new();
    private string? _lastDir;
    private readonly NumericUpDown _max = new() { Minimum = 1, Maximum = 200000, Value = 5000, Width = 90 };
    private readonly Button _go = new() { Text = "Найти", AutoSize = true };
    private readonly Button _stop = new() { Text = "Остановить", AutoSize = true, Enabled = false };
    private readonly Button _save = new() { Text = "Сохранить результаты…", AutoSize = true, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(1000, 0) };
    private readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 160, Height = 14 };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = SystemColors.Window, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
    };

    private CancellationTokenSource? _cts;
    private ProductQuery? _lastQuery;
    private string _lastWhere = "";
    private ProductSearchResult? _lastResult;

    public ProductSearchForm(IReadOnlyList<NetworkConfig> networks, string? selectedId, AppSettings settings,
        Func<string> exportDir, Action<string?> rememberExportDir)
    {
        _networks = networks;
        _settings = settings;
        _exportDir = exportDir;
        _rememberExportDir = rememberExportDir;

        Text = "Поиск товара";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1180, 720);
        MinimumSize = new Size(820, 480);

        _where.Items.Add(AllChecked);
        foreach (var n in networks) _where.Items.Add(n.Name);
        _where.Items.Add(LocalItem);
        int sel = selectedId is null ? -1 : networks.ToList().FindIndex(n => n.Id == selectedId);
        _where.SelectedIndex = sel >= 0 ? sel + 1 : 0;

        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10, 10, 10, 4) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Row(string caption, Control c, string? hint = null)
        {
            form.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(3, 7, 12, 3) });
            var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            cell.Controls.Add(c);
            if (hint is not null) cell.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 7, 3, 3) });
            form.Controls.Add(cell);
        }
        Row("Где искать:", _where, "сеть из списка или свои файлы .json");
        _localPanel.Controls.AddRange(new Control[] { _pickFiles, _pickFolder, _clearFiles, _localInfo });
        form.Controls.Add(_localCaption);
        form.Controls.Add(_localPanel);
        Row("Название товара:", _text, "часть названия, например «сэндвич тунец»");
        form.Controls.Add(new Label());
        form.Controls.Add(_phrase);
        Row("Гео (необязательно):", _geo, "регион или адрес магазина целиком или частью");
        form.Controls.Add(new Label());
        form.Controls.Add(_geoExact);
        Row("Максимум результатов:", _max);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 0, 10, 0), WrapContents = true };
        buttons.Controls.AddRange(new Control[] { _go, _stop, _save });
        _bar.Margin = new Padding(14, 8, 3, 3);
        buttons.Controls.Add(_bar);
        _status.Margin = new Padding(8, 7, 3, 3);
        buttons.Controls.Add(_status);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Копировать путь к файлу (источник)", null, (_, _) => CopyCell("Источник (полный путь)"));
        menu.Items.Add("Копировать ID оффера", null, (_, _) => CopyCell("ID оффера"));
        menu.Items.Add("Открыть ссылку товара", null, (_, _) => OpenUrl());
        _grid.ContextMenuStrip = menu;

        Controls.Add(_grid);
        Controls.Add(buttons);
        Controls.Add(form);

        UpdateLocalInfo();
        _where.SelectedIndexChanged += (_, _) => OnWhereChanged();
        _pickFiles.Click += (_, _) => PickFiles();
        _pickFolder.Click += (_, _) => PickFolder();
        _clearFiles.Click += (_, _) => { _localFiles.Clear(); UpdateLocalInfo(); };
        AllowDrop = true;
        _grid.AllowDrop = true;
        DragEnter += OnDragEnter;
        _grid.DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _grid.DragDrop += OnDragDrop;

        AcceptButton = _go;
        _go.Click += async (_, _) => await RunSearchAsync();
        _stop.Click += (_, _) => _cts?.Cancel();
        _save.Click += (_, _) => SaveResults();
        FormClosing += (_, _) => _cts?.Cancel();
    }

    private bool IsLocal => _where.SelectedIndex == _where.Items.Count - 1;

    private void OnWhereChanged()
    {
        bool local = IsLocal;
        _localCaption.Visible = local;
        _localPanel.Visible = local;
        if (local && _localFiles.Count == 0) PickFiles();
    }

    private void UpdateLocalInfo()
    {
        if (_localFiles.Count == 0)
        {
            _localInfo.Text = "файлы не выбраны (можно перетащить .json в окно)";
            _tip.SetToolTip(_localInfo, "");
            return;
        }
        string first = string.Join(", ", _localFiles.Take(3).Select(Path.GetFileName));
        _localInfo.Text = $"выбрано: {_localFiles.Count} — {first}{(_localFiles.Count > 3 ? "…" : "")}";
        _tip.SetToolTip(_localInfo, string.Join("\n", _localFiles));
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.GetFiles(p, "*.json", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
                    AddFile(f);
            }
            else if (File.Exists(p)) AddFile(p);
        }
        UpdateLocalInfo();
    }

    private void AddFile(string path)
    {
        if (!_localFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) _localFiles.Add(path);
        _lastDir = Path.GetDirectoryName(path);
    }

    private void PickFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Выберите файлы фидов",
            Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = _lastDir ?? "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
    }

    private void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Папка с файлами .json (подпапки не просматриваются)", SelectedPath = _lastDir ?? "" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int before = _localFiles.Count;
        AddPaths(new[] { dlg.SelectedPath });
        if (_localFiles.Count == before)
            MessageBox.Show(this, "В этой папке нет новых файлов .json.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
        AddPaths(paths);
        if (_localFiles.Count > 0) _where.SelectedIndex = _where.Items.Count - 1;
    }

    private void SetBusy(bool busy)
    {
        _go.Enabled = !busy;
        _stop.Enabled = busy;
        _where.Enabled = _text.Enabled = _geo.Enabled = _phrase.Enabled = _geoExact.Enabled = _max.Enabled = !busy;
        _pickFiles.Enabled = _pickFolder.Enabled = _clearFiles.Enabled = !busy;
        _bar.Visible = busy;
    }

    private async Task RunSearchAsync()
    {
        string text = _text.Text.Trim();
        if (text.Length == 0)
        {
            MessageBox.Show(this, "Введите название товара.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        List<ProductSearchTarget> targets;
        string whereText;
        if (IsLocal)
        {
            _localFiles.RemoveAll(f => !File.Exists(f));
            UpdateLocalInfo();
            if (_localFiles.Count == 0)
            {
                MessageBox.Show(this, "Выберите файлы .json или папку с ними (кнопки справа от списка) либо перетащите файлы в окно.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            targets = new List<ProductSearchTarget> { new("Локальные файлы", _localFiles.ToList()) };
            whereText = $"Локальные файлы ({_localFiles.Count})";
        }
        else
        {
            List<NetworkConfig> nets = _where.SelectedIndex <= 0
                ? _networks.Where(n => n.Enabled && n.Sources.Count > 0).ToList()
                : _networks.Where(n => n.Name == (string)_where.SelectedItem!).Take(1).ToList();
            if (nets.Count == 0 || nets.All(n => n.Sources.Count == 0))
            {
                MessageBox.Show(this, "Нет сетей с источниками: отметьте сети галочками, выберите сеть с файлами или режим «Локальные файлы…».", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            targets = nets.Select(n => new ProductSearchTarget(n.Name, n.Sources.ToList())).ToList();
            whereText = string.Join(", ", nets.Select(n => n.Name));
        }

        var query = new ProductQuery
        {
            Text = text,
            WholePhrase = _phrase.Checked,
            Geo = _geo.Text.Trim(),
            GeoExact = _geoExact.Checked,
            MaxResults = (int)_max.Value,
        };

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<string>(s => _status.Text = s);
        _grid.DataSource = null;
        _save.Enabled = false;
        _status.Text = "Поиск…";
        SetBusy(true);

        ProductSearchResult result;
        try
        {
            result = await Task.Run(() => ProductSearcher.Search(targets, query, _settings, progress, ct));
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _status.Text = "Ошибка поиска: " + ex.Message;
            return;
        }

        SetBusy(false);
        _lastQuery = query;
        _lastWhere = whereText;
        _lastResult = result;
        ShowResult(result, query);
    }

    private void ShowResult(ProductSearchResult r, ProductQuery q)
    {
        var table = new DataTable();
        foreach (var col in new[] { "Сеть", "Файл JSON", "Гео", "Откуда гео", "Товар" }) table.Columns.Add(col, typeof(string));
        table.Columns.Add("Цена", typeof(decimal));
        table.Columns.Add("Старая цена", typeof(decimal));
        foreach (var col in new[] { "Цена от", "ID оффера", "ID каталога", "Ссылка", "Штрихкод", "Источник (полный путь)" })
            table.Columns.Add(col, typeof(string));

        table.BeginLoadData();
        foreach (var h in r.Hits)
        {
            table.Rows.Add(h.Network, h.FileName, h.Geo, h.GeoFromId ? "id оффера" : "каталог", h.Name,
                h.Price is { } p ? (object)p : DBNull.Value, h.OldPrice is { } o ? (object)o : DBNull.Value, h.PriceFrom ? "да" : "",
                h.OfferId, h.CatalogId, h.Url, h.Barcode, h.Source);
        }
        table.EndLoadData();
        _grid.DataSource = table;

        int[] widths = { 110, 170, 260, 80, 330, 70, 90, 60, 230, 200, 260, 110, 400 };
        for (int i = 0; i < _grid.Columns.Count && i < widths.Length; i++) _grid.Columns[i].Width = widths[i];
        _grid.Columns["Цена"]!.DefaultCellStyle.Format = "0.##";
        _grid.Columns["Старая цена"]!.DefaultCellStyle.Format = "0.##";

        string msg = r.Hits.Count == 0
            ? "Ничего не найдено."
            : $"Найдено: {r.Hits.Count:N0}.";
        msg += $" Просмотрено файлов: {r.FilesScanned}, {r.Elapsed.TotalSeconds:N1} с.";
        if (r.Truncated) msg += $" Показаны первые {q.MaxResults:N0}: уточните запрос или увеличьте предел.";
        if (r.Cancelled) msg += " Поиск остановлен — список неполный.";
        if (r.Errors.Count > 0) msg += $" Не удалось прочитать файлов: {r.Errors.Count}.";
        _status.Text = msg;
        _save.Enabled = r.Hits.Count > 0 || r.Errors.Count > 0;

        if (r.Errors.Count > 0)
            MessageBox.Show(this, "Часть файлов не удалось прочитать:\n\n" + string.Join("\n", r.Errors.Take(10)) +
                                  (r.Errors.Count > 10 ? $"\n… и ещё {r.Errors.Count - 10}" : ""),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private string? CurrentCell(string column)
    {
        if (_grid.CurrentRow is null || !_grid.Columns.Contains(column)) return null;
        return _grid.CurrentRow.Cells[column].Value?.ToString();
    }

    private void CopyCell(string column)
    {
        string? v = CurrentCell(column);
        if (!string.IsNullOrEmpty(v)) Clipboard.SetText(v);
    }

    private void OpenUrl()
    {
        string? url = CurrentCell("Ссылка");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); }
        catch { /* браузер не открылся — не критично */ }
    }

    private void SaveResults()
    {
        if (_lastResult is null || _lastQuery is null) return;
        using var dlg = new SaveFileDialog
        {
            Title = "Сохранить результаты поиска",
            Filter = "Excel (*.xlsx)|*.xlsx|CSV для Excel (*.csv)|*.csv|Текстовый файл (*.txt)|*.txt|JSON (*.json)|*.json",
            FileName = $"поиск_товара_{DateTime.Now:yyyyMMdd_HHmm}",
            InitialDirectory = _exportDir(),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            ProductSearchExporter.Write(dlg.FileName, _lastQuery, _lastWhere, _lastResult);
            _rememberExportDir(Path.GetDirectoryName(dlg.FileName));
            _status.Text = "Результаты сохранены: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось записать файл: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
