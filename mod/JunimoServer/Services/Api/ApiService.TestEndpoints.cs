using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using JunimoServer.Services.Auth;
using JunimoServer.Services.CropSaver;
using JunimoServer.Services.Lobby;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace JunimoServer.Services.Api;

// Test-only HTTP endpoints (/test/*), split out from ApiService so the production
// dispatcher never names them. Reachable only under Env.IsTest — see the gate in
// ApiService.HandleRequest and the OpenAPI filter in ApiService.StartServer.
//
// SEAM: to compile these out of production binaries entirely, define
// INCLUDE_TEST_ENDPOINTS for test builds only and wrap this file's body (and
// ApiService.TestEndpoints.Models.cs's body) plus the two // SEAM references in
// ApiService.cs (the dispatcher gate and the OpenAPI predicate).
public partial class ApiService
{
    /// <summary>
    /// True if the request path targets the test-only endpoint namespace. The single
    /// definition of "what is a /test/ route", shared by the dispatcher gate and the
    /// OpenAPI spec filter so the two cannot disagree.
    /// </summary>
    private static bool IsTestPath(string path) =>
        path.StartsWith("/test/", StringComparison.Ordinal);

    /// <summary>
    /// Routes /test/* requests to their handlers. Only invoked under Env.IsTest (the
    /// caller gates on it) and after auth has passed, so in production these routes
    /// are indistinguishable from unknown routes. An unknown /test/* path (or an
    /// unsupported method) falls through to the same generic 404 as any missing route.
    /// </summary>
    private async Task DispatchTestEndpointAsync(
        string method,
        string path,
        HttpListenerRequest request,
        HttpListenerResponse response
    )
    {
        switch (method)
        {
            case "GET":
                switch (path)
                {
                    case "/test/crops":
                        await WriteJsonAsync(response, await HandleGetTestCropsAsync());
                        return;
                    case "/test/festival_state":
                        await WriteJsonAsync(response, await HandleGetTestFestivalStateAsync());
                        return;
                    case "/test/farmers":
                        await WriteJsonAsync(response, await HandleGetTestFarmersAsync(request));
                        return;
                    case "/test/save_tmp_exists":
                        await WriteJsonAsync(response, HandleGetTestSaveTmpExists(request));
                        return;
                }
                break;
            case "POST":
                switch (path)
                {
                    case "/test/set_date":
                        await WriteJsonAsync(response, await HandlePostTestSetDateAsync(request));
                        return;
                    case "/test/farmevent":
                        await WriteJsonAsync(response, await HandlePostTestFarmEventAsync(request));
                        return;
                    case "/test/saver_crop":
                        await WriteJsonAsync(response, await HandlePostTestSaverCropAsync(request));
                        return;
                    case "/test/house_upgrade":
                        await WriteJsonAsync(
                            response,
                            await HandlePostTestHouseUpgradeAsync(request)
                        );
                        return;
                    case "/test/stamp_claim":
                        await WriteJsonAsync(response, await HandlePostTestStampClaimAsync());
                        return;
                    case "/test/galaxy_relogin":
                        await WriteJsonAsync(response, await HandlePostTestGalaxyReloginAsync());
                        return;
                    case "/test/seed_import_source":
                        await WriteJsonAsync(
                            response,
                            await HandlePostTestSeedImportSourceAsync(request)
                        );
                        return;
                    case "/test/import_save":
                        await WriteJsonAsync(
                            response,
                            await HandlePostTestImportSaveAsync(request)
                        );
                        return;
                    case "/test/console":
                        await WriteJsonAsync(
                            response,
                            await HandlePostTestConsoleCommandAsync(request)
                        );
                        return;
                    case "/test/corrupt_save":
                        await WriteJsonAsync(
                            response,
                            await HandlePostTestCorruptSaveAsync(request)
                        );
                        return;
                }
                break;
        }

        await WriteNotFoundAsync(response, path);
    }

    [ApiEndpoint(
        "GET",
        "/test/crops",
        Summary = "Enumerate every HoeDirt-with-crop in the world (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestCropsResponse), 200)]
    private async Task<TestCropsResponse> HandleGetTestCropsAsync()
    {
        var crops = new List<TestCrop>();
        try
        {
            await RunOnGameThreadAsync(() =>
            {
                Utility.ForEachLocation(location =>
                {
                    var locName = location.Name;

                    foreach (var feature in location.terrainFeatures.Values)
                    {
                        if (feature is not HoeDirt dirt)
                        {
                            continue;
                        }

                        var crop = dirt.crop;
                        if (crop == null)
                        {
                            continue;
                        }

                        crops.Add(
                            new TestCrop
                            {
                                LocationName = locName,
                                TileX = (int)dirt.Tile.X,
                                TileY = (int)dirt.Tile.Y,
                                IsAlive = !crop.dead.Value,
                                IsInPot = false,
                                SeedItemId = crop.netSeedIndex.Value,
                                IsManaged = CropSaverOverrides.IsManaged(locName, dirt.Tile),
                                IsSeasonImmune = location.IsCropSeasonImmune(),
                            }
                        );
                    }

                    foreach (var pot in location.Objects.Values.OfType<IndoorPot>())
                    {
                        var dirt = pot.hoeDirt.Value;
                        if (dirt == null)
                        {
                            continue;
                        }

                        var crop = dirt.crop;
                        if (crop == null)
                        {
                            continue;
                        }

                        crops.Add(
                            new TestCrop
                            {
                                LocationName = locName,
                                TileX = (int)pot.TileLocation.X,
                                TileY = (int)pot.TileLocation.Y,
                                IsAlive = !crop.dead.Value,
                                IsInPot = true,
                                SeedItemId = crop.netSeedIndex.Value,
                                IsManaged = CropSaverOverrides.IsManaged(locName, pot.TileLocation),
                                IsSeasonImmune = location.IsCropSeasonImmune(),
                            }
                        );
                    }

                    return true;
                });
            });
        }
        catch (Exception ex)
        {
            return new TestCropsResponse { Success = false, Error = ex.Message };
        }

        return new TestCropsResponse { Success = true, Crops = crops };
    }

    [ApiEndpoint(
        "GET",
        "/test/festival_state",
        Summary = "Read the host's current festival state (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestFestivalStateResponse), 200)]
    private async Task<TestFestivalStateResponse> HandleGetTestFestivalStateAsync()
    {
        var result = new TestFestivalStateResponse();
        try
        {
            await RunOnGameThreadAsync(() =>
            {
                result.IsFestivalDay = SDateHelper.IsFestivalToday();
                result.WhereIsTodaysFest = Game1.whereIsTodaysFest;
                result.IsFestivalActive = Game1.CurrentEvent?.isFestival == true;
                result.FestivalStartReady = Game1.netReady.GetNumberReady("festivalStart");
                result.FestivalStartRequired = Game1.netReady.GetNumberRequired("festivalStart");
                result.FestivalEndReady = Game1.netReady.GetNumberReady("festivalEnd");
                result.FestivalEndRequired = Game1.netReady.GetNumberRequired("festivalEnd");
                result.TimeOfDay = Game1.timeOfDay;
                result.Success = true;
            });
        }
        catch (Exception ex)
        {
            // Never LogLevel.Error here (test poison per .claude/rules/debugging.md) — surface via response.
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    [ApiEndpoint(
        "GET",
        "/test/farmers",
        Summary = "Farmers in a location, by the server-side collection CabinPlacementValidator reads (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestFarmersResponse), 200)]
    private async Task<TestFarmersResponse> HandleGetTestFarmersAsync(HttpListenerRequest request)
    {
        // ?location=<name> (default "Farm"): which location's farmer collection to report.
        // CabinPlacementValidator iterates Game1.getFarm().farmers — a FarmerCollection that
        // filters Game1.otherFarmers by currentLocation == Farm (FarmerCollection.cs:49,58).
        // A warped farmer's TilePoint replicates globally before its currentLocation does, so a
        // test must mirror THIS membership (location-filtered + bounding-box tile), not a global
        // getOnlineFarmers()+TilePoint read, or it sees the farmer in position before the
        // validator's collection does and issues !cabin too early.
        var locationName = request.QueryString["location"] ?? "Farm";

        var result = new TestFarmersResponse();
        try
        {
            await RunOnGameThreadAsync(() =>
            {
                var location = Game1.getLocationFromName(locationName);
                if (location == null)
                {
                    result.Success = true;
                    return;
                }

                foreach (var farmer in location.farmers)
                {
                    // The validator tests GetBoundingBox().Intersects(tileRect); its center tile
                    // is what a single-tile occupancy check resolves to.
                    var center = farmer.GetBoundingBox().Center;
                    result.Farmers.Add(
                        new TestFarmer
                        {
                            Id = farmer.UniqueMultiplayerID,
                            TileX = center.X / 64,
                            TileY = center.Y / 64,
                        }
                    );
                }

                result.Success = true;
            });
        }
        catch (Exception ex)
        {
            // Never LogLevel.Error here (test poison per .claude/rules/debugging.md) — surface via response.
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    [ApiEndpoint(
        "POST",
        "/test/set_date",
        Summary = "Set season/day/year directly (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestSetDateResponse), 200)]
    private async Task<TestSetDateResponse> HandlePostTestSetDateAsync(HttpListenerRequest request)
    {
        TestSetDateRequest? body = null;
        try
        {
            using var reader = new System.IO.StreamReader(
                request.InputStream,
                request.ContentEncoding
            );
            var json = await reader.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(json))
            {
                body = JsonConvert.DeserializeObject<TestSetDateRequest>(json);
            }
        }
        catch (Exception ex)
        {
            return new TestSetDateResponse
            {
                Success = false,
                Error = $"Failed to parse body: {ex.Message}",
            };
        }

        if (body == null || string.IsNullOrEmpty(body.Season))
        {
            return new TestSetDateResponse
            {
                Success = false,
                Error = "Missing 'season' in request body",
            };
        }

        if (!Enum.TryParse<Season>(body.Season, ignoreCase: true, out var season))
        {
            return new TestSetDateResponse
            {
                Success = false,
                Error = $"Invalid season '{body.Season}' (expected spring/summer/fall/winter)",
            };
        }

        if (body.Day < 1 || body.Day > 28)
        {
            return new TestSetDateResponse
            {
                Success = false,
                Error = $"Day {body.Day} out of range (1-28)",
            };
        }

        if (body.Year < 1)
        {
            return new TestSetDateResponse
            {
                Success = false,
                Error = $"Year {body.Year} out of range (>= 1)",
            };
        }

        // Fail closed before mutating: with no world loaded netWorldState.Value is null, so the
        // replication push below would silently no-op while we still reported Success — E2E setup
        // would advance on a date jump that never reached peers. (gameMode == 3 is playingGameMode.)
        if (Game1.gameMode != 3 || !Game1.IsServer)
        {
            return new TestSetDateResponse { Success = false, Error = "Server not ready" };
        }

        await RunOnGameThreadAsync(() =>
        {
            Game1.season = season;
            Game1.dayOfMonth = body.Day;
            Game1.year = body.Year;

            // Reconcile the date-dependent host state, mirroring the engine's own new-day reset
            // block (newDayAfterFade, Game1.cs:7818/7842/updateWeatherIcon): a date jump is "start
            // of this day", so reset to morning, clear any prior festival target, and recompute the
            // host's weather icon from the new date. weatherIcon is local (not replicated) — each
            // instance derives it from isFestivalDay — so this only fixes the host; clients recompute
            // theirs once the date replicates. Without it, a stale weatherIcon == 1 (festival) bleeds
            // onto a non-festival day, making performTenMinuteClockUpdate load a non-existent
            // Data/Festivals/<season><day> file and crash the update loop.
            Game1.timeOfDay = 600;
            Game1.gameTimeInterval = 0;
            Game1.whereIsTodaysFest = null;
            Game1.updateWeatherIcon();

            // Push the reconciled date + time to NetWorldState so peers replicate them (both are
            // replicated NetFields; see decompiled Game1.cs:8264 for Stardew's own day-end usage).
            Game1.netWorldState.Value.UpdateFromGame1();
        });

        Monitor.Log(
            $"Date set to {season} {body.Day}, year {body.Year} via test API",
            LogLevel.Info
        );

        return new TestSetDateResponse
        {
            Success = true,
            Season = season.ToString().ToLowerInvariant(),
            Day = body.Day,
            Year = body.Year,
        };
    }

    [ApiEndpoint(
        "POST",
        "/test/farmevent",
        Summary = "Queue an overnight FarmEvent for the next night (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestFarmEventResponse), 200)]
    private async Task<TestFarmEventResponse> HandlePostTestFarmEventAsync(
        HttpListenerRequest request
    )
    {
        // Game1.farmEventOverride is consumed by _newDayAfterFade as the fallback when no random
        // farm event is picked (decompiled Game1.cs:8588), so this deterministically queues the
        // event for the next overnight transition without seeding mail or stats.
        var type = request.QueryString["type"]?.ToLowerInvariant() ?? "qiplane";

        StardewValley.Events.FarmEvent? farmEvent = type switch
        {
            "qiplane" => new StardewValley.Events.QiPlaneEvent(),
            _ => null,
        };

        if (farmEvent == null)
        {
            return new TestFarmEventResponse
            {
                Success = false,
                Error = $"Unknown farm event type '{type}' (supported: qiplane)",
            };
        }

        await RunOnGameThreadAsync(() => Game1.farmEventOverride = farmEvent);

        Monitor.Log(
            $"Queued overnight farm event '{type}' for the next night via test API",
            LogLevel.Info
        );

        return new TestFarmEventResponse { Success = true, Type = type };
    }

    [ApiEndpoint(
        "POST",
        "/test/saver_crop",
        Summary = "Mutate a CropSaver entry (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestSaverCropResponse), 200)]
    private async Task<TestSaverCropResponse> HandlePostTestSaverCropAsync(
        HttpListenerRequest request
    )
    {
        TestSaverCropRequest? body = null;
        try
        {
            using var reader = new System.IO.StreamReader(
                request.InputStream,
                request.ContentEncoding
            );
            var json = await reader.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(json))
            {
                body = JsonConvert.DeserializeObject<TestSaverCropRequest>(json);
            }
        }
        catch (Exception ex)
        {
            return new TestSaverCropResponse
            {
                Success = false,
                Error = $"Failed to parse body: {ex.Message}",
            };
        }

        if (body == null || string.IsNullOrEmpty(body.LocationName))
        {
            return new TestSaverCropResponse
            {
                Success = false,
                Error = "Missing 'locationName' in request body",
            };
        }

        var loader = JunimoServer.Services.CropSaver.CropSaver.Instance?.DataLoader;
        if (loader == null)
        {
            return new TestSaverCropResponse
            {
                Success = false,
                Error = "CropSaver not initialized",
            };
        }

        SDate? newDatePlanted = null;
        if (body.DatePlanted != null)
        {
            if (
                string.IsNullOrEmpty(body.DatePlanted.Season)
                || !Enum.TryParse<Season>(
                    body.DatePlanted.Season,
                    ignoreCase: true,
                    out var dpSeason
                )
            )
            {
                return new TestSaverCropResponse
                {
                    Success = false,
                    Error = $"Invalid datePlanted.season '{body.DatePlanted.Season}'",
                };
            }
            if (body.DatePlanted.Day < 1 || body.DatePlanted.Day > 28)
            {
                return new TestSaverCropResponse
                {
                    Success = false,
                    Error = $"datePlanted.day {body.DatePlanted.Day} out of range (1-28)",
                };
            }
            if (body.DatePlanted.Year < 1)
            {
                return new TestSaverCropResponse
                {
                    Success = false,
                    Error = $"datePlanted.year {body.DatePlanted.Year} out of range (>= 1)",
                };
            }
            newDatePlanted = new SDate(
                body.DatePlanted.Day,
                dpSeason.ToString().ToLowerInvariant(),
                body.DatePlanted.Year
            );
        }

        var tile = new Vector2(body.TileX, body.TileY);
        TestSaverCropResponse result = new();
        await RunOnGameThreadAsync(() =>
        {
            var saverCrop = loader.GetSaverCrop(body.LocationName, tile);
            if (saverCrop == null)
            {
                result = new TestSaverCropResponse
                {
                    Success = false,
                    Found = false,
                    Error =
                        $"No SaverCrop entry at {body.LocationName} ({body.TileX},{body.TileY})",
                };
                return;
            }

            if (body.ExtraDays.HasValue)
            {
                saverCrop.extraDays = body.ExtraDays.Value;
            }

            if (body.OwnerId.HasValue)
            {
                saverCrop.ownerId = body.OwnerId.Value;
            }

            if (newDatePlanted != null)
            {
                saverCrop.datePlanted = newDatePlanted;
            }

            result = new TestSaverCropResponse
            {
                Success = true,
                Found = true,
                ExtraDays = saverCrop.extraDays,
                OwnerId = saverCrop.ownerId,
            };
        });

        return result;
    }

    [ApiEndpoint(
        "POST",
        "/test/house_upgrade",
        Summary = "Run a debug house-upgrade command on the host to verify it's blocked (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestHouseUpgradeResponse), 200)]
    private async Task<TestHouseUpgradeResponse> HandlePostTestHouseUpgradeAsync(
        HttpListenerRequest request
    )
    {
        var command = request.QueryString["command"];
        if (string.IsNullOrWhiteSpace(command))
        {
            return new TestHouseUpgradeResponse
            {
                Success = false,
                Error = "Missing 'command' query parameter",
            };
        }

        // Route through parseDebugInput so the real vanilla handler — and thus the
        // HostFarmhouseUpgradeGuard Harmony prefix — is exercised, exactly as an admin typing it
        // at the console would be. Set Success only after the command actually runs, so a throw
        // can't report a false-positive pass to the guard test.
        var result = new TestHouseUpgradeResponse();
        try
        {
            await RunOnGameThreadAsync(() =>
            {
                Game1.game1.parseDebugInput(command);
                result.HostHouseUpgradeLevel = Game1.MasterPlayer.HouseUpgradeLevel;
                result.Success = true;
            });
        }
        catch (Exception ex)
        {
            // Don't log at Error level — that trips ServerContainer's error cancellation and
            // poisons the test (.claude/rules/debugging.md). Surface via the response instead.
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    [ApiEndpoint(
        "POST",
        "/test/stamp_claim",
        Summary = "Stamp a synthetic abandoned slot claim onto an uncustomized homed farmhand (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestStampClaimResponse), 200)]
    private async Task<TestStampClaimResponse> HandlePostTestStampClaimAsync()
    {
        // Synthetic platform id mimicking a Steam/GOG stamp. Fixed so a test could match it,
        // but the test only needs StampedUid; the value just has to be non-empty.
        const string syntheticUserId = "test-stuck-claim-9999";

        var result = new TestStampClaimResponse();
        try
        {
            await RunOnGameThreadAsync(() =>
            {
                var farm = Game1.getFarm();
                if (farm == null)
                {
                    result.Error = "No farm loaded";
                    return;
                }

                // Find an unclaimed slot with an existing farmhand entry whose home resolves to its
                // cabin: owner present, not customized, no userID yet (exactly IsCabinAvailable's
                // "available" shape). Stamping its userID reproduces the homed abandoned-claim state
                // — the case vanilla's load-time ResetFarmhandState does NOT clear (NetWorldState.cs
                // :783 returns true for a homed farmhand, skipping the userID-clearing else-branch),
                // so only the sweep heals it on reload.
                foreach (var building in farm.buildings)
                {
                    if (!building.isCabin || LobbyService.IsLobbyCabin(building))
                    {
                        continue;
                    }

                    var cabin = building.GetIndoors<Cabin>();
                    var owner = cabin?.owner;
                    if (owner == null)
                    {
                        continue;
                    }

                    if (owner.isCustomized.Value)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(owner.userID.Value))
                    {
                        continue;
                    }

                    // Pin homeLocation to this cabin so TryAssignFarmhandHome resolves on reload
                    // (the slot's home should already be its cabin; set it to be deterministic).
                    owner.homeLocation.Value = cabin.NameOrUniqueName;
                    owner.userID.Value = syntheticUserId;

                    result.StampedUid = owner.UniqueMultiplayerID;
                    result.StampedUserId = syntheticUserId;
                    result.HomeLocation = owner.homeLocation.Value ?? "";
                    result.Success = true;
                    return;
                }

                result.Error =
                    "No uncustomized, unclaimed cabin slot with an owner entry found to stamp";
            });
        }
        catch (Exception ex)
        {
            // Never LogLevel.Error here (test poison per .claude/rules/debugging.md) — surface via response.
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    [ApiEndpoint(
        "POST",
        "/test/galaxy_relogin",
        Summary = "Trigger a Galaxy re-sign-in on demand, no outage (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestGalaxyReloginResponse), 200)]
    private async Task<TestGalaxyReloginResponse> HandlePostTestGalaxyReloginAsync()
    {
        // No-op safety probe for the Steam-reconnect-triggered Galaxy-reauth fix: re-login while
        // Galaxy is HEALTHY and a client is connected, so the test can verify the live lobby and
        // invite code survive. Runs the same BeginGalaxyReSignIn the real fix uses.
        var result = new TestGalaxyReloginResponse();
        try
        {
            await RunOnGameThreadAsync(() =>
            {
                result.Triggered = GalaxyAuthService.TriggerGalaxyReSignInForTest();
                if (!result.Triggered)
                {
                    result.Error =
                        "Galaxy not initialized (no STEAM_AUTH_URL, or not yet signed in)";
                }
            });
        }
        catch (Exception ex)
        {
            result.Triggered = false;
            result.Error = ex.Message;
        }

        return result;
    }

    [ApiEndpoint(
        "POST",
        "/test/seed_import_source",
        Summary = "Seed the active game's master + FarmHouse to look like a real importable owner (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestSeedImportSourceResponse), 200)]
    private async Task<TestSeedImportSourceResponse> HandlePostTestSeedImportSourceAsync(
        HttpListenerRequest request
    )
    {
        // The in-process generator always creates a "Server" master, but a real imported co-op save's
        // <player> is a human owner. So before saving, this makes the active master (Game1.player —
        // which is what the swap import demotes) look like a real owner: a non-Server name + an
        // inventory (so the re-import guard doesn't mistake it for a clone-blank Server master), plus
        // optional world-gating/relationship/house state and FarmHouse contents the import tests
        // assert move/carry. Mirrors /test/stamp_claim's "construct a precise pre-save state" role.
        TestSeedImportSourceRequest body;
        try
        {
            using var reader = new System.IO.StreamReader(
                request.InputStream,
                request.ContentEncoding
            );
            var json = await reader.ReadToEndAsync();
            body = string.IsNullOrWhiteSpace(json)
                ? new TestSeedImportSourceRequest()
                : JsonConvert.DeserializeObject<TestSeedImportSourceRequest>(json)
                    ?? new TestSeedImportSourceRequest();
        }
        catch (Exception ex)
        {
            return new TestSeedImportSourceResponse
            {
                Success = false,
                Error = $"Failed to parse body: {ex.Message}",
            };
        }

        var result = new TestSeedImportSourceResponse();
        try
        {
            await RunOnGameThreadAsync(() =>
            {
                var master = Game1.player;
                var farmHouse = Game1.getLocationFromName("FarmHouse") as FarmHouse;
                if (master == null || farmHouse == null)
                {
                    result.Error = "Master player or FarmHouse not available";
                    return;
                }

                // Identity: a non-Server name + a guaranteed inventory item so the save's <player>
                // reads as a real played owner (defeats the clone-blank re-import fingerprint).
                master.Name = string.IsNullOrEmpty(body.OwnerName)
                    ? "ImportedOwner"
                    : body.OwnerName;
                master.displayName = master.Name;
                if (!master.Items.Any(i => i != null))
                {
                    master.Items.Add(ItemRegistry.Create("(O)388", 10)); // wood — marks a played owner
                }

                if (body.HouseUpgradeLevel.HasValue)
                {
                    master.HouseUpgradeLevel = body.HouseUpgradeLevel.Value;
                }
                if (body.CaveChoice.HasValue)
                {
                    master.caveChoice.Value = body.CaveChoice.Value;
                }
                if (!string.IsNullOrEmpty(body.Spouse))
                {
                    master.spouse = body.Spouse;
                }
                if (!string.IsNullOrEmpty(body.MailFlag))
                {
                    master.mailReceived.Add(body.MailFlag);
                }
                if (!string.IsNullOrEmpty(body.EventSeen))
                {
                    master.eventsSeen.Add(body.EventSeen);
                }
                if (body.ShadowFriendshipPoints.HasValue)
                {
                    // Mirror the Dark Shrine pacifism gate (MasterPlayer.friendshipData[key].Points).
                    var key = string.IsNullOrEmpty(body.ShadowFriendshipKey)
                        ? "Krobus"
                        : body.ShadowFriendshipKey;
                    if (!master.friendshipData.TryGetValue(key, out var fr) || fr == null)
                    {
                        fr = new Friendship();
                        master.friendshipData[key] = fr;
                    }
                    fr.Points = body.ShadowFriendshipPoints.Value;
                }
                if (body.DaysPlayed.HasValue)
                {
                    master.stats.DaysPlayed = (uint)body.DaysPlayed.Value;
                }

                // FarmHouse contents the contents-move test asserts: a chest (with a known item) and
                // a fridge item. Place the chest at a known open floor tile.
                if (body.PlaceChest)
                {
                    var tile = new Vector2(body.ChestTileX, body.ChestTileY);
                    var chest = new Chest(playerChest: true, tile, "232");
                    chest.Items.Clear();
                    chest.Items.Add(ItemRegistry.Create("(O)388", 5)); // wood — a known chest item
                    farmHouse.objects[tile] = chest;
                    result.ChestPlaced = true;
                }
                if (body.PlaceFridgeItem && farmHouse.fridge.Value != null)
                {
                    farmHouse.fridge.Value.Items.Add(ItemRegistry.Create("(O)24", 1)); // parsnip
                    result.FridgeItemPlaced = true;
                }

                // Pet: spawn a Pet into the FarmHouse characters for the household-relocation test.
                if (body.SpawnPet)
                {
                    var existing = master.getPet();
                    if (existing == null)
                    {
                        var pet = new Pet(5, 5, "0", "Cat") { Name = "TestPet" };
                        pet.homeLocationName.Value = "Farm";
                        farmHouse.addCharacter(pet);
                        pet.currentLocation = farmHouse;
                        result.PetSpawned = true;
                    }
                    else
                    {
                        result.PetSpawned = true; // already present
                    }
                }

                // Cellar item: place a known object in the master's "Cellar"-1 (the location the owner
                // built their casks in while master). The "Cellar" location always exists in
                // Game1.locations regardless of house level, so this faithfully reproduces cellar
                // contents the swap must carry into the demoted owner's reassigned cellar.
                if (body.PlaceCellarItem)
                {
                    var cellar = Game1.getLocationFromName("Cellar");
                    if (cellar != null)
                    {
                        var tile = new Vector2(2, 2);
                        var cask = new Cask(tile); // an aged-goods cask — the canonical cellar object
                        cellar.objects[tile] = cask;
                        result.CellarItemPlaced = true;
                    }
                }

                // Inject a userID onto a spare uncustomized farmhand slot (for the collision test).
                // LAN never stamps a userID, so the test needs a server-side inject to set up the
                // cross-farmhand collision the import guard must catch.
                if (!string.IsNullOrEmpty(body.InjectFarmhandUserId))
                {
                    var stamped = false;
                    foreach (var building in Game1.getFarm().buildings)
                    {
                        if (!building.isCabin || LobbyService.IsLobbyCabin(building))
                        {
                            continue;
                        }
                        var slot = building.GetIndoors<Cabin>()?.owner;
                        if (
                            slot == null
                            || slot.isCustomized.Value
                            || !string.IsNullOrEmpty(slot.userID.Value)
                        )
                        {
                            continue;
                        }
                        slot.userID.Value = body.InjectFarmhandUserId;
                        stamped = true;
                        break;
                    }
                    result.FarmhandUserIdInjected = stamped;
                }

                result.OwnerUid = master.UniqueMultiplayerID;
                result.OwnerName = master.Name;
                result.Success = true;
            });
        }
        catch (Exception ex)
        {
            // Never LogLevel.Error here (test poison) — surface via response.
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    [ApiEndpoint(
        "POST",
        "/test/import_save",
        Summary = "Clone an existing save under a new name and run saves-import on it (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestImportSaveResponse), 200)]
    private async Task<TestImportSaveResponse> HandlePostTestImportSaveAsync(
        HttpListenerRequest request
    )
    {
        // Mirrors the operator console path (SaveImportService.ExecuteImport) but on the game thread
        // (matching /test/stamp_claim). ExecuteImport's Layer A logic stays engine-free; the live
        // engine here is incidental. Because ExecuteImport rejects importing the currently-active
        // save, the test must import a DIFFERENT folder — so this endpoint first clones a source
        // folder (default: the active save) to a fresh target folder, then imports the target.
        TestImportSaveRequest body;
        try
        {
            using var reader = new System.IO.StreamReader(
                request.InputStream,
                request.ContentEncoding
            );
            var json = await reader.ReadToEndAsync();
            body = string.IsNullOrWhiteSpace(json)
                ? new TestImportSaveRequest()
                : JsonConvert.DeserializeObject<TestImportSaveRequest>(json)
                    ?? new TestImportSaveRequest();
        }
        catch (Exception ex)
        {
            return new TestImportSaveResponse
            {
                Success = false,
                Error = $"Failed to parse body: {ex.Message}",
            };
        }

        var result = new TestImportSaveResponse();

        // Resolve source. The target FOLDER name is computed by CloneSaveFolder from the clone's
        // re-stamped uniqueIDForThisGame so that Constants.SaveFolderName after load EQUALS the
        // folder name (a real operator import already satisfies this — folders are named
        // {farmName}_{uniqueID}; a naive "{name}-import" clone would not, and the finalizer's
        // wrong-save guard — SaveFolderName == intent.SaveName — would then skip the finalize).
        var sourceName = string.IsNullOrEmpty(body.SourceSaveName)
            ? Constants.SaveFolderName
            : body.SourceSaveName;

        // The caller's TargetSaveName is used only as a uniqueness SEED (so distinct tests don't
        // collide); the actual folder name is derived. SkipClone imports an existing folder verbatim.
        string targetName;
        if (!body.SkipClone)
        {
            try
            {
                targetName = CloneSaveFolder(sourceName, body.TargetSaveName);
                result.TargetSaveName = targetName;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error =
                    $"Failed to clone save '{sourceName}' → '{body.TargetSaveName}': {ex.Message}";
                return result;
            }
        }
        else
        {
            targetName = string.IsNullOrEmpty(body.TargetSaveName)
                ? sourceName
                : body.TargetSaveName;
            // SkipClone imports the name verbatim (no CloneSaveFolder guard runs); validate it here so
            // a request-supplied name can't escape SavesPath via the hash/import path below.
            if (!IsSafeSaveName(targetName))
            {
                result.Success = false;
                result.Error =
                    "TargetSaveName must be a bare folder name (no path separators) when SkipClone is set";
                return result;
            }
            result.TargetSaveName = targetName;
        }

        // Capture a pre-import hash of the target's main file so the resilience test can assert
        // byte-unchanged on a failed import.
        result.PreImportMainFileHash = TryHashSaveMainFile(targetName);

        // Run the import on the game thread.
        SaveImport.SaveImportService.ImportResult import = null;
        try
        {
            await RunOnGameThreadAsync(() =>
            {
                import = _saveImportService.ExecuteImport(
                    targetName,
                    string.IsNullOrWhiteSpace(body.SwapHostTo) ? null : body.SwapHostTo
                );
            });
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            return result;
        }

        result.PostImportMainFileHash = TryHashSaveMainFile(targetName);

        result.Success = import?.Success ?? false;
        result.Swapped = import?.Swapped ?? false;
        result.RepointedBind = import?.RepointedBind ?? false;
        result.FormerOwnerUid = import?.FormerOwnerUid ?? 0;
        result.ImportError = import?.Error;
        return result;
    }

    [ApiEndpoint(
        "POST",
        "/test/console",
        Summary = "Invoke a registered SMAPI console command by name (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestConsoleCommandResponse), 200)]
    private async Task<TestConsoleCommandResponse> HandlePostTestConsoleCommandAsync(
        HttpListenerRequest request
    )
    {
        // Drives the saves-reload guard/kick path, which lives only in the console command — no HTTP
        // endpoint reaches it, and SMAPI 4.4 has no public command-invoke API. See InvokeConsoleCommand.
        TestConsoleCommandRequest body;
        try
        {
            using var reader = new System.IO.StreamReader(
                request.InputStream,
                request.ContentEncoding
            );
            var json = await reader.ReadToEndAsync();
            body =
                JsonConvert.DeserializeObject<TestConsoleCommandRequest>(json)
                ?? new TestConsoleCommandRequest();
        }
        catch (Exception ex)
        {
            return new TestConsoleCommandResponse
            {
                Success = false,
                Error = $"Failed to parse body: {ex.Message}",
            };
        }

        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return new TestConsoleCommandResponse { Success = false, Error = "Name is required" };
        }

        try
        {
            InvokeConsoleCommand(body.Name, body.Args ?? Array.Empty<string>());
            return new TestConsoleCommandResponse { Success = true };
        }
        catch (Exception ex)
        {
            return new TestConsoleCommandResponse { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Invokes a console command's callback by name via SMAPI internals (no public API exists).
    /// Chain (SMAPI 4.4): <c>SCore.Instance</c> → <c>CommandManager</c> field → <c>Get(name)</c> →
    /// <c>Command.Callback</c> (<c>Action&lt;string,string[]&gt;</c>); a future SMAPI may rename these.
    /// Runs off the game thread, as a real console command does. Test-only.
    /// </summary>
    private static void InvokeConsoleCommand(string name, string[] args)
    {
        var smapiAsm = typeof(IModHelper).Assembly;
        var scoreType =
            smapiAsm.GetType("StardewModdingAPI.Framework.SCore")
            ?? throw new InvalidOperationException("SCore type not found");
        var instance =
            scoreType
                .GetProperty(
                    "Instance",
                    System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public
                )
                ?.GetValue(null)
            ?? throw new InvalidOperationException("SCore.Instance is null");

        var commandManager =
            scoreType
                .GetField(
                    "CommandManager",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public
                )
                ?.GetValue(instance)
            ?? throw new InvalidOperationException("SCore.CommandManager is null");

        var command = commandManager
            .GetType()
            .GetMethod("Get", new[] { typeof(string) })
            ?.Invoke(commandManager, new object[] { name });
        if (command == null)
        {
            throw new InvalidOperationException($"Console command '{name}' is not registered");
        }

        var callback =
            command.GetType().GetProperty("Callback")?.GetValue(command) as Action<string, string[]>
            ?? throw new InvalidOperationException(
                $"Console command '{name}' has no invocable callback"
            );

        callback(name, args);
    }

    /// <summary>
    /// Clones <paramref name="sourceName"/> into a NEW independent save whose folder name matches its
    /// internal identity, and returns the actual folder name. Re-stamps <c>uniqueIDForThisGame</c> to
    /// a fresh value (derived deterministically from <paramref name="seed"/> — Date.Now is unavailable
    /// and irrelevant here) and names the folder <c>{FilterFileName(farmName)}_{newId}</c>, so that
    /// <c>Constants.SaveFolderName</c> after the engine loads it EQUALS the folder name. Without this,
    /// the clone (same uniqueID as the source, different folder name) loads with a SaveFolderName that
    /// differs from its folder, and the finalizer's wrong-save guard skips the finalize.
    /// </summary>
    private static string CloneSaveFolder(string sourceName, string seed)
    {
        // sourceName is request-supplied; keep it from escaping SavesPath (the derived targetName is
        // built from FilterFileName + a numeric id, so it's safe by construction).
        if (!IsSafeSaveName(sourceName))
        {
            throw new ArgumentException(
                $"Invalid source save name '{sourceName}'",
                nameof(sourceName)
            );
        }

        var savesPath = Constants.SavesPath;
        var sourceDir = System.IO.Path.Combine(savesPath, sourceName);
        if (!System.IO.Directory.Exists(sourceDir))
        {
            throw new System.IO.DirectoryNotFoundException($"Source save '{sourceName}' not found");
        }

        var sourceMain = System.IO.Path.Combine(sourceDir, sourceName);
        if (!System.IO.File.Exists(sourceMain))
        {
            throw new System.IO.FileNotFoundException($"Source main file '{sourceName}' not found");
        }

        // Read farmName + a fresh unique id from the source XML.
        var doc = new System.Xml.XmlDocument();
        doc.Load(sourceMain);
        var farmName =
            doc.SelectSingleNode("//SaveGame/player/farmName")?.InnerText
            ?? doc.SelectSingleNode("//SaveGame/farmhands/Farmer/farmName")?.InnerText
            ?? "Imported";

        // Deterministic new id from the source id + seed (no Date.Now; just needs to be distinct from
        // the source and stable for this call). Use an unsigned cast of the hash — Math.Abs would
        // throw on int.MinValue. The +1 keeps it strictly above the source id.
        var sourceId = doc.SelectSingleNode("//SaveGame/uniqueIDForThisGame")?.InnerText ?? "0";
        ulong.TryParse(sourceId, out var srcIdVal);
        var seedOffset = (ulong)((uint)(seed ?? "import").GetHashCode() % 1000000) + 1;
        var newId = (srcIdVal == 0 ? 1000000000UL : srcIdVal) + seedOffset;

        var targetName = $"{StardewValley.SaveGame.FilterFileName(farmName)}_{newId}";
        var targetDir = System.IO.Path.Combine(savesPath, targetName);
        if (System.IO.Directory.Exists(targetDir))
        {
            System.IO.Directory.Delete(targetDir, recursive: true);
        }
        System.IO.Directory.CreateDirectory(targetDir);

        // Re-stamp uniqueIDForThisGame in the cloned main XML and write it as the target's main file.
        var idNode = doc.SelectSingleNode("//SaveGame/uniqueIDForThisGame");
        if (idNode != null)
        {
            idNode.InnerText = newId.ToString();
        }
        doc.Save(System.IO.Path.Combine(targetDir, targetName));

        // Copy SaveGameInfo too (re-stamp its uniqueIDForThisGame if present), plus any other files.
        foreach (var file in System.IO.Directory.GetFiles(sourceDir))
        {
            var fileName = System.IO.Path.GetFileName(file);
            if (fileName == sourceName)
            {
                continue; // main file already written above
            }
            // Skip the engine's recovery backups (named after the SOURCE folder). Copying them into a
            // differently-named target folder would leave untransformed pre-swap copies on the volume;
            // and were they ever renamed to {target}_*, SaveGame.TryReadSaveFileWithFallback could
            // silently auto-recover the un-swapped original. A fresh clone needs no backups.
            if (
                fileName.EndsWith("_old", StringComparison.Ordinal)
                || fileName.Contains("_STARDEWVALLEYSAVETMP")
            )
            {
                continue;
            }
            if (fileName == "SaveGameInfo")
            {
                try
                {
                    var sgi = new System.Xml.XmlDocument();
                    sgi.Load(file);
                    var sgiId = sgi.SelectSingleNode("//uniqueIDForThisGame");
                    if (sgiId != null)
                    {
                        sgiId.InnerText = newId.ToString();
                    }
                    sgi.Save(System.IO.Path.Combine(targetDir, "SaveGameInfo"));
                    continue;
                }
                catch
                {
                    // fall through to a raw copy if SaveGameInfo isn't parseable
                }
            }
            System.IO.File.Copy(file, System.IO.Path.Combine(targetDir, fileName), overwrite: true);
        }

        return targetName;
    }

    /// <summary>
    /// True when <paramref name="saveName"/> is a bare save-folder name safe to combine under
    /// <c>Constants.SavesPath</c> — a single path segment with no directory separators and no
    /// <c>..</c> traversal. These test endpoints take the name from request input, so guard it before
    /// any <c>Path.Combine</c> to keep an untrusted value from escaping the saves directory.
    /// </summary>
    private static bool IsSafeSaveName(string? saveName) =>
        !string.IsNullOrEmpty(saveName)
        && System.IO.Path.GetFileName(saveName) == saveName
        && saveName != "."
        && saveName != "..";

    private static string TryHashSaveMainFile(string saveName)
    {
        try
        {
            var mainFile = System.IO.Path.Combine(Constants.SavesPath, saveName, saveName);
            if (!System.IO.File.Exists(mainFile))
            {
                return "";
            }
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = System.IO.File.OpenRead(mainFile);
            return Convert.ToBase64String(sha.ComputeHash(stream));
        }
        catch
        {
            return "";
        }
    }

    [ApiEndpoint(
        "POST",
        "/test/corrupt_save",
        Summary = "Clone a save under a new name and corrupt the clone's <player> (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestSaveFileOpResponse), 200)]
    private async Task<TestSaveFileOpResponse> HandlePostTestCorruptSaveAsync(
        HttpListenerRequest request
    )
    {
        // Used by the resilience test: clone the active (valid) save to a target folder, then mangle
        // the clone's main file so a swap import of it fails the XmlDocument.Load — proving the
        // transform leaves the file byte-unchanged on a malformed input. Plain file IO (off-thread).
        TestCorruptSaveRequest body;
        try
        {
            using var reader = new System.IO.StreamReader(
                request.InputStream,
                request.ContentEncoding
            );
            var json = await reader.ReadToEndAsync();
            body = string.IsNullOrWhiteSpace(json)
                ? new TestCorruptSaveRequest()
                : JsonConvert.DeserializeObject<TestCorruptSaveRequest>(json)
                    ?? new TestCorruptSaveRequest();
        }
        catch (Exception ex)
        {
            return new TestSaveFileOpResponse
            {
                Success = false,
                Error = $"Failed to parse body: {ex.Message}",
            };
        }

        if (string.IsNullOrEmpty(body.TargetSaveName))
        {
            return new TestSaveFileOpResponse
            {
                Success = false,
                Error = "TargetSaveName required",
            };
        }

        try
        {
            var source = string.IsNullOrEmpty(body.SourceSaveName)
                ? Constants.SaveFolderName
                : body.SourceSaveName;
            // CloneSaveFolder derives the real folder name (re-stamped uniqueID); return it so the
            // caller imports the actual corrupted folder with SkipClone.
            var actualTarget = CloneSaveFolder(source, body.TargetSaveName);

            // Mangle the clone's main file into non-well-formed XML so XmlDocument.Load throws.
            var mainFile = System.IO.Path.Combine(Constants.SavesPath, actualTarget, actualTarget);
            System.IO.File.WriteAllText(mainFile, "<SaveGame><player>NOT WELL FORMED");
            return new TestSaveFileOpResponse { Success = true, TargetSaveName = actualTarget };
        }
        catch (Exception ex)
        {
            return new TestSaveFileOpResponse { Success = false, Error = ex.Message };
        }
    }

    [ApiEndpoint(
        "GET",
        "/test/save_tmp_exists",
        Summary = "Whether a leftover .tmp exists next to a save's main file (test-only)",
        Tag = "Test"
    )]
    [ApiResponse(typeof(TestSaveFileOpResponse), 200)]
    private TestSaveFileOpResponse HandleGetTestSaveTmpExists(HttpListenerRequest request)
    {
        var saveName = request.QueryString["saveName"];
        if (string.IsNullOrEmpty(saveName))
        {
            return new TestSaveFileOpResponse
            {
                Success = false,
                Error = "saveName query required",
            };
        }
        if (!IsSafeSaveName(saveName))
        {
            return new TestSaveFileOpResponse
            {
                Success = false,
                Error = "saveName must be a bare folder name (no path separators)",
            };
        }
        try
        {
            var tmp = System.IO.Path.Combine(Constants.SavesPath, saveName, saveName + ".tmp");
            return new TestSaveFileOpResponse
            {
                Success = true,
                Exists = System.IO.File.Exists(tmp),
            };
        }
        catch (Exception ex)
        {
            return new TestSaveFileOpResponse { Success = false, Error = ex.Message };
        }
    }
}
