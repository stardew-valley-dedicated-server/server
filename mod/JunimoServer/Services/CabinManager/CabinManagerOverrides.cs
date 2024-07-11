using JunimoServer.Services.PersistentOption;
using JunimoServer.Util;
using Microsoft.Xna.Framework;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Network;
using System;
using System.IO;
using System.Linq;

namespace JunimoServer.Services.CabinManager
{
    public class CabinManagerOverrides
    {
        private static IMonitor _monitor;
        private static IModHelper _helper;
        private static CabinManagerData _cabinManagerData;
        private static PersistentOptions _options;
        private static Action<long> _onServerJoined;
        private static Multiplayer _multiplayer;


        public static void Initialize(IModHelper helper, IMonitor monitor, PersistentOptions options, Action<long> onServerJoined)
        {
            _helper = helper;
            _monitor = monitor;
            _options = options;
            _onServerJoined = onServerJoined;
        }

        public static void SetCabinManagerData(CabinManagerData cabinManagerData)
        {
            _cabinManagerData = cabinManagerData;
        }

        public static void sendServerIntroduction_Postfix(long peer)
        {
            _monitor.Log($"sendServerIntroduction_Postfix: {peer}", LogLevel.Debug);
            _onServerJoined?.Invoke(peer);
        }


        public static void processIncomingMessage_Postfix(Multiplayer __instance, IncomingMessage msg)
        {
            _monitor.Log($"Called processIncomingMessage_Prefix type: {msg.MessageType}");

            var allCabins = Game1.getFarm().buildings.Where(building => building.isCabin);

            foreach (var cabins in allCabins)
            {
                var indoor = (Cabin)cabins.indoors.Value;
                _monitor.Log($"cabin: {indoor.NameOrUniqueName}");
                foreach (var warp in indoor.warps)
                {
                    _monitor.Log($"warp: {warp.TargetX},{warp.TargetY}");
                }
            }

            if (msg.MessageType != 8)
            {
                return;
            }

            NPC character = _helper.Reflection.GetMethod(__instance, "readNPC").Invoke<NPC>(msg.Reader);
            _monitor.Log("Warping Player");
            GameLocation location = _helper.Reflection.GetMethod(__instance, "readLocation").Invoke<GameLocation>(msg.Reader);
            _monitor.Log(location.NameOrUniqueName);

            if (character != null && location != null)
            {
                var tileVec = msg.Reader.ReadVector2();
                _monitor.Log($"loc {location.NameOrUniqueName} tile: {tileVec.X}, {tileVec.Y}");

                Game1.warpCharacter(character, location, tileVec);
            }

            // _monitor.Log($"Farm has # buildings: {Game1.getFarm().buildings.Count}");
        }

        public static Building GetHiddenPlayerCabin(Farm farm, long playerId)
        {
            return farm.buildings
                .Where(building => building.isCabin)
                .FirstOrDefault(building =>
                    ((Cabin)building.indoors.Value).owner.UniqueMultiplayerID == playerId
                    && IsBuildingInHiddenStack(building)
                );
        }

        public static bool IsPlayersCabinInStack(Farm farm, long playerId)
        {
            var cabin = farm.buildings
                .Where(building => building.isCabin)
                .FirstOrDefault(building =>
                    ((Cabin)building.indoors.Value).owner.UniqueMultiplayerID == playerId
                );

            return cabin != null && IsBuildingInHiddenStack(cabin);
        }


        public static Building GetOwnersCabin(Farm farm)
        {
            return farm.buildings
                .Where(building => building.isCabin)
                .FirstOrDefault(building =>
                    ((Cabin)building.indoors.Value).owner.UniqueMultiplayerID == _helper.GetOwnerPlayerId());
        }

        public static bool IsBuildingInHiddenStack(Building building)
        {
            return building.tileX.Value == CabinManagerService.HiddenCabinX
                   && building.tileY.Value == CabinManagerService.HiddenCabinY;
        }


        public static void UpdateAllCabinsWarps()
        {
            foreach (Building cabin in Game1.getFarm().buildings.Where(building => building.isCabin))
            {
                Cabin indoors = (Cabin)cabin.indoors.Value;
                UpdateCabinWarps(indoors);
            }
        }

        // warps are not saved in XML so this should be fine to write to the game's state directly
        public static void UpdateCabinWarps(Cabin cabin)
        {
            var farm = Game1.getFarm(); // dont mutate this, we arent copying like in the other methods

            if (_options.Data.CabinStrategy == CabinStrategy.FarmhouseStack)
            {
                foreach (Warp warp in cabin.warps.Where(warp => warp.TargetName == "Farm"))
                {
                    warp.TargetX = farm.GetMainFarmHouseEntry().X;
                    warp.TargetY = farm.GetMainFarmHouseEntry().Y;
                }

                return;
            }

            var cabinStack = GetStackLocation(farm);
            var isCabinHidden = IsPlayersCabinInStack(farm, cabin.owner.UniqueMultiplayerID);

            if (!isCabinHidden) return;


            var ownerPlayerId = _helper.GetOwnerPlayerId();
            var isOwnersCabin = ownerPlayerId == cabin.owner.UniqueMultiplayerID;

            var stackX = (int)Math.Round(cabinStack.Location.X, 0);
            var stackY = (int)Math.Round(cabinStack.Location.Y, 0);


            if (!isOwnersCabin)
            {
                foreach (Warp warp in cabin.warps.Where(warp => warp.TargetName == "Farm"))
                {
                    warp.TargetX = stackX + 2;
                    warp.TargetY = stackY + 2;
                }
            }

            // if were not using the owners cabin to act as the stack position
            // handle updating owner cabin warp to farmhouse entrance
            var cabinStackChosenCabinOwner = cabinStack.CabinChosen != null
                ? ((Cabin)cabinStack.CabinChosen.indoors.Value).owner.UniqueMultiplayerID
                : 0;
            var ownersCabinIsBeingStackedOn = cabinStackChosenCabinOwner == ownerPlayerId;

            if (isOwnersCabin && !ownersCabinIsBeingStackedOn)
            {
                foreach (Warp warp in cabin.warps.Where(warp => warp.TargetName == "Farm"))
                {
                    warp.TargetX = farm.GetMainFarmHouseEntry().X;
                    warp.TargetY = farm.GetMainFarmHouseEntry().Y;
                }
            }
        }

        // farm is mutated so make a copy
        public static void UpdateCabins(Farm farm, long playerId)
        {

            Farmer farmer = Game1.getFarmer(playerId);
            var cabinStack = GetStackLocation(farm);
            var cabinToMove = GetHiddenPlayerCabin(farm, playerId);


            if (_options.Data.CabinStrategy == CabinStrategy.FarmhouseStack)
            {
                return;
            }

            // CabinStack Strategy
            var ownerPlayerId = _helper.GetOwnerPlayerId();
            var ownersCabin = GetOwnersCabin(farm);

            var stackX = (int)Math.Round(cabinStack.Location.X, 0);
            var stackY = (int)Math.Round(cabinStack.Location.Y, 0);

            // make sure a cabin is in the stack if player's cabin was moved out of it so they see something
            // CabinChosen is used for legacy stardew servers that never saved a DefaultCabinLocation, so it saves a cabin
            if (cabinToMove == null && cabinStack.CabinChosen == null)
            {
                var firstNonOwnerStackedCabin = farm.buildings
                    .FirstOrDefault(building =>
                        building.isCabin
                        && IsBuildingInHiddenStack(building)
                        && ((Cabin)building.indoors.Value).owner.UniqueMultiplayerID != ownerPlayerId
                    );

                cabinToMove = firstNonOwnerStackedCabin ?? ownersCabin;
            }

            // move cabin
            if (cabinToMove != null)
            {
                //hide the building underneath where cabin will be placed
                if (cabinStack.CabinChosen != null)
                {
                    cabinStack.CabinChosen.tileX.Value = CabinManagerService.HiddenCabinX - 1;
                    cabinStack.CabinChosen.tileY.Value = CabinManagerService.HiddenCabinY - 1;
                }

                cabinToMove.tileX.Value = stackX;
                cabinToMove.tileY.Value = stackY;

                var cabinGameLocation = (Cabin)cabinToMove.indoors.Value;
                if (cabinGameLocation.owner.UniqueMultiplayerID != ownerPlayerId)
                {
                    foreach (var warp in cabinGameLocation.warps.Where(warp => warp.TargetName == "Farm"))
                    {
                        warp.TargetX = stackX + 2;
                        warp.TargetY = stackY + 2;
                    }
                }
            }

            // TODO: This was working immediately after the migration to 1.6.5, but broke with 1.6.8!?
            //       No idea what changed, but "ownersCabin" is null here and throws. Imo we don't need it anyway,
            //       we adjust warps above already and overriding them with the actual cabin reference position is redundant.
            //       We are stacking them on the same location, so we should be able to just rely on what we do above.
            //       Leaving this here for reference though, cabin management is broken and I'm really not 100% sure.

            // If were not using the owners cabin to act as the stack position
            // Handle updating owner cabin warp to farmhouse entrance
            //var cabinStackChosenCabinOwner = cabinStack.CabinChosen != null
            //    ? ((Cabin)cabinStack.CabinChosen.indoors.Value).owner.UniqueMultiplayerID
            //    : 0;
            //var ownersCabinIsBeingStackedOn = cabinStackChosenCabinOwner == ownerPlayerId;
            //if (!ownersCabinIsBeingStackedOn)
            //{
            //    foreach (var warp in ((Cabin)ownersCabin.indoors.Value).warps.Where(warp => warp.TargetName == "Farm"))
            //    {
            //        warp.TargetX = farm.GetMainFarmHouseEntry().X;
            //        warp.TargetY = farm.GetMainFarmHouseEntry().Y;
            //    }
            //}
        }

        private static StackLocation GetStackLocation(Farm farm)
        {
            if (_cabinManagerData.DefaultCabinLocation.HasValue)
            {
                return new StackLocation(_cabinManagerData.DefaultCabinLocation.Value, null);
            }

            var firstNonHiddenCabin = farm.buildings
                .FirstOrDefault(building => building.isCabin && !IsBuildingInHiddenStack(building));
            if (firstNonHiddenCabin != null)
            {
                var cabinLocation = new Vector2(firstNonHiddenCabin.tileX.Value, firstNonHiddenCabin.tileY.Value);
                return new StackLocation(cabinLocation, firstNonHiddenCabin);
            }

            return new StackLocation(new Vector2(50, 14), null);
        }

        private struct StackLocation
        {
            public Vector2 Location;
            public Building CabinChosen;

            public StackLocation(Vector2 location, Building cabinChosen)
            {
                Location = location;
                CabinChosen = cabinChosen;
            }
        }


        public static void sendMessage_Prefix(GameServer __instance, long peerId, ref OutgoingMessage message)
        {
            var isLocation = message.MessageType == Multiplayer.locationIntroduction;
            var isLocationDelta = message.MessageType == Multiplayer.locationDelta;

            if (!isLocation && !isLocationDelta)
            {
                return;
            }

            _multiplayer ??= _helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();
            using IncomingMessage incMsg = new IncomingMessage();
            using (MemoryStream memoryStream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(memoryStream))
                {
                    message.Write(writer);
                    memoryStream.Position = 0;
                    using (BinaryReader reader = new BinaryReader(memoryStream))
                    {
                        incMsg.Read(reader);
                    }
                }
            }


            if (isLocation)
            {
                var forceCurrentLocation = incMsg.Reader.ReadBoolean();
                var netRoot = NetRoot<GameLocation>.Connect(incMsg.Reader);
                var loc = netRoot.Value;

                if (loc is Farm farm)
                {
                    UpdateCabins(farm, peerId);
                }


                message = new OutgoingMessage(
                    Multiplayer.locationIntroduction,
                    Game1.serverHost.Value,
                    forceCurrentLocation,
                    _multiplayer.writeObjectFullBytes(netRoot, peerId)
                );
            }

            if (isLocationDelta)
            {
                var isStructure = incMsg.Reader.ReadByte() > 0;
                var locationName = incMsg.Reader.ReadString();

                if (locationName.StartsWith("Cabin"))
                {
                    var loc = Game1.getLocationFromName(locationName, isStructure);
                    if (loc is Cabin cabin)
                    {
                        UpdateCabinWarps(cabin);
                    }
                }
            }
        }
    }
}