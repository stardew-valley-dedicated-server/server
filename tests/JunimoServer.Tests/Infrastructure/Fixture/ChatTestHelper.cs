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
    /// Polls chat history until all keywords appear in messages with Seq > the active cursor.
    /// Uses sequence-based tracking to correctly handle duplicate response texts.
    ///
    /// <para>
    /// <paramref name="prepareIteration"/>, when set, runs at the start of each iteration and
    /// returns the cursor that iteration matches against (the resend path uses it to re-fire and
    /// re-cursor per poll, so a reply from an earlier send can't satisfy a later poll). Null keeps
    /// the fixed <paramref name="seqBefore"/>.
    /// </para>
    /// </summary>
    public async Task<bool> PollForKeywordsAsync(
        long seqBefore,
        string[] keywords,
        TimeSpan? timeout = null,
        Action<ChatHistoryResult>? onPoll = null,
        Func<Task<long>>? prepareIteration = null
    )
    {
        return await PollingHelper.WaitUntilAsync(
            WaitName.Polling_TestBase_WaitForChatMessageAfter,
            async () =>
            {
                var cursor = prepareIteration is null ? seqBefore : await prepareIteration();

                var chat = await _testBase.GameClient.GetChatHistory(20);
                if (chat?.Messages == null)
                {
                    return false;
                }

                onPoll?.Invoke(chat);
                var newMessages = chat.Messages.Where(m => m.Seq > cursor).ToList();
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
            {
                break;
            }

            if (attempt == 0)
            {
                Log(
                    $"Command '{command}' response not found, retrying (server may have been in day transition)"
                );
            }
        }

        Xunit.Assert.NotNull(chatHistory);
        Log($"Command: {command}");
        Log($"Response ({chatHistory.Messages.Count} messages):");
        foreach (var msg in chatHistory.Messages)
        {
            Log($"  {msg.Message}");
        }

        var allFound = AllChatKeywordsPresent(chatHistory, expectedContains);
        foreach (var expected in expectedContains)
        {
            if (
                !chatHistory.Messages.Any(m =>
                    m.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                Log($"  MISSING: '{expected}'");
            }
        }
        return allFound;
    }

    /// <summary>
    /// Outcome of a capture read (<see cref="ResendUntilResponseAsync"/> /
    /// <see cref="SendOnceAndCaptureAsync"/>). <see cref="ObservedReply"/> is the latest reply seen
    /// in the command's family (or the matching reply on success), or null if none appeared — which
    /// distinguishes an accepted command from a wrong-reason reply.
    /// </summary>
    public readonly record struct CommandResponse(bool Matched, string? ObservedReply)
    {
        public string Describe() =>
            ObservedReply is null
                ? "no reply observed (command accepted or no response sent)"
                : $"got reply \"{ObservedReply}\"";
    }

    /// <summary>
    /// Resends <paramref name="command"/> each poll until <paramref name="expectedContains"/>
    /// appears, capturing the latest reply in the command's family
    /// (<paramref name="replyFamilyPrefix"/>, e.g. "Can't move cabin") so a wrong-reason failure
    /// names the actual reply instead of recurring as an opaque miss.
    ///
    /// <para>
    /// IDEMPOTENT commands ONLY — resending a mutating command double-applies it. Used for
    /// <c>!cabin</c> rejection reads whose "obstacle" is a live, replicated farmer/state that can
    /// lag the client warp by a tick: a single send landing on a lagged tick sees no collision and
    /// the move is silently accepted, and there's no re-fire to recover. Single-shot mutating
    /// commands stay on <see cref="AssertResponseAsync"/>; a single-shot self-identifying read
    /// (where an early resend would be UNSAFE) uses <see cref="SendOnceAndCaptureAsync"/>.
    /// </para>
    /// </summary>
    public Task<CommandResponse> ResendUntilResponseAsync(
        string command,
        string expectedContains,
        string? replyFamilyPrefix = null,
        TimeSpan? timeout = null
    ) =>
        CaptureResponseAsync(
            command,
            () => SendAndSnapshotCursorAsync(command),
            expectedContains,
            replyFamilyPrefix,
            timeout
        );

    /// <summary>
    /// Sends <paramref name="command"/> ONCE, then polls until <paramref name="expectedContains"/>
    /// appears — same self-identifying <see cref="CommandResponse"/> as
    /// <see cref="ResendUntilResponseAsync"/>, but WITHOUT resending. Use when an early resend would
    /// permanently mask the rejection (e.g. an accepted <c>!cabin</c> relocates the cabin over its
    /// own footprint, so the validator's self-overlap guard skips the obstacle forever); such a site
    /// must gate the single send on the obstacle being server-visible beforehand.
    /// </summary>
    public Task<CommandResponse> SendOnceAndCaptureAsync(
        string command,
        string expectedContains,
        string? replyFamilyPrefix = null,
        TimeSpan? timeout = null
    )
    {
        // Memoize the cursor so the prepare hook sends only on the first poll and returns that same
        // cursor thereafter — the reply stays matchable without re-firing the command.
        long? fixedCursor = null;
        return CaptureResponseAsync(
            command,
            async () => fixedCursor ??= await SendAndSnapshotCursorAsync(command),
            expectedContains,
            replyFamilyPrefix,
            timeout
        );
    }

    /// <summary>Snapshots the pre-send seq cursor, sends <paramref name="command"/>, and returns the
    /// cursor — so a reply to this send reads as Seq &gt; cursor.</summary>
    private async Task<long> SendAndSnapshotCursorAsync(string command)
    {
        var chatBefore = await _testBase.GameClient.GetChatHistory(20);
        var cursor = chatBefore?.TotalReceived ?? 0;
        await _testBase.GameClient.SendChat(command);
        return cursor;
    }

    private async Task<CommandResponse> CaptureResponseAsync(
        string command,
        Func<Task<long>> sendAndSnapshotCursor,
        string expectedContains,
        string? replyFamilyPrefix,
        TimeSpan? timeout
    )
    {
        // Advanced by the prepare hook each poll to that send's cursor, so a captured reply
        // attributes to the current send, not a stale reply from a prior one.
        long cursor = 0;
        string? observedReply = null;

        var matched = await PollForKeywordsAsync(
            seqBefore: 0,
            keywords: new[] { expectedContains },
            timeout: timeout,
            onPoll: chat =>
            {
                var hit = chat
                    .Messages.Where(m => m.Seq > cursor)
                    .FirstOrDefault(m =>
                        m.Message.Contains(expectedContains, StringComparison.OrdinalIgnoreCase)
                    );
                if (hit != null)
                {
                    observedReply = hit.Message;
                    return;
                }

                // No expected match this poll: remember the latest family reply so a wrong-reason
                // failure (e.g. "blocked by terrain or object") names what actually came back.
                if (replyFamilyPrefix != null)
                {
                    var family = chat
                        .Messages.Where(m =>
                            m.Seq > cursor
                            && m.Message.Contains(
                                replyFamilyPrefix,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        .LastOrDefault();
                    if (family != null)
                    {
                        observedReply = family.Message;
                    }
                }
            },
            prepareIteration: async () => cursor = await sendAndSnapshotCursor()
        );

        var result = new CommandResponse(matched, observedReply);
        Log($"{command} → expected \"{expectedContains}\": {result.Describe()}");

        return result;
    }

    private static bool AllChatKeywordsPresent(ChatHistoryResult? chat, string[] keywords)
    {
        if (chat?.Messages == null)
        {
            return false;
        }

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
