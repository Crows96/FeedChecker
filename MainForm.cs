using System.Data;
using System.Text;

namespace FeedCheckerGui;

public sealed record UiProgress(string NetworkId, string Network, int NetIndex, int NetCount,
    string Phase, string Source, int SourceIndex, int SourceCount, double Overall);

public sealed class MainForm : Form
{
    private static readonly Color Bad = Color.MistyRose;
    private static readonly Color Caution = Color.LemonChiffon;
    private static readonly Color Good = Color.Honeydew;

    private readonly AppStorage _storage;
    private HistoryStore _history;
    private readonly Dictionary<string, NetworkCheckResult> _results = new();
    private readonly Dictionary<string, RunRecord?> _lastRecords = new();
    private bool _populating;
    private CancellationTokenSource? _cts;
    private List<GeoRow> _shownGeos = new();
    private string _shownNetwork = "";

    private readonly Button _btnStart = new() { Text = "▶ Старт", AutoSize = true };
    private readonly Button _btnStop = new() { Text = "■ Стоп", AutoSize = true };
    private readonly Button _btnAddNet = new() { Text = "＋ Сеть…", AutoSize = true };
    private readonly Button _btnImportS3 = new() { Text = "Сети из S3…", AutoSize = true };
    private readonly Button _btnEditNet = new() { Text = "Изменить…", AutoSize = true };
    private readonly Button _btnDelNet = new() { Text = "Удалить", AutoSize = true };
    private readonly Button _btnSettings = new() { Text = "Настройки…", AutoSize = true };
    private readonly Button _btnExportGeo = new() { Text = "Выгрузить гео…", AutoSize = true };
    private readonly Button _btnReport = new() { Text = "Сохранить отчёт…", AutoSize = true };

    private readonly ListView _list = new();
    private readonly Label _fresh = new();
    private readonly ToolTip _tip = new();
    private readonly SplitContainer _split = new();
    private readonly TabControl _tabs = new();
    private readonly TabPage _tabIssues = new("Ошибки и предупреждения");
    private readonly TextBox _summary = new();
    private readonly DataGridView _gridCompare = MakeGrid();
    private readonly DataGridView _gridIssues = MakeGrid();
    private readonly DataGridView _gridGeo = MakeGrid();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();

    private AppSettings Settings => _storage.Settings;

    public MainForm()
    {
        _storage = AppStorage.Load();
        _history = new HistoryStore(Settings.HistoryDir);

        Text = "FeedChecker — проверка фидов";
        Font = new Font("Segoe UI", 9F);
        Width = Settings.WindowWidth;
        Height = Settings.WindowHeight;
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        WireEvents();
        ApplyWindowPosition();
        RefreshList(null);
        SetRunning(false);
        if (_storage.Warning is not null) _status.Text = _storage.Warning;
    }

    // ------------------------------------------------------------------ интерфейс

    private static DataGridView MakeGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        BackgroundColor = SystemColors.Window,
    };

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // верхняя панель
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(6) };
        _btnStart.Font = new Font(Font, FontStyle.Bold);
        _btnStop.Font = new Font(Font, FontStyle.Bold);
        bar.Controls.Add(_btnStart);
        bar.Controls.Add(_btnStop);
        _btnAddNet.Margin = new Padding(24, 3, 3, 3);
        bar.Controls.Add(_btnAddNet);
        bar.Controls.Add(_btnImportS3);
        bar.Controls.Add(_btnEditNet);
        bar.Controls.Add(_btnDelNet);
        bar.Controls.Add(_btnSettings);
        _btnExportGeo.Margin = new Padding(24, 3, 3, 3);
        bar.Controls.Add(_btnExportGeo);
        bar.Controls.Add(_btnReport);

        // список сетей
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.CheckBoxes = true;
        _list.GridLines = true;
        _list.Columns.Add("Сеть", 170);
        _list.Columns.Add("Источн.", 60);
        _list.Columns.Add("Статус", 105);
        _list.Columns.Add("date_start", 85);
        _list.Columns.Add("Свежесть", 125);
        _list.Columns.Add("Проверено", 100);

        _fresh.Dock = DockStyle.Fill;
        _fresh.AutoSize = true;
        _fresh.Padding = new Padding(4, 6, 4, 6);
        _fresh.Font = new Font(Font, FontStyle.Bold);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(_list, 0, 0);
        left.Controls.Add(_fresh, 0, 1);

        // результаты
        _tabs.Dock = DockStyle.Fill;
        var tabSummary = new TabPage("Сводка");
        _summary.Dock = DockStyle.Fill;
        _summary.Multiline = true;
        _summary.ReadOnly = true;
        _summary.ScrollBars = ScrollBars.Vertical;
        _summary.Font = new Font("Consolas", 10F);
        _summary.BackColor = SystemColors.Window;
        tabSummary.Controls.Add(_summary);
        var tabCompare = new TabPage("Сравнение");
        tabCompare.Controls.Add(_gridCompare);
        _tabIssues.Controls.Add(_gridIssues);
        var tabGeo = new TabPage("Гео");
        tabGeo.Controls.Add(_gridGeo);
        _tabs.TabPages.Add(tabSummary);
        _tabs.TabPages.Add(tabCompare);
        _tabs.TabPages.Add(_tabIssues);
        _tabs.TabPages.Add(tabGeo);

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.Panel1.Controls.Add(left);
        _split.Panel2.Controls.Add(_tabs);
        Load += (_, _) =>
        {
            int max = Math.Max(150, _split.Width - 250);
            _split.SplitterDistance = Math.Clamp(Settings.SplitterDistance, 150, max);
        };

        // нижняя панель
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Padding = new Padding(6) };
        _progress.Dock = DockStyle.Fill;
        _progress.Minimum = 0;
        _progress.Maximum = 1000;
        _progress.Height = 22;
        _status.Dock = DockStyle.Fill;
        _status.AutoSize = true;
        _status.Text = "Добавьте сеть и нажмите «Старт».";
        bottom.Controls.Add(_progress, 0, 0);
        bottom.Controls.Add(_status, 0, 1);

        root.Controls.Add(bar, 0, 0);
        root.Controls.Add(_split, 0, 1);
        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);
    }

    private void WireEvents()
    {
        _btnStart.Click += OnStart;
        _btnStop.Click += (_, _) =>
        {
            _cts?.Cancel();
            _btnStop.Enabled = false;
            _status.Text = "Останавливаю…";
        };
        _btnAddNet.Click += (_, _) => OnAddNetwork();
        _btnImportS3.Click += (_, _) => OnImportFromS3();
        _btnEditNet.Click += (_, _) => OnEditNetwork();
        _btnDelNet.Click += (_, _) => OnDeleteNetwork();
        _btnSettings.Click += (_, _) => OnSettings();
        _btnExportGeo.Click += (_, _) => OnExportGeo();
        _btnReport.Click += (_, _) => OnSaveReport();

        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.DoubleClick += (_, _) => { if (_btnEditNet.Enabled) OnEditNetwork(); };
        _list.ItemChecked += (_, e) =>
        {
            if (_populating) return;
            var net = _storage.Networks.FirstOrDefault(n => n.Id == (string)e.Item.Tag!);
            if (net is null) return;
            net.Enabled = e.Item.Checked;
            SaveStorage();
        };

        _gridCompare.CellFormatting += (_, e) =>
        {
            if (e.CellStyle is null || e.RowIndex < 0 || e.RowIndex >= _gridCompare.Rows.Count) return;
            var status = _gridCompare.Rows[e.RowIndex].Cells[6].Value as string;
            if (status is not null && status != "OK" && status != "нет истории") e.CellStyle.BackColor = Caution;
        };
        _gridIssues.CellFormatting += (_, e) =>
        {
            if (e.CellStyle is null || e.RowIndex < 0 || e.RowIndex >= _gridIssues.Rows.Count) return;
            if (_gridIssues.Rows[e.RowIndex].Cells[0].Value as string == "Ошибка") e.CellStyle.BackColor = Bad;
        };

        FormClosing += (_, _) =>
        {
            _cts?.Cancel();
            SaveWindowState();
            SaveStorage();
        };
    }

    private void SetRunning(bool running)
    {
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _btnAddNet.Enabled = !running;
        _btnImportS3.Enabled = !running;
        _btnEditNet.Enabled = !running;
        _btnDelNet.Enabled = !running;
        _btnSettings.Enabled = !running;
    }

    private void ApplyWindowPosition()
    {
        if (Settings.WindowLeft is int l && Settings.WindowTop is int t)
        {
            var rect = new Rectangle(l, t, Width, Height);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect)))
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(l, t);
            }
        }
        if (Settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowState()
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        Settings.WindowWidth = b.Width;
        Settings.WindowHeight = b.Height;
        Settings.WindowLeft = b.Left;
        Settings.WindowTop = b.Top;
        Settings.Maximized = WindowState == FormWindowState.Maximized;
        Settings.SplitterDistance = _split.SplitterDistance;
    }

    private void SaveStorage()
    {
        var err = _storage.Save();
        if (err is not null) _status.Text = "Не удалось сохранить настройки: " + err;
    }

    // ------------------------------------------------------------------ список сетей

    private NetworkConfig? Selected()
    {
        if (_list.SelectedItems.Count == 0) return null;
        var id = (string)_list.SelectedItems[0].Tag!;
        return _storage.Networks.FirstOrDefault(n => n.Id == id);
    }

    private ListViewItem? ItemFor(string id) =>
        _list.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag! == id);

    private void RefreshList(string? selectId)
    {
        _populating = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var n in _storage.Networks)
        {
            if (!_lastRecords.TryGetValue(n.Id, out var rec))
            {
                rec = _history.Load(n.Id).LastOrDefault();
                _lastRecords[n.Id] = rec;
            }

            var item = new ListViewItem(new[] { n.Name, n.Sources.Count.ToString(), "", "", "", "" }) { Tag = n.Id, Checked = n.Enabled };
            _list.Items.Add(item);

            if (_results.TryGetValue(n.Id, out var r))
            {
                var (text, color) = StatusOf(r);
                SetRow(item, text, color, r.Run?.Summary.DateStartMax, r.FinishedLocal.ToString("dd.MM HH:mm"));
            }
            else if (rec is null) SetRow(item, "не проверялась", SystemColors.Window, null, "");
            else SetRow(item, "по истории", SystemColors.Window, rec.DateStartMax, rec.TimestampUtc.ToLocalTime().ToString("dd.MM HH:mm"));

            if (n.Id == selectId) item.Selected = true;
        }
        _list.EndUpdate();
        _populating = false;

        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
        UpdateFreshLabel();
        ShowSelected();
    }

    private void SetRow(ListViewItem item, string status, Color color, DateOnly? date, string last)
    {
        var (fresh, days) = FreshnessInfo.Evaluate(date, Settings.Today());
        item.SubItems[2].Text = status;
        item.SubItems[3].Text = date?.ToString("yyyy-MM-dd") ?? "";
        item.SubItems[4].Text = date is null ? "" : FreshnessInfo.Text(fresh, days);
        item.SubItems[5].Text = last;
        if (date is not null && fresh != Freshness.Today && color == Good) color = Caution;
        item.BackColor = color;
    }

    private static (string Text, Color Color) StatusOf(NetworkCheckResult r)
    {
        if (r.Run is null) return ("Ошибка", Bad);
        if (r.Run.Cancelled) return ("Остановлено", Color.WhiteSmoke);
        if (r.Run.Files.Count == 0) return ("Нет источников", Color.WhiteSmoke);
        if (r.Run.Files.Any(f => !f.IsValid)) return ("Невалиден", Bad);
        long err = r.Run.Summary.Errors;
        long warn = r.Run.Summary.Warnings + (r.Comparison?.Problems ?? 0);
        if (err > 0) return ($"Ошибки: {err}", Bad);
        if (warn > 0) return ($"Предупр.: {warn}", Caution);
        return ("OK", Good);
    }

    private void UpdateFreshLabel()
    {
        int total = _list.Items.Count;
        string today = Settings.Today().ToString("yyyy-MM-dd");
        var late = new List<string>();
        foreach (ListViewItem it in _list.Items)
            if (it.SubItems[3].Text != today) late.Add(it.Text);

        int ok = total - late.Count;
        _fresh.Text = total == 0 ? "Добавьте сеть кнопкой «＋ Сеть…»" : $"Обновилось сегодня: {ok} из {total}";
        _fresh.ForeColor = total > 0 && late.Count == 0 ? Color.DarkGreen : SystemColors.ControlText;
        _tip.SetToolTip(_fresh, late.Count == 0 ? "" : "Не обновились: " + string.Join(", ", late));
    }

    // ------------------------------------------------------------------ сети: добавить, изменить, удалить

    private void OnAddNetwork()
    {
        using var dlg = new NetworkDialog(null, _storage.Networks.Select(n => n.Name).ToList(), Settings.S3.Bucket);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _storage.Networks.Add(dlg.Result);
        SaveStorage();
        RefreshList(dlg.Result.Id);
    }

    private void OnEditNetwork()
    {
        var net = Selected();
        if (net is null) return;
        var others = _storage.Networks.Where(n => n.Id != net.Id).Select(n => n.Name).ToList();
        using var dlg = new NetworkDialog(net.Clone(), others, Settings.S3.Bucket);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int index = _storage.Networks.FindIndex(n => n.Id == net.Id);
        if (index >= 0) _storage.Networks[index] = dlg.Result;
        _results.Remove(net.Id); // источники могли измениться — прежний результат неактуален
        SaveStorage();
        RefreshList(net.Id);
    }

    private void OnDeleteNetwork()
    {
        var net = Selected();
        if (net is null) return;
        var answer = MessageBox.Show(this,
            $"Удалить сеть «{net.Name}»?\n\nДа — удалить вместе с историей проверок.\nНет — удалить сеть, а историю оставить на диске.\nОтмена — ничего не делать.",
            "Удаление сети", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return;
        if (answer == DialogResult.Yes) _history.DeleteAll(net.Id);

        _storage.Networks.RemoveAll(n => n.Id == net.Id);
        _results.Remove(net.Id);
        _lastRecords.Remove(net.Id);
        SaveStorage();
        RefreshList(null);
    }

    private async void OnImportFromS3()
    {
        var s3 = Settings.S3;
        if (!s3.IsConfigured || string.IsNullOrWhiteSpace(s3.Bucket))
        {
            MessageBox.Show(this, "Сначала заполните раздел S3 в «Настройках»: бакет, идентификатор и секретный ключ.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnImportS3.Enabled = false;
        _status.Text = "Получаю список папок в S3…";
        try
        {
            var client = S3Client.FromSettings(s3);
            var listing = await Task.Run(() => client.ListAsync(s3.Bucket, "", CancellationToken.None));

            var used = new HashSet<string>(_storage.Networks.SelectMany(n => n.Sources), StringComparer.OrdinalIgnoreCase);
            var folders = listing.Folders.Where(f => !used.Contains($"s3://{s3.Bucket}/{f}")).ToList();
            int already = listing.Folders.Count - folders.Count;
            if (folders.Count == 0)
            {
                MessageBox.Show(this,
                    listing.Folders.Count == 0 ? "В бакете нет папок." : "Все папки бакета уже добавлены как сети.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _status.Text = "Новых папок в S3 нет.";
                return;
            }

            using var dlg = new S3ImportDialog(s3.Bucket, folders, already);
            if (dlg.ShowDialog(this) != DialogResult.OK) { _status.Text = ""; return; }

            var names = new HashSet<string>(_storage.Networks.Select(n => n.Name), StringComparer.CurrentCultureIgnoreCase);
            string? lastId = null;
            foreach (var folder in dlg.Selected)
            {
                string baseName = folder.TrimEnd('/');
                string name = baseName;
                for (int k = 2; !names.Add(name); k++) name = $"{baseName} ({k})";
                var net = new NetworkConfig { Name = name, Sources = { $"s3://{s3.Bucket}/{folder}" } };
                _storage.Networks.Add(net);
                lastId = net.Id;
            }

            SaveStorage();
            RefreshList(lastId);
            _status.Text = $"Добавлено сетей из S3: {dlg.Selected.Count}.";
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка S3: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Не удалось получить список папок S3", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnImportS3.Enabled = true;
        }
    }

    private void OnSettings()
    {
        using var dlg = new SettingsDialog(Settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        SaveStorage();
        _history = new HistoryStore(Settings.HistoryDir);
        _lastRecords.Clear();
        RefreshList(Selected()?.Id);
    }

    // ------------------------------------------------------------------ запуск

    private sealed class NetProgress : IProgress<RunProgress>
    {
        private readonly IProgress<UiProgress> _ui;
        private readonly NetworkConfig _net;
        private readonly int _index, _count;

        public NetProgress(IProgress<UiProgress> ui, NetworkConfig net, int index, int count)
        {
            _ui = ui;
            _net = net;
            _index = index;
            _count = count;
        }

        public void Report(RunProgress p) =>
            _ui.Report(new UiProgress(_net.Id, _net.Name, _index, _count, p.Phase, p.SourceName,
                p.SourceIndex, p.SourceCount, (_index + p.Overall) / _count));
    }

    private async void OnStart(object? sender, EventArgs e)
    {
        var nets = _storage.Networks.Where(n => n.Enabled).ToList();
        if (nets.Count == 0)
        {
            MessageBox.Show(this, "Отметьте галочкой сети, которые нужно проверить (или сначала добавьте сеть).",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (var n in nets)
        {
            _results.Remove(n.Id);
            var item = ItemFor(n.Id);
            if (item is not null) { item.SubItems[2].Text = "в очереди"; item.BackColor = SystemColors.Window; }
        }

        ShowSelected(); // не показываем прежний результат, пока идёт новая проверка
        SetRunning(true);
        _progress.Value = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var ui = new Progress<UiProgress>(OnProgress); // создан в UI-потоке, отчёты приходят в UI-поток
        var settings = Settings;
        var history = _history;

        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < nets.Count; i++)
                {
                    if (token.IsCancellationRequested) break;
                    var net = nets[i];
                    NetworkCheckResult r;
                    try { r = NetworkChecker.Check(net, settings, history, new NetProgress(ui, net, i, nets.Count), token); }
                    catch (Exception ex) { r = NetworkCheckResult.Failure(net, ex.Message); }
                    BeginInvoke((MethodInvoker)(() => OnNetworkDone(r)));
                }
            });

            UpdateFreshLabel();
            _progress.Value = token.IsCancellationRequested ? _progress.Value : _progress.Maximum;
            _status.Text = token.IsCancellationRequested
                ? "Остановлено."
                : $"Готово. Проверено сетей: {nets.Count}. {_fresh.Text}.";
        }
        catch (Exception ex)
        {
            _status.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(this, ex.ToString(), "Ошибка при проверке", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void OnProgress(UiProgress p)
    {
        _progress.Value = Math.Clamp((int)Math.Round(p.Overall * 1000), 0, 1000);
        if (p.Phase is "Готово" or "Остановлено") return;
        var item = ItemFor(p.NetworkId);
        if (item is not null) item.SubItems[2].Text = p.Phase + "…";
        _status.Text = $"Сеть {p.NetIndex + 1} из {p.NetCount} «{p.Network}» — {p.Phase}: {p.Source} (файл {p.SourceIndex + 1} из {p.SourceCount}) — {p.Overall:P0}";
    }

    private void OnNetworkDone(NetworkCheckResult r)
    {
        var net = r.Network;
        _results[net.Id] = r;
        if (r.Record is not null) _lastRecords[net.Id] = r.Record;

        var item = ItemFor(net.Id);
        if (item is not null)
        {
            var (text, color) = StatusOf(r);
            SetRow(item, text, color, r.Run?.Summary.DateStartMax, r.FinishedLocal.ToString("dd.MM HH:mm"));
        }

        UpdateFreshLabel();
        if (Selected()?.Id == net.Id) ShowSelected();
    }

    // ------------------------------------------------------------------ показ результата

    private void ClearRight()
    {
        _summary.Clear();
        _gridCompare.DataSource = null;
        _gridIssues.DataSource = null;
        _gridGeo.DataSource = null;
        _shownGeos = new List<GeoRow>();
        _shownNetwork = "";
        _tabIssues.Text = "Ошибки и предупреждения";
    }

    private void ShowSelected()
    {
        var net = Selected();
        if (net is null) { ClearRight(); return; }
        _shownNetwork = net.Name;

        if (_results.TryGetValue(net.Id, out var r) && r.Run is not null)
        {
            _summary.Text = BuildSummary(net, r);
            FillComparison(r.Comparison);
            FillIssues(r);
            SetGeos(r.Run.Summary.Geos);
            return;
        }

        // в этой сессии проверка не запускалась — показываем последнюю сохранённую из истории
        _gridCompare.DataSource = null;
        _gridIssues.DataSource = null;
        _tabIssues.Text = "Ошибки и предупреждения";
        _lastRecords.TryGetValue(net.Id, out var rec);
        var sb = new StringBuilder();
        sb.AppendLine($"Сеть: {net.Name}");
        sb.AppendLine($"Источников: {net.Sources.Count}");
        sb.AppendLine();
        if (rec is null)
        {
            sb.AppendLine("Проверок ещё не было. Отметьте сеть галочкой и нажмите «Старт».");
            _summary.Text = sb.ToString();
            SetGeos(new List<GeoRow>());
            return;
        }

        sb.AppendLine("В этой сессии проверка не запускалась. Последняя сохранённая:");
        sb.AppendLine($"  {rec.TimestampUtc.ToLocalTime():dd.MM.yyyy HH:mm}, date_start {rec.DateStartMax:yyyy-MM-dd}");
        sb.AppendLine($"  гео: {rec.GeoCount:N0}, офферов: {rec.OffersWithDuplicates:N0}, уникальных товаров: {rec.UniqueProducts:N0}");
        sb.AppendLine($"  среднее на гео: с дублями {rec.AvgWithDuplicates:N1}, без дублей {rec.AvgWithoutDuplicates:N1}");
        _summary.Text = sb.ToString();
        SetGeos(rec.Geos.Select(g => new GeoRow { Name = g.Name, Offers = g.Offers, UniqueProducts = g.UniqueProducts, Files = "" }).ToList());
    }

    private static string BuildSummary(NetworkConfig net, NetworkCheckResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Сеть: {net.Name}   (проверено {r.FinishedLocal:dd.MM.yyyy HH:mm})");
        if (r.Note is not null)
        {
            sb.AppendLine();
            sb.AppendLine("ВАЖНО: " + r.Note);
        }

        if (r.Comparison is { } c)
        {
            sb.AppendLine();
            if (c.Previous is null)
                sb.AppendLine("Сравнение: это первая проверка сети, сравнивать пока не с чем (результат сохранён в историю).");
            else
                sb.AppendLine($"Сравнение с проверкой от {c.Previous.TimestampUtc.ToLocalTime():dd.MM.yyyy HH:mm}" +
                              (c.AverageCount >= 2 ? $"; среднее за {c.KeepDays} дн. по {c.AverageCount} проверкам" : ""));
            sb.AppendLine($"Расхождений с историей и эталоном: {c.Problems:N0}");
            if (r.Record is { Updated: false })
                sb.AppendLine("date_start не изменился с прошлой проверки: запись не учитывается в среднем.");
        }

        sb.AppendLine();
        sb.Append(BuildRunSummary(r.Run!));
        return sb.ToString();
    }

    private static string BuildRunSummary(RunResult r)
    {
        var s = r.Summary;
        var sb = new StringBuilder();
        string geoWord = s.GeoType switch
        {
            GeoType.Regions => "Регионов",
            GeoType.Shops => "Магазинов",
            GeoType.Mixed => "Гео (регионы и магазины)",
            _ => "Гео",
        };

        sb.AppendLine($"Файлов: {s.Files}, валидных: {s.ValidFiles}");
        if (r.Cancelled) sb.AppendLine("ПРОВЕРКА ОСТАНОВЛЕНА — результат неполный.");
        sb.AppendLine();
        sb.AppendLine($"{geoWord}: {s.GeoCount:N0}");
        sb.AppendLine($"Офферов (с дублями): {s.OffersWithDuplicates:N0}");
        sb.AppendLine($"Уникальных товаров (без дублей): {s.UniqueProducts:N0}");
        sb.AppendLine($"Среднее офферов на гео:  с дублями {s.AvgOffersPerGeoWithDuplicates:N1},  без дублей {s.AvgOffersPerGeoWithoutDuplicates:N1}");
        if (s.MinGeo is not null && s.MaxGeo is not null)
        {
            sb.AppendLine($"Меньше всего офферов: {s.MinGeo.Name} — {s.MinGeo.Offers:N0}");
            sb.AppendLine($"Больше всего офферов: {s.MaxGeo.Name} — {s.MaxGeo.Offers:N0}");
        }

        if (s.DateStartMax is not null)
            sb.AppendLine($"date_start: {s.DateStartMin:yyyy-MM-dd} … {s.DateStartMax:yyyy-MM-dd}");

        sb.AppendLine();
        sb.AppendLine($"Ошибок: {s.Errors:N0}    Предупреждений: {s.Warnings:N0}");

        var totals = new Dictionary<IssueCode, long>();
        foreach (var f in r.Files)
            foreach (var (code, n) in f.Issues.Totals) totals[code] = totals.GetValueOrDefault(code) + n;
        foreach (var i in s.NetworkIssues) totals[i.Code] = totals.GetValueOrDefault(i.Code) + 1;
        foreach (var (code, n) in totals.OrderBy(k => IssueInfo.SeverityOf(k.Key)).ThenBy(k => k.Key))
        {
            string sev = IssueInfo.SeverityOf(code) == IssueSeverity.Error ? "ОШИБКА" : "предупр.";
            sb.AppendLine($"  [{sev}] {IssueInfo.Title(code)}: {n:N0}");
        }

        sb.AppendLine();
        sb.AppendLine("Файлы:");
        foreach (var f in r.Files)
        {
            if (!f.IsValid)
            {
                sb.AppendLine($"  {f.DisplayName}: НЕВАЛИДЕН — {f.InvalidReason}");
                continue;
            }

            sb.AppendLine($"  {f.DisplayName}: {f.FileSizeBytes / 1024.0 / 1024.0:N1} МБ, {f.Elapsed.TotalSeconds:N1} c");
            sb.AppendLine($"      каталогов {f.CatalogCount:N0}, гео {f.Geos.Count:N0}, офферов {f.CatalogOfferRefs:N0}, уникальных товаров {f.UniqueProducts():N0}");
        }

        return sb.ToString();
    }

    private static IEnumerable<FeedIssue> AllIssues(NetworkCheckResult r)
    {
        IEnumerable<FeedIssue> all = r.Run is null ? Enumerable.Empty<FeedIssue>()
            : r.Run.Files.SelectMany(f => f.Issues.Items).Concat(r.Run.Summary.NetworkIssues);
        if (r.Comparison is not null) all = all.Concat(r.Comparison.Issues.Items);
        return all.OrderBy(i => i.Severity).ThenBy(i => i.Code);
    }

    private void FillComparison(ComparisonResult? c)
    {
        var table = new DataTable();
        foreach (var col in new[] { "Показатель", "Сейчас", "Прошлая проверка", "Изменение", "Среднее за период", "Эталон", "Статус" })
            table.Columns.Add(col, typeof(string));

        if (c is not null)
        {
            foreach (var m in c.Metrics)
            {
                table.Rows.Add(
                    m.Name,
                    RunComparer.Fmt(m.Current, m.Decimal),
                    m.Previous is null ? "—" : RunComparer.Fmt(m.Previous.Value, m.Decimal),
                    m.DeltaPercent is null ? "—" : RunComparer.PctText(m.DeltaPercent.Value),
                    m.Average is null ? "—" : RunComparer.Fmt(m.Average.Value, m.Decimal),
                    m.Expected is null ? "—" : RunComparer.Fmt(m.Expected.Value, m.Decimal),
                    m.Status);
            }
        }

        _gridCompare.DataSource = table;
    }

    private void FillIssues(NetworkCheckResult r)
    {
        const int maxRows = 100_000;
        var all = AllIssues(r).ToList();

        var table = new DataTable();
        table.Columns.Add("Серьёзность", typeof(string));
        table.Columns.Add("Файл / сеть", typeof(string));
        table.Columns.Add("Проблема", typeof(string));
        table.Columns.Add("Где (каталог / оффер / гео)", typeof(string));
        table.Columns.Add("Подробности", typeof(string));
        table.BeginLoadData();
        foreach (var i in all.Take(maxRows))
            table.Rows.Add(i.Severity == IssueSeverity.Error ? "Ошибка" : "Предупреждение", i.File, IssueInfo.Title(i.Code), i.Where, i.Details);
        table.EndLoadData();

        _gridIssues.DataSource = table;
        _tabIssues.Text = all.Count > maxRows
            ? $"Ошибки и предупреждения (показано {maxRows:N0} из {all.Count:N0})"
            : $"Ошибки и предупреждения ({all.Count:N0})";
    }

    private void SetGeos(List<GeoRow> geos)
    {
        _shownGeos = geos;
        var table = new DataTable();
        table.Columns.Add("Гео", typeof(string));
        table.Columns.Add("Офферов", typeof(long));
        table.Columns.Add("Уникальных товаров", typeof(long));
        table.Columns.Add("Файл", typeof(string));
        table.BeginLoadData();
        foreach (var g in geos) table.Rows.Add(g.Name, g.Offers, g.UniqueProducts, g.Files);
        table.EndLoadData();

        _gridGeo.DataSource = table;
        if (_gridGeo.Columns.Count >= 3)
        {
            _gridGeo.Columns[1].DefaultCellStyle.Format = "N0";
            _gridGeo.Columns[2].DefaultCellStyle.Format = "N0";
        }
    }

    // ------------------------------------------------------------------ выгрузки

    private static string SafeName(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s;
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private string ExportDir() =>
        !string.IsNullOrEmpty(Settings.LastExportDir) && Directory.Exists(Settings.LastExportDir) ? Settings.LastExportDir : "";

    private void OnExportGeo()
    {
        if (_shownGeos.Count == 0)
        {
            MessageBox.Show(this, "Нет данных по гео: выберите сеть, для которой есть результат или история.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Выгрузить гео в файл",
            Filter = "CSV с цифрами (*.csv)|*.csv|Только названия, по одному в строке (*.txt)|*.txt",
            FileName = $"{SafeName(_shownNetwork)}_гео_{DateTime.Now:yyyyMMdd}",
            InitialDirectory = ExportDir(),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            if (string.Equals(Path.GetExtension(dlg.FileName), ".txt", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllLines(dlg.FileName, _shownGeos.Select(g => g.Name), new UTF8Encoding(true));
            }
            else
            {
                var lines = new List<string> { "Гео;Офферов;Уникальных товаров;Файл" };
                lines.AddRange(_shownGeos.Select(g => $"{Csv(g.Name)};{g.Offers};{g.UniqueProducts};{Csv(g.Files)}"));
                File.WriteAllLines(dlg.FileName, lines, new UTF8Encoding(true)); // BOM — чтобы Excel правильно показал кириллицу
            }

            Settings.LastExportDir = Path.GetDirectoryName(dlg.FileName);
            SaveStorage();
            _status.Text = $"Гео выгружено ({_shownGeos.Count:N0} строк): {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось записать файл: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnSaveReport()
    {
        var net = Selected();
        if (net is null || !_results.TryGetValue(net.Id, out var r) || r.Run is null)
        {
            MessageBox.Show(this, "Отчёт строится по результату проверки. Сначала запустите проверку этой сети.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Сохранить отчёт",
            Filter = "Текстовый файл (*.txt)|*.txt",
            FileName = $"{SafeName(net.Name)}_отчёт_{DateTime.Now:yyyyMMdd_HHmm}",
            InitialDirectory = ExportDir(),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(BuildSummary(net, r));

            if (r.Comparison is { } c)
            {
                sb.AppendLine();
                sb.AppendLine("Сравнение:");
                foreach (var m in c.Metrics)
                {
                    sb.AppendLine($"  {m.Name}: сейчас {RunComparer.Fmt(m.Current, m.Decimal)}; " +
                                  $"прошлая {(m.Previous is null ? "—" : RunComparer.Fmt(m.Previous.Value, m.Decimal))}; " +
                                  $"изменение {(m.DeltaPercent is null ? "—" : RunComparer.PctText(m.DeltaPercent.Value))}; " +
                                  $"среднее {(m.Average is null ? "—" : RunComparer.Fmt(m.Average.Value, m.Decimal))}; " +
                                  $"эталон {(m.Expected is null ? "—" : RunComparer.Fmt(m.Expected.Value, m.Decimal))}; {m.Status}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Проблемы (не более 5000 строк):");
            foreach (var i in AllIssues(r).Take(5000))
            {
                string sev = i.Severity == IssueSeverity.Error ? "ОШИБКА" : "предупр.";
                sb.AppendLine($"  [{sev}] {IssueInfo.Title(i.Code)} | {i.File} | {i.Where} | {i.Details}");
            }

            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            Settings.LastExportDir = Path.GetDirectoryName(dlg.FileName);
            SaveStorage();
            _status.Text = "Отчёт сохранён: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось записать файл: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
