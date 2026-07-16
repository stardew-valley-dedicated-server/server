using Spectre.Console;

namespace Diagnostics;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            ConsoleUi.PrintHelp();
            return 0;
        }

        ConsoleUi.PrintIntro();

        // With `docker compose exec -it` both streams are a PTY → interactive.
        // Without `-it` both are pipes → non-interactive (skip the wizard, write a template).
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        // Prompt first, then do the machine work in one burst — so the wizard wait doesn't sit
        // between the state capture and the report timestamp, keeping them within ~a second.
        var reported = interactive ? Wizard.Run() : null;

        var server = new ServerClient();
        var sidecarStatus = "not checked";
        await AnsiConsole
            .Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(
                "Reading live server state...",
                async ctx =>
                {
                    if (Config.ApiEnabled)
                    {
                        await server.CollectAsync(path =>
                            ctx.Status($"[dim]GET {Markup.Escape(path)}...[/]")
                        );
                    }
                    ctx.Status("[dim]Probing steam-auth sidecar...[/]");
                    sidecarStatus = await SteamAuthProbe.ProbeAsync();
                }
            );

        var report = new ReportBuilder(server, reported, sidecarStatus).Build();

        var zipPath = await AnsiConsole
            .Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(
                "Building diagnostics zip...",
                _ => Task.FromResult(ZipWriter.Write(report))
            );

        ConsoleUi.PrintDone(zipPath, interactive);
        return 0;
    }
}
