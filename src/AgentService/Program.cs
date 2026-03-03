using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Serilog;

var instance = ResolveInstance(args);
var configPath = AgentPaths.GetConfigPath(instance);
var logPath = AgentPaths.GetLogPath(instance);
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = instance == "kitchen" ? "SmartPedidoAgent-Kitchen" : "SmartPedidoAgent-Cashier";
});

builder.Services.AddSerilog();
builder.Services.AddSingleton(new AgentRuntime(instance, configPath));
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<PrintEngine>();
builder.Services.AddSingleton<TemplateFormatter>();
builder.Services.AddHttpClient<SmartPedidoApiClient>((sp, client) =>
{
    var cfg = sp.GetRequiredService<ConfigStore>().Load();
    client.BaseAddress = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.Add("x-agent-token", cfg.AgentToken);
}).AddPolicyHandler(GetRetryPolicy());
builder.Services.AddHostedService<AgentWorker>();

var app = builder.Build();
app.Run();

static string ResolveInstance(string[] args)
{
    var fromArg = args.FirstOrDefault(a => a.StartsWith("--instance=", StringComparison.OrdinalIgnoreCase))?.Split('=')[1];
    var fromEnv = Environment.GetEnvironmentVariable("SMARTPEDIDO_INSTANCE");
    var resolved = (fromArg ?? fromEnv ?? "kitchen").ToLowerInvariant();
    return resolved is "cashier" ? "cashier" : "kitchen";
}

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() => HttpPolicyExtensions
    .HandleTransientHttpError()
    .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
    .WaitAndRetryAsync(5, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

record AgentRuntime(string Instance, string ConfigPath);

static class AgentPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SmartPedido");
    public static string GetConfigPath(string instance) => Path.Combine(Root, instance, "config.json");
    public static string GetLogPath(string instance) => Path.Combine(Root, instance, "logs", "agent-.log");
    public static string GetDbPath(string instance) => Path.Combine(Root, instance, "state.db");
}

public class AgentConfig
{
    public string ApiBaseUrl { get; set; } = "https://api.smartpedido.local";
    public string AgentToken { get; set; } = string.Empty;
    public string AgentType { get; set; } = "kitchen";
    public string PrintMode { get; set; } = "pdf";
    public string PrinterName { get; set; } = string.Empty;
    public string EscposTransport { get; set; } = "usbRaw";
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 9100;
    public int PollIntervalMs { get; set; } = 5000;
}

public class ConfigStore
{
    private readonly AgentRuntime _runtime;
    public ConfigStore(AgentRuntime runtime) => _runtime = runtime;

    public AgentConfig Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_runtime.ConfigPath)!);
        if (!File.Exists(_runtime.ConfigPath))
        {
            var cfg = new AgentConfig { AgentType = _runtime.Instance };
            Save(cfg);
            return cfg;
        }

        var json = File.ReadAllText(_runtime.ConfigPath);
        return JsonSerializer.Deserialize<AgentConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? new AgentConfig { AgentType = _runtime.Instance };
    }

    public void Save(AgentConfig config)
    {
        config.AgentType = _runtime.Instance;
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_runtime.ConfigPath, json);
    }
}

public class AgentWorker : BackgroundService
{
    private readonly ILogger<AgentWorker> _logger;
    private readonly ConfigStore _configStore;
    private readonly SmartPedidoApiClient _api;
    private readonly PrintEngine _printer;
    private readonly IdempotencyStore _state;

    public AgentWorker(ILogger<AgentWorker> logger, ConfigStore configStore, SmartPedidoApiClient api, PrintEngine printer, IdempotencyStore state)
    {
        _logger = logger;
        _configStore = configStore;
        _api = api;
        _printer = printer;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SmartPedido Agent started");
        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _configStore.Load();
            try
            {
                if (cfg.AgentType == "kitchen")
                    await ProcessKitchen(cfg, stoppingToken);
                else
                    await ProcessCashier(cfg, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Poll loop failed");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(cfg.PollIntervalMs, 1000)), stoppingToken);
        }
    }

    private const int MaxAckAttempts = 8;

    private async Task ProcessKitchen(AgentConfig cfg, CancellationToken ct)
    {
        await RetryPendingAcks("order", ct);
        var jobs = await _api.GetKitchenOrders(ct);
        foreach (var job in jobs)
        {
            if (!_state.TryClaim("order", job.Id)) continue;
            if (!await _printer.Print(job.Id, job.Payload, cfg, ct))
                continue;

            _state.MarkPrintedLocal("order", job.Id);
            await TryAcknowledgePrinted("order", job.Id, ct);
        }
    }

    private async Task ProcessCashier(AgentConfig cfg, CancellationToken ct)
    {
        await RetryPendingAcks("print-job", ct);
        var jobs = await _api.GetCashierJobs(ct);
        foreach (var job in jobs)
        {
            if (!_state.TryClaim("print-job", job.Id)) continue;
            if (!await _printer.Print(job.Id, job.Payload, cfg, ct))
                continue;

            _state.MarkPrintedLocal("print-job", job.Id);
            await TryAcknowledgePrinted("print-job", job.Id, ct);
        }
    }

    private async Task RetryPendingAcks(string kind, CancellationToken ct)
    {
        foreach (var pending in _state.GetPendingAcks(kind, MaxAckAttempts))
            await TryAcknowledgePrinted(kind, pending.Id, ct);
    }

    private async Task TryAcknowledgePrinted(string kind, string id, CancellationToken ct)
    {
        try
        {
            if (kind == "order")
                await _api.MarkOrderPrinted(id, ct);
            else
                await _api.MarkPrintJobPrinted(id, ct);

            _state.MarkAckedRemote(kind, id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ACK printed job {Kind}/{Id}", kind, id);
            _state.RegisterAckFailure(kind, id, ex.Message);
        }
    }
}

public record AgentJob(string Id, JsonElement Payload);

public class SmartPedidoApiClient
{
    private readonly HttpClient _client;
    private readonly ILogger<SmartPedidoApiClient> _logger;

    public SmartPedidoApiClient(HttpClient client, ILogger<SmartPedidoApiClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<AgentJob>> GetKitchenOrders(CancellationToken ct)
        => await GetJobs("api/agent/orders?status=PRINTING", ct);

    public async Task<IReadOnlyCollection<AgentJob>> GetCashierJobs(CancellationToken ct)
        => await GetJobs("api/agent/print-jobs?status=QUEUED&type=CASHIER_TABLE_SUMMARY", ct);

    private async Task<IReadOnlyCollection<AgentJob>> GetJobs(string path, CancellationToken ct)
    {
        using var response = await _client.GetAsync(path, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            _logger.LogError("Auth failure when requesting {Path}", path);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.GetProperty("data");
        var list = new List<AgentJob>();
        foreach (var item in root.EnumerateArray())
        {
            var id = ResolveJobId(item);
            list.Add(new AgentJob(id, item.Clone()));
        }

        return list;
    }

    public Task MarkOrderPrinted(string id, CancellationToken ct)
        => PostAsync(path: $"api/agent/orders/{id}/printed", ct);

    public Task MarkPrintJobPrinted(string id, CancellationToken ct)
        => PostAsync(path: $"api/agent/print-jobs/{id}/printed", ct);

    private async Task PostAsync(string path, CancellationToken ct)
    {
        using var response = await _client.PostAsync(path, null, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            _logger.LogError("Auth failure when posting {Path}", path);

        response.EnsureSuccessStatusCode();
    }

    private static string ResolveJobId(JsonElement item)
    {
        if (TryResolveProperty(item, "id", out var id) ||
            TryResolveProperty(item, "orderId", out id) ||
            TryResolveProperty(item, "uuid", out id) ||
            TryResolveProperty(item, "printJobId", out id))
            return id;

        var raw = item.GetRawText();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool TryResolveProperty(JsonElement item, string name, out string value)
    {
        if (item.TryGetProperty(name, out var property))
        {
            value = property.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
                return true;
        }

        value = string.Empty;
        return false;
    }
}

public class IdempotencyStore
{
    private readonly string _connectionString;

    public IdempotencyStore(AgentRuntime runtime)
    {
        var dbPath = AgentPaths.GetDbPath(runtime.Instance);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS jobs (
                    kind TEXT NOT NULL,
                    id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    print_attempts INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT NULL,
                    PRIMARY KEY(kind, id)
                );
                """;
            cmd.ExecuteNonQuery();
        }

        MigrateProcessedTable(conn);
        Cleanup(conn);
    }

    public bool TryClaim(string kind, string id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        var now = DateTimeOffset.UtcNow.ToString("O");
        cmd.CommandText = "INSERT OR IGNORE INTO jobs(kind, id, status, created_at, updated_at, print_attempts, last_error) VALUES($kind, $id, 'CLAIMED', $createdAt, $updatedAt, 0, NULL)";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$createdAt", now);
        cmd.Parameters.AddWithValue("$updatedAt", now);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void MarkPrintedLocal(string kind, string id)
        => UpdateStatus(kind, id, "PRINTED_LOCAL");

    public void MarkAckedRemote(string kind, string id)
        => UpdateStatus(kind, id, "ACKED_REMOTE");

    public void RegisterAckFailure(string kind, string id, string error)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET print_attempts = print_attempts + 1, updated_at = $updatedAt, last_error = $lastError WHERE kind = $kind AND id = $id AND status = 'PRINTED_LOCAL'";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$lastError", error);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyCollection<PendingAckJob> GetPendingAcks(string kind, int maxAttempts)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, print_attempts, updated_at FROM jobs WHERE kind = $kind AND status = 'PRINTED_LOCAL' AND print_attempts < $maxAttempts";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$maxAttempts", maxAttempts);

        var now = DateTimeOffset.UtcNow;
        var pending = new List<PendingAckJob>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var attempts = reader.GetInt32(1);
            var updatedAt = DateTimeOffset.Parse(reader.GetString(2));
            var backoffSeconds = Math.Min(Math.Pow(2, attempts), 300);
            if (updatedAt.AddSeconds(backoffSeconds) <= now)
                pending.Add(new PendingAckJob(id));
        }

        return pending;
    }

    private void UpdateStatus(string kind, string id, string status)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET status = $status, updated_at = $updatedAt, last_error = $lastError WHERE kind = $kind AND id = $id";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$lastError", DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void MigrateProcessedTable(SqliteConnection conn)
    {
        using var existsCmd = conn.CreateCommand();
        existsCmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'processed'";
        if (Convert.ToInt32(existsCmd.ExecuteScalar()) == 0) return;

        using (var migrateCmd = conn.CreateCommand())
        {
            migrateCmd.CommandText = """
                INSERT OR IGNORE INTO jobs(kind, id, status, created_at, updated_at, print_attempts, last_error)
                SELECT kind, id, 'ACKED_REMOTE', created_at, created_at, 0, NULL
                FROM processed;
                """;
            migrateCmd.ExecuteNonQuery();
        }

        using var dropCmd = conn.CreateCommand();
        dropCmd.CommandText = "DROP TABLE IF EXISTS processed";
        dropCmd.ExecuteNonQuery();
    }

    private void Cleanup(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE status = 'ACKED_REMOTE' AND updated_at < $limit";
        cmd.Parameters.AddWithValue("$limit", DateTimeOffset.UtcNow.AddDays(-7).ToString("O"));
        cmd.ExecuteNonQuery();
    }
}

public record PendingAckJob(string Id);

public class PrintEngine
{
    private readonly TemplateFormatter _formatter;
    private readonly ILogger<PrintEngine> _logger;

    public PrintEngine(TemplateFormatter formatter, ILogger<PrintEngine> logger)
    {
        _formatter = formatter;
        _logger = logger;
    }

    public async Task<bool> Print(string id, JsonElement payload, AgentConfig cfg, CancellationToken ct)
    {
        try
        {
            if (cfg.PrintMode.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            {
                var pdfPath = await SavePdfFromPayload(id, payload, ct);
                return PrintPdfSilently(pdfPath, cfg.PrinterName);
            }

            var text = _formatter.Format(payload, cfg.AgentType);
            var bytes = Encoding.UTF8.GetBytes(text + "\n\n\n");
            return cfg.EscposTransport == "tcp9100"
                ? PrintTcp(cfg.Ip, cfg.Port, bytes)
                : RawPrinterHelper.SendBytesToPrinter(cfg.PrinterName, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Print failed for {Id}", id);
            return false;
        }
    }

    private async Task<string> SavePdfFromPayload(string id, JsonElement payload, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"smartpedido-{id}.pdf");
        if (payload.TryGetProperty("pdfBase64", out var base64))
        {
            await File.WriteAllBytesAsync(tmp, Convert.FromBase64String(base64.GetString() ?? string.Empty), ct);
        }

        return tmp;
    }

    private bool PrintPdfSilently(string pdfPath, string printerName)
    {
        var sumatra = Path.Combine(AppContext.BaseDirectory, "sumatrapdf", "SumatraPDF.exe");
        if (!File.Exists(sumatra))
            throw new FileNotFoundException("SumatraPDF not found", sumatra);

        var args = $"-print-to \"{printerName}\" -silent \"{pdfPath}\"";
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(sumatra, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null) return false;
        if (!process.WaitForExit(30000))
        {
            process.Kill(true);
            return false;
        }

        return process.ExitCode == 0;
    }

    private bool PrintTcp(string ip, int port, byte[] bytes)
    {
        using var client = new System.Net.Sockets.TcpClient();
        client.Connect(ip, port);
        using var stream = client.GetStream();
        stream.Write(bytes, 0, bytes.Length);
        return true;
    }
}

public class TemplateFormatter
{
    public string Format(JsonElement payload, string type)
    {
        var sb = new StringBuilder();
        var table = payload.TryGetProperty("table", out var t) ? t.ToString() : string.Empty;
        if (!string.IsNullOrEmpty(table)) sb.AppendLine($"MESA {table}");

        if (payload.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                sb.AppendLine($"- {item.GetProperty("name").GetString()}");
                if (item.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                    foreach (var option in options.EnumerateArray()) sb.AppendLine($"  * {option.GetString()}");
                if (item.TryGetProperty("notes", out var notes)) sb.AppendLine($"  obs: {notes.GetString()}");
            }
        }

        if (type == "cashier")
        {
            sb.AppendLine("--- FECHAMENTO ---");
            if (payload.TryGetProperty("total", out var total)) sb.AppendLine($"TOTAL: {total}");
        }

        return sb.ToString();
    }
}

public static class RawPrinterHelper
{
    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern bool OpenPrinter(string szPrinter, out nint hPrinter, nint pd);

    [DllImport("winspool.Drv", SetLastError = true, ExactSpelling = true)]
    public static extern bool ClosePrinter(nint hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern bool StartDocPrinter(nint hPrinter, int level, [In] DOCINFOA di);

    [DllImport("winspool.Drv", SetLastError = true, ExactSpelling = true)]
    public static extern bool EndDocPrinter(nint hPrinter);

    [DllImport("winspool.Drv", SetLastError = true, ExactSpelling = true)]
    public static extern bool StartPagePrinter(nint hPrinter);

    [DllImport("winspool.Drv", SetLastError = true, ExactSpelling = true)]
    public static extern bool EndPagePrinter(nint hPrinter);

    [DllImport("winspool.Drv", SetLastError = true, ExactSpelling = true)]
    public static extern bool WritePrinter(nint hPrinter, nint pBytes, int dwCount, out int dwWritten);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName = "SmartPedido";
        [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile = string.Empty;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType = "RAW";
    }

    public static bool SendBytesToPrinter(string printerName, byte[] bytes)
    {
        if (!OpenPrinter(printerName, out var hPrinter, nint.Zero)) return false;
        try
        {
            var di = new DOCINFOA();
            if (!StartDocPrinter(hPrinter, 1, di)) return false;
            if (!StartPagePrinter(hPrinter)) return false;
            var unmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);
                var success = WritePrinter(hPrinter, unmanagedBytes, bytes.Length, out _);
                EndPagePrinter(hPrinter);
                EndDocPrinter(hPrinter);
                return success;
            }
            finally
            {
                Marshal.FreeCoTaskMem(unmanagedBytes);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }
}
