using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace FeedCheckerGui;

// ===================== Настройки S3 и шифрование секретного ключа =====================

public sealed class S3Settings
{
    public string Endpoint { get; set; } = "storage.yandexcloud.net";
    public string Region { get; set; } = "ru-central1";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    /// <summary>Секретный ключ, зашифрованный средствами Windows (DPAPI) для текущего пользователя.</summary>
    public string SecretKeyEncrypted { get; set; } = "";

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrEmpty(SecretKeyEncrypted);

    public string GetSecret() => Secrets.Unprotect(SecretKeyEncrypted);

    public void SetSecret(string secret) => SecretKeyEncrypted = Secrets.Protect(secret);
}

internal static class Secrets
{
    public static string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    /// <summary>Пустая строка, если ключ не расшифровывается (например, файл настроек перенесён на другой компьютер).</summary>
    public static string Unprotect(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(base64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException) { return ""; }
        catch (FormatException) { return ""; }
    }
}

// ===================== Адрес вида s3://бакет/папка/ =====================

public static class S3Path
{
    public static bool TryParse(string value, out string bucket, out string key)
    {
        bucket = "";
        key = "";
        if (!value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase)) return false;
        string rest = value[5..];
        int slash = rest.IndexOf('/');
        bucket = slash < 0 ? rest : rest[..slash];
        key = slash < 0 ? "" : rest[(slash + 1)..];
        return bucket.Length > 0;
    }
}

// ===================== Подпись запросов AWS Signature V4 =====================

internal static class SigV4
{
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Кодирование по RFC 3986: всё, кроме A-Z a-z 0-9 - _ . ~, превращается в %XX (UTF-8).</summary>
    public static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            bool unreserved = b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';
            if (unreserved) sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string EncodePath(string path) => string.Join("/", path.Split('/').Select(Encode));

    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join("&", query
            .Select(kv => (Name: Encode(kv.Key), Value: Encode(kv.Value)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + p.Value));

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    /// <summary>Возвращает значение заголовка Authorization. Остальные заголовки (x-amz-date, x-amz-content-sha256) вызывающий ставит сам.</summary>
    public static string Sign(string method, string host, string canonicalPath, string canonicalQuery,
        IReadOnlyDictionary<string, string>? extraHeaders, string payloadHash, DateTime utcNow,
        string region, string service, string accessKey, string secretKey, out string amzDate)
    {
        amzDate = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        if (extraHeaders is not null)
            foreach (var kv in extraHeaders) headers[kv.Key.ToLowerInvariant()] = kv.Value.Trim();

        string canonicalHeaders = string.Concat(headers.Select(kv => kv.Key + ":" + kv.Value + "\n"));
        string signedHeaders = string.Join(";", headers.Keys);
        string canonicalRequest = string.Join("\n", method, canonicalPath, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);

        string scope = $"{date}/{region}/{service}/aws4_request";
        string stringToSign = "AWS4-HMAC-SHA256\n" + amzDate + "\n" + scope + "\n" +
                              Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        byte[] key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        string signature = Hex(Hmac(key, stringToSign));

        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }
}

// ===================== Клиент S3 (чтение: список и скачивание) =====================

public sealed record S3Object(string Key, long Size);

public sealed class S3Listing
{
    public List<S3Object> Files { get; } = new();
    /// <summary>Подпапки (прямые), с завершающим «/».</summary>
    public List<string> Folders { get; } = new();
}

public sealed class S3Exception : Exception
{
    public int Status { get; }
    public string Code { get; }

    public S3Exception(int status, string code, string message) : base(Compose(code, message))
    {
        Status = status;
        Code = code;
    }

    private static string Compose(string code, string message)
    {
        string hint = code switch
        {
            "AccessDenied" => "нет прав на чтение (проверьте роль storage.viewer и имя бакета)",
            "SignatureDoesNotMatch" => "неверный секретный ключ или регион",
            "InvalidAccessKeyId" => "неверный идентификатор ключа",
            "NoSuchBucket" => "бакет не найден",
            "NoSuchKey" => "файл не найден",
            "RequestTimeTooSkewed" => "время на компьютере сильно отличается от серверного",
            "Timeout" => "сервер не ответил вовремя",
            _ => "",
        };
        string text = $"S3: {code}";
        if (hint.Length > 0) text += $" — {hint}";
        if (!string.IsNullOrWhiteSpace(message) && message != code) text += $" ({message})";
        return text;
    }
}

public sealed class S3Client
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly string _scheme;
    private readonly string _authority;
    private readonly string _region;
    private readonly string _accessKey;
    private readonly string _secretKey;

    private static HttpClient CreateHttp()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FeedChecker/1.0");
        return client;
    }

    public S3Client(string endpoint, string region, string accessKey, string secretKey)
    {
        endpoint = endpoint.Trim().TrimEnd('/');
        string scheme = "https";
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { scheme = "http"; endpoint = endpoint[7..]; }
        else if (endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) endpoint = endpoint[8..];

        _scheme = scheme;
        _authority = new Uri($"{scheme}://{endpoint}").Authority; // без порта по умолчанию, как отправит HttpClient
        _region = string.IsNullOrWhiteSpace(region) ? "ru-central1" : region.Trim();
        _accessKey = accessKey.Trim();
        _secretKey = secretKey;
    }

    public static S3Client FromSettings(S3Settings s) => new(s.Endpoint, s.Region, s.AccessKeyId, s.GetSecret());

    private Task<HttpResponseMessage> SendAsync(string bucket, string key,
        IReadOnlyList<KeyValuePair<string, string>> query, HttpCompletionOption option, CancellationToken ct)
    {
        string path = "/" + bucket + (key.Length > 0 ? "/" + key : "");
        string canonicalPath = SigV4.EncodePath(path);
        string canonicalQuery = SigV4.CanonicalQuery(query);
        string auth = SigV4.Sign("GET", _authority, canonicalPath, canonicalQuery, null, SigV4.EmptyPayloadHash,
            DateTime.UtcNow, _region, "s3", _accessKey, _secretKey, out string amzDate);

        string url = $"{_scheme}://{_authority}{canonicalPath}" + (canonicalQuery.Length > 0 ? "?" + canonicalQuery : "");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", SigV4.EmptyPayloadHash);
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        return Http.SendAsync(req, option, ct);
    }

    private static S3Exception ErrorFrom(HttpStatusCode status, string body)
    {
        string code = status.ToString(), message = "";
        try
        {
            var root = XDocument.Parse(body).Root;
            code = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value ?? code;
            message = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value ?? "";
        }
        catch { /* ответ не в формате XML — оставляем код статуса */ }
        return new S3Exception((int)status, code, message);
    }

    private static string? Val(XElement e, string name) =>
        e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value;

    /// <summary>Прямое содержимое «папки» (префикса): файлы и подпапки. Листинг постраничный, страницы собираются автоматически.</summary>
    public async Task<S3Listing> ListAsync(string bucket, string prefix, CancellationToken ct)
    {
        var result = new S3Listing();
        string? token = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("list-type", "2"),
                new("delimiter", "/"),
            };
            if (prefix.Length > 0) query.Add(new("prefix", prefix));
            if (token is not null) query.Add(new("continuation-token", token));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            string xml;
            try
            {
                using var resp = await SendAsync(bucket, "", query, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                xml = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw ErrorFrom(resp.StatusCode, xml);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new S3Exception(0, "Timeout", "Сервер не ответил за 60 секунд");
            }
            catch (HttpRequestException ex)
            {
                throw new S3Exception(0, "Network", "Не удалось подключиться: " + ex.Message);
            }

            var root = XDocument.Parse(xml).Root!;
            foreach (var c in root.Elements().Where(e => e.Name.LocalName == "Contents"))
            {
                string key = Val(c, "Key") ?? "";
                long.TryParse(Val(c, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size);
                if (key.Length == 0 || key == prefix || key.EndsWith('/')) continue; // «маркер папки»
                result.Files.Add(new S3Object(key, size));
            }

            foreach (var p in root.Elements().Where(e => e.Name.LocalName == "CommonPrefixes"))
            {
                var v = Val(p, "Prefix");
                if (!string.IsNullOrEmpty(v)) result.Folders.Add(v);
            }

            bool truncated = string.Equals(Val(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);
            token = truncated ? Val(root, "NextContinuationToken") : null;
        }
        while (token is not null);

        return result;
    }

    /// <summary>Файлы .json из S3-папки (только прямые, без вложенных папок), как источники для проверки.</summary>
    public async Task<List<FeedSource>> ListFeedSourcesAsync(string folderUri, CancellationToken ct)
    {
        if (!S3Path.TryParse(folderUri, out var bucket, out var prefix))
            throw new InvalidOperationException("Неверный адрес S3: " + folderUri);
        var listing = await ListAsync(bucket, prefix, ct).ConfigureAwait(false);
        return listing.Files
            .Where(f => f.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new FeedSource($"s3://{bucket}/{f.Key}"))
            .ToList();
    }

    /// <summary>Скачивает файл в папку кэша потоком. До трёх попыток при обрывах связи и ошибках сервера 5xx.</summary>
    public async Task<string> DownloadAsync(string bucket, string key, string cacheDir, IProgress<double>? progress, CancellationToken ct,
        Action<DateTimeOffset?>? onLastModified = null)
    {
        Directory.CreateDirectory(cacheDir);
        string target = FeedDownloader.CachePathFor($"s3://{bucket}/{key}", cacheDir);
        string temp = target + ".part";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await SendAsync(bucket, key, Array.Empty<KeyValuePair<string, string>>(),
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw ErrorFrom(resp.StatusCode, body);
                }

                onLastModified?.Invoke(resp.Content.Headers.LastModified);
                long? total = resp.Content.Headers.ContentLength;
                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        if (total is > 0) progress?.Report(Math.Min(1.0, done / (double)total.Value));
                    }
                }

                File.Move(temp, target, overwrite: true);
                progress?.Report(1.0);
                return target;
            }
            catch (Exception ex) when (attempt < 3 && (ex is HttpRequestException or IOException || ex is S3Exception { Status: >= 500 }))
            {
                TryDelete(temp);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* не критично */ }
    }
}
