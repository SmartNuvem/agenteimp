using System.Diagnostics;
using System.Drawing.Printing;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentConfigApp;

public class MainForm : Form
{
    private readonly ComboBox _instance = new() { Left = 180, Top = 20, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _api = new() { Left = 180, Top = 55, Width = 420 };
    private readonly TextBox _token = new() { Left = 180, Top = 90, Width = 420 };
    private readonly ComboBox _type = new() { Left = 180, Top = 125, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _mode = new() { Left = 180, Top = 160, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _printer = new() { Left = 180, Top = 195, Width = 420, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _transport = new() { Left = 180, Top = 230, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _ip = new() { Left = 180, Top = 265, Width = 220 };
    private readonly NumericUpDown _port = new() { Left = 500, Top = 265, Width = 100, Minimum = 1, Maximum = 65535, Value = 9100 };
    private readonly NumericUpDown _interval = new() { Left = 180, Top = 300, Width = 120, Minimum = 1000, Maximum = 120000, Value = 5000, Increment = 500 };

    public MainForm(string initialInstance)
    {
        Text = "SmartPedido Agent Config";
        Width = 660;
        Height = 470;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        _instance.Items.AddRange(["kitchen", "cashier"]);
        _type.Items.AddRange(["kitchen", "cashier"]);
        _mode.Items.AddRange(["pdf", "escpos"]);
        _transport.Items.AddRange(["usbRaw", "tcp9100"]);

        Controls.AddRange([
            LabelAt("Instância", 20), _instance,
            LabelAt("API Base URL", 55), _api,
            LabelAt("Token", 90), _token,
            LabelAt("Tipo", 125), _type,
            LabelAt("Modo impressão", 160), _mode,
            LabelAt("Impressora", 195), _printer,
            LabelAt("ESC/POS transporte", 230), _transport,
            LabelAt("ESC/POS IP", 265), _ip,
            LabelAt("Porta", 265, 440), _port,
            LabelAt("Intervalo (ms)", 300), _interval
        ]);

        var save = ButtonAt("Salvar", 20, 350, async (_, _) => { Save(); await RestartService(); });
        var test = ButtonAt("Testar conexão", 120, 350, async (_, _) => await TestConnection());
        var restart = ButtonAt("Reiniciar serviço", 260, 350, async (_, _) => await RestartService());
        var logs = ButtonAt("Abrir pasta de logs", 420, 350, (_, _) => OpenLogs());
        Controls.AddRange([save, test, restart, logs]);

        _instance.SelectedIndexChanged += (_, _) => LoadSelected();
        foreach (string p in PrinterSettings.InstalledPrinters) _printer.Items.Add(p);

        _instance.SelectedItem = initialInstance is "cashier" ? "cashier" : "kitchen";
    }

    private Label LabelAt(string text, int top, int left = 20) => new() { Text = text, Left = left, Top = top + 4, Width = 150 };

    private Button ButtonAt(string text, int left, int top, EventHandler onClick)
    {
        var btn = new Button { Text = text, Left = left, Top = top, Width = 130 };
        btn.Click += onClick;
        return btn;
    }

    private string CurrentInstance => (_instance.SelectedItem?.ToString() ?? "kitchen").ToLowerInvariant();
    private static string ConfigPath(string instance) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SmartPedido", instance, "config.json");

    private void LoadSelected()
    {
        var path = ConfigPath(CurrentInstance);
        if (!File.Exists(path))
        {
            _type.SelectedItem = CurrentInstance;
            _mode.SelectedItem = "pdf";
            _transport.SelectedItem = "usbRaw";
            return;
        }

        var cfg = JsonSerializer.Deserialize<AgentConfigUi>(File.ReadAllText(path)) ?? new AgentConfigUi();
        _api.Text = cfg.ApiBaseUrl;
        _token.Text = cfg.AgentToken;
        _type.SelectedItem = cfg.AgentType;
        _mode.SelectedItem = cfg.PrintMode;
        _printer.SelectedItem = cfg.PrinterName;
        _transport.SelectedItem = cfg.EscposTransport;
        _ip.Text = cfg.Ip;
        _port.Value = cfg.Port;
        _interval.Value = cfg.PollIntervalMs;
    }

    private void Save()
    {
        var path = ConfigPath(CurrentInstance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cfg = new AgentConfigUi
        {
            ApiBaseUrl = _api.Text.Trim(),
            AgentToken = _token.Text.Trim(),
            AgentType = CurrentInstance,
            PrintMode = _mode.SelectedItem?.ToString() ?? "pdf",
            PrinterName = _printer.SelectedItem?.ToString() ?? string.Empty,
            EscposTransport = _transport.SelectedItem?.ToString() ?? "usbRaw",
            Ip = _ip.Text.Trim(),
            Port = (int)_port.Value,
            PollIntervalMs = (int)_interval.Value
        };
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        MessageBox.Show("Configuração salva com sucesso.");
    }

    private async Task TestConnection()
    {
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(_api.Text.Trim().TrimEnd('/') + "/") };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("x-agent-token", _token.Text.Trim());
            var route = CurrentInstance == "kitchen"
                ? "api/agent/orders?status=PRINTING"
                : "api/agent/print-jobs?status=QUEUED&type=CASHIER_TABLE_SUMMARY";
            using var res = await client.GetAsync(route);
            MessageBox.Show($"Status: {(int)res.StatusCode} {res.StatusCode}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Falha: {ex.Message}");
        }
    }

    private async Task RestartService()
    {
        var service = CurrentInstance == "kitchen" ? "SmartPedidoAgent-Kitchen" : "SmartPedidoAgent-Cashier";
        await Task.Run(() =>
        {
            Process.Start(new ProcessStartInfo("sc.exe", $"stop {service}") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
            Process.Start(new ProcessStartInfo("sc.exe", $"start {service}") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
        });
        MessageBox.Show("Serviço reiniciado.");
    }

    private void OpenLogs()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SmartPedido", CurrentInstance, "logs");
        Directory.CreateDirectory(path);
        Process.Start("explorer.exe", path);
    }
}

public class AgentConfigUi
{
    public string ApiBaseUrl { get; set; } = "";
    public string AgentToken { get; set; } = "";
    public string AgentType { get; set; } = "kitchen";
    public string PrintMode { get; set; } = "pdf";
    public string PrinterName { get; set; } = "";
    public string EscposTransport { get; set; } = "usbRaw";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 9100;
    public int PollIntervalMs { get; set; } = 5000;
}
