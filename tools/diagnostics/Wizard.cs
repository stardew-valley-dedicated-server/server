using Spectre.Console;

namespace Diagnostics;

/// <summary>The human's account of the problem — details the server can't observe on its own.</summary>
internal sealed class ReportedDetails
{
    public string? ClientMods { get; set; }
    public string? ClientModList { get; set; }
    public string? AffectedPlayer { get; set; }
    public string? Platforms { get; set; }
    public string? SharedSteamAccount { get; set; }
    public string? Hosting { get; set; }
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
            "Which player is affected ([white]your name on the server[/])?"
        );
        // Device platform is human-only knowledge: the server sees the transport (Steam / Galaxy /
        // LAN), and mobile ships with the Galaxy SDK, so a Galaxy connection is PC-GOG or iOS or
        // Android indistinguishably.
        details.Platforms = AskText(
            "Which platforms are the relevant clients on? ([white]e.g. PC-Steam, PC-GOG, iOS, Android, Switch[/])"
        );
        // Steam allows one live session per account: a client signing in with the server's account
        // logs the server out, so every reconnect fails with a still-valid invite code. Common enough
        // to ask up front — it explains a whole class of "connection failed" reports on its own.
        details.SharedSteamAccount = AskChoice(
            "Do you use the [white]same Steam account[/] for the server and for a game client?",
            "No — the server has its own Steam account",
            "Yes — the same account runs the server and plays",
            "Not sure",
            "Not using Steam"
        );
        details.Hosting = AskChoice(
            "Where is this server hosted relative to the players?",
            "Remote (VPS / cloud / different network)",
            "Same local network (LAN) as the players",
            "Mixed (some local, some remote)",
            "Not sure"
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
