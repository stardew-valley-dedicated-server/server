using System;
using System.Collections.Generic;
using System.Linq;

namespace JunimoServer.Services.ChatCommands;

/// <summary>
/// Buffers a chat command's per-line private-message output and flushes only one page
/// of it, so long command replies don't overflow the game's 10-message ChatBox FIFO.
/// Each line stays its own message, preserving the game's per-message prefix formatting.
/// </summary>
public class ChatResponseScope
{
    private readonly List<(long playerId, string line)> _buffer = new();
    private const int LinesPerPage = 8;

    public void BufferLine(long playerId, string line)
    {
        _buffer.Add((playerId, line));
    }

    public void Flush(Action<long, string> sendLine, int page, string commandForFooter)
    {
        // Group by target player (nearly always a single recipient).
        foreach (var group in _buffer.GroupBy(b => b.playerId))
        {
            var lines = group.Select(g => g.line).ToList();
            var totalPages = (int)Math.Ceiling(lines.Count / (double)LinesPerPage);
            var clampedPage = Math.Clamp(page, 1, Math.Max(1, totalPages));

            if (totalPages <= 1)
            {
                foreach (var line in lines)
                {
                    sendLine(group.Key, line);
                }

                continue;
            }

            var skip = (clampedPage - 1) * LinesPerPage;
            foreach (var line in lines.Skip(skip).Take(LinesPerPage))
            {
                sendLine(group.Key, line);
            }

            if (clampedPage < totalPages)
            {
                sendLine(
                    group.Key,
                    $"(page {clampedPage}/{totalPages} — {commandForFooter} --page {clampedPage + 1})"
                );
            }
            else
            {
                sendLine(group.Key, $"(page {clampedPage}/{totalPages})");
            }
        }

        _buffer.Clear();
    }
}
