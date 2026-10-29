using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FeedCheckerGui;

/// <summary>
/// Окно входа во Фронтир во встроенном браузере (WebView2). Вы входите как обычно (под VPN);
/// как только Фронтир откроется, программа сохраняет cookie входа (зашифрованно) и окно закрывается.
/// </summary>
internal sealed class FrontierLoginForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Label _info = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        Text = "Загружаю браузер…",
    };
    private readonly Button _done = new() { Text = "Я вошёл — сохранить", AutoSize = true, Margin = new Padding(6) };
    private readonly string _baseUrl;
    private bool _saving;

    public FrontierLoginForm(string baseUrl)
    {
        _baseUrl = baseUrl.Trim().TrimEnd('/');
        Text = "Вход во Фронтир";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1100, 760);
        ShowInTaskbar = false;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 2, RowCount = 1 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_info, 0, 0);
        top.Controls.Add(_done, 1, 0);
        Controls.Add(_web);   // сначала «заполняющий» элемент, потом верхняя панель
        Controls.Add(top);

        _done.Click += async (_, _) => await SaveAsync();
        Shown += async (_, _) => await InitAsync();
    }

    public bool Saved { get; private set; }

    private async Task InitAsync()
    {
        try
        {
            Directory.CreateDirectory(FrontierSession.WebViewDir);
            var env = await CoreWebView2Environment.CreateAsync(null, FrontierSession.WebViewDir);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _info.Text = "Войдите (VPN должен быть включён). Окно закроется само, когда откроется Фронтир; если нет — нажмите кнопку справа.";
            _web.CoreWebView2.Navigate(_baseUrl + "/catalogs");
        }
        catch (Exception ex)
        {
            _info.Text = "Не удалось запустить встроенный браузер.";
            MessageBox.Show(this,
                "Не удалось запустить встроенный браузер (WebView2): " + ex.Message +
                "\n\nЕсли на компьютере нет WebView2 Runtime, установите «Evergreen Runtime» с сайта Microsoft.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_saving || !e.IsSuccess || e.HttpStatusCode < 200 || e.HttpStatusCode >= 300) return;
        try
        {
            string src = _web.CoreWebView2.Source;
            if (!Uri.TryCreate(src, UriKind.Absolute, out var cur) || !Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri)) return;
            if (!string.Equals(cur.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)) return; // ещё на странице входа
            await SaveAsync();
        }
        catch
        {
            // автоматическое сохранение не критично: остаётся кнопка
        }
    }

    private async Task SaveAsync()
    {
        if (_saving || _web.CoreWebView2 is null) return;
        _saving = true;
        try
        {
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(_baseUrl);
            string header = string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value));
            if (header.Length == 0)
            {
                _info.Text = "Cookie пока нет — войдите во Фронтир и нажмите кнопку ещё раз.";
                return;
            }

            FrontierSession.Save(header);
            Saved = true;
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            _info.Text = "Не удалось сохранить вход: " + ex.Message;
        }
        finally
        {
            _saving = false;
        }
    }
}
