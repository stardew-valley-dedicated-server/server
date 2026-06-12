using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.Tests.Infrastructure.Fixture;

internal sealed class ChatTestHelper
{
    private readonly TestBase _testBase;
    private readonly string _displayName;

    public ChatTestHelper(TestBase testBase, string displayName)
    {
        _testBase = testBase;
        _displayName = displayName;
    }

    /// <summary>
    /// Polls chat history until all keywords appear in messages with Seq > seqBefore.
    /// Uses sequence-based tracking to correctly handle duplicate response texts.
    /// </summary>
    public async Task<bool> PollForKeywordsAsync(
        long seqBefore,
        string[] keywords,
        TimeSpan? timeout = null,
        Action<ChatHistoryResult>? onPoll = null
    )
    {
        return await PollingHelper.WaitUntilAsync(
            WaitName.Polling_TestBase_WaitForChatMessageAfter,
            async () =>
            {
                var chat = await _testBase.GameClient.GetChatHistory(20);
                if (chat?.Messages == null)
                    return false;
                onPoll?.Invoke(chat);
                var newMessages = chat.Messages.Where(m => m.Seq > seqBefore).ToList();
                return keywords.All(k =>
                    newMessages.Any(m => m.Message.Contains(k, StringComparison.OrdinalIgnoreCase))
                );
            },
            timeout ?? TestTimings.ChatCommandTimeout
        );
    }

    /// <summary>
    /// Sends a chat command and polls until ALL expected keywords appear in chat.
    /// Used for fire-and-forget commands where the response confirms processing.
    /// </summary>
    public async Task SendAndWaitAsync(string command, params string[] expectedContains)
    {
        var chatBefore = await _testBase.GameClient.GetChatHistory(20);
        var seqBefore = chatBefore?.TotalReceived ?? 0;
        await _testBase.GameClient.SendChat(command);
        await PollForKeywordsAsync(seqBefore, expectedContains);
    }

    /// <summary>
    /// Sends a chat command, polls for response keywords with retry on timeout
    /// (handles day-transition timing), logs the response, and returns whether
    /// all keywords were found. Retries once if first attempt times out.
    /// </summary>
    public async Task<bool> AssertResponseAsync(string command, params string[] expectedContains)
    {
        ChatHistoryResult? chatHistory = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var chatBefore = await _testBase.GameClient.GetChatHistory(20);
            var seqBefore = chatBefore?.TotalReceived ?? 0;
            await _testBase.GameClient.SendChat(command);
            var found = await PollForKeywordsAsync(
                seqBefore,
                expectedContains,
                onPoll: h => chatHistory = h
            );
            if (found)
                break;
            if (attempt == 0)
                Log(
                    $"Command '{command}' response not found, retrying (server may have been in day transition)"
                );
        }

        Xunit.Assert.NotNull(chatHistory);
        Log($"Command: {command}");
        Log($"Response ({chatHistory.Messages.Count} messages):");
        foreach (var msg in chatHistory.Messages)
            Log($"  {msg.Message}");

        var allFound = AllChatKeywordsPresent(chatHistory, expectedContains);
        foreach (var expected in expectedContains)
        {
            if (
                !chatHistory.Messages.Any(m =>
                    m.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)
                )
            )
                Log($"  MISSING: '{expected}'");
        }
        return allFound;
    }

    private static bool AllChatKeywordsPresent(ChatHistoryResult? chat, string[] keywords)
    {
        if (chat?.Messages == null)
            return false;
        return keywords.All(k =>
            chat.Messages.Any(m => m.Message.Contains(k, StringComparison.OrdinalIgnoreCase))
        );
    }

    private void Log(string message) =>
        SetupEventBus.EmitTestAnnotation(
            _displayName,
            AnnotationLevel.Info,
            AnnotationSource.Body,
            message
        );
}
