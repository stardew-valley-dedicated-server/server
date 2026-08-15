using System;
using System.Linq;
using JunimoServer.Services.Auth;
using JunimoServer.Shared;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;

namespace JunimoServer.Services.Commands;

/// <summary>
/// Operator console commands for farmhand ownership:
/// <c>farmhand release &lt;name|uid&gt;</c> makes a slot claimable by the next player to select
/// it on any transport — the first successful claim becomes the recorded owner;
/// <c>farmhand rebind &lt;name|uid&gt; &lt;platformId&gt;</c> re-points ownership to another
/// platform identity (operator-driven migration — there is no self-service migration path by
/// design). Rebinds survive restarts even on not-yet-customized slots (operator-origin records
/// are exempt from the abandoned-claim sweep). Vanilla <c>/unlinkPlayer</c> clears only the
/// stamp, not the ownership record; <c>release</c> is the supported path.
/// </summary>
internal static class FarmhandCommand
{
    private static IModHelper _helper;
    private static IMonitor _monitor;
    private static FarmhandOwnershipService _ownership;

    private static readonly CommandDescriptor Descriptor = new()
    {
        Name = "farmhand",
        Description =
            "Farmhand ownership management. Run 'farmhand release <name|uid>' to make "
            + "a slot claimable by the next player to select it, 'farmhand rebind <name|uid> "
            + "<platformId>' to re-point ownership (platform id = the Steam64 or GOG Galaxy "
            + "id shown in the server's connect log).",
        Subcommands =
        {
            new SubcommandDescriptor
            {
                Name = "release",
                Description = "Make a slot claimable: farmhand release <name|uid>",
            },
            new SubcommandDescriptor
            {
                Name = "rebind",
                Description = "Re-point ownership: farmhand rebind <name|uid> <platformId>",
            },
        },
    };

    public static void Register(
        IModHelper helper,
        IMonitor monitor,
        FarmhandOwnershipService ownership
    )
    {
        _helper = helper;
        _monitor = monitor;
        _ownership = ownership;

        CommandDescriptorRegistry.Add(Descriptor);
        helper.ConsoleCommands.Add(
            Descriptor.Name,
            Descriptor.Description,
            (cmd, args) => HandleCommand(args)
        );
    }

    private static void HandleCommand(string[] args)
    {
        if (args.Length == 0)
        {
            foreach (var line in Descriptor.HelpLines())
            {
                _monitor.Log(line, LogLevel.Info);
            }
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "release":
                if (args.Length < 2)
                {
                    _monitor.Log("Usage: farmhand release <name|uid>", LogLevel.Warn);
                    return;
                }
                GameThreadOneShot.Run(
                    _helper,
                    _monitor,
                    "farmhand command",
                    () => Release(args[1])
                );
                break;
            case "rebind":
                if (args.Length < 3)
                {
                    _monitor.Log("Usage: farmhand rebind <name|uid> <platformId>", LogLevel.Warn);
                    return;
                }
                GameThreadOneShot.Run(
                    _helper,
                    _monitor,
                    "farmhand command",
                    () => Rebind(args[1], args[2])
                );
                break;
            default:
                _monitor.Log(
                    $"Unknown farmhand subcommand: {args[0]}. Use: farmhand [{Descriptor.SubcommandNames}]",
                    LogLevel.Warn
                );
                break;
        }
    }

    private static void Release(string nameOrUid)
    {
        var farmhand = ResolveFarmhand(nameOrUid);
        if (farmhand == null)
        {
            return;
        }

        var uid = farmhand.UniqueMultiplayerID;
        var hadOwner = _ownership.TryGetOwner(uid, out _);
        var wasReleased = _ownership.IsReleased(uid);
        var hadStamp = ClearStamp(farmhand, uid);

        if (!farmhand.isCustomized.Value)
        {
            // A fresh slot needs no released marker — clearing the claim markers re-opens it.
            var removed = _ownership.RemoveOwner(uid);
            if (!removed && !hadStamp)
            {
                _monitor.Log(
                    $"Farmhand '{ChatRedaction.MaskValue(farmhand.Name)}' (uid={uid}) had no ownership record or stamp — nothing to release.",
                    LogLevel.Info
                );
                return;
            }

            _monitor.Log(
                $"Released uncustomized farmhand slot (uid={uid}): claim markers cleared, slot open again. "
                    + PersistenceNote(),
                LogLevel.Info
            );
            return;
        }

        _ownership.MarkReleased(uid);
        var ownershipNote =
            hadOwner ? "cleared"
            : wasReleased ? "was already released"
            : "was absent";
        _monitor.Log(
            $"Released farmhand '{ChatRedaction.MaskValue(farmhand.Name)}' (uid={uid}): "
                + $"ownership {ownershipNote}, stamp {(hadStamp ? "cleared" : "was absent")}. "
                + "The next Steam/GOG player to select it becomes the owner "
                + "(a direct-IP claim returns it to the shared LAN pool instead). "
                + PersistenceNote(),
            LogLevel.Info
        );
    }

    private static void Rebind(string nameOrUid, string platformId)
    {
        if (!FarmhandOwnershipService.IsValidPlatformId(platformId))
        {
            _monitor.Log(
                $"'{platformId}' is not a valid platform id (expected a decimal Steam64 or GOG "
                    + "Galaxy id — the value shown in the server's connect log).",
                LogLevel.Warn
            );
            return;
        }

        var farmhand = ResolveFarmhand(nameOrUid);
        if (farmhand == null)
        {
            return;
        }

        var uid = farmhand.UniqueMultiplayerID;
        var platform = FarmhandOwnershipService.ClassifyPlatformId(platformId);
        _ownership.RecordOwner(uid, platform, platformId, FarmhandOwnershipService.OriginOperator);
        ClearStamp(farmhand, uid);

        _monitor.Log(
            $"Rebound farmhand '{ChatRedaction.MaskValue(farmhand.Name)}' (uid={uid}) to a {platform} identity. "
                + PersistenceNote(),
            LogLevel.Info
        );
    }

    /// <summary>Clears the legacy platform stamp on the persisted farmhand root (it is
    /// Galaxy-space and would gray the slot client-side for anyone else) and mirrors the clear
    /// onto the live copy while the owner is connected, so any read before disconnect reflects
    /// the change. Returns whether a stamp was present.</summary>
    private static bool ClearStamp(Farmer farmhand, long uid)
    {
        var hadStamp = !string.IsNullOrEmpty(farmhand.userID.Value);
        farmhand.userID.Value = "";
        if (Game1.otherFarmers.TryGetValue(uid, out var live))
        {
            live.userID.Value = "";
        }

        return hadStamp;
    }

    /// <summary>
    /// The ownership store writes through immediately, but the record points at world state
    /// that may exist only in memory — a cleared stamp, or the target farmhand itself (a
    /// cabin-backfilled slot created since the last save is in no file yet, and a record
    /// bound to it evaporates via the orphan-drop on the next load). So on an empty server
    /// the world is saved on the spot (the paused start-of-day state satisfies every save
    /// precondition); with players online the next day-save persists it — guaranteed to
    /// happen while players are on, and no save-now is attempted (mid-day
    /// <c>saveFarmhands</c> outside the sleep barrier is unsafe, see <see cref="SaveNow"/>).
    /// </summary>
    private static string PersistenceNote()
    {
        if (OnlineFarmers.CountOthers() > 0)
        {
            return "Live now; hits disk at the next day-save.";
        }

        return SaveNow.TrySave(_helper, out var error)
            ? "Persisted."
            : $"Live now; immediate save failed ({error}) — persists at the next day-save.";
    }

    /// <summary>Resolves a farmhand from persisted farmhandData by UniqueMultiplayerID or by
    /// case-insensitive name; logs a Warn (never Error — test poison) when unresolved/ambiguous.</summary>
    private static Farmer ResolveFarmhand(string nameOrUid)
    {
        var farmhandData = Game1.netWorldState?.Value?.farmhandData;
        if (farmhandData == null)
        {
            _monitor.Log("farmhandData unavailable.", LogLevel.Warn);
            return null;
        }

        var all = farmhandData.FieldDict.Values.Select(r => r.Value).Where(f => f != null).ToList();

        if (long.TryParse(nameOrUid, out var uid))
        {
            var byUid = all.FirstOrDefault(f => f.UniqueMultiplayerID == uid);
            if (byUid != null)
            {
                return byUid;
            }
        }

        var byName = all.Where(f =>
                string.Equals(f.Name, nameOrUid, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (byName.Count == 1)
        {
            return byName[0];
        }

        _monitor.Log(
            byName.Count > 1
                ? $"Multiple farmhands are named '{nameOrUid}' — use the uid instead ({string.Join(", ", byName.Select(f => f.UniqueMultiplayerID))})."
                : $"No farmhand matches '{nameOrUid}' (by uid or name).",
            LogLevel.Warn
        );
        return null;
    }
}
