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
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly Button _btnSave = new() { Text = "Сохранить ▾", AutoSize = true };
    private CheckState _headerState = CheckState.Unchecked;
    private bool _loaded; // окно уже показано (до этого список ещё создаёт своё окно и шлёт служебные события)
    private readonly Button _btnImportS3 = new() { Text = "Сети из S3…", AutoSize = true };
    private readonly Button _btnEditNet = new() { Text = "Изменить…", AutoSize = true };
    private readonly Button _btnDelNet = new() { Text = "Удалить", AutoSize = true };
    private readonly Button _btnSettings = new() { Text = "Настройки…", AutoSize = true };
    private readonly Button _btnSearch = new() { Text = "Поиск товара…", AutoSize = true };
    private ProductSearchForm? _searchForm;

    private readonly ListView _list = new();
    private readonly Label _fresh = new();
    private readonly ToolTip _tip = new();
    private readonly SplitContainer _split = new();
    private readonly TabControl _tabs = new();
    private readonly TabPage _tabIssues = new("Ошибки и предупреждения");
    private readonly TextBox _summary = new();
    private readonly DataGridView _gridCompare = MakeGrid();
    private readonly TabPage _tabGeoCmp = new("Сравнение по гео");
    private readonly DataGridView _gridGeoCmp = MakeGrid();
    private readonly CheckBox _chkOnlyDiffs = new() { Text = "Только расхождения", AutoSize = true };
    private readonly CheckBox _chkShowHidden = new() { Text = "Показывать скрытые", AutoSize = true };
    private readonly Button _btnHideGeo = new() { Text = "Скрыть выбранные", AutoSize = true };
    private readonly Button _btnUnhideGeo = new() { Text = "Вернуть выбранные", AutoSize = true };
    private readonly Label _lblGeoCmp = new() { AutoSize = true };
    private readonly TabPage _tabFrontier = new("Фронтир");
    private readonly DataGridView _gridFrontier = MakeGrid();
    private readonly CheckBox _chkFrontierDiffs = new() { Text = "Только расхождения", AutoSize = true };
    private readonly Label _lblFrontier = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    private readonly DataGridView _gridIssues = MakeGrid();
    private readonly DataGridView _gridGeo = MakeGrid();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();

    private AppSettings Settings => _storage.Settings;

    public MainForm()
    {
        _storage = AppStorage.Load();
        _history = new HistoryStore(Settings.HistoryDir);
        foreach (var (id, saved) in ResultStore.LoadAll(_storage.Networks))
        {
            _results[id] = saved;
            if (saved.Record is not null) _lastRecords[id] = saved.Record;
        }

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
        _mode.Items.AddRange(new object[] { "Все отмеченные сети", "Только непроверенные", "Только необновившиеся" });
        _mode.SelectedIndex = Math.Clamp(Settings.RunMode, 0, 2);
        _mode.Margin = new Padding(3, 5, 12, 3);
        _tip.SetToolTip(_mode,
            "Все отмеченные сети — проверить каждую сеть с галочкой.\n" +
            "Только непроверенные — пропустить сети, уже успешно проверенные сегодня.\n" +
            "Только необновившиеся — только сети, у которых date_start не сегодняшний.");
        bar.Controls.Add(new Label { Text = "Режим:", AutoSize = true, Margin = new Padding(3, 8, 3, 0) });
        bar.Controls.Add(_mode);
        bar.Controls.Add(_btnStart);
        bar.Controls.Add(_btnStop);
        _btnAddNet.Margin = new Padding(24, 3, 3, 3);
        bar.Controls.Add(_btnAddNet);
        bar.Controls.Add(_btnImportS3);
        bar.Controls.Add(_btnEditNet);
        bar.Controls.Add(_btnDelNet);
        _btnSearch.Margin = new Padding(24, 3, 3, 3);
        bar.Controls.Add(_btnSearch);
        _btnSettings.Margin = new Padding(24, 3, 3, 3);
        bar.Controls.Add(_btnSettings);
        _btnSave.Margin = new Padding(24, 3, 3, 3);
        bar.Controls.Add(_btnSave);

        // список сетей
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.CheckBoxes = true;
        _list.GridLines = true;
        _list.Columns.Add("Сеть", 150);
        _list.Columns.Add("Источн.", 55);
        _list.Columns.Add("Статус", 100);
        _list.Columns.Add("Дата фида", 80);
        _list.Columns.Add("Свежесть", 115);
        _list.Columns.Add("Проверено", 90);
        _list.ClientSizeChanged += (_, _) => FitNetworkColumn();
        _list.ColumnWidthChanged += (_, e) => { if (e.ColumnIndex != 0) FitNetworkColumn(); };
        _list.OwnerDraw = true; // нужно только ради галочки в заголовке «Сеть»; всё остальное рисуется как обычно
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawItem += (_, e) => e.DrawDefault = true;
        _list.DrawSubItem += (_, e) => e.DrawDefault = true;

        _fresh.Dock = DockStyle.Fill;
        _fresh.AutoSize = true;
        _fresh.Padding = new Padding(16, 0, 4, 0);
        _fresh.Font = new Font(Font, FontStyle.Bold);

        // слева такая же вкладка, как справа: верхние и нижние края обеих частей совпадают по высоте
        var leftTabs = new TabControl { Dock = DockStyle.Fill };
        var tabNets = new TabPage("Сети");
        tabNets.Controls.Add(_list);
        leftTabs.TabPages.Add(tabNets);

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
        var geoCmpLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        geoCmpLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        geoCmpLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var geoCmpBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 0) };
        _chkOnlyDiffs.Margin = new Padding(3, 6, 12, 3);
        _chkShowHidden.Margin = new Padding(3, 6, 12, 3);
        _lblGeoCmp.Margin = new Padding(3, 6, 3, 3);
        _tip.SetToolTip(_btnHideGeo, "Скрыть выделенные гео (например, магазин просто сменил адрес): по ним не будет предупреждений.");
        _tip.SetToolTip(_btnUnhideGeo, "Вернуть выделенные скрытые гео (включите «Показывать скрытые», чтобы их увидеть).");
        geoCmpBar.Controls.Add(_chkOnlyDiffs);
        geoCmpBar.Controls.Add(_chkShowHidden);
        geoCmpBar.Controls.Add(_btnHideGeo);
        geoCmpBar.Controls.Add(_btnUnhideGeo);
        geoCmpBar.Controls.Add(_lblGeoCmp);
        geoCmpLayout.Controls.Add(geoCmpBar, 0, 0);
        geoCmpLayout.Controls.Add(_gridGeoCmp, 0, 1);
        _tabGeoCmp.Controls.Add(geoCmpLayout);
        _tabs.TabPages.Add(tabCompare);
        _tabs.TabPages.Add(_tabGeoCmp);
        var frontierLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        frontierLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frontierLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var frontierBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 0) };
        _chkFrontierDiffs.Margin = new Padding(3, 6, 12, 3);
        _lblFrontier.Margin = new Padding(3, 6, 3, 3);
        _chkFrontierDiffs.CheckedChanged += (_, _) => ApplyFrontierFilter();
        var btnSaveFrontier = new Button { Text = "Сохранить расхождения…", AutoSize = true, Margin = new Padding(3, 2, 12, 3) };
        btnSaveFrontier.Click += (_, _) => OnSaveFrontier(selectedOnly: true);
        frontierBar.Controls.Add(btnSaveFrontier);
        frontierBar.Controls.Add(_chkFrontierDiffs);
        frontierBar.Controls.Add(_lblFrontier);
        frontierLayout.Controls.Add(frontierBar, 0, 0);
        frontierLayout.Controls.Add(_gridFrontier, 0, 1);
        _tabFrontier.Controls.Add(frontierLayout);
        _tabs.TabPages.Add(_tabFrontier);
        _tabs.TabPages.Add(_tabIssues);
        _tabs.TabPages.Add(tabGeo);

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.Panel1.Controls.Add(leftTabs);
        _split.Panel2.Controls.Add(_tabs);
        Load += (_, _) =>
        {
            try
            {
                // слева минимум столько, чтобы все столбцы списка влезли без прокрутки; справа остаётся не меньше 380
                int total = _split.Width, gap = _split.SplitterWidth;
                int left = Math.Min(620, Math.Max(150, total - 380 - gap));
                _split.Panel2MinSize = Math.Max(25, Math.Min(380, total - left - gap));
                _split.Panel1MinSize = left;
                int max = Math.Max(left, total - _split.Panel2MinSize - gap);
                _split.SplitterDistance = Math.Clamp(Settings.SplitterDistance, left, max);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // если окно слишком узкое — оставляем положение по умолчанию
            }
            FitNetworkColumn();
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
        // нижняя строка: слева статус, справа «Обновилось сегодня: N из M»
        var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusRow.Controls.Add(_status, 0, 0);
        statusRow.Controls.Add(_fresh, 1, 0);
        bottom.Controls.Add(_progress, 0, 0);
        bottom.Controls.Add(statusRow, 0, 1);

        root.Controls.Add(bar, 0, 0);
        root.Controls.Add(_split, 0, 1);
        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);
    }

    private void WireEvents()
    {
        _btnStart.Click += OnStart;
        _mode.SelectedIndexChanged += (_, _) =>
        {
            Settings.RunMode = Math.Max(0, _mode.SelectedIndex);
            SaveStorage();
        };
        var saveMenu = new ContextMenuStrip();
        saveMenu.Items.Add("Выгрузить гео…", null, (_, _) => OnExportGeo());
        saveMenu.Items.Add("Сохранить отчёт…", null, (_, _) => OnSaveReport());
        saveMenu.Items.Add("Сохранить ошибки…", null, (_, _) => OnSaveErrors());
        saveMenu.Items.Add("Сохранить сверку с Фронтиром…", null, (_, _) => OnSaveFrontier(selectedOnly: false));
        _btnSave.Click += (_, _) => saveMenu.Show(_btnSave, new Point(0, _btnSave.Height));
        _list.ColumnClick += (_, e) =>
        {
            if (e.Column == 0) SetAllChecked(_headerState != CheckState.Checked);
        };
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
        _btnSearch.Click += (_, _) => OnProductSearch();

        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.DoubleClick += (_, _) => { if (_btnEditNet.Enabled) OnEditNetwork(); };
        Shown += (_, _) =>
        {
            _loaded = true;
            UpdateHeaderCheck();
        };
        _list.ItemChecked += (_, e) =>
        {
            // при создании окна списка WinForms сам «пересоздаёт» строки и шлёт ItemChecked — их игнорируем
            if (_populating || !_loaded) return;
            var net = _storage.Networks.FirstOrDefault(n => n.Id == (string)e.Item.Tag!);
            if (net is null) return;
            net.Enabled = e.Item.Checked;
            SaveStorage();
            UpdateHeaderCheck();
        };

        _gridCompare.CellFormatting += (_, e) =>
        {
            if (e.CellStyle is null || e.RowIndex < 0 || e.RowIndex >= _gridCompare.Rows.Count) return;
            var status = _gridCompare.Rows[e.RowIndex].Cells[6].Value as string;
            if (status is not null && status != "OK" && status != "нет истории") e.CellStyle.BackColor = Caution;
        };
        _chkOnlyDiffs.CheckedChanged += (_, _) => ApplyGeoCmpFilter();
        _chkShowHidden.CheckedChanged += (_, _) => ApplyGeoCmpFilter();
        _btnHideGeo.Click += (_, _) => ToggleGeoHidden(true);
        _btnUnhideGeo.Click += (_, _) => ToggleGeoHidden(false);
        _gridGeoCmp.CellFormatting += (_, e) =>
        {
            if (e.CellStyle is null || e.RowIndex < 0 || e.RowIndex >= _gridGeoCmp.Rows.Count || _gridGeoCmp.Columns.Count < 10) return;
            var row = _gridGeoCmp.Rows[e.RowIndex];
            if (row.Cells[9].Value as string == "да") { e.CellStyle.ForeColor = SystemColors.GrayText; return; }
            var status = row.Cells[6].Value as string;
            if (status is "Пропало" or "Падение") e.CellStyle.BackColor = Bad;
            else if (status is "Новое" or "Рост" or "Вернулось") e.CellStyle.BackColor = Caution;
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
        _mode.Enabled = !running;
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
                SetRow(item, text, color, r.Run is null ? null : FeedDate(r.Run.Summary.DateStartMax, r.Run.Summary.LastModifiedMaxUtc), r.FinishedLocal.ToString("dd.MM HH:mm"));
            }
            else if (rec is null) SetRow(item, "не проверялась", SystemColors.Window, null, "");
            else SetRow(item, "по истории", SystemColors.Window, FeedDate(rec.DateStartMax, rec.LastModifiedMaxUtc), rec.TimestampUtc.ToLocalTime().ToString("dd.MM HH:mm"));

            if (n.Id == selectId) item.Selected = true;
        }
        _list.EndUpdate();
        _populating = false;

        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
        UpdateHeaderCheck();
        FitNetworkColumn();
        UpdateFreshLabel();
        ShowSelected();
    }

    /// <summary>Дата фида для списка сетей: сегодняшняя, если сегодня обновился файл или date_start; иначе самая поздняя из дат.</summary>
    private DateOnly? FeedDate(DateOnly? dateStart, DateTimeOffset? lastModifiedUtc) =>
        FeedDates.Effective(dateStart, lastModifiedUtc, Settings.TimezoneHours, Settings.Today());

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
        long warn = r.Run.Summary.Warnings + (r.Comparison?.Problems ?? 0) + (r.Frontier?.Problems ?? 0);
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

    private void SetAllChecked(bool value)
    {
        _populating = true; // чтобы не сохранять настройки после каждой галочки
        foreach (ListViewItem item in _list.Items)
        {
            item.Checked = value;
            var net = _storage.Networks.FirstOrDefault(n => n.Id == (string)item.Tag!);
            if (net is not null) net.Enabled = value;
        }
        _populating = false;
        SaveStorage();
        UpdateHeaderCheck();
    }

    private bool _fitting;

    /// <summary>Столбец «Сеть» растягивается на всю свободную ширину списка, поэтому горизонтальной прокрутки нет.</summary>
    private void FitNetworkColumn()
    {
        if (_fitting || _list.Columns.Count == 0) return;
        _fitting = true;
        try
        {
            int others = 0;
            for (int i = 1; i < _list.Columns.Count; i++) others += _list.Columns[i].Width;
            _list.Columns[0].Width = Math.Max(130, _list.ClientSize.Width - others);
        }
        finally
        {
            _fitting = false;
        }
    }

    private void UpdateHeaderCheck()
    {
        int total = _list.Items.Count;
        int on = _list.Items.Cast<ListViewItem?>().Count(i => i is not null && i.Checked);
        _headerState = total == 0 || on == 0 ? CheckState.Unchecked
            : on == total ? CheckState.Checked
            : CheckState.Indeterminate;
        _list.Columns[0].Text = _list.Columns[0].Text; // заставляет заголовок перерисоваться
        _list.Invalidate(true);
    }

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        if (e.ColumnIndex != 0)
        {
            e.DrawDefault = true;
            return;
        }

        e.DrawBackground();
        var state = _headerState switch
        {
            CheckState.Checked => System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal,
            CheckState.Indeterminate => System.Windows.Forms.VisualStyles.CheckBoxState.MixedNormal,
            _ => System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal,
        };
        var glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
        var point = new Point(e.Bounds.Left + 4, e.Bounds.Top + (e.Bounds.Height - glyph.Height) / 2);
        CheckBoxRenderer.DrawCheckBox(e.Graphics, point, state);

        var textRect = new Rectangle(e.Bounds.Left + glyph.Width + 10, e.Bounds.Top,
            Math.Max(0, e.Bounds.Width - glyph.Width - 14), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, "Сеть", _list.Font, textRect, SystemColors.ControlText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    // ------------------------------------------------------------------ сети: добавить, изменить, удалить

    private void OnAddNetwork()
    {
        using var dlg = new NetworkDialog(null, _storage.Networks.Select(n => n.Name).ToList(), Settings.ConfiguredS3.Select(c => c.Bucket).ToList());
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
        using var dlg = new NetworkDialog(net.Clone(), others, Settings.ConfiguredS3.Select(c => c.Bucket).ToList());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int index = _storage.Networks.FindIndex(n => n.Id == net.Id);
        if (index >= 0) _storage.Networks[index] = dlg.Result;
        _results.Remove(net.Id); // источники могли измениться — прежний результат неактуален
        ResultStore.Delete(net.Id);
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
        ResultStore.Delete(net.Id);
        _lastRecords.Remove(net.Id);
        SaveStorage();
        RefreshList(null);
    }

    private async void OnImportFromS3()
    {
        var conns = Settings.ConfiguredS3.ToList();
        if (conns.Count == 0)
        {
            MessageBox.Show(this, "Сначала заполните раздел S3 в «Настройках»: бакет, идентификатор и секретный ключ.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnImportS3.Enabled = false;
        _status.Text = "Получаю список папок в S3…";
        try
        {
            var found = new List<(string Bucket, string Folder)>();
            var errors = new List<string>();
            foreach (var c in conns)
            {
                try
                {
                    var client = S3Client.FromSettings(c);
                    var listing = await Task.Run(() => client.ListAsync(c.Bucket, "", CancellationToken.None));
                    foreach (var f in listing.Folders) found.Add((c.Bucket, f));
                }
                catch (Exception ex)
                {
                    errors.Add($"{c.Bucket}: {ex.Message}");
                }
            }

            if (found.Count == 0 && errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));

            var used = new HashSet<string>(_storage.Networks.SelectMany(n => n.Sources), StringComparer.OrdinalIgnoreCase);
            var fresh = found.Where(x => !used.Contains($"s3://{x.Bucket}/{x.Folder}")).ToList();
            int already = found.Count - fresh.Count;
            if (fresh.Count == 0)
            {
                MessageBox.Show(this,
                    found.Count == 0 ? "В бакетах нет папок." : "Все папки бакетов уже добавлены как сети.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _status.Text = "Новых папок в S3 нет.";
                return;
            }

            using var dlg = new S3ImportDialog(fresh, already, errors.Count == 0 ? null : "Не удалось прочитать: " + string.Join("; ", errors));
            if (dlg.ShowDialog(this) != DialogResult.OK) { _status.Text = ""; return; }

            var names = new HashSet<string>(_storage.Networks.Select(n => n.Name), StringComparer.CurrentCultureIgnoreCase);
            string? lastId = null;
            foreach (var (bucket, folder) in dlg.Selected)
            {
                string baseName = folder.TrimEnd('/');
                string name = baseName;
                if (!names.Add(name))
                {
                    name = $"{baseName} ({bucket})";
                    for (int k = 2; !names.Add(name); k++) name = $"{baseName} ({bucket}) {k}";
                }

                var net = new NetworkConfig { Name = name, Sources = { $"s3://{bucket}/{folder}" } };
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

    /// <summary>Поиск товара по названию и гео: по файлам сетей или по локальным файлам.</summary>
    private void OnProductSearch()
    {
        if (_searchForm is { IsDisposed: false })
        {
            _searchForm.Activate();
            return;
        }

        _searchForm = new ProductSearchForm(_storage.Networks, Selected()?.Id, Settings, ExportDir, dir =>
        {
            Settings.LastExportDir = dir;
            SaveStorage();
        });
        _searchForm.FormClosed += (_, _) => _searchForm = null;
        _searchForm.Show(this);
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
        switch (_mode.SelectedIndex)
        {
            case 1:
                await StartRunAsync(n => !CheckedToday(n), "Все отмеченные сети уже проверены сегодня — проверять нечего.");
                break;
            case 2:
                await StartRunAsync(NotFreshToday, "У всех отмеченных сетей date_start уже сегодняшний — перепроверять нечего.");
                break;
            default:
                await StartRunAsync(_ => true, null);
                break;
        }
    }

    /// <summary>Сеть уже успешно проверена сегодня: в этой сессии либо (после перезапуска) по записи истории.</summary>
    private bool CheckedToday(NetworkConfig n)
    {
        if (_results.TryGetValue(n.Id, out var r))
            return r.Run is { Cancelled: false } run && run.Files.Count > 0 && run.Files.All(f => f.IsValid);

        return _lastRecords.TryGetValue(n.Id, out var rec) && rec is not null &&
               DateOnly.FromDateTime(rec.TimestampUtc.AddHours(Settings.TimezoneHours)) == Settings.Today();
    }

    /// <summary>date_start сети не сегодняшний (или неизвестен).</summary>
    private bool NotFreshToday(NetworkConfig n)
    {
        DateOnly? date = null;
        if (_results.TryGetValue(n.Id, out var r) && r.Run is not null) date = FeedDate(r.Run.Summary.DateStartMax, r.Run.Summary.LastModifiedMaxUtc);
        else if (_lastRecords.TryGetValue(n.Id, out var rec) && rec is not null) date = FeedDate(rec.DateStartMax, rec.LastModifiedMaxUtc);
        return date != Settings.Today();
    }

    private async Task StartRunAsync(Func<NetworkConfig, bool> filter, string? nothingToDo)
    {
        var ticked = _storage.Networks.Where(n => n.Enabled).ToList();
        if (ticked.Count == 0)
        {
            MessageBox.Show(this, "Отметьте галочкой сети, которые нужно проверить (или сначала добавьте сеть).",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var nets = ticked.Where(filter).ToList();
        if (nets.Count == 0)
        {
            MessageBox.Show(this, nothingToDo ?? "Нечего проверять.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int skipped = ticked.Count - nets.Count;

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
                : $"Готово. Проверено сетей: {nets.Count}" +
                  (skipped > 0 ? $" (пропущено уже проверенных: {skipped})" : "") + $". {_fresh.Text}.";
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
        ResultStore.SaveAsync(r);

        UpdateRowFromResult(r);
        UpdateFreshLabel();
        if (Selected()?.Id == net.Id) ShowSelected();
    }

    private void UpdateRowFromResult(NetworkCheckResult r)
    {
        var item = ItemFor(r.Network.Id);
        if (item is null) return;
        var (text, color) = StatusOf(r);
        SetRow(item, text, color, r.Run is null ? null : FeedDate(r.Run.Summary.DateStartMax, r.Run.Summary.LastModifiedMaxUtc), r.FinishedLocal.ToString("dd.MM HH:mm"));
    }

    // ------------------------------------------------------------------ показ результата

    private void ClearRight()
    {
        _summary.Clear();
        _gridCompare.DataSource = null;
        ClearGeoComparison();
        ClearFrontier();
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
            FillGeoComparison(r.Comparison);
            FillFrontier(r.Frontier);
            if (r.Frontier is null)
                _lblFrontier.Text = net.FrontierRetailerId is null
                    ? "Для этой сети не указан ID ретейлера Фронтира: нажмите «Изменить…» и заполните поле «Фронтир»."
                    : "Сверка с Фронтиром в этой проверке не выполнялась.";
            FillIssues(r);
            SetGeos(r.Run.Summary.Geos);
            return;
        }

        // в этой сессии проверка не запускалась — показываем последнюю сохранённую из истории
        _gridCompare.DataSource = null;
        ClearGeoComparison();
        ClearFrontier();
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

    private string BuildSummary(NetworkConfig net, NetworkCheckResult r)
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

        if (r.Frontier is { } fr)
        {
            sb.AppendLine();
            sb.AppendLine($"Фронтир (ретейлер {fr.RetailerId}): {fr.Message}");
            if (fr.State != FrontierState.Failed)
                sb.AppendLine($"  каталогов во Фронтире: {fr.FrontierCatalogs:N0}, гео в JSON: {fr.JsonGeos:N0}, с актуальным периодом: {fr.Fresh:N0}, расхождений: {fr.Problems:N0}");
        }

        sb.AppendLine();
        sb.Append(BuildRunSummary(r.Run!));
        return sb.ToString();
    }

    /// <summary>Блок «Свежесть фида»: все три даты (Last-Modified, date_start, date_end) и общий вывод.</summary>
    private void AppendFreshness(StringBuilder sb, NetworkSummary s)
    {
        var today = Settings.Today();
        int tz = Settings.TimezoneHours;
        var lmDate = FeedDates.LocalDate(s.LastModifiedMaxUtc, tz);

        string Status(DateOnly? d)
        {
            var (f, days) = FreshnessInfo.Evaluate(d, today);
            return FreshnessInfo.Text(f, days);
        }

        bool lmFresh = lmDate == today;
        bool startFresh = s.DateStartMax == today;
        string verdict = lmFresh && startFresh ? "АКТУАЛЕН (изменён сегодня, date_start сегодняшний)"
            : lmFresh ? "АКТУАЛЕН (по дате изменения файла)"
            : startFresh ? "АКТУАЛЕН (по date_start)"
            : "НЕ АКТУАЛЕН: файл сегодня не менялся и date_start не сегодняшний";

        sb.AppendLine($"Свежесть фида: {verdict}");
        sb.AppendLine(s.LastModifiedMaxUtc is { } lm
            ? $"  Last-Modified: {lm.ToOffset(TimeSpan.FromHours(tz)):dd.MM.yyyy HH:mm} — {Status(lmDate)}"
            : "  Last-Modified: сервер дату не сообщил");
        sb.AppendLine(s.DateStartMax is null
            ? "  date_start: нет"
            : $"  date_start: {(s.DateStartMin == s.DateStartMax ? $"{s.DateStartMax:yyyy-MM-dd}" : $"{s.DateStartMin:yyyy-MM-dd} … {s.DateStartMax:yyyy-MM-dd}")} — {Status(s.DateStartMax)}");
        if (s.DateEndMax is { } end)
        {
            int left = end.DayNumber - today.DayNumber;
            string endText = left >= 0 ? $"действует, осталось дней: {left}" : $"истёк {-left} дн. назад";
            sb.AppendLine($"  date_end: {(s.DateEndMin == end ? $"{end:yyyy-MM-dd}" : $"{s.DateEndMin:yyyy-MM-dd} … {end:yyyy-MM-dd}")} — {endText}");
        }
        else
        {
            sb.AppendLine("  date_end: не указан");
        }
    }

    private string BuildRunSummary(RunResult r)
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

        AppendFreshness(sb, s);

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

            sb.AppendLine($"  {f.DisplayName}: {f.FileSizeBytes / 1024.0 / 1024.0:N1} МБ, {f.Elapsed.TotalSeconds:N1} c" +
                          (f.LastModifiedUtc is { } fm ? $", изменён {fm.ToLocalTime():dd.MM.yyyy HH:mm}" : ""));
            sb.AppendLine($"      каталогов {f.CatalogCount:N0}, гео {f.GeoCount:N0}, офферов {f.CatalogOfferRefs:N0}, уникальных товаров {f.UniqueProducts():N0}");
        }

        return sb.ToString();
    }

    private static IEnumerable<FeedIssue> AllIssues(NetworkCheckResult r)
    {
        IEnumerable<FeedIssue> all = r.Run is null ? Enumerable.Empty<FeedIssue>()
            : r.Run.Files.SelectMany(f => f.Issues.Items).Concat(r.Run.Summary.NetworkIssues);
        if (r.Comparison is not null) all = all.Concat(r.Comparison.VisibleIssues);
        if (r.Frontier is not null) all = all.Concat(r.Frontier.Issues.Items);
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

    private void ClearFrontier()
    {
        _gridFrontier.DataSource = null;
        _lblFrontier.Text = "";
        _tabFrontier.Text = "Фронтир";
    }

    private void ApplyFrontierFilter()
    {
        if (_gridFrontier.DataSource is not DataTable table) return;
        table.DefaultView.RowFilter = _chkFrontierDiffs.Checked ? "[Статус] <> 'OK'" : "";
    }

    /// <summary>Вкладка «Фронтир»: по каждому гео — офферы и период в JSON и во Фронтире.</summary>
    private void FillFrontier(FrontierComparison? f)
    {
        if (f is null) { ClearFrontier(); return; }
        _tabFrontier.Text = $"Фронтир ({FrontierComparison.StateText(f.State)})";
        _lblFrontier.Text = f.Message;
        _lblFrontier.ForeColor = f.State == FrontierState.Updated ? Color.DarkGreen : Color.Firebrick;

        var table = new DataTable();
        foreach (var col in new[] { "Гео", "Файл JSON", "В JSON", "Во Фронтире", "Разница", "Период JSON", "Период Фронтира", "Статус", "Подробности" })
            table.Columns.Add(col, typeof(string));

        table.BeginLoadData();
        foreach (var r in f.Rows)
        {
            string diff = r.JsonOffers is { } j && r.FrontierOffers is { } fo ? (fo - j).ToString("+#,0;-#,0;0") : "—";
            string jsonPeriod = r.JsonStart is { } js ? js.ToString("yyyy-MM-dd") : "—";
            string frontierPeriod = r.FrontierStart is { } fs
                ? fs.ToString("yyyy-MM-dd") + (r.FrontierEnd is { } fe ? " – " + fe.ToString("yyyy-MM-dd") : "")
                : "—";
            table.Rows.Add(r.Name, r.Files, r.JsonOffers?.ToString("N0") ?? "—", r.FrontierOffers?.ToString("N0") ?? "—", diff,
                jsonPeriod, frontierPeriod, r.Status, r.Details);
        }
        table.EndLoadData();

        _gridFrontier.DataSource = table;
        ApplyFrontierFilter();
    }

    private void ClearGeoComparison()
    {
        _gridGeoCmp.DataSource = null;
        _lblGeoCmp.Text = "";
        _tabGeoCmp.Text = "Сравнение по гео";
    }

    private void ApplyGeoCmpFilter()
    {
        if (_gridGeoCmp.DataSource is not DataTable table) return;
        var parts = new List<string>();
        if (!_chkShowHidden.Checked) parts.Add("[Скрыто] = ''");
        if (_chkOnlyDiffs.Checked) parts.Add("[Статус] <> 'OK'");
        table.DefaultView.RowFilter = string.Join(" AND ", parts);
    }

    private static string DateText(DateTime? utc) => utc is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd") : "";

    /// <summary>Вкладка «Сравнение по гео»: уникальные офферы каждого магазина/региона сейчас и раньше, с историей появления и пропаж.</summary>
    private void FillGeoComparison(ComparisonResult? c)
    {
        if (c is null || c.GeoRows.Count == 0)
        {
            ClearGeoComparison();
            return;
        }

        var table = new DataTable();
        table.Columns.Add("Гео", typeof(string));
        table.Columns.Add("Уникальных сейчас", typeof(long));
        table.Columns.Add("Было", typeof(long));
        table.Columns.Add("Изменение", typeof(long));
        table.Columns.Add("Изменение %", typeof(double));
        table.Columns.Add(c.AverageCount >= 2 ? $"Среднее за {c.KeepDays} дн." : "Среднее за период", typeof(double));
        table.Columns.Add("Статус", typeof(string));
        table.Columns.Add("Впервые видели", typeof(string));
        table.Columns.Add("Последний раз", typeof(string));
        table.Columns.Add("Скрыто", typeof(string));
        table.BeginLoadData();
        foreach (var g in c.GeoRows)
        {
            table.Rows.Add(g.Name,
                (object?)g.Current ?? DBNull.Value,
                (object?)g.Previous ?? DBNull.Value,
                (object?)g.Delta ?? DBNull.Value,
                (object?)g.DeltaPercent ?? DBNull.Value,
                (object?)g.Average ?? DBNull.Value,
                g.Status,
                DateText(g.FirstSeenUtc),
                DateText(g.LastSeenUtc),
                g.Hidden ? "да" : "");
        }
        table.EndLoadData();

        _gridGeoCmp.DataSource = table;
        if (_gridGeoCmp.Columns.Count >= 10)
        {
            _gridGeoCmp.Columns[1].DefaultCellStyle.Format = "N0";
            _gridGeoCmp.Columns[2].DefaultCellStyle.Format = "N0";
            _gridGeoCmp.Columns[3].DefaultCellStyle.Format = "+#,0;-#,0;0";
            _gridGeoCmp.Columns[4].DefaultCellStyle.Format = "+0.0;-0.0;0.0";
            _gridGeoCmp.Columns[5].DefaultCellStyle.Format = "N1";
        }

        var visible = c.GeoRows.Where(x => !x.Hidden).ToList();
        int removed = visible.Count(x => x.Status == "Пропало");
        int dropped = visible.Count(x => x.Status == "Падение");
        int added = visible.Count(x => x.Status == "Новое");
        int back = visible.Count(x => x.Status == "Вернулось");
        int grown = visible.Count(x => x.Status == "Рост");
        int hidden = c.GeoRows.Count - visible.Count;

        _lblGeoCmp.Text = c.Previous is null
            ? "Это первая проверка сети: сравнивать пока не с чем."
            : $"Гео: {visible.Count:N0}. Пропало {removed}, упало {dropped}, новых {added}, вернулось {back}, выросло {grown}" +
              (hidden > 0 ? $". Скрыто: {hidden}" : "") + $". Порог: {Settings.GeoDropPercent:0.#}%";
        int warnings = removed + dropped + added;
        _tabGeoCmp.Text = warnings > 0 ? $"Сравнение по гео ({warnings})" : "Сравнение по гео";
        ApplyGeoCmpFilter();
    }

    /// <summary>Скрыть или вернуть выделенные гео. Пометка хранится в реестре гео сети и сохраняется между проверками.</summary>
    private void ToggleGeoHidden(bool hidden)
    {
        var net = Selected();
        if (net is null || !_results.TryGetValue(net.Id, out var r) || r.Comparison is null)
        {
            MessageBox.Show(this, "Сначала запустите проверку сети: скрывать можно гео из её результата.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var names = _gridGeoCmp.SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.Cells[0].Value as string)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            MessageBox.Show(this, "Выделите в таблице одну или несколько строк.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var registry = _history.LoadGeoRegistry(net.Id);
            foreach (var n in names)
                if (registry.Entries.TryGetValue(n, out var entry)) entry.Hidden = hidden;
            _history.SaveGeoRegistry(net.Id, registry);
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось сохранить пометку: " + ex.Message;
            return;
        }

        r.Comparison.SetHidden(names, hidden);
        ResultStore.SaveAsync(r);
        UpdateRowFromResult(r);
        ShowSelected();
        _status.Text = hidden
            ? $"Скрыто гео: {names.Count}. Увидеть их можно галочкой «Показывать скрытые», вернуть — кнопкой «Вернуть выбранные»."
            : $"Возвращено гео: {names.Count}.";
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
            Filter = "Excel (*.xlsx)|*.xlsx|CSV для Excel (*.csv)|*.csv|Только названия, по одному в строке (*.txt)|*.txt|JSON (*.json)|*.json",
            FileName = $"{SafeName(_shownNetwork)}_гео_{DateTime.Now:yyyyMMdd}",
            InitialDirectory = ExportDir(),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            ReportExporter.WriteGeo(dlg.FileName, _shownGeos);
            Settings.LastExportDir = Path.GetDirectoryName(dlg.FileName);
            SaveStorage();
            _status.Text = $"Гео выгружено ({_shownGeos.Count:N0} строк): {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось записать файл: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string BuildReportText(NetworkConfig net, NetworkCheckResult r)
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
        return sb.ToString();
    }

    /// <summary>Выгрузка расхождений сверки с Фронтиром: по выбранной сети или по всем проверенным в этой сессии.</summary>
    private void OnSaveFrontier(bool selectedOnly)
    {
        var withFrontier = _storage.Networks.Where(n => _results.TryGetValue(n.Id, out var res) && res.Frontier is not null).ToList();
        if (withFrontier.Count == 0)
        {
            MessageBox.Show(this, "Нет результатов сверки с Фронтиром. Укажите ID ретейлера в настройках сети («Изменить…») и запустите проверку.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        List<NetworkConfig> nets;
        var selected = Selected();
        bool selectedHas = selected is not null && withFrontier.Any(n => n.Id == selected.Id);
        if (selectedOnly || withFrontier.Count == 1)
        {
            if (selectedOnly && !selectedHas)
            {
                MessageBox.Show(this, "Для выбранной сети нет результата сверки с Фронтиром.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            nets = selectedHas ? new List<NetworkConfig> { selected! } : withFrontier;
        }
        else
        {
            var answer = MessageBox.Show(this,
                "Сохранить сверку с Фронтиром по всем сетям, проверенным в этой сессии?\n\nДа — по всем сетям\nНет — только по выбранной сети\nОтмена — ничего не делать",
                "Сохранить сверку с Фронтиром", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes) nets = withFrontier;
            else if (selectedHas) nets = new List<NetworkConfig> { selected! };
            else
            {
                MessageBox.Show(this, "Выберите в списке сеть, для которой есть результат сверки.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }

        var items = nets.Select(n => (Network: n.Name, Result: _results[n.Id].Frontier!)).ToList();
        if (items.Sum(i => i.Result.Problems) == 0)
        {
            MessageBox.Show(this, "Расхождений с Фронтиром нет — сохранять нечего.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string label = nets.Count == 1 ? SafeName(nets[0].Name) : "все_сети";
        using var dlg = new SaveFileDialog
        {
            Title = "Сохранить сверку с Фронтиром",
            Filter = "Excel (*.xlsx)|*.xlsx|CSV для Excel (*.csv)|*.csv|Текстовый файл (*.txt)|*.txt|JSON (*.json)|*.json",
            FileName = $"фронтир_{label}_{DateTime.Now:yyyyMMdd_HHmm}",
            InitialDirectory = ExportDir(),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            FrontierExporter.Write(dlg.FileName, items);
            Settings.LastExportDir = Path.GetDirectoryName(dlg.FileName);
            SaveStorage();
            _status.Text = "Сверка с Фронтиром сохранена: " + dlg.FileName;
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
            Filter = "Excel, книга с листами (*.xlsx)|*.xlsx|CSV для Excel (*.csv)|*.csv|Текстовый отчёт (*.txt)|*.txt|JSON (*.json)|*.json",
            FileName = $"{SafeName(net.Name)}_отчёт_{DateTime.Now:yyyyMMdd_HHmm}",
            InitialDirectory = ExportDir(),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            ReportExporter.WriteReport(dlg.FileName, net.Name, r, () => BuildReportText(net, r));
            Settings.LastExportDir = Path.GetDirectoryName(dlg.FileName);
            SaveStorage();
            _status.Text = "Отчёт сохранён: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось записать файл: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static long TotalIssueCount(NetworkCheckResult r)
    {
        long total = 0;
        if (r.Run is not null)
        {
            total += r.Run.Files.Sum(f => f.Issues.Totals.Values.Sum());
            total += r.Run.Summary.NetworkIssues.Count;
        }
        if (r.Comparison is not null) total += r.Comparison.Problems;
        if (r.Frontier is not null) total += r.Frontier.Problems;
        return total;
    }

    private void OnSaveErrors()
    {
        var withResults = _storage.Networks.Where(n => _results.TryGetValue(n.Id, out var res) && res.Run is not null).ToList();
        if (withResults.Count == 0)
        {
            MessageBox.Show(this, "Пока нет результатов проверки. Сначала запустите проверку.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        List<NetworkConfig> nets;
        var selected = Selected();
        bool selectedHasResult = selected is not null && withResults.Any(n => n.Id == selected.Id);
        if (withResults.Count > 1)
        {
            var answer = MessageBox.Show(this,
                "Сохранить ошибки по всем сетям, проверенным в этой сессии?\n\nДа — по всем сетям\nНет — только по выбранной сети\nОтмена — ничего не делать",
                "Сохранить ошибки", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes) nets = withResults;
            else if (selectedHasResult) nets = new List<NetworkConfig> { selected! };
            else
            {
                MessageBox.Show(this, "Выберите в списке сеть, для которой есть результат.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        else nets = withResults;

        var rows = new List<IssueRow>();
        long total = 0;
        foreach (var n in nets)
        {
            var r = _results[n.Id];
            total += TotalIssueCount(r);
            foreach (var i in AllIssues(r)) rows.Add(IssueExporter.ToRow(n.Name, i));
        }

        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Ошибок и предупреждений нет — сохранять нечего.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string label = nets.Count == 1 ? SafeName(nets[0].Name) : "все_сети";
        using var dlg = new SaveFileDialog
        {
            Title = "Сохранить ошибки",
            Filter = "Excel (*.xlsx)|*.xlsx|CSV для Excel (*.csv)|*.csv|Текстовый файл (*.txt)|*.txt|JSON (*.json)|*.json",
            FileName = $"ошибки_{label}_{DateTime.Now:yyyyMMdd_HHmm}",
            InitialDirectory = ExportDir(),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            IssueExporter.Write(dlg.FileName, rows);
            Settings.LastExportDir = Path.GetDirectoryName(dlg.FileName);
            SaveStorage();
            _status.Text = $"Ошибки сохранены ({rows.Count:N0} строк" +
                           (total > rows.Count ? $", всего найдено {total:N0}: подробно хранится не более 20 000 записей каждого типа" : "") +
                           $"): {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось записать файл: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
