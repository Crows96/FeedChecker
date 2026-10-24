using System.Globalization;

namespace FeedCheckerGui;

/// <summary>Маленькое окно «введите строку» (в WinForms готового нет).</summary>
internal static class InputBox
{
    public static string? Show(IWin32Window owner, string title, string prompt)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(540, 112),
        };
        var label = new Label { Text = prompt, Left = 12, Top = 12, AutoSize = true };
        var box = new TextBox { Left = 12, Top = 36, Width = 516 };
        var ok = new Button { Text = "OK", Left = 372, Top = 74, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", Left = 453, Top = 74, Width = 75, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}

/// <summary>Окно «Сеть»: название, источники (файлы/ссылки/txt со ссылками) и необязательный эталон.</summary>
internal sealed class NetworkDialog : Form
{
    private readonly TextBox _name = new();
    private readonly ListBox _sources = new();
    private readonly TextBox _geo = new();
    private readonly TextBox _unique = new();
    private readonly TextBox _avg = new();
    private readonly TextBox _tol = new();
    private readonly NetworkConfig _cfg;
    private readonly List<string> _otherNames;
    private readonly string _bucket;

    public NetworkConfig Result => _cfg;

    public NetworkDialog(NetworkConfig? existing, List<string> otherNames, string defaultBucket)
    {
        _cfg = existing ?? new NetworkConfig();
        _otherNames = otherNames;
        _bucket = defaultBucket;

        Text = existing is null ? "Новая сеть" : "Сеть: " + existing.Name;
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(660, 540);

        Controls.Add(new Label { Text = "Название сети:", Left = 12, Top = 14, AutoSize = true });
        _name.SetBounds(12, 36, 636, 24);
        Controls.Add(_name);

        Controls.Add(new Label { Text = "Источники (файлы или ссылки на фиды):", Left = 12, Top = 72, AutoSize = true });
        _sources.SetBounds(12, 94, 490, 190);
        _sources.SelectionMode = SelectionMode.MultiExtended;
        _sources.HorizontalScrollbar = true;
        Controls.Add(_sources);

        var btnFiles = new Button { Text = "Добавить файлы…", Left = 512, Top = 94, Width = 136 };
        var btnUrl = new Button { Text = "Добавить ссылку…", Left = 512, Top = 128, Width = 136 };
        var btnTxt = new Button { Text = "Загрузить txt…", Left = 512, Top = 162, Width = 136 };
        var btnS3 = new Button { Text = "Добавить S3-папку…", Left = 512, Top = 196, Width = 136 };
        var btnDel = new Button { Text = "Удалить выбранные", Left = 512, Top = 244, Width = 136 };
        Controls.AddRange(new Control[] { btnFiles, btnUrl, btnTxt, btnS3, btnDel });

        var grp = new GroupBox { Text = "Эталон (необязательно: пустое поле не проверяется)", Left = 12, Top = 296, Width = 636, Height = 180 };
        AddRow(grp, "Количество гео (регионов/магазинов):", _geo, 26);
        AddRow(grp, "Уникальных товаров в сети (всего):", _unique, 56);
        AddRow(grp, "Среднее уникальных товаров на гео:", _avg, 86);
        AddRow(grp, "Допуск, %:", _tol, 116);
        grp.Controls.Add(new Label
        {
            Text = "История за последние дни сравнивается всегда, эталон — это дополнительная ручная проверка.",
            Left = 12, Top = 148, AutoSize = true, ForeColor = SystemColors.GrayText,
        });
        Controls.Add(grp);

        var ok = new Button { Text = "Сохранить", Left = 462, Top = 494, Width = 90 };
        var cancel = new Button { Text = "Отмена", Left = 558, Top = 494, Width = 90, DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        // значения
        _name.Text = _cfg.Name;
        foreach (var s in _cfg.Sources) _sources.Items.Add(s);
        var b = _cfg.Baseline;
        _geo.Text = b.GeoCount?.ToString() ?? "";
        _unique.Text = b.UniqueProducts?.ToString() ?? "";
        _avg.Text = b.AvgUniquePerGeo?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        _tol.Text = b.TolerancePercent.ToString("0.##", CultureInfo.CurrentCulture);

        btnFiles.Click += (_, _) => OnAddFiles();
        btnUrl.Click += (_, _) =>
        {
            var url = InputBox.Show(this, "Добавить ссылку", "Ссылка на фид (http/https):");
            if (!string.IsNullOrWhiteSpace(url)) AddSource(url);
        };
        btnTxt.Click += (_, _) => OnAddTxt();
        btnS3.Click += (_, _) => OnAddS3();
        btnDel.Click += (_, _) =>
        {
            foreach (var item in _sources.SelectedItems.Cast<object>().ToList()) _sources.Items.Remove(item);
        };
        ok.Click += (_, _) => OnOk();
    }

    private static void AddRow(GroupBox g, string text, TextBox box, int y)
    {
        g.Controls.Add(new Label { Text = text, Left = 12, Top = y + 4, AutoSize = true });
        box.SetBounds(300, y, 140, 24);
        g.Controls.Add(box);
    }

    private void AddSource(string value)
    {
        value = value.Trim().Trim('"');
        if (value.Length == 0) return;
        foreach (var existing in _sources.Items)
            if (string.Equals(existing?.ToString(), value, StringComparison.OrdinalIgnoreCase)) return;
        _sources.Items.Add(value);
    }

    private void OnAddFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Выберите файлы фидов",
            Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) AddSource(f);
    }

    private void OnAddTxt()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Выберите txt со ссылками (по одной в строке)",
            Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var line in File.ReadAllLines(dlg.FileName))
        {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith('#')) continue;
            AddSource(s);
        }
    }

    private void OnAddS3()
    {
        var text = InputBox.Show(this, "Добавить S3-папку",
            "Папка в бакете (например: winelab/) или полный адрес вида s3://бакет/папка/");
        if (string.IsNullOrWhiteSpace(text)) return;

        string value = text.Trim();
        if (!value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_bucket))
            {
                Warn("Сначала укажите бакет в «Настройках» (раздел S3) или введите полный адрес s3://бакет/папка/");
                return;
            }
            value = $"s3://{_bucket}/{value.Trim('/')}/";
        }
        else if (!value.EndsWith('/') && !value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            value += "/"; // без «/» на конце считаем, что это папка
        }

        AddSource(value);
    }

    private static bool TryNum(string text, out double? value)
    {
        text = text.Trim().Replace(" ", "").Replace(',', '.');
        if (text.Length == 0) { value = null; return true; }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0)
        {
            value = d;
            return true;
        }
        value = null;
        return false;
    }

    private void OnOk()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0) { Warn("Введите название сети."); return; }
        if (_otherNames.Any(n => string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            Warn("Сеть с таким названием уже есть.");
            return;
        }

        if (!TryNum(_geo.Text, out var geo) || !TryNum(_unique.Text, out var uni) ||
            !TryNum(_avg.Text, out var avg) || !TryNum(_tol.Text, out var tol))
        {
            Warn("Числа в эталоне заданы неверно: нужны неотрицательные числа или пустое поле.");
            return;
        }

        _cfg.Name = name;
        _cfg.Sources = _sources.Items.Cast<object>().Select(o => o.ToString() ?? "").Where(s => s.Length > 0).ToList();
        _cfg.Baseline = new Baseline
        {
            GeoCount = geo is null ? null : (int)Math.Round(geo.Value),
            UniqueProducts = uni is null ? null : (long)Math.Round(uni.Value),
            AvgUniquePerGeo = avg,
            TolerancePercent = tol ?? 5,
        };
        DialogResult = DialogResult.OK;
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}

/// <summary>Окно «Настройки»: папки кэша и истории, пороги сравнения, доступ к S3.</summary>
internal sealed class SettingsDialog : Form
{
    private readonly AppSettings _s;
    private readonly TextBox _cache = new();
    private readonly TextBox _history = new();
    private readonly CheckBox _delete = new() { Text = "Удалять скачанные файлы после проверки" };
    private readonly NumericUpDown _days = new() { Minimum = 1, Maximum = 365 };
    private readonly NumericUpDown _tol = new() { Minimum = 0, Maximum = 100 };
    private readonly NumericUpDown _geoDrop = new() { Minimum = 0, Maximum = 100 };
    private readonly NumericUpDown _tz = new() { Minimum = -12, Maximum = 14 };

    private readonly TextBox _s3Endpoint = new();
    private readonly TextBox _s3Region = new();
    private readonly TextBox _s3Bucket = new();
    private readonly TextBox _s3Key = new();
    private readonly TextBox _s3Secret = new() { UseSystemPasswordChar = true };
    private readonly Button _s3Test = new() { Text = "Проверить подключение" };
    private readonly Label _s3Info = new() { AutoSize = true, MaximumSize = new Size(600, 0) };

    public SettingsDialog(AppSettings settings)
    {
        _s = settings;
        Text = "Настройки";
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 624);

        AddFolderRow("Папка кэша (скачанные фиды):", _cache, 14);
        AddFolderRow("Папка истории проверок:", _history, 76);

        _delete.SetBounds(12, 136, 400, 24);
        Controls.Add(_delete);

        AddNumberRow("Хранить историю, дней:", _days, 176, "");
        AddNumberRow("Допуск изменения по сети, %:", _tol, 208, "к прошлой проверке и к среднему за период");
        AddNumberRow("Порог падения офферов у гео, %:", _geoDrop, 240, "предупреждать, если у магазина/региона стало меньше на столько");
        AddNumberRow("Часовой пояс для «сегодня» (UTC+):", _tz, 272, "3 — Москва");

        var grp = new GroupBox { Text = "Хранилище S3 (Yandex Object Storage)", Left = 12, Top = 312, Width = 616, Height = 242 };
        AddS3Row(grp, "Адрес сервиса:", _s3Endpoint, 26);
        AddS3Row(grp, "Регион:", _s3Region, 56);
        AddS3Row(grp, "Бакет:", _s3Bucket, 86);
        AddS3Row(grp, "Идентификатор ключа:", _s3Key, 116);
        AddS3Row(grp, "Секретный ключ:", _s3Secret, 146);
        _s3Test.SetBounds(200, 182, 190, 28);
        grp.Controls.Add(_s3Test);
        _s3Info.Location = new Point(12, 214);
        grp.Controls.Add(_s3Info);
        Controls.Add(grp);

        var ok = new Button { Text = "OK", Left = 452, Top = 580, Width = 85 };
        var cancel = new Button { Text = "Отмена", Left = 543, Top = 580, Width = 85, DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        _cache.Text = _s.CacheDir;
        _history.Text = _s.HistoryDir;
        _delete.Checked = _s.DeleteDownloaded;
        _days.Value = Math.Clamp(_s.KeepHistoryDays, 1, 365);
        _tol.Value = (decimal)Math.Clamp(_s.NetworkTolerancePercent, 0, 100);
        _geoDrop.Value = (decimal)Math.Clamp(_s.GeoDropPercent, 0, 100);
        _tz.Value = Math.Clamp(_s.TimezoneHours, -12, 14);

        _s3Endpoint.Text = _s.S3.Endpoint;
        _s3Region.Text = _s.S3.Region;
        _s3Bucket.Text = _s.S3.Bucket;
        _s3Key.Text = _s.S3.AccessKeyId;
        if (!string.IsNullOrEmpty(_s.S3.SecretKeyEncrypted))
            _s3Info.Text = "Секретный ключ сохранён (в зашифрованном виде). Оставьте поле пустым, чтобы не менять его.";

        _s3Test.Click += (_, _) => OnTestS3();
        ok.Click += (_, _) => OnOk();
    }

    private void AddFolderRow(string caption, TextBox box, int top)
    {
        Controls.Add(new Label { Text = caption, Left = 12, Top = top, AutoSize = true });
        box.SetBounds(12, top + 22, 520, 24);
        Controls.Add(box);
        var browse = new Button { Text = "Обзор…", Left = 540, Top = top + 21, Width = 88 };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = box.Text, UseDescriptionForTitle = true, Description = caption };
            if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        };
        Controls.Add(browse);
    }

    private void AddNumberRow(string caption, NumericUpDown box, int top, string hint)
    {
        Controls.Add(new Label { Text = caption, Left = 12, Top = top + 3, AutoSize = true });
        box.SetBounds(270, top, 80, 24);
        Controls.Add(box);
        if (hint.Length > 0)
            Controls.Add(new Label { Text = hint, Left = 360, Top = top + 3, AutoSize = true, ForeColor = SystemColors.GrayText });
    }

    private static void AddS3Row(GroupBox g, string caption, TextBox box, int y)
    {
        g.Controls.Add(new Label { Text = caption, Left = 12, Top = y + 4, AutoSize = true });
        box.SetBounds(200, y, 400, 24);
        g.Controls.Add(box);
    }

    private async void OnTestS3()
    {
        string bucket = _s3Bucket.Text.Trim();
        string keyId = _s3Key.Text.Trim();
        string secret = _s3Secret.Text.Length > 0 ? _s3Secret.Text : _s.S3.GetSecret();
        if (bucket.Length == 0 || keyId.Length == 0 || secret.Length == 0)
        {
            _s3Info.Text = "Заполните бакет, идентификатор и секретный ключ.";
            return;
        }

        _s3Test.Enabled = false;
        _s3Info.Text = "Проверяю подключение…";
        try
        {
            var client = new S3Client(_s3Endpoint.Text, _s3Region.Text, keyId, secret);
            var listing = await Task.Run(() => client.ListAsync(bucket, "", CancellationToken.None));
            _s3Info.Text = $"Подключение успешно. Папок в бакете: {listing.Folders.Count}.";
        }
        catch (Exception ex)
        {
            _s3Info.Text = "Не получилось: " + ex.Message;
        }
        finally
        {
            _s3Test.Enabled = true;
        }
    }

    private void OnOk()
    {
        string cache = _cache.Text.Trim(), hist = _history.Text.Trim();
        if (cache.Length == 0 || hist.Length == 0)
        {
            MessageBox.Show(this, "Укажите обе папки.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(hist);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось создать папку: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _s.CacheDir = cache;
        _s.HistoryDir = hist;
        _s.DeleteDownloaded = _delete.Checked;
        _s.KeepHistoryDays = (int)_days.Value;
        _s.NetworkTolerancePercent = (double)_tol.Value;
        _s.GeoDropPercent = (double)_geoDrop.Value;
        _s.TimezoneHours = (int)_tz.Value;

        _s.S3.Endpoint = string.IsNullOrWhiteSpace(_s3Endpoint.Text) ? "storage.yandexcloud.net" : _s3Endpoint.Text.Trim();
        _s.S3.Region = string.IsNullOrWhiteSpace(_s3Region.Text) ? "ru-central1" : _s3Region.Text.Trim();
        _s.S3.Bucket = _s3Bucket.Text.Trim();
        _s.S3.AccessKeyId = _s3Key.Text.Trim();
        if (_s3Secret.Text.Length > 0) _s.S3.SetSecret(_s3Secret.Text);
        DialogResult = DialogResult.OK;
    }
}

/// <summary>Выбор папок S3, из которых нужно создать сети (одна папка — одна сеть).</summary>
internal sealed class S3ImportDialog : Form
{
    private readonly CheckedListBox _list = new() { CheckOnClick = true, IntegralHeight = false };

    public List<string> Selected { get; } = new();

    public S3ImportDialog(string bucket, List<string> folders, int alreadyAdded)
    {
        Text = "Сети из S3";
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 460);

        string note = alreadyAdded > 0 ? $"  (уже добавлено ранее: {alreadyAdded})" : "";
        Controls.Add(new Label
        {
            Text = $"Папки бакета «{bucket}». Каждая отмеченная станет отдельной сетью.{note}",
            Left = 12, Top = 12, AutoSize = true, MaximumSize = new Size(496, 0),
        });

        _list.SetBounds(12, 52, 496, 340);
        foreach (var f in folders) _list.Items.Add(f, true);
        Controls.Add(_list);

        var all = new Button { Text = "Отметить все", Left = 12, Top = 400, Width = 110 };
        var none = new Button { Text = "Снять все", Left = 128, Top = 400, Width = 110 };
        var ok = new Button { Text = "Добавить сети", Left = 304, Top = 422, Width = 112 };
        var cancel = new Button { Text = "Отмена", Left = 422, Top = 422, Width = 86, DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { all, none, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        all.Click += (_, _) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true); };
        none.Click += (_, _) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, false); };
        ok.Click += (_, _) =>
        {
            Selected.Clear();
            foreach (var item in _list.CheckedItems) Selected.Add(item.ToString() ?? "");
            Selected.RemoveAll(s => s.Length == 0);
            if (Selected.Count == 0)
            {
                MessageBox.Show(this, "Отметьте хотя бы одну папку.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
        };
    }
}
