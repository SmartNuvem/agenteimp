using System.Diagnostics;
using System.Security.Principal;

namespace AgentConfigApp;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (!IsAdministrator())
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args)
            };
            Process.Start(psi);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault()?.ToLowerInvariant() ?? "kitchen"));
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
