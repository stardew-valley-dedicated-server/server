using System.Text.RegularExpressions;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley.GameData.Weddings;

namespace JunimoTestClient.GameTweaks;

/// <summary>
/// Caps the wedding script's scripted <c>pause</c> holds so a rendered ceremony plays faster. Every
/// command still executes (the test guards against SKIPPING commands, which this doesn't do). The
/// vanilla <c>Data/Weddings</c> "default" script holds ~20.5s across eleven pauses; these are
/// millisecond-based (<c>Game1.pauseTime</c>), so no TPS change shortens them — only this data edit
/// does, bringing them to ~7.9s. Ceremony parts that ARE tick-inflated (globalFade, NPC walk) belong to
/// the TPS-agnostic patches, not here.
///
/// <para>
/// Client-side only, <c>EventScript</c> values only (never <c>Attendees</c>); the server mod's copy is
/// untouched (the host force-ends its copy on the wait gate). Only exact <c>pause &lt;int&gt;</c>
/// segments are rewritten, so a future script reshape degrades to vanilla pacing, never a broken test.
/// </para>
/// </summary>
public class WeddingPaceCompressor
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;

    // Long enough to still read on the recording, short enough to shed most of the vanilla holds.
    private const int MaxPauseMs = 800;

    private const string WeddingsAsset = "Data/Weddings";

    // Anchored on the '/' command delimiter (or script start/end) so it only matches a standalone
    // `pause` command, never a "pause" inside a quoted dialogue string — letting us edit the raw asset
    // without the quote-aware split/rejoin the engine's own parser needs.
    private static readonly Regex PauseSegment = new(
        @"(?<=^|/)pause (\d+)(?=/|$)",
        RegexOptions.Compiled
    );

    public WeddingPaceCompressor(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    public void Apply()
    {
        _helper.Events.Content.AssetRequested += OnAssetRequested;
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo(WeddingsAsset))
        {
            return;
        }

        e.Edit(asset =>
        {
            var data = asset.GetData<WeddingData>();
            if (data.EventScript == null)
            {
                return;
            }

            int rewritten = 0;
            // Snapshot keys so we can reassign values while iterating.
            foreach (var key in data.EventScript.Keys.ToList())
            {
                (data.EventScript[key], var n) = CompressPauses(data.EventScript[key]);
                rewritten += n;
            }

            _monitor.Log(
                $"[WeddingPace] Capped scripted pauses to {MaxPauseMs}ms — rewrote {rewritten} segment(s).",
                LogLevel.Trace
            );
        });
    }

    /// <summary>Caps every whole <c>pause &lt;int&gt;</c> command to <see cref="MaxPauseMs"/>, leaving the
    /// rest of the raw script byte-identical. Returns the rewritten script and how many were capped.</summary>
    private static (string Script, int Rewritten) CompressPauses(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return (script, 0);
        }

        int rewritten = 0;
        var result = PauseSegment.Replace(
            script,
            match =>
            {
                if (!int.TryParse(match.Groups[1].Value, out var ms) || ms <= MaxPauseMs)
                {
                    return match.Value;
                }
                rewritten++;
                return $"pause {MaxPauseMs}";
            }
        );
        return (result, rewritten);
    }
}
