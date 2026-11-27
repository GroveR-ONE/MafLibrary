using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

var dataRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("MAF_DATA") ?? Path.Combine(Directory.GetCurrentDirectory(), "data"));
var uploadsRoot = Path.Combine(dataRoot, "uploads");
Directory.CreateDirectory(uploadsRoot);
var uploadTempRoot = Path.Combine(uploadsRoot, ".staging");
Directory.CreateDirectory(uploadTempRoot);
var staleBefore = DateTime.UtcNow.AddDays(-1);
foreach (var stale in Directory.EnumerateFiles(uploadTempRoot, "*", SearchOption.TopDirectoryOnly)
    .Where(path => File.GetLastWriteTimeUtc(path) < staleBefore))
{
    try { File.Delete(stale); } catch (IOException) { }
}
var assetIoGate = new SemaphoreSlim(1, 1);
const long MaxUploadBytes = 100L * 1024 * 1024;
const long MaxImagePixels = 40_000_000;
// Snapshots from an interrupted backup must not leak into the next archive.
foreach (var stale in Directory.EnumerateFiles(dataRoot, "library-backup-*.db*"))
{
    try { File.Delete(stale); } catch (IOException) { }
}
var jsonFile = Path.Combine(dataRoot, "library.json");
var legacyJsonFile = Path.Combine(dataRoot, "library.json.old");
var dbFile = Path.Combine(dataRoot, "library.db");

// Read-only self-check used by tools/check-library.ps1. It never starts the web
// server and never writes to the catalog, so it is safe to run next to a live app.
if (args.Contains("--check-library", StringComparer.Ordinal))
{
    if (!File.Exists(dbFile))
    {
        Console.Error.WriteLine($"[MAF] check: нет файла базы {dbFile}");
        Environment.Exit(1);
    }

    var failures = new List<string>();
    var data = LibraryStore.Read(dbFile);
    using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = dbFile,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString()))
    {
        connection.Open();
        string Scalar(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar()) ?? "";
        }
        if (Scalar("PRAGMA integrity_check;") != "ok") failures.Add("integrity_check");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            using var reader = command.ExecuteReader();
            if (reader.Read()) failures.Add("foreign_key_check");
        }
        if (Scalar("SELECT value FROM meta WHERE key = 'initialized';") != "1") failures.Add("initialized marker");
    }

    var referencedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in data.Items)
    {
        if (string.IsNullOrWhiteSpace(item.OrderNumber) || string.IsNullOrWhiteSpace(item.ProductNumber))
        {
            failures.Add($"изделие {item.Id} без номера");
            continue;
        }
        if (item.Number != $"{item.OrderNumber}.{item.ProductNumber}")
            failures.Add($"изделие {item.Id}: номер {item.Number} не собран из частей");
        foreach (var asset in item.Assets)
        {
            var storedName = Path.GetFileName(asset.StoredName);
            referencedAssets.Add(storedName);
            var assetPath = Path.Combine(uploadsRoot, storedName);
            if (!File.Exists(assetPath))
                failures.Add($"нет файла {asset.StoredName} у изделия {item.Number}");
            else if (new FileInfo(assetPath).Length != asset.Size)
                failures.Add($"размер файла {asset.StoredName} не совпадает с базой");
        }
    }

    var uploadFiles = Directory.EnumerateFiles(uploadsRoot, "*", SearchOption.AllDirectories).ToArray();
    var orphanFiles = uploadFiles.Select(file => Path.GetRelativePath(uploadsRoot, file).Replace('\\', '/'))
        .Where(file => !referencedAssets.Contains(file)).ToArray();
    if (orphanFiles.Length > 0) Console.WriteLine($"[MAF] check: orphan_files={orphanFiles.Length} (не используются, не удалены)");
    Console.WriteLine($"[MAF] check: upload_files={uploadFiles.Length} referenced_assets={referencedAssets.Count}");

    var assets = data.Items.Sum(item => item.Assets.Count);
    Console.WriteLine($"[MAF] check: items={data.Items.Count} categories={data.Categories.Count} orders={data.Orders.Count} assets={assets}");
    if (failures.Count > 0)
    {
        foreach (var failure in failures) Console.Error.WriteLine($"[MAF] check: FAIL {failure}");
        Environment.Exit(1);
    }
    Console.WriteLine("[MAF] check: OK");
    Environment.Exit(0);
}

// The library runs over plain HTTP inside a trusted local network. Remote HTTPS
// can still be added later by a reverse proxy in front of the application.
var urls = Environment.GetEnvironmentVariable("MAF_URLS") ?? Environment.GetEnvironmentVariable("MAF_HTTP_URL") ?? "http://0.0.0.0:5080";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(urls);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 110 * 1024 * 1024);
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

Console.WriteLine($"[MAF] Listening: {urls}");
foreach (var address in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()).Where(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(item)))
    Console.WriteLine($"[MAF] LAN: http://{address}:{new Uri(urls.Split(';')[0]).Port}");

var pin = Environment.GetEnvironmentVariable("MAF_ADMIN_PIN") ?? "admin";
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var usersFile = Environment.GetEnvironmentVariable("MAF_USERS_FILE")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MafLibrary", "users.json");
var users = LoadWebUsers(usersFile);
var sessions = new ConcurrentDictionary<string, (string User, DateTime ExpiresUtc)>(StringComparer.Ordinal);
var loginFailures = new ConcurrentDictionary<string, (int Count, DateTime WindowStartUtc)>(StringComparer.Ordinal);

LibraryStore store;
try
{
    store = new LibraryStore(dbFile);
}
catch (Exception error)
{
    Console.Error.WriteLine($"[MAF] Не удалось открыть базу данных {dbFile}: {error.Message}");
    throw;
}

// First run: the database file has just been created empty. Import the JSON archive
// once, and only when there is no data. The initialized marker keeps a deliberately
// cleared catalog empty instead of restoring it from the old file on every start.
if (!store.IsInitialized())
{
    if (store.IsEmpty()) MigrateIfPresent(store, jsonFile, legacyJsonFile, jsonOptions);
    store.MarkInitialized();
}

bool Authorized(HttpRequest request) => request.Headers["X-Edit-Pin"] == pin;
string SafeName(string name) => Regex.Replace(Path.GetFileName(name), @"[^\p{L}\p{Nd}._() -]", "_");
void DeleteUpload(string storedName)
{
    var file = Path.Combine(uploadsRoot, Path.GetFileName(storedName));
    if (File.Exists(file)) File.Delete(file);
}

string? UploadSpaceError(long requiredBytes)
{
    try
    {
        var drive = new DriveInfo(Path.GetPathRoot(uploadsRoot)!);
        var reserveBytes = 256L * 1024 * 1024;
        if (drive.AvailableFreeSpace < requiredBytes + reserveBytes)
            return $"Недостаточно места на диске для безопасной загрузки. Свободно: {drive.AvailableFreeSpace / 1024 / 1024} МБ.";
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
    {
        return "Не удалось проверить свободное место на диске. Загрузка отменена.";
    }
    return null;
}

async Task<(string? Error, string Extension, string Kind, long Size, string TempPath)> StageUploadAsync(IFormFile file, CancellationToken cancellationToken)
{
    if (file.Length <= 0 || file.Length > MaxUploadBytes)
        return ("Файл пустой или превышает лимит 100 МБ.", "", "", 0, "");
    if (file.Length > int.MaxValue)
        return ("Файл слишком большой для обработки.", "", "", 0, "");

    var safeName = SafeName(file.FileName);
    var claimedExtension = Path.GetExtension(safeName).ToLowerInvariant();
    var ext = claimedExtension;
    var kind = "";
    var tempPath = Path.Combine(uploadTempRoot, Guid.NewGuid().ToString("N") + ".upload");
    var spaceError = UploadSpaceError(file.Length);
    if (spaceError is not null) return (spaceError, "", "", 0, "");
    var keepStagedFile = false;

    try
    {
        await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await file.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(true);
        }

        var actualSize = new FileInfo(tempPath).Length;
        if (actualSize != file.Length) return ("Размер принятого файла не совпадает с исходным. Загрузка отменена.", "", "", 0, "");

        var prefix = new byte[Math.Min(512, checked((int)actualSize))];
        await using (var input = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await input.ReadExactlyAsync(prefix, cancellationToken);

        if (prefix.Length >= 8 && prefix[0] == 0x89 && prefix.AsSpan(1, 7).SequenceEqual("PNG\r\n\x1a\n"u8)) { ext = ".png"; kind = "image"; }
        else if (prefix.Length >= 3 && prefix[0] == 0xff && prefix[1] == 0xd8 && prefix[2] == 0xff) { ext = ".jpg"; kind = "image"; }
        else if (prefix.AsSpan().StartsWith("GIF87a"u8) || prefix.AsSpan().StartsWith("GIF89a"u8)) { ext = ".gif"; kind = "image"; }
        else if (prefix.Length >= 12 && prefix.AsSpan(0, 4).SequenceEqual("RIFF"u8) && prefix.AsSpan(8, 4).SequenceEqual("WEBP"u8)) { ext = ".webp"; kind = "image"; }
        else if (prefix.AsSpan().StartsWith("%PDF-"u8)) { ext = ".pdf"; kind = "drawing"; }
        else if (claimedExtension is ".html" or ".htm" && LooksLikeHtml(prefix)) { ext = claimedExtension; kind = "model"; }
        else return ("Тип содержимого не распознан или не соответствует поддерживаемым файлам: PNG, JPG, WEBP, GIF, PDF, HTML.", "", "", 0, "");

        if (kind == "image" && !new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" }.Contains(claimedExtension, StringComparer.OrdinalIgnoreCase))
            return ("Расширение имени не соответствует содержимому изображения.", "", "", 0, "");
        if (kind == "drawing" && claimedExtension != ".pdf")
            return ("Расширение имени не соответствует содержимому PDF.", "", "", 0, "");

        if (kind == "image")
        {
            if (!TryReadRasterDimensions(tempPath, ext, out var width, out var height))
                return ("Не удалось проверить размеры изображения. Файл повреждён или имеет неподдерживаемую кодировку.", "", "", 0, "");
            if ((long)width * height > MaxImagePixels)
                return ("Изображение слишком большое: максимум 40 мегапикселей.", "", "", 0, "");
        }

        keepStagedFile = true;
        return (null, ext, kind, actualSize, tempPath);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (IOException)
    {
        return ("Не удалось записать временный файл. Проверьте свободное место и права доступа.", "", "", 0, "");
    }
    finally
    {
        if (!keepStagedFile && File.Exists(tempPath)) File.Delete(tempPath);
    }
}

static bool LooksLikeHtml(ReadOnlySpan<byte> bytes)
{
    var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
    return text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("<body", StringComparison.OrdinalIgnoreCase);
}

static bool TryReadRasterDimensions(string path, string extension, out int width, out int height)
{
    width = 0;
    height = 0;
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
    Span<byte> header = stackalloc byte[32];

    if (extension == ".png")
    {
        if (stream.Read(header[..24]) != 24) return false;
        width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
        return width > 0 && height > 0;
    }

    if (extension == ".gif")
    {
        if (stream.Read(header[..10]) != 10) return false;
        width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
        height = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2));
        return width > 0 && height > 0;
    }

    if (extension == ".webp")
    {
        if (stream.Read(header[..30]) < 30) return false;
        var chunk = System.Text.Encoding.ASCII.GetString(header.Slice(12, 4));
        if (chunk == "VP8X")
        {
            width = 1 + header[24] + (header[25] << 8) + (header[26] << 16);
            height = 1 + header[27] + (header[28] << 8) + (header[29] << 16);
            return width > 0 && height > 0;
        }
        if (chunk == "VP8L" && header[20] == 0x2f)
        {
            width = 1 + header[21] + ((header[22] & 0x3f) << 8);
            height = 1 + (header[22] >> 6) + (header[23] << 2) + ((header[24] & 0x0f) << 10);
            return width > 0 && height > 0;
        }
        if (chunk == "VP8 " && header[23] == 0x9d && header[24] == 0x01 && header[25] == 0x2a)
        {
            width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(26, 2)) & 0x3fff;
            height = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2)) & 0x3fff;
            return width > 0 && height > 0;
        }
        return false;
    }

    if (extension != ".jpg") return false;
    stream.Position = 2;
    var markerBytes = new byte[2];
    var scanned = 0;
    while (scanned < 1024 * 1024 && stream.Read(markerBytes, 0, 1) == 1)
    {
        scanned++;
        if (markerBytes[0] != 0xff) continue;
        do
        {
            if (stream.Read(markerBytes, 1, 1) != 1) return false;
            scanned++;
        } while (markerBytes[1] == 0xff && scanned < 1024 * 1024);

        var marker = markerBytes[1];
        if (marker is 0xd8 or 0xd9 or 0x01 or >= 0xd0 and <= 0xd7) continue;
        if (marker == 0xda) return false;
        if (stream.Read(markerBytes, 0, 2) != 2) return false;
        scanned += 2;
        var segmentLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(markerBytes);
        if (segmentLength < 2) return false;
        if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
        {
            if (stream.Read(header[..5]) != 5) return false;
            height = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header.Slice(1, 2));
            width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header.Slice(3, 2));
            return width > 0 && height > 0;
        }
        var skip = segmentLength - 2;
        stream.Seek(skip, SeekOrigin.Current);
        scanned += skip;
    }
    return false;
}

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isLoginApi = path.Equals("/api/session/login", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/session/logout", StringComparison.OrdinalIgnoreCase);
    var isPublicApi = path.Equals("/api/public/library", StringComparison.OrdinalIgnoreCase);
    var isPrivateStatic = path.Equals("/library.html", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/private-scripts")
        || path.Equals("/app.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/orders.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/categories.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/order-filter.js", StringComparison.OrdinalIgnoreCase);
    var isInternal = path.Equals("/library.html", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/orders.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/categories.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/order-filter.js", StringComparison.OrdinalIgnoreCase);
    var isPublicPage = path.Equals("/", StringComparison.Ordinal) || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/style.css", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/public.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/pdfjs/pdf.min.mjs", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/pdfjs/pdf.worker.min.mjs", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/login.html", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/fonts");
    var isPrivate = !isPublicPage && (path.Equals("/library", StringComparison.OrdinalIgnoreCase)
        || isPrivateStatic
        || path.Equals("/api/session/me", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/files")
        || path.StartsWithSegments("/api") && !isLoginApi && !isPublicApi);
    var staticCookie = context.Request.Cookies["maf-session"];
    if ((isInternal || isPrivateStatic && path.StartsWithSegments("/private-scripts"))
        && (staticCookie is null || !sessions.TryGetValue(staticCookie, out var staticUser) || staticUser.ExpiresUtc <= DateTime.UtcNow))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    if (!isPrivate)
    {
        if (path.Equals("/api/session/me", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (path.Equals("/", StringComparison.Ordinal) || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath!, "index.html"));
            return;
        }
        await next();
        return;
    }

    var token = context.Request.Cookies["maf-session"];
    if (token is not null && sessions.TryGetValue(token, out var session) && session.ExpiresUtc > DateTime.UtcNow)
    {
        context.Items["maf-user"] = session.User;
        await next();
        return;
    }
    if (token is not null) sessions.TryRemove(token, out _);

    if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin) && Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            && !originUri.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }

    if (path.Equals("/library", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/login.html", permanent: false);
        return;
    }
    if (path.Equals("/login.html", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath!, "login.html"));
        return;
    }
    if (path.Equals("/app.js", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.ContentType = "text/javascript; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath!, "app.js"));
        return;
    }
    if (path.Equals("/orders.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/categories.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/order-filter.js", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.ContentType = "text/javascript; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath!, path.Value!.TrimStart('/')));
        return;
    }
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new { error = "Требуется вход в полную библиотеку." });
});
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Request.Path.Equals("/app.js", StringComparison.OrdinalIgnoreCase)) context.Response.ContentType = "text/javascript; charset=utf-8";
        return Task.CompletedTask;
    });
    await next();
});

app.MapGet("/api/public/library", () =>
{
    var data = store.Load();
    var output = Results.Json(new
    {
        data.Categories,
        data.Orders,
        Items = data.Items.Select(item => new
        {
            item.Id, item.Number, item.Category, item.OrderNumber, item.ProductNumber,
            item.Name, item.Dimensions, item.Notes,
            Assets = item.Assets.Select(asset => new { asset.Id, asset.Name, asset.Kind, asset.Size, Url = $"/public-files/{Uri.EscapeDataString(Path.GetFileName(asset.StoredName))}" })
        })
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    return output;
});
app.MapGet("/public-files/{storedName}", (HttpContext context, string storedName) => ServeAsset(context, storedName));
app.MapGet("/api/library", () => Results.Ok(store.Load()));
app.MapPost("/api/auth", (HttpRequest request, PinRequest authRequest) =>
{
    if (request.HttpContext.Items["maf-user"] is null) return Results.Unauthorized();
    return authRequest.Pin == pin ? Results.Ok(new { ok = true }) : Results.Unauthorized();
});
app.MapGet("/private-scripts/{name}", (HttpContext context, string name) =>
{
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app.js", "orders.js", "categories.js", "order-filter.js" };
    if (!allowed.Contains(name)) return Results.NotFound();
    var token = context.Request.Cookies["maf-session"];
    if (token is null || !sessions.TryGetValue(token, out var session) || session.ExpiresUtc <= DateTime.UtcNow) return Results.Unauthorized();
    return Results.File(Path.Combine(app.Environment.WebRootPath!, name), "text/javascript; charset=utf-8");
});
app.MapPost("/api/session/login", async (HttpContext context, LoginRequest request) =>
{
    if (users.Count == 0) return Results.Problem("Персональные учётные записи не настроены на сервере.", statusCode: StatusCodes.Status503ServiceUnavailable);
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password) || request.Username.Length > 64 || request.Password.Length > 1024)
        return Results.Unauthorized();
    var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var now = DateTime.UtcNow;
    var failures = loginFailures.GetOrAdd(remote, _ => (0, now));
    if (now - failures.WindowStartUtc > TimeSpan.FromMinutes(15)) failures = (0, now);
    if (failures.Count >= 10) return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    if (!users.TryGetValue(request.Username ?? "", out var user) || !VerifyPassword(request.Password ?? "", user.PasswordHash))
    {
        loginFailures[remote] = (failures.Count + 1, failures.WindowStartUtc);
        return Results.Unauthorized();
    }
    loginFailures.TryRemove(remote, out _);
    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    sessions[token] = (user.Username, now.AddHours(8));
    context.Response.Cookies.Append("maf-session", token, new CookieOptions
    {
        HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
        Path = "/", MaxAge = TimeSpan.FromHours(8), IsEssential = true
    });
    return Results.Ok(new { ok = true });
}).DisableAntiforgery();
app.MapPost("/api/session/logout", (HttpContext context) =>
{
    var token = context.Request.Cookies["maf-session"];
    if (token is not null) sessions.TryRemove(token, out _);
    context.Response.Cookies.Delete("maf-session", new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, HttpOnly = true });
    return Results.Ok(new { ok = true });
});
app.MapGet("/api/session/me", (HttpContext context) => context.Items["maf-user"] is string username ? Results.Ok(new { username }) : Results.Unauthorized());
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/library", (HttpContext context) => Results.File(Path.Combine(app.Environment.WebRootPath!, "library.html"), "text/html; charset=utf-8"));
IResult ServeAsset(HttpContext context, string storedName)
{
    var path = Path.Combine(uploadsRoot, Path.GetFileName(storedName));
    if (!File.Exists(path)) return Results.NotFound();
    var ext = Path.GetExtension(path).ToLowerInvariant();
    var contentType = ext is ".html" or ".htm" ? "text/html; charset=utf-8" : ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", ".pdf" => "application/pdf", _ => "application/octet-stream" };
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "public, max-age=300";
    context.Response.Headers.ContentDisposition = "inline";
    if (ext is ".html" or ".htm") context.Response.Headers.ContentSecurityPolicy = "sandbox allow-scripts allow-forms; default-src data: blob: 'unsafe-inline' 'unsafe-eval'; img-src data: blob:; style-src data: blob: 'unsafe-inline'; font-src data: blob:; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
    return Results.File(path, contentType, enableRangeProcessing: true);
}
app.MapPost("/api/categories", (HttpRequest request, CategoryRequest input) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    var category = (input.Category ?? "").Trim();
    if (string.IsNullOrWhiteSpace(category)) return Results.BadRequest("Название категории обязательно.");
    store.AddCategory(category);
    return Results.Ok(new { category });
});
app.MapPost("/api/orders", (HttpRequest request, OrderInput input) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    var number = (input.Number ?? "").Trim();
    var name = (input.Name ?? "").Trim();
    if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(name)) return Results.BadRequest("Номер и название заказа обязательны.");
    store.AddOrder(number, name);
    return Results.Ok(new { number, name });
});
app.MapPut("/api/orders/{number}", (HttpRequest request, string number, OrderInput input) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    var updated = store.UpdateOrder(number, input.Name ?? "");
    if (updated is null) return Results.NotFound();
    return Results.Ok(updated);
});
app.MapDelete("/api/orders/{number}", (HttpRequest request, string number) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    return store.DeleteOrder(number) switch
    {
        StoreResult.NotFound => Results.NotFound(),
        StoreResult.Conflict => Results.Conflict("Заказ используется изделиями и не может быть удалён."),
        _ => Results.Ok()
    };
});
app.MapPut("/api/orders/{number}/number", (HttpRequest request, string number, OrderNumberInput input) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    var newNumber = (input.Number ?? "").Trim();
    if (string.IsNullOrWhiteSpace(newNumber)) return Results.BadRequest("Новый номер заказа обязателен.");
    var result = store.RenameOrderNumber(number, newNumber);
    if (result == StoreResult.NotFound) return Results.NotFound();
    if (result == StoreResult.Conflict) return Results.Conflict("Такой номер заказа уже существует.");
    var order = store.FindOrder(newNumber);
    return Results.Ok(order ?? new OrderReference { Number = newNumber });
});
app.MapPut("/api/categories/{category}", (HttpRequest request, string category, CategoryRequest input) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    var newName = (input.Category ?? "").Trim();
    if (string.IsNullOrWhiteSpace(newName)) return Results.BadRequest("Название категории обязательно.");
    var result = store.RenameCategory(category, newName);
    if (result == StoreResult.NotFound) return Results.NotFound();
    if (result == StoreResult.Conflict) return Results.Conflict("Такая категория уже существует.");
    return Results.Ok(new { category = newName });
});
app.MapDelete("/api/categories/{category}", (HttpRequest request, string category) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    return store.DeleteCategory(category) switch
    {
        StoreResult.NotFound => Results.NotFound(),
        StoreResult.Conflict => Results.Conflict("Категория используется изделиями и не может быть удалена."),
        _ => Results.Ok()
    };
});
app.MapDelete("/api/library", (HttpRequest request) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    assetIoGate.Wait();
    try
    {
    foreach (var stored in store.Clear()) DeleteUpload(stored);
    return Results.Ok(new { cleared = true });
    }
    finally { assetIoGate.Release(); }
});
app.MapPost("/api/items", (HttpRequest request, ItemInput input) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    var orderNumber = (input.OrderNumber ?? "").Trim();
    var productNumber = (input.ProductNumber ?? "").Trim();
    if (string.IsNullOrWhiteSpace(orderNumber) || string.IsNullOrWhiteSpace(productNumber)) return Results.BadRequest("Номер заказа и номер изделия обязательны.");
    var item = new Item
    {
        Id = Guid.NewGuid().ToString("N"),
        Category = (input.Category ?? "").Trim(),
        OrderNumber = orderNumber,
        ProductNumber = productNumber,
        Name = (input.Name ?? "").Trim(),
        Notes = (input.Notes ?? "").Trim(),
        Dimensions = (input.Dimensions ?? "").Trim(),
        SourcePath = (input.SourcePath ?? "").Trim(),
    };
    store.AddItem(item);
    return Results.Ok(item);
});
app.MapPut("/api/items/{id}", (HttpRequest request, string id, ItemInput input) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    var orderNumber = (input.OrderNumber ?? "").Trim();
    var productNumber = (input.ProductNumber ?? "").Trim();
    if (string.IsNullOrWhiteSpace(orderNumber) || string.IsNullOrWhiteSpace(productNumber)) return Results.BadRequest("Номер заказа и номер изделия обязательны.");
    var item = new Item
    {
        Id = id,
        Category = (input.Category ?? "").Trim(),
        OrderNumber = orderNumber,
        ProductNumber = productNumber,
        Name = (input.Name ?? "").Trim(),
        Notes = (input.Notes ?? "").Trim(),
        Dimensions = (input.Dimensions ?? "").Trim(),
        SourcePath = (input.SourcePath ?? "").Trim(),
    };
    if (!store.UpdateItem(item)) return Results.NotFound();
    return Results.Ok(store.GetItem(id) ?? item);
});
app.MapDelete("/api/items/{id}", (HttpRequest request, string id) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    assetIoGate.Wait();
    try
    {
    var stored = store.DeleteItem(id);
    if (stored is null) return Results.NotFound();
    foreach (var name in stored) DeleteUpload(name);
    return Results.Ok();
    }
    finally { assetIoGate.Release(); }
});
app.MapPost("/api/items/{id}/files", async (HttpRequest request, string id, IFormFile file) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (store.GetItem(id) is null) return Results.NotFound();
    await assetIoGate.WaitAsync();
    try
    {
        var staged = await StageUploadAsync(file, request.HttpContext.RequestAborted);
    if (staged.Error is not null)
    {
        if (staged.Error.StartsWith("Недостаточно места", StringComparison.OrdinalIgnoreCase)
            || staged.Error.StartsWith("Не удалось проверить свободное место", StringComparison.OrdinalIgnoreCase)
            || staged.Error.StartsWith("Не удалось записать временный файл", StringComparison.OrdinalIgnoreCase))
            return Results.Problem(staged.Error, statusCode: StatusCodes.Status507InsufficientStorage);
        return Results.BadRequest(staged.Error);
    }

    var name = SafeName(file.FileName);
    var ext = staged.Extension;
    var kind = staged.Kind;
    var stored = Guid.NewGuid().ToString("N") + ext;
    var path = Path.Combine(uploadsRoot, stored);
    try
    {
        // Staging and final paths share the same volume, so rename is atomic.
        File.Move(staged.TempPath, path);
    }
    catch (IOException)
    {
        if (File.Exists(staged.TempPath)) File.Delete(staged.TempPath);
        return Results.Problem("Не удалось завершить загрузку файла. Проверьте свободное место и права доступа.", statusCode: StatusCodes.Status507InsufficientStorage);
    }

    var asset = new Asset { Id = Guid.NewGuid().ToString("N"), Name = name, StoredName = stored, Kind = kind, Size = staged.Size };
    string? replaceKind = kind is "image" or "drawing" or "model" ? kind : null;
    List<string>? previous;
    try
    {
        previous = store.AddAsset(id, asset, replaceKind);
    }
    catch
    {
        if (File.Exists(path)) File.Delete(path);
        throw;
    }
    if (previous is null) { if (File.Exists(path)) File.Delete(path); return Results.NotFound(); }
    foreach (var oldStored in previous) DeleteUpload(oldStored);
    return Results.Ok(new { asset.Id, asset.Name, asset.StoredName, asset.Kind, asset.Size, originalName = name });
    }
    finally { assetIoGate.Release(); }
}).DisableAntiforgery();
app.MapDelete("/api/items/{id}/files/{assetId}", (HttpRequest request, string id, string assetId) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    assetIoGate.Wait();
    try
    {
    var stored = store.DeleteAsset(id, assetId);
    if (stored is null) return Results.NotFound();
    DeleteUpload(stored);
    return Results.Ok();
    }
    finally { assetIoGate.Release(); }
});
app.MapGet("/files/{storedName}", (HttpContext context, string storedName) =>
{
    return ServeAsset(context, storedName);
});
app.MapPost("/api/import-json", async (HttpRequest request, IFormFile file) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    await assetIoGate.WaitAsync();
    try
    {
    using var reader = new StreamReader(file.OpenReadStream());
    var text = await reader.ReadToEndAsync();
    // Old exports may still carry Number, Type and AnalogGroup. They are checked
    // before anything is written so a mismatched archive cannot corrupt the catalog.
    var legacy = ReadLegacyFields(text);
    if (legacy.Error is not null) return Results.BadRequest(legacy.Error);
    var imported = JsonSerializer.Deserialize<LibraryData>(text, jsonOptions);
    if (imported is null) return Results.BadRequest("JSON не распознан как экспорт библиотеки.");
    imported.Categories ??= new();
    imported.Orders ??= new();
    imported.Items ??= new();
    ApplyLegacyNumbers(imported, text);
    if (imported.Items.Any(item => string.IsNullOrWhiteSpace(item.OrderNumber) || string.IsNullOrWhiteSpace(item.ProductNumber)))
        return Results.BadRequest("В импорте есть изделия без номера заказа или номера изделия. Ничего не изменено.");
    var result = store.MergeImport(imported, uploadsRoot);
    return Results.Ok(new { read = imported.Items.Count, added = result.Added, duplicatesSkipped = result.Skipped });
    }
    finally { assetIoGate.Release(); }
}).DisableAntiforgery();
app.MapGet("/api/backup.zip", async (HttpContext httpContext) =>
{
    if (!Authorized(httpContext.Request)) return Results.Unauthorized();

    var snapshotPath = Path.Combine(dataRoot, $"library-backup-{Guid.NewGuid():N}.db");
    var archivePath = Path.Combine(dataRoot, $"library-backup-{Guid.NewGuid():N}.zip");
    try
    {
        await assetIoGate.WaitAsync();
        try
        {
            // Keep asset rows and upload files unchanged from the DB snapshot through
            // archive creation, and write to disk instead of retaining the ZIP in RAM.
            store.BackupTo(snapshotPath);
            var snapshot = LibraryStore.Read(snapshotPath);
            var assetSizes = snapshot.Items.SelectMany(item => item.Assets)
                .GroupBy(asset => Path.GetFileName(asset.StoredName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Size, StringComparer.OrdinalIgnoreCase);

            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(snapshotPath, "library.db", CompressionLevel.Fastest);
                if (File.Exists(legacyJsonFile)) archive.CreateEntryFromFile(legacyJsonFile, "library.json.old", CompressionLevel.Fastest);

                var json = archive.CreateEntry("library.json", CompressionLevel.Fastest);
                await using (var stream = json.Open()) await JsonSerializer.SerializeAsync(stream, snapshot, jsonOptions);

                foreach (var sourcePath in Directory.EnumerateFiles(uploadsRoot, "*", SearchOption.AllDirectories)
                    .Where(path => !Path.GetRelativePath(uploadsRoot, path).StartsWith(".staging\\", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var relative = Path.GetRelativePath(uploadsRoot, sourcePath).Replace('\\', '/');
                    var size = new FileInfo(sourcePath).Length;
                    if (assetSizes.TryGetValue(relative, out var expectedSize) && size != expectedSize)
                        throw new IOException($"Размер ассета изменился относительно снимка базы: {relative}");
                    archive.CreateEntryFromFile(sourcePath, $"uploads/{relative}", CompressionLevel.Fastest);
                }
            }
        }
        finally { assetIoGate.Release(); }

        httpContext.Response.OnCompleted(() =>
        {
            try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch (IOException) { }
            return Task.CompletedTask;
        });
        return Results.File(archivePath, "application/zip", $"maf-library-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip", enableRangeProcessing: true);
    }
    finally
    {
        foreach (var file in new[] { snapshotPath, snapshotPath + "-wal", snapshotPath + "-shm", snapshotPath + "-journal" })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
        }
    }
});

// The first-run migration reads either the current JSON file or the archived
// library.json.old, validates it the same way as a manual import, and writes it in
// one transaction. A failure stops startup instead of serving an empty catalog.
static void MigrateIfPresent(LibraryStore store, string jsonFile, string legacyJsonFile, JsonSerializerOptions jsonOptions)
{
    var source = File.Exists(jsonFile) ? jsonFile : File.Exists(legacyJsonFile) ? legacyJsonFile : null;
    if (source is null) return;

    var text = File.ReadAllText(source);
    var legacy = ReadLegacyFields(text);
    if (legacy.Error is not null)
        throw new InvalidOperationException($"Миграция из {Path.GetFileName(source)} остановлена: {legacy.Error}");

    var data = JsonSerializer.Deserialize<LibraryData>(text, jsonOptions)
        ?? throw new InvalidOperationException($"Файл {Path.GetFileName(source)} не распознан как экспорт библиотеки.");
    data.Categories ??= new();
    data.Orders ??= new();
    data.Items ??= new();
    ApplyLegacyNumbers(data, text);

    if (data.Items.Any(item => string.IsNullOrWhiteSpace(item.OrderNumber) || string.IsNullOrWhiteSpace(item.ProductNumber)))
        throw new InvalidOperationException($"В файле {Path.GetFileName(source)} есть изделия без номера заказа или номера изделия. Миграция остановлена.");

    store.ImportAll(data);
    Console.WriteLine($"[MAF] Migrated from {Path.GetFileName(source)}: {data.Items.Count} items, {data.Categories.Count} categories, {data.Orders.Count} orders");
}

// Old exports carried a full Number that could disagree with its two parts. When a
// stored item has only a Number, it is split once into order and product numbers.
static bool TrySplitLegacyNumber(string legacyNumber, out string orderNumber, out string productNumber)
{
    orderNumber = ""; productNumber = "";
    var text = (legacyNumber ?? "").Trim();
    var separator = text.IndexOf('.');
    if (separator <= 0 || separator == text.Length - 1) return false;
    orderNumber = text[..separator].Trim();
    productNumber = text[(separator + 1)..].Trim();
    return orderNumber.Length > 0 && productNumber.Length > 0 && orderNumber.All(char.IsDigit) && productNumber.All(char.IsDigit);
}

// Exports written by older versions still contain Number, Type and AnalogGroup.
// Those fields are not part of the catalog any more: they are inspected here so a
// value that would be silently lost is reported instead. Number-only records are
// split into their two parts with leading and trailing zeros kept intact.
static (string? Error, int Split) ReadLegacyFields(string text)
{
    JsonDocument document;
    try { document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
    catch (JsonException error) { return ($"JSON не разобран: {error.Message}", 0); }
    using (document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object) return ("Ожидался объект JSON с полями Items, Categories и Orders.", 0);
        var root = document.RootElement;
        var items = FindProperty(root, "items");
        if (items is null || items.Value.ValueKind != JsonValueKind.Array) return ("В файле нет списка изделий (Items).", 0);
        var split = 0;
        foreach (var item in items.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return ("Список изделий содержит неверную запись.", 0);
            var number = ReadString(item, "number");
            var orderNumber = ReadString(item, "orderNumber");
            var productNumber = ReadString(item, "productNumber");
            var hasParts = !string.IsNullOrWhiteSpace(orderNumber) && !string.IsNullOrWhiteSpace(productNumber);
            if (hasParts)
            {
                if (!string.IsNullOrWhiteSpace(number) && !number.Equals($"{orderNumber}.{productNumber}", StringComparison.Ordinal))
                    return ($"Изделие «{ReadString(item, "name")}»: полный номер {number} не совпадает с частями {orderNumber} и {productNumber}. Импорт остановлен.", 0);
            }
            else if (!string.IsNullOrWhiteSpace(number))
            {
                if (!TrySplitLegacyNumber(number, out var oldOrder, out var oldProduct) ||
                    (!string.IsNullOrWhiteSpace(orderNumber) && orderNumber != oldOrder) ||
                    (!string.IsNullOrWhiteSpace(productNumber) && productNumber != oldProduct))
                    return ($"Изделие «{ReadString(item, "name")}»: номер {number} нельзя однозначно разделить на номер заказа и номер изделия. Импорт остановлен.", 0);
                if (string.IsNullOrWhiteSpace(orderNumber) && string.IsNullOrWhiteSpace(productNumber)) split++;
            }
            else
            {
                return ($"Изделие «{ReadString(item, "name")}» не содержит номера. Импорт остановлен.", 0);
            }
            var type = ReadString(item, "type");
            if (!string.IsNullOrWhiteSpace(type)) return ($"Изделие «{ReadString(item, "name")}» содержит поле Type со значением «{type}», которое больше не поддерживается. Импорт остановлен.", 0);
            var analogGroup = ReadString(item, "analogGroup");
            if (!string.IsNullOrWhiteSpace(analogGroup)) return ($"Изделие «{ReadString(item, "name")}» содержит поле AnalogGroup со значением «{analogGroup}», которое больше не поддерживается. Импорт остановлен.", 0);
        }
        return (null, 0);
    }
}

// Copies the checked legacy Number into the two parts on a freshly deserialized
// item, so an old export with Number-only records still imports with zeros kept.
static void ApplyLegacyNumbers(LibraryData data, string text)
{
    JsonDocument document;
    try { document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
    catch (JsonException) { return; }
    using (document)
    {
        var items = document.RootElement.ValueKind == JsonValueKind.Object ? FindProperty(document.RootElement, "items") : null;
        if (items is null || items.Value.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        foreach (var element in items.Value.EnumerateArray())
        {
            if (index >= data.Items.Count) break;
            var item = data.Items[index++];
            if (!string.IsNullOrWhiteSpace(item.OrderNumber) && !string.IsNullOrWhiteSpace(item.ProductNumber)) continue;
            if (!TrySplitLegacyNumber(ReadString(element, "number"), out var orderNumber, out var productNumber)) continue;
            if (string.IsNullOrWhiteSpace(item.OrderNumber)) item.OrderNumber = orderNumber;
            if (string.IsNullOrWhiteSpace(item.ProductNumber)) item.ProductNumber = productNumber;
        }
    }
}

static JsonElement? FindProperty(JsonElement element, string name)
{
    foreach (var property in element.EnumerateObject())
        if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
    return null;
}

static string ReadString(JsonElement element, string name) =>
    FindProperty(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";

app.Run();

static Dictionary<string, WebUser> LoadWebUsers(string path)
{
    if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var document = JsonDocument.Parse(File.ReadAllText(path));
    var list = document.RootElement.ValueKind == JsonValueKind.Array
        ? JsonSerializer.Deserialize<List<WebUser>>(document.RootElement.GetRawText(), options) ?? new()
        : new List<WebUser> { JsonSerializer.Deserialize<WebUser>(document.RootElement.GetRawText(), options)! };
    return list.Where(user => !string.IsNullOrWhiteSpace(user.Username) && !string.IsNullOrWhiteSpace(user.PasswordHash))
        .ToDictionary(user => user.Username, StringComparer.OrdinalIgnoreCase);
}

static bool VerifyPassword(string password, string encoded)
{
    var parts = encoded.Split('$');
    if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations < 100_000 || iterations > 2_000_000) return false;
    try
    {
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
    catch (FormatException) { return false; }
}

sealed record WebUser(string Username, string PasswordHash);
