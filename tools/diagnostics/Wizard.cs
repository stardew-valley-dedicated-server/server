using Spectre.Console;

namespace Diagnostics;

/// <summary>The human's account of the problem — details the server can't observe on its own.</summary>
internal sealed class ReportedDetails
{
    public string? ClientMods { get; set; }
    public string? ClientModList { get; set; }
    public string? AffectedPlayer { get; set; }
    public string? Reproducibility { get; set; }
    public string? StartedAfterChange { get; set; }
}

/// <summary>Interactive prompts for the details a triager needs but the server can't collect.</summary>
internal static class Wizard
{
    public static ReportedDetails Run()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[cyan]A few technical details the server can't see on its own[/] [dim](all optional).[/]"
        );
        AnsiConsole.WriteLine();

        var details = new ReportedDetails
        {
            ClientMods = AskChoice(
                "Do you use [white]client-side mods[/]?",
                "No",
                "Yes",
                "Not sure"
            ),
        };
        if (details.ClientMods == "Yes")
        {
            details.ClientModList = AskText("Which ones ([white]name + version[/])?");
        }

        details.AffectedPlayer = AskText(
            "Which player is affected ([white]your name on the server[/]), and on what platform ([white]Steam / GOG / OS[/])?"
        );
        details.Reproducibility = AskChoice(
            "Does it happen [white]every time[/] or just [white]once[/]?",
            "Every time",
            "Once",
            "Not sure"
        );
        details.StartedAfterChange = AskText(
            "Did it start after a change ([white]mod added, update, setting[/])? (optional)"
        );

        return details;
    }

    /// <summary>
    /// Runs a selection prompt and echoes the answer, because Spectre's SelectionPrompt erases itself
    /// once chosen (unlike TextPrompt, which persists). Echoing keeps the full Q&amp;A visible.
    /// </summary>
    private static string AskChoice(string title, params string[] choices)
    {
        var answer = AnsiConsole.Prompt(
            new SelectionPrompt<string>().Title(title).AddChoices(choices)
        );
        AnsiConsole.MarkupLine($"{title} [green]{Markup.Escape(answer)}[/]");
        return answer;
    }

    /// <summary>Runs an optional free-text prompt. TextPrompt already persists its line on screen.</summary>
    private static string AskText(string prompt) =>
        AnsiConsole.Prompt(new TextPrompt<string>(prompt).AllowEmpty());
}
