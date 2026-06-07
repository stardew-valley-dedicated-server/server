using JunimoServer.Services.CropSaver;
using JunimoServer.Services.Lobby;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace JunimoServer.Services.Api
{
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
            string method, string path,
            HttpListenerRequest request, HttpListenerResponse response)
        {
            switch (method)
            {
                case "GET":
                    switch (path)
                    {
                        case "/test/crops":
                            await WriteJsonAsync(response, await HandleGetTestCropsAsync());
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
                            await WriteJsonAsync(response, await HandlePostTestHouseUpgradeAsync(request));
                            return;
                        case "/test/stamp_claim":
                            await WriteJsonAsync(response, await HandlePostTestStampClaimAsync());
                            return;
                    }
                    break;
            }

            await WriteNotFoundAsync(response, path);
        }

        [ApiEndpoint("GET", "/test/crops", Summary = "Enumerate every HoeDirt-with-crop in the world (test-only)", Tag = "Test")]
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
                            if (feature is not HoeDirt dirt) continue;
                            var crop = dirt.crop;
                            if (crop == null) continue;
                            crops.Add(new TestCrop
                            {
                                LocationName = locName,
                                TileX = (int)dirt.Tile.X,
                                TileY = (int)dirt.Tile.Y,
                                IsAlive = !crop.dead.Value,
                                IsInPot = false,
                                SeedItemId = crop.netSeedIndex.Value,
                                IsManaged = CropSaverOverrides.IsManaged(locName, dirt.Tile)
                            });
                        }

                        foreach (var pot in location.Objects.Values.OfType<IndoorPot>())
                        {
                            var dirt = pot.hoeDirt.Value;
                            if (dirt == null) continue;
                            var crop = dirt.crop;
                            if (crop == null) continue;
                            crops.Add(new TestCrop
                            {
                                LocationName = locName,
                                TileX = (int)pot.TileLocation.X,
                                TileY = (int)pot.TileLocation.Y,
                                IsAlive = !crop.dead.Value,
                                IsInPot = true,
                                SeedItemId = crop.netSeedIndex.Value,
                                IsManaged = CropSaverOverrides.IsManaged(locName, pot.TileLocation)
                            });
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

        [ApiEndpoint("POST", "/test/set_date", Summary = "Set season/day/year directly (test-only)", Tag = "Test")]
        [ApiResponse(typeof(TestSetDateResponse), 200)]
        private async Task<TestSetDateResponse> HandlePostTestSetDateAsync(HttpListenerRequest request)
        {
            TestSetDateRequest? body = null;
            try
            {
                using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
                var json = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    body = JsonConvert.DeserializeObject<TestSetDateRequest>(json);
                }
            }
            catch (Exception ex)
            {
                return new TestSetDateResponse { Success = false, Error = $"Failed to parse body: {ex.Message}" };
            }

            if (body == null || string.IsNullOrEmpty(body.Season))
            {
                return new TestSetDateResponse { Success = false, Error = "Missing 'season' in request body" };
            }

            if (!Enum.TryParse<Season>(body.Season, ignoreCase: true, out var season))
            {
                return new TestSetDateResponse { Success = false, Error = $"Invalid season '{body.Season}' (expected spring/summer/fall/winter)" };
            }

            if (body.Day < 1 || body.Day > 28)
            {
                return new TestSetDateResponse { Success = false, Error = $"Day {body.Day} out of range (1-28)" };
            }

            if (body.Year < 1)
            {
                return new TestSetDateResponse { Success = false, Error = $"Year {body.Year} out of range (>= 1)" };
            }

            await RunOnGameThreadAsync(() =>
            {
                Game1.season = season;
                Game1.dayOfMonth = body.Day;
                Game1.year = body.Year;
                // Writes to Game1.season/dayOfMonth/year mirror NetWorldState
                // fields that replicate to peers. Without this push, the next
                // peer's sendServerIntroduction snapshot carries the previous
                // day's stale NetField — see decompiled Game1.cs:8264 for
                // Stardew's own day-end usage of the same primitive.
                Game1.netWorldState.Value.UpdateFromGame1();
            });

            Monitor.Log($"Date set to {season} {body.Day}, year {body.Year} via test API", LogLevel.Info);

            return new TestSetDateResponse
            {
                Success = true,
                Season = season.ToString().ToLowerInvariant(),
                Day = body.Day,
                Year = body.Year
            };
        }

        [ApiEndpoint("POST", "/test/farmevent", Summary = "Queue an overnight FarmEvent for the next night (test-only)", Tag = "Test")]
        [ApiResponse(typeof(TestFarmEventResponse), 200)]
        private async Task<TestFarmEventResponse> HandlePostTestFarmEventAsync(HttpListenerRequest request)
        {
            // Game1.farmEventOverride is consumed by _newDayAfterFade as the fallback when no random
            // farm event is picked (decompiled Game1.cs:8588), so this deterministically queues the
            // event for the next overnight transition without seeding mail or stats.
            var type = request.QueryString["type"]?.ToLowerInvariant() ?? "qiplane";

            StardewValley.Events.FarmEvent? farmEvent = type switch
            {
                "qiplane" => new StardewValley.Events.QiPlaneEvent(),
                _ => null
            };

            if (farmEvent == null)
            {
                return new TestFarmEventResponse { Success = false, Error = $"Unknown farm event type '{type}' (supported: qiplane)" };
            }

            await RunOnGameThreadAsync(() => Game1.farmEventOverride = farmEvent);

            Monitor.Log($"Queued overnight farm event '{type}' for the next night via test API", LogLevel.Info);

            return new TestFarmEventResponse { Success = true, Type = type };
        }

        [ApiEndpoint("POST", "/test/saver_crop", Summary = "Mutate a CropSaver entry (test-only)", Tag = "Test")]
        [ApiResponse(typeof(TestSaverCropResponse), 200)]
        private async Task<TestSaverCropResponse> HandlePostTestSaverCropAsync(HttpListenerRequest request)
        {
            TestSaverCropRequest? body = null;
            try
            {
                using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
                var json = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    body = JsonConvert.DeserializeObject<TestSaverCropRequest>(json);
                }
            }
            catch (Exception ex)
            {
                return new TestSaverCropResponse { Success = false, Error = $"Failed to parse body: {ex.Message}" };
            }

            if (body == null || string.IsNullOrEmpty(body.LocationName))
            {
                return new TestSaverCropResponse { Success = false, Error = "Missing 'locationName' in request body" };
            }

            var loader = JunimoServer.Services.CropSaver.CropSaver.Instance?.DataLoader;
            if (loader == null)
            {
                return new TestSaverCropResponse { Success = false, Error = "CropSaver not initialized" };
            }

            SDate? newDatePlanted = null;
            if (body.DatePlanted != null)
            {
                if (string.IsNullOrEmpty(body.DatePlanted.Season)
                    || !Enum.TryParse<Season>(body.DatePlanted.Season, ignoreCase: true, out var dpSeason))
                {
                    return new TestSaverCropResponse
                    {
                        Success = false,
                        Error = $"Invalid datePlanted.season '{body.DatePlanted.Season}'"
                    };
                }
                if (body.DatePlanted.Day < 1 || body.DatePlanted.Day > 28)
                {
                    return new TestSaverCropResponse
                    {
                        Success = false,
                        Error = $"datePlanted.day {body.DatePlanted.Day} out of range (1-28)"
                    };
                }
                if (body.DatePlanted.Year < 1)
                {
                    return new TestSaverCropResponse
                    {
                        Success = false,
                        Error = $"datePlanted.year {body.DatePlanted.Year} out of range (>= 1)"
                    };
                }
                newDatePlanted = new SDate(body.DatePlanted.Day, dpSeason.ToString().ToLowerInvariant(), body.DatePlanted.Year);
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
                        Error = $"No SaverCrop entry at {body.LocationName} ({body.TileX},{body.TileY})"
                    };
                    return;
                }

                if (body.ExtraDays.HasValue) saverCrop.extraDays = body.ExtraDays.Value;
                if (body.OwnerId.HasValue) saverCrop.ownerId = body.OwnerId.Value;
                if (newDatePlanted != null) saverCrop.datePlanted = newDatePlanted;

                result = new TestSaverCropResponse
                {
                    Success = true,
                    Found = true,
                    ExtraDays = saverCrop.extraDays,
                    OwnerId = saverCrop.ownerId
                };
            });

            return result;
        }

        [ApiEndpoint("POST", "/test/house_upgrade", Summary = "Run a debug house-upgrade command on the host to verify it's blocked (test-only)", Tag = "Test")]
        [ApiResponse(typeof(TestHouseUpgradeResponse), 200)]
        private async Task<TestHouseUpgradeResponse> HandlePostTestHouseUpgradeAsync(HttpListenerRequest request)
        {
            var command = request.QueryString["command"];
            if (string.IsNullOrWhiteSpace(command))
            {
                return new TestHouseUpgradeResponse { Success = false, Error = "Missing 'command' query parameter" };
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

        [ApiEndpoint("POST", "/test/stamp_claim", Summary = "Stamp a synthetic abandoned slot claim onto an uncustomized homed farmhand (test-only)", Tag = "Test")]
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
                        if (!building.isCabin || LobbyService.IsLobbyCabin(building)) continue;

                        var cabin = building.GetIndoors<Cabin>();
                        var owner = cabin?.owner;
                        if (owner == null) continue;
                        if (owner.isCustomized.Value) continue;
                        if (!string.IsNullOrEmpty(owner.userID.Value)) continue;

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

                    result.Error = "No uncustomized, unclaimed cabin slot with an owner entry found to stamp";
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
    }
}
