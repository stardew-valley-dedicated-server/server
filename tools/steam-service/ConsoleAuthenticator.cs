using SteamKit2.Authentication;

namespace SteamService;

/// <summary>
/// Console-based authenticator for Steam 2FA (Steam Guard).
/// Presents all available authentication options to the user.
/// </summary>
public class ConsoleAuthenticator : IAuthenticator
{
    private bool _hasShownDeviceConfirmation = false;
    private string? _pendingCode = null;

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
            Logger.Log("[Steam] Incorrect code, try again.");

        // If user already entered a code during device confirmation prompt, use it
        if (!string.IsNullOrEmpty(_pendingCode))
        {
            var code = _pendingCode;
            _pendingCode = null;
            return Task.FromResult(code);
        }

        Console.Write("Enter Steam Guard code from mobile app: ");
        var inputCode = Console.ReadLine()?.Trim() ?? "";
        return Task.FromResult(inputCode);
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
            Logger.Log("[Steam] Incorrect code, try again.");

        // If user already entered a code during device confirmation prompt, use it
        if (!string.IsNullOrEmpty(_pendingCode))
        {
            var code = _pendingCode;
            _pendingCode = null;
            return Task.FromResult(code);
        }

        Console.Write($"Enter Steam Guard code sent to {email}: ");
        var inputCode = Console.ReadLine()?.Trim() ?? "";
        return Task.FromResult(inputCode);
    }

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        // Only show this prompt once
        if (_hasShownDeviceConfirmation)
        {
            // Already accepted, continue waiting for mobile approval
            return Task.FromResult(true);
        }
        _hasShownDeviceConfirmation = true;

        Console.WriteLine();
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              Steam Guard Authentication                        ║");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════════╣");
        Console.WriteLine("║                                                               ║");
        Console.WriteLine("║  [1] Approve in Steam Mobile App (recommended)                ║");
        Console.WriteLine("║  [2] Enter code from Steam Mobile App or Email                ║");
        Console.WriteLine("║                                                               ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.Write("Choice [1]: ");

        var choice = Console.ReadLine()?.Trim();

        if (choice == "2")
        {
            // User wants to enter a code - prompt now
            Console.Write("Enter Steam Guard code: ");
            _pendingCode = Console.ReadLine()?.Trim() ?? "";

            // Return false to reject device confirmation
            // SteamKit2 will then call GetDeviceCodeAsync or GetEmailCodeAsync
            return Task.FromResult(false);
        }

        // Default: accept device confirmation, wait for mobile approval
        Logger.Log("[Steam] Waiting for approval on your Steam Mobile App...");
        Logger.Log("[Steam] Open Steam app on your phone and approve the login request.");
        return Task.FromResult(true);
    }
}
