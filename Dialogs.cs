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
    private readonly List<string> _buckets;

    public NetworkConfig Result => _cfg;

    public NetworkDialog(NetworkConfig? existing, List<string> otherNames, List<string> buckets)
    {
        _cfg = existing ?? new NetworkConfig();
        _otherNames = otherNames;
        _buckets = buckets;

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
        using var dlg = new S3AddDialog(_buckets);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is null) return;
        AddSource(dlg.Result);
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

/// <summary>Окно «Настройки»: общие параметры и доступ к S3 (один или два бакета).</summary>
internal sealed class SettingsDialog : Form
{
    private readonly AppSettings _s;
    private readonly TextBox _cache = new();
    private readonly TextBox _history = new();
    private readonly CheckBox _delete = new() { Text = "Удалять скачанные файлы после проверки" };
    private readonly NumericUpDown _days = new() { Minimum = 1, Maximum = 365 };
    private readonly NumericUpDown _geoDays = new() { Minimum = 1, Maximum = 3650 };
    private readonly NumericUpDown _tol = new() { Minimum = 0, Maximum = 100 };
    private readonly NumericUpDown _geoDrop = new() { Minimum = 0, Maximum = 100 };
    private readonly NumericUpDown _tz = new() { Minimum = -12, Maximum = 14 };
    private readonly S3Editor _s3a;
    private readonly S3Editor _s3b;

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
        ClientSize = new Size(660, 500);

        var tabs = new TabControl { Left = 8, Top = 8, Width = 644, Height = 440 };
        var general = new TabPage("Общие");
        var pageA = new TabPage("S3: бакет 1");
        var pageB = new TabPage("S3: бакет 2 (необязательно)");
        tabs.TabPages.Add(general);
        tabs.TabPages.Add(pageA);
        tabs.TabPages.Add(pageB);
        Controls.Add(tabs);

        AddFolderRow(general, "Папка кэша (скачанные фиды):", _cache, 14);
        AddFolderRow(general, "Папка истории проверок:", _history, 76);
        _delete.SetBounds(12, 136, 400, 24);
        general.Controls.Add(_delete);
        AddNumberRow(general, "Хранить историю, дней:", _days, 176, "");
        AddNumberRow(general, "Помнить гео, дней:", _geoDays, 208, "пропавший магазин/регион остаётся в списке «Пропало» столько дней");
        AddNumberRow(general, "Допуск изменения по сети, %:", _tol, 240, "к прошлой проверке и к среднему за период");
        AddNumberRow(general, "Порог падения офферов у гео, %:", _geoDrop, 272, "предупреждать, если у магазина/региона стало меньше на столько");
        AddNumberRow(general, "Часовой пояс для «сегодня» (UTC+):", _tz, 304, "3 — Москва");

        var existing = _s.S3Connections;
        _s3a = new S3Editor(pageA, existing.Count > 0 ? existing[0] : null, null, null,
            "Основной бакет. Если бакет один, второй вкладкой можно не пользоваться.");
        _s3b = new S3Editor(pageB, existing.Count > 1 ? existing[1] : null, existing.Count > 0 ? existing[0] : null,
            _s3a.EffectiveSecret,
            "Второй бакет. Оставьте поле «Бакет» пустым, если он не нужен. Если ключи те же, что у первого, секретный ключ вводить не нужно.");

        var ok = new Button { Text = "OK", Left = 470, Top = 458, Width = 85 };
        var cancel = new Button { Text = "Отмена", Left = 561, Top = 458, Width = 85, DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        _cache.Text = _s.CacheDir;
        _history.Text = _s.HistoryDir;
        _delete.Checked = _s.DeleteDownloaded;
        _days.Value = Math.Clamp(_s.KeepHistoryDays, 1, 365);
        _geoDays.Value = Math.Clamp(_s.GeoHistoryDays, 1, 3650);
        _tol.Value = (decimal)Math.Clamp(_s.NetworkTolerancePercent, 0, 100);
        _geoDrop.Value = (decimal)Math.Clamp(_s.GeoDropPercent, 0, 100);
        _tz.Value = Math.Clamp(_s.TimezoneHours, -12, 14);

        ok.Click += (_, _) => OnOk();
    }

    private void AddFolderRow(Control page, string caption, TextBox box, int top)
    {
        page.Controls.Add(new Label { Text = caption, Left = 12, Top = top, AutoSize = true });
        box.SetBounds(12, top + 22, 520, 24);
        page.Controls.Add(box);
        var browse = new Button { Text = "Обзор…", Left = 540, Top = top + 21, Width = 88 };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = box.Text, UseDescriptionForTitle = true, Description = caption };
            if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        };
        page.Controls.Add(browse);
    }

    private static void AddNumberRow(Control page, string caption, NumericUpDown box, int top, string hint)
    {
        page.Controls.Add(new Label { Text = caption, Left = 12, Top = top + 3, AutoSize = true });
        box.SetBounds(270, top, 80, 24);
        page.Controls.Add(box);
        if (hint.Length > 0)
            page.Controls.Add(new Label { Text = hint, Left = 360, Top = top + 3, AutoSize = true, ForeColor = SystemColors.GrayText });
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

        var a = _s3a.ToSettings();
        var b = _s3b.ToSettings();
        if (a is not null && b is not null && string.Equals(a.Bucket, b.Bucket, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "В обоих бакетах указано одно и то же имя. Для второго бакета введите другое имя или очистите поле.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _s.CacheDir = cache;
        _s.HistoryDir = hist;
        _s.DeleteDownloaded = _delete.Checked;
        _s.KeepHistoryDays = (int)_days.Value;
        _s.GeoHistoryDays = (int)_geoDays.Value;
        _s.NetworkTolerancePercent = (double)_tol.Value;
        _s.GeoDropPercent = (double)_geoDrop.Value;
        _s.TimezoneHours = (int)_tz.Value;

        var list = new List<S3Settings>();
        if (a is not null) list.Add(a);
        if (b is not null) list.Add(b);
        _s.S3Connections = list;
        DialogResult = DialogResult.OK;
    }
}

/// <summary>Поля одного подключения к S3 на вкладке настроек.</summary>
internal sealed class S3Editor
{
    private readonly TextBox _endpoint = new();
    private readonly TextBox _region = new();
    private readonly TextBox _bucket = new();
    private readonly TextBox _keyId = new();
    private readonly TextBox _secret = new() { UseSystemPasswordChar = true };
    private readonly Button _test = new() { Text = "Проверить подключение" };
    private readonly Label _info = new() { AutoSize = true, MaximumSize = new Size(600, 0) };
    private readonly S3Settings? _orig;
    private readonly Func<string, string>? _borrowSecret;

    public S3Editor(TabPage page, S3Settings? existing, S3Settings? prefillFrom, Func<string, string>? borrowSecret, string description)
    {
        _orig = existing;
        _borrowSecret = borrowSecret;
        var defaults = new S3Settings();

        page.Controls.Add(new Label { Text = description, Left = 12, Top = 10, AutoSize = true, MaximumSize = new Size(610, 0), ForeColor = SystemColors.GrayText });
        AddRow(page, "Адрес сервиса:", _endpoint, 58);
        AddRow(page, "Регион:", _region, 90);
        AddRow(page, "Бакет:", _bucket, 122);
        AddRow(page, "Идентификатор ключа:", _keyId, 154);
        AddRow(page, "Секретный ключ:", _secret, 186);
        _test.SetBounds(200, 226, 190, 28);
        page.Controls.Add(_test);
        _info.Location = new Point(12, 266);
        page.Controls.Add(_info);

        _endpoint.Text = existing?.Endpoint ?? prefillFrom?.Endpoint ?? defaults.Endpoint;
        _region.Text = existing?.Region ?? prefillFrom?.Region ?? defaults.Region;
        _bucket.Text = existing?.Bucket ?? "";
        _keyId.Text = existing?.AccessKeyId ?? prefillFrom?.AccessKeyId ?? "";
        if (!string.IsNullOrEmpty(existing?.SecretKeyEncrypted))
            _info.Text = "Секретный ключ сохранён (в зашифрованном виде). Оставьте поле пустым, чтобы не менять его.";

        _test.Click += async (_, _) => await TestAsync();
    }

    private static void AddRow(Control page, string caption, TextBox box, int y)
    {
        page.Controls.Add(new Label { Text = caption, Left = 12, Top = y + 4, AutoSize = true });
        box.SetBounds(200, y, 410, 24);
        page.Controls.Add(box);
    }

    /// <summary>Введённый секретный ключ, либо сохранённый (если идентификатор не менялся), либо взятый у первого бакета.</summary>
    public string EffectiveSecret(string keyId)
    {
        if (_secret.Text.Length > 0) return _secret.Text;
        if (_orig is not null && _orig.AccessKeyId == keyId) return _orig.GetSecret();
        return _borrowSecret?.Invoke(keyId) ?? "";
    }

    private string EffectiveSecret() => EffectiveSecret(_keyId.Text.Trim());

    /// <summary>Подключение из введённых полей; null — если бакет не указан (вкладка не используется).</summary>
    public S3Settings? ToSettings()
    {
        string bucket = _bucket.Text.Trim();
        if (bucket.Length == 0) return null;
        var s = new S3Settings
        {
            Endpoint = string.IsNullOrWhiteSpace(_endpoint.Text) ? "storage.yandexcloud.net" : _endpoint.Text.Trim(),
            Region = string.IsNullOrWhiteSpace(_region.Text) ? "ru-central1" : _region.Text.Trim(),
            Bucket = bucket,
            AccessKeyId = _keyId.Text.Trim(),
        };
        string secret = EffectiveSecret(s.AccessKeyId);
        if (secret.Length > 0) s.SetSecret(secret);
        return s;
    }

    private async Task TestAsync()
    {
        string bucket = _bucket.Text.Trim();
        string keyId = _keyId.Text.Trim();
        string secret = EffectiveSecret();
        if (bucket.Length == 0 || keyId.Length == 0 || secret.Length == 0)
        {
            _info.Text = "Заполните бакет, идентификатор и секретный ключ.";
            return;
        }

        _test.Enabled = false;
        _info.Text = "Проверяю подключение…";
        try
        {
            var client = new S3Client(_endpoint.Text, _region.Text, keyId, secret);
            var listing = await Task.Run(() => client.ListAsync(bucket, "", CancellationToken.None));
            _info.Text = $"Подключение успешно. Папок в бакете: {listing.Folders.Count}.";
        }
        catch (Exception ex)
        {
            _info.Text = "Не получилось: " + ex.Message;
        }
        finally
        {
            _test.Enabled = true;
        }
    }
}

/// <summary>Добавление S3-папки в сеть: выбор бакета и имя папки (или полный адрес s3://…).</summary>
internal sealed class S3AddDialog : Form
{
    private readonly ComboBox _bucket = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _folder = new();

    public string? Result { get; private set; }

    public S3AddDialog(List<string> buckets)
    {
        Text = "Добавить S3-папку";
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(540, 170);

        Controls.Add(new Label { Text = "Бакет:", Left = 12, Top = 14, AutoSize = true });
        _bucket.SetBounds(12, 34, 516, 24);
        foreach (var b in buckets) _bucket.Items.Add(b);
        if (_bucket.Items.Count > 0) _bucket.SelectedIndex = 0;
        Controls.Add(_bucket);

        Controls.Add(new Label
        {
            Text = "Папка в бакете (например: winelab/) или полный адрес вида s3://бакет/папка/",
            Left = 12, Top = 70, AutoSize = true,
        });
        _folder.SetBounds(12, 92, 516, 24);
        Controls.Add(_folder);

        var ok = new Button { Text = "Добавить", Left = 372, Top = 128, Width = 75 };
        var cancel = new Button { Text = "Отмена", Left = 453, Top = 128, Width = 75, DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => OnOk();
    }

    private void OnOk()
    {
        string text = _folder.Text.Trim();
        if (text.Length == 0)
        {
            MessageBox.Show(this, "Введите папку или адрес.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string value = text;
        if (!value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            if (_bucket.SelectedItem is not string bucket)
            {
                MessageBox.Show(this, "Бакеты не настроены. Укажите их в «Настройках» или введите полный адрес s3://бакет/папка/",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            value = $"s3://{bucket}/{text.Trim('/')}/";
        }

        if (!value.EndsWith('/') && !value.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) value += "/";
        Result = value;
        DialogResult = DialogResult.OK;
    }
}

/// <summary>Выбор папок S3, из которых нужно создать сети (одна папка — одна сеть).</summary>
internal sealed class S3ImportDialog : Form
{
    private readonly CheckedListBox _list = new() { CheckOnClick = true, IntegralHeight = false };
    private readonly List<(string Bucket, string Folder)> _items;

    public List<(string Bucket, string Folder)> Selected { get; } = new();

    public S3ImportDialog(List<(string Bucket, string Folder)> items, int alreadyAdded, string? warning)
    {
        _items = items;
        Text = "Сети из S3";
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 500);

        string note = alreadyAdded > 0 ? $"  (уже добавлено ранее: {alreadyAdded})" : "";
        Controls.Add(new Label
        {
            Text = "Каждая отмеченная папка станет отдельной сетью." + note,
            Left = 12, Top = 12, AutoSize = true, MaximumSize = new Size(536, 0),
        });

        _list.SetBounds(12, 40, 536, 340);
        foreach (var (bucket, folder) in items) _list.Items.Add($"{bucket}  /  {folder}", true);
        Controls.Add(_list);

        if (!string.IsNullOrEmpty(warning))
            Controls.Add(new Label { Text = warning, Left = 12, Top = 386, AutoSize = true, MaximumSize = new Size(536, 0), ForeColor = Color.Firebrick });

        var all = new Button { Text = "Отметить все", Left = 12, Top = 460, Width = 110 };
        var none = new Button { Text = "Снять все", Left = 128, Top = 460, Width = 110 };
        var ok = new Button { Text = "Добавить сети", Left = 334, Top = 460, Width = 112 };
        var cancel = new Button { Text = "Отмена", Left = 452, Top = 460, Width = 96, DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { all, none, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        all.Click += (_, _) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true); };
        none.Click += (_, _) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, false); };
        ok.Click += (_, _) =>
        {
            Selected.Clear();
            foreach (int index in _list.CheckedIndices) Selected.Add(_items[index]);
            if (Selected.Count == 0)
            {
                MessageBox.Show(this, "Отметьте хотя бы одну папку.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
        };
    }
}
