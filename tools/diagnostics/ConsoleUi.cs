using Spectre.Console;
using Spectre.Console.Rendering;

namespace Diagnostics;

/// <summary>Non-interactive console output: the intro, help panel, and completion panel.</summary>
internal static class ConsoleUi
{
    public static void PrintIntro()
    {
        AnsiConsole.Write(new Rule("[bold cyan]Server Diagnostics[/]").LeftJustified());
        AnsiConsole.MarkupLine("[dim]Collecting server state into a single attachable file.[/]");
        AnsiConsole.WriteLine();
    }

    public static void PrintHelp()
    {
        var panel = new Panel(
            new Rows(
                new Markup("[bold]diagnostics[/]"),
                new Rule().RuleStyle("dim"),
                new Markup(
                    "\nCollects server build identity, logs, settings, installed mods, and live"
                ),
                new Markup(
                    "state into a single [white].zip[/] under [white]./diagnostics/[/] on the host."
                ),
                new Markup("\nRun with a TTY ([white]-it[/]) for the technical-details wizard.")
            )
        )
        {
            Header = new PanelHeader(" Server Diagnostics ", Justify.Center),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1),
        };
        AnsiConsole.Write(panel);
    }

    public static void PrintDone(string zipPath, bool interactive)
    {
        var hostPath = "./diagnostics/" + Path.GetFileName(zipPath);
        AnsiConsole.WriteLine();

        var lines = new List<IRenderable>
        {
            new Markup($"[green]Diagnostics written to[/] [white]{Markup.Escape(hostPath)}[/]"),
            new Markup("[dim]Attach this file to your support thread or GitHub issue.[/]"),
        };
        if (!interactive)
        {
            lines.Add(
                new Markup(
                    "[yellow]Note:[/] fill in the [white]Technical details to include[/] template inside report.md."
                )
            );
        }
        lines.Add(
            new Markup(
                $"[dim]If ./diagnostics isn't bind-mounted (bare deploy):[/] docker compose cp sdvd-server:{Markup.Escape(zipPath)} ."
            )
        );

        AnsiConsole.Write(
            new Panel(new Rows(lines))
            {
                Header = new PanelHeader(" Done ", Justify.Center),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Green),
                Padding = new Padding(2, 1),
            }
        );
    }
}
