# Client Manipulation Techniques

This document catalogs server-to-client control mechanisms discovered through static analysis of the decompiled Stardew Valley 1.6.15 source code. These techniques enable a dedicated server mod to communicate with, control, and deliver content to connected game clients without requiring client-side mods.

::: warning Internal Reference
This is a developer reference for the dedicated server project. The mechanisms described here are part of the game's normal networking protocol. They are not exploits against players. All techniques operate within the host authority model that the game already uses.
:::

## Assumption Validation

The following table summarizes which assumptions about client limitations were confirmed or invalidated by the analysis.

| # | Assumption | Status | Finding |
|---|-----------|--------|---------|
| 1 | Server can directly modify farmhand inventory | **Confirmed** | `Farmer.Items` is a `NetRef<Inventory>` on the farmer root; server writes propagate via delta sync (msg type 0) |
| 2 | Server can grant items via mail | **Confirmed** | `%item` and `%action` commands in mail data execute client-side when opened |
| 3 | Server can force events on clients | **Confirmed** | Message type 4 warps the client and starts any event by ID |
| 4 | `modData` syncs in multiplayer | **Confirmed** | `ModDataDictionary` extends `NetStringDictionary`, fully synced and persisted |
| 5 | Server validates client inventory changes | **Invalidated** | No server-side validation. Clients send farmer deltas and the server propagates them as-is |
| 6 | NPC dialogue is synced over the network | **Invalidated** | Dialogue is resolved client-side from data assets. NPC dialogue stacks are local |
| 7 | Mail item grants are validated server-side | **Invalidated** | `LetterViewerMenu.HandleItemCommand()` runs entirely on the client |
| 8 | Event rewards are validated server-side | **Invalidated** | Events run locally. `Event.DefaultCommands.AddItem()` calls `Game1.player.addItemByMenuIfNecessary()` |
| 9 | Debug commands are accessible in production | **Invalidated** | Gated by `Program.enableCheats` or `Game1.isRunningMacro`, not available to normal clients |
| 10 | `FarmerTeam` is client-writable | **Partially** | Team state uses `RequestPlayerAction` pattern; farmhands must request changes through the host authority |

## Networking Architecture

### Message Types

The multiplayer protocol uses numbered message types. The server dispatches incoming messages in `Multiplayer.processIncomingMessage()` (line 1565). Messages flagged in `isClientBroadcastType()` (line 216) are relayed to all clients.

| Type | Name | Direction | Purpose |
|------|------|-----------|---------|
| 0 | `farmerDelta` | Bidirectional | Delta sync of `NetFarmerRoot` (inventory, mail, stats, modData) |
| 1 | `serverIntroduction` | Server → Client | Full serialized host farmer, team root, and world state |
| 2 | `playerIntroduction` | Server → Client | New player joined, broadcasts farmer data |
| 3 | `activeLocation` | Client → Server | Client reports active location |
| 4 | `eventRequest` | Server → Client | Force client to warp and play an event |
| 6 | `locationDelta` | Server → Client | Delta sync of `GameLocation` root (objects, terrain, NPCs) |
| 7 | `temporaryAnimatedSprites` | Server → Client | Broadcast visual/audio sprites to a location |
| 8 | `warpCharacter` | Server → Client | Warp an NPC to a location |
| 10 | `chatMessage` | Bidirectional | Player chat messages |
| 12 | `worldState` | Server → Client | `NetWorldState` sync |
| 13 | `teamDelta` | Server → Client | `FarmerTeam` delta sync |
| 14 | `newDaySync` | Bidirectional | Day transition synchronization barriers |
| 15 | `chatInfoMessage` | Server → Client | System chat notifications (translation key + args) |
| 17 | `farmerGainExperience` | Server → Client | Shared experience gains |
| 18 | `serverToClientsMessage` | Server → Client | String-switched commands (`festivalEvent`, `endFest`, `trainApproach`) |
| 19 | `playerDisconnected` | Server → Client | Player left notification |
| 20 | `sharedAchievement` | Server → Client | Achievement unlock broadcast |
| 21 | `globalMessage` | Server → Client | HUD message broadcast (translation key + token args) |
| 22 | `partyWideMail` | Server → Client | Deliver mail to all players |
| 23 | `forceKick` | Server → Client | Forcibly disconnect a client |
| 24 | `removeLocationFromLookup` | Server → Client | Remove a location from the client's lookup |
| 25 | `farmerKilledMonster` | Server → Client | Shared monster kill stat |
| 26 | `requestGrandpaReevaluation` | Server → Client | Trigger grandpa shrine re-evaluation |
| 27 | `nutDig` | Server → Client | Golden walnut dig notification |
| 28 | `passoutRequest` | Bidirectional | Passout synchronization request |
| 29 | `passout` | Server → Client | Passout execution |
| 30 | `startNewDaySync` | Server → Client | Signals server ready for new day |
| 31 | `readySync` | Bidirectional | Ready check barriers (sleep, festivals) |
| 32 | `chestHitSync` | Bidirectional | Chest interaction sync |
| 33 | `dedicatedServerSync` | Bidirectional | Dedicated server specific sync |
| 127 | `compressed` | N/A | Compressed message wrapper (should be decompressed before dispatch) |

### Delta Sync Model

State synchronization uses Netcode delta serialization. Each tick (every `farmerDeltaBroadcastPeriod = 3` ticks), dirty NetField trees are serialized as binary deltas and broadcast:

```
Multiplayer.broadcastFarmerDeltas()  →  writeObjectDeltaBytes(farmerRoot)
                                     →  sendMessage(peerId, type=0, deltaBytes)
```

**Authority model**: Each player owns their own `NetFarmerRoot`. The client sends deltas to the server, and the server propagates them to other clients. The server (host) owns all `GameLocation` roots, `NetWorldState`, and `FarmerTeam`.

**Key implication**: The server can write to any farmhand's `NetFarmerRoot` fields (inventory, mail, modData, stats) and the changes will propagate to that client via delta sync. There is no client-side rejection of server-originated deltas on the farmer root.

```
Server writes to farmer.Items[0] = new Item(...)
  → NetFarmerRoot marks dirty
  → Next broadcastFarmerDeltas() sends delta to client
  → Client applies delta, item appears in inventory
```

### Server Introduction

When a farmhand connects, `GameServer.sendServerIntroduction()` sends a full serialized snapshot:

```csharp
// Network/GameServer.cs:396
sendMessage(peer, new OutgoingMessage(1, Game1.serverHost.Value,
    Game1.multiplayer.writeObjectFullBytes(Game1.serverHost, peer),     // Host farmer
    Game1.multiplayer.writeObjectFullBytes(Game1.player.teamRoot, peer), // Team state
    Game1.multiplayer.writeObjectFullBytes(Game1.netWorldState, peer))); // World state
```

This means any state the server has set on the host farmer, team, or world is delivered to connecting clients immediately.

## Leverage Mechanisms

Ranked by feasibility and reliability for dedicated server use.

### Tier 1: High Feasibility

#### modData Data Channel

`ModDataDictionary` is present on almost every game object and provides an arbitrary `string → string` key-value store that is both **net-synced** and **save-persisted**.

**Objects with modData**:

| Object | File | Line |
|--------|------|------|
| `Farmer` (via `Character`) | `Character.cs` | 515 |
| `Item` | `Item.cs` | 87 |
| `GameLocation` | `GameLocation.cs` | 631 |
| `Building` | `Building.cs` | 161 |
| `TerrainFeature` | `TerrainFeature.cs` | 37 |
| `Crop` | `Crop.cs` | 191 |
| `Quest` | `Quest.cs` | 102 |
| `Projectile` | `Projectile.cs` | 196 |

**How it works**: `ModDataDictionary` extends `NetStringDictionary<string, NetString>`. It's added to the parent object's `NetFields`, so changes propagate via the parent's delta sync. The server can write:

```csharp
farmer.modData["JunimoServer.Notification"] = "Welcome to the server!";
farmer.modData["JunimoServer.Config"] = jsonPayload;
```

These values sync to the client automatically and persist across saves. This makes `modData` the most reliable general-purpose data channel from server to client.

**Limitations**: Client-side code must poll or observe the modData to act on it. Without a client mod, modData values are inert data. The vanilla game only reads specific modData keys it knows about.

#### Direct Inventory Mutation

The server holds a reference to each farmhand's `Farmer` object in `Game1.otherFarmers`. Since `Farmer.Items` is a `NetRef<Inventory>` containing a `NetObjectList<Item>`, direct writes propagate via delta sync.

```csharp
// Server-side: grant an item to a farmhand
Item item = ItemRegistry.Create("(O)388", amount: 99); // 99 Wood
farmer.Items[farmer.Items.IndexOf(null)] = item;        // Place in first empty slot
```

**Caveats**:
- `Farmer.OnItemReceived` (line 4840) only fires for `IsLocalPlayer`, so HUD pickup notifications won't display on the client
- The client will see the item appear in their inventory silently
- Stack limits and slot validation must be handled by the server (there is no automatic enforcement)
- Max inventory size is 36 slots (`Farmer.maxInventorySpace`)

#### Mail System

The mail system is a powerful mechanism because it supports embedded commands that execute client-side when the player reads the letter.

**Delivery methods**:

1. **Direct field write**: Add mail IDs to `farmer.mailbox` (appears immediately) or `farmer.mailForTomorrow`
2. **Party-wide broadcast**: Message type 22 via `broadcastPartyWideMail()` delivers to all players

```csharp
// Multiplayer.cs:1475
protected virtual void receivePartyWideMail(IncomingMessage msg)
{
    string mail_key = msg.Reader.ReadString();
    PartyWideMessageQueue message_queue = (PartyWideMessageQueue)msg.Reader.ReadInt32();
    bool no_letter = msg.Reader.ReadBoolean();
    _performPartyWideMail(mail_key, message_queue, no_letter);
}
```

**Mail content commands** (parsed by `LetterViewerMenu`):

| Command | Syntax | Effect |
|---------|--------|--------|
| Item by ID | `%item id <qualifiedId> [count]%%` | Creates any item via `ItemRegistry.Create()` |
| Object | `%item object <id> <count>%%` | Random object from list |
| Money | `%item money <min> [max]%%` | Grants gold directly |
| Cooking recipe | `%item cookingrecipe <name>%%` | Teaches a cooking recipe |
| Crafting recipe | `%item craftingrecipe <name>%%` | Teaches a crafting recipe |
| Quest | `%item quest <questId> [immediate]%%` | Attaches a quest |
| Trigger action | `%action <action_string>%%` | Runs any `TriggerActionManager` action |

The `%action` command is particularly powerful. It can invoke any registered trigger action including `AddItem`, `AddMoney`, `AddBuff`, `AddQuest`, `AddFriendshipPoints`, and more.

**The `%&NL&%` suffix**: Appending this to a mail key sets the mail flag without showing a letter. Useful for setting game state flags silently.

::: tip
Mail requires the key to exist in `Data/mail` for the letter content to render. The server mod can add custom mail entries via SMAPI's content API.
:::

#### Forced Events

Message type 4 forces a client to warp to a location and play an event.

```csharp
// Multiplayer.cs:1607
case 4:
    string eventId = msg.Reader.ReadString();
    bool flag = msg.Reader.ReadBoolean();      // Use local player as actor?
    bool notify_when_done = msg.Reader.ReadBoolean();
    tileX = msg.Reader.ReadInt32();
    tileY = msg.Reader.ReadInt32();
    request = readLocationRequest(msg.Reader);  // Target location
    // ... creates cloned farmer actor, warps, starts event
```

Events run entirely client-side with full access to event commands including:

- `addItem`: grants items to the player
- `awardFestivalPrize`: grants festival rewards
- `action`: runs arbitrary trigger actions
- `mail`: adds mail flags
- `friendship`: modifies friendship values
- `removeItem`: removes items from inventory

**Constraint**: The event ID must exist in the location's event data (`location.findEventById(eventId)` must return non-null).

### Tier 2: Medium Feasibility

#### NPC Dialogue Manipulation

NPC dialogue is resolved client-side from data assets, not synced over the network. However, dialogue supports powerful embedded commands.

**Dialogue commands** (parsed in `Dialogue.cs`):

| Command | Syntax | Effect |
|---------|--------|--------|
| `$action` | `$action <triggerAction>` | Runs a `TriggerActionManager` action when displayed |
| Item grant | `[qualifiedItemId]` in text | Creates and gives the item to the player |
| `$v` | `$v <eventId>` | Triggers playing a game event |
| `$t` | `$t <topicId> <days>` | Adds a conversation topic |

The `$action` command (line 624 of `Dialogue.cs`) is the most flexible:

```csharp
dialogues.Add(new DialogueLine("", delegate
{
    TriggerActionManager.TryRunAction(commandArgs, out var error2, out var exception);
}));
```

**Server leverage**: If the server mod patches `Data/Characters` or dialogue files via SMAPI's content API, NPCs will speak the modified dialogue to players, executing any embedded commands.

#### Location Object and Debris Spawning

The server controls location state via `GameLocation` root deltas (message type 6). Objects placed on the map sync to clients automatically.

```csharp
// Place an object in a location
Object obj = new Object("388", 1); // Wood
location.objects.Add(new Vector2(10, 10), obj);
// → Syncs to all clients in the location via delta
```

The server can also broadcast `TemporaryAnimatedSprite` instances (message type 7) for visual/audio effects:

```csharp
// Multiplayer.cs:1587
case 7:
    location = readLocation(msg.Reader);
    if (location != null)
    {
        readSprites(msg.Reader, location, delegate(TemporaryAnimatedSprite sprite)
        {
            location.temporarySprites.Add(sprite);
        });
    }
```

Sprites support `text` fields for display, `startSound`/`endSound` for audio cues, and full position/motion physics.

#### Global Messages

Message type 21 pushes HUD notification text to all clients.

```csharp
// Multiplayer.cs:811
broadcastGlobalMessage(string translationKey, bool onlyShowIfEmpty,
    GameLocation location, params string[] substitutions)
```

The `substitutions` pass through `TokenParser.ParseText()` which resolves tokens like `[LocalizedText ...]`, `[ItemName qualifiedId]`, etc. The `translationKey` falls back to displaying the raw string if the key is not found in content.

**Use case**: Server notifications, welcome messages, announcements. Purely visual, no state changes.

#### FarmerTeam Shared State

`FarmerTeam` is synced via team delta (message type 13). It contains shared state fields including:

- `broadcastedMail`: party-wide mail tracking
- `specialOrders`: active special orders
- `completedSpecialOrders`: completed order tracking
- `luauIngredients`, `grangeDisplay`: festival state
- `sharedDailyLuck`: luck value
- `toggleMineShrineOvernight`, `toggleSkullShrineOvernight`: mine difficulty

The server (host) is the authority on team state. Farmhand modifications go through `RequestPlayerAction()` which routes the request to the host for approval.

### Tier 3: Low Feasibility

#### TemporaryAnimatedSprite Text

Sprites broadcast via message type 7 can carry a `text` field (line 1102 of `TemporaryAnimatedSprite.cs`). This text renders at the sprite's position in the game world.

**Limitations**: Text is purely visual, no interactivity. The sprite must be in the player's current location to be visible. Ephemeral; does not persist across location changes or saves.

#### Sign Text Tokens

`Object.signText` (line 448 of `Object.cs`) passes through `TokenParser.ParseText()` on change (line 836) and then `Utility.FilterDirtyWords()`. Sign objects placed by the server can display tokenized text.

**Limitations**: Requires placing a sign object in the game world. Text is filtered for profanity. Read-only display only.

## Exploit Surface

::: warning
This section documents potential abuse vectors in the game's networking code. Understanding these helps the dedicated server project avoid introducing vulnerabilities and properly validate inputs.
:::

### Debug Command Paths

Debug/cheat commands in `ChatCommands.cs` are gated:

```csharp
// ChatCommands.cs:530
public static bool AllowCheats
{
    get
    {
        if (!Program.enableCheats)
            return Game1.isRunningMacro;
        return true;
    }
}
```

`Program.enableCheats` is `false` in production builds. The macro path (`Game1.isRunningMacro`) is only set during `Game1.runMacro()` calls. **Not exploitable by clients in normal gameplay.**

### StaticDelegateBuilder Reflection

`StaticDelegateBuilder` (in `StardewValley.Internal`) resolves arbitrary static method delegates from strings in the format `"FullTypeName, AssemblyName:MethodName"`:

```csharp
// StaticDelegateBuilder.cs:71
Type type = Type.GetType(text);
MethodInfo method = type.GetMethod(text2,
    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
createdDelegate = (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), null, method);
```

This is used by data-driven systems (machine rules, item queries, trigger actions) to resolve delegates from data assets. If the server controls data assets (via SMAPI content patching), it can specify arbitrary static methods as callbacks in:

- Machine output rules (`Data/Machines`)
- Item query resolvers (`Data/Objects`)
- Trigger action handlers (`Data/TriggerActions`)

### Activator.CreateInstance Usage

Several systems instantiate types from string names in data assets:

| System | Data Source | Pattern |
|--------|------------|---------|
| Building types | `Data/Buildings` → `BuildingType` | `Activator.CreateInstance(Type.GetType(type))` |
| Location types | `Data/Locations` → `Type` | `Activator.CreateInstance(Type.GetType(type))` |
| Tool types | `Data/Tools` | Same pattern |
| Special order objectives | `Data/SpecialOrders` | `namespace + "." + type + "Objective"` |
| Trinket effects | `Data/Trinkets` | Same pattern |

These are all gated by the data asset content, not directly accessible via network messages. Only relevant if the server mod patches data assets.

### Error Handling Behavior

Several message handlers silently swallow exceptions:

**`receiveChatInfoMessage`** (Multiplayer.cs:1215–1236):
```csharp
catch (ContentLoadException) { }
catch (FormatException) { }
catch (OverflowException) { }
catch (KeyNotFoundException) { }
```

**`Dialogue` parsing** (Dialogue.cs:383): Falls back to `"..."` on any exception.

**`LoadString`** (LocalizedContentManager.cs:572): Returns the raw key path string if the key is not found.

**`LetterViewerMenu`** mail parsing (line 226): Falls back to `"..."` on parse errors.

This means malformed messages generally fail silently rather than crashing clients. This is good for stability but means errors may go unnoticed during development.

### No Server-Side Validation of Client State

The most significant architectural finding: **the server does not validate farmer deltas received from clients**. A malicious client could theoretically:

- Add arbitrary items to their inventory
- Set arbitrary mail flags / quest completions
- Modify their own stats, recipes, and achievements
- Change their modData to any values

This is inherent to the peer-to-peer authority model. For the dedicated server project, this means **server-side validation of client farmer deltas should be considered** if preventing client cheating is a goal.

## Priority Actions

Ordered by impact and implementation complexity.

### 1. Implement modData Communication Channel

**Why first**: modData is the most reliable, general-purpose channel. It syncs automatically, persists across saves, and requires no special message handling.

**Implementation**:
- Define a key namespace (e.g., `JunimoServer.*`)
- Write structured JSON to `farmer.modData["JunimoServer.State"]`
- Poll or observe changes client-side (if a client mod is developed later)
- Immediate use: store server config, player permissions, session metadata

**Code path**: `Farmer` → `Character.modData` → `NetStringDictionary` → delta sync via msg type 0

### 2. Build Mail-Based Item Delivery

**Why second**: Mail provides a polished UX for item delivery with built-in notification (mailbox flag) and player agency (they choose when to open it).

**Implementation**:
- Register custom mail entries via SMAPI content API
- Add mail keys to `farmer.mailbox` for immediate delivery
- Use `%item id <qualifiedId> <count>%%` for item attachments
- Use `%action AddMoney <amount>%%` for currency grants

**Code path**: Server writes `farmer.mailbox.Add("custom_mail_id")` → delta sync → client sees mailbox flag → opens letter → `LetterViewerMenu.HandleItemCommand()` grants items

### 3. Direct Inventory Mutation for System Items

**Why third**: Useful for granting tools, quest items, or correcting inventory state without requiring player interaction.

**Implementation**:
- Find first empty slot: `farmer.Items.IndexOf(null)`
- Create item: `ItemRegistry.Create(qualifiedId, amount, quality)`
- Assign: `farmer.Items[slot] = item`
- Consider adding a HUD notification via global message (msg type 21) since `OnItemReceived` won't fire

**Code path**: Server writes `farmer.Items[n]` → `NetObjectList<Item>` marks dirty → delta sync via msg type 0

### 4. Event-Based Tutorials and Cutscenes

**Why fourth**: Events provide the richest player experience but require event data authoring.

**Implementation**:
- Define custom events in location data via SMAPI content API
- Send message type 4 to trigger on specific clients
- Use event commands for narrative, item grants, and state changes

**Code path**: Server sends msg type 4 with event ID, location, tile → client warps → `location.startEvent(event)` → event commands execute locally

## Appendix A: Synced Farmer NetFields

All fields registered in `Farmer.initNetFields()` (lines 2087–2224) are delta-synced via message type 0. This is the complete list of server-writable state on a farmhand.

### Inventory and Equipment

| Field | Type | Line | Notes |
|-------|------|------|-------|
| `netItems` | `NetRef<Inventory>` | 2106 | Full inventory (36 slots max) |
| `cursorSlotItem` | `NetRef<Item>` | 2109 | Item held by mouse cursor |
| `temporaryItem` | `NetRef<Item>` | 2108 | Temporary held item |
| `_recoveredItem` | `NetRef<Item>` | 2193 | Marlon's item recovery |
| `itemsLostLastDeath` | `NetObjectList<Item>` | 2194 | Lost items for recovery service |
| `hat` | `NetRef<Hat>` | N/A | Equipment slot |
| `boots` | `NetRef<Boots>` | N/A | Equipment slot |
| `leftRing`, `rightRing` | `NetRef<Ring>` | N/A | Equipment slots |
| `shirtItem`, `pantsItem` | `NetRef<Clothing>` | N/A | Equipment slots |
| `trinketItems` | `NetList<Trinket>` | 2211 | Trinket equipment (1.6+) |
| `toolBeingUpgraded` | `NetRef<Tool>` | 2171 | Tool at Clint's |
| `personalShippingBin` | `NetObjectList<Item>` | 2181 | Player's shipping bin contents |

### Mail and Progress

| Field | Type | Line | Notes |
|-------|------|------|-------|
| `mailReceived` | `NetStringHashSet` | 2125 | All received mail flags (includes game state flags) |
| `mailForTomorrow` | `NetStringHashSet` | 2126 | Mail queued for next day |
| `mailbox` | `NetStringList` | 2127 | Currently in mailbox |
| `eventsSeen` | `NetStringHashSet` | 2129 | Seen event IDs |
| `triggerActionsRun` | `NetStringHashSet` | 2128 | Completed trigger actions |
| `questLog` | `NetObjectList<Quest>` | 2149 | Active quests |
| `dialogueQuestionsAnswered` | `NetStringHashSet` | N/A | Tracked dialogue choices |
| `activeDialogueEvents` | `NetStringIntDict` | 2156 | Active conversation topics with remaining days |
| `cookingRecipes` | `NetStringIntDict` | 2154 | Known cooking recipes + times cooked |
| `craftingRecipes` | `NetStringIntDict` | 2155 | Known crafting recipes + times crafted |

### Stats and Collections

| Field | Type | Line | Notes |
|-------|------|------|-------|
| `basicShipped` | `NetIntDictionary` | 2187 | Items shipped by ID |
| `mineralsFound` | `NetIntDictionary` | 2188 | Museum minerals found |
| `recipesCooked` | `NetIntDictionary` | 2189 | Recipes cooked counts |
| `fishCaught` | `NetIntIntDict` | 2190 | Fish caught by ID |
| `archaeologyFound` | `NetIntIntDict` | N/A | Artifacts found |
| `specialItems` | `NetIntList` | 2159 | Special item flags |
| `specialBigCraftables` | `NetIntList` | 2160 | Special big craftable flags |
| `friendshipData` | `NetStringDictionary<Friendship>` | 2143 | NPC friendship data |

### State and Position

| Field | Type | Line | Notes |
|-------|------|------|-------|
| `modData` | `ModDataDictionary` | (Character:561) | Arbitrary key-value data (inherited) |
| `locationBeforeForcedEvent` | `NetString` | 2208 | Return location after forced event |
| `companions` | `NetList<NPC>` | 2212 | Companion NPCs following player |
| `acceptedDailyQuest` | `NetBool` | N/A | Daily quest board flag |
| `hasCompletedAllMonsterSlayerQuests` | `NetBool` | N/A | Adventurer's Guild completion |

## Appendix B: TriggerActionManager Actions

Complete list of built-in trigger actions available for `%action` (mail), `$action` (dialogue), and event `action` commands. All defined in `TriggerActionManager.cs`.

| Action | Args | Effect | Line |
|--------|------|--------|------|
| `Null` | N/A | No-op | 21 |
| `If` | `<gameStateQuery> <action> [elseAction]` | Conditional execution | 29 |
| `AddBuff` | `<buffId>` | Apply a buff to player | 88 |
| `RemoveBuff` | `<buffId>` | Remove a buff | 101 |
| `AddMail` | `<mailId>` | Add to `mailReceived` | 112 |
| `RemoveMail` | `<mailId>` | Remove from all mail lists | 123 |
| `AddQuest` | `<questId>` | Add quest to log | 134 |
| `RemoveQuest` | `<questId>` | Remove quest | 145 |
| `AddSpecialOrder` | `<orderId>` | Start a special order | 156 |
| `RemoveSpecialOrder` | `<orderId>` | Cancel a special order | 167 |
| `AddItem` | `<itemId> [count] [quality]` | Grant item to player | 178 |
| `RemoveItem` | `<itemId> [count]` | Remove item from inventory | 193 |
| `AddMoney` | `<amount>` | Add/subtract gold (clamped to 0) | 204 |
| `AddFriendshipPoints` | `<npcName> <points>` | Change NPC friendship | 219 |
| `AddConversationTopic` | `<topicId> [days]` | Add dialogue event (default 4 days) | 236 |
| `RemoveConversationTopic` | `<topicId>` | Remove dialogue event | 247 |
| `IncrementStat` | `<statKey> [amount]` | Increment a game stat | 259 |
| `MarkActionApplied` | `<actionId>` | Flag a trigger action as completed | 270 |
| `MarkCookingRecipeKnown` | `<recipeName>` | Teach a cooking recipe | 281 |
| `MarkCraftingRecipeKnown` | `<recipeName>` | Teach a crafting recipe | 292 |
| `MarkEventSeen` | `<eventId>` | Flag event as seen | 303 |
| `MarkQuestionAnswered` | `<questionId>` | Flag dialogue question as answered | 314 |
| `MarkSongHeard` | `<songId>` | Flag song as heard (jukebox) | 325 |
| `RemoveTemporaryAnimatedSprites` | `<locationName>` | Clear temp sprites | 337 |
| `SetNpcInvisible` | `<npcName> [days]` | Hide NPC for N days | 345 |
| `SetNpcVisible` | `<npcName>` | Restore NPC visibility | 363 |

::: tip
The `If` action supports full Game State Queries, enabling conditional logic like `If "PLAYER_COMBAT_LEVEL Current 5" AddItem (W)65` (grant Meowmere sword if combat level 5+).
:::

## Appendix C: Farmhand Connection Lifecycle

Understanding the full join/disconnect flow is important for knowing when server-side state manipulation takes effect.

### Connection Flow

```
Client connects
  → GameServer.checkFarmhandRequest()        // Validates auth, cabin, availability
  → Game1.multiplayer.addPlayer(farmer)      // Adds to Game1.otherFarmers
  → broadcastPlayerIntroduction(farmer)      // Sends farmer to all other clients (msg type 2)
  → Send always-active locations             // Farm, farmhouse, etc.
  → If same-day reconnect: send last disconnect location
  → sendServerIntroduction(peerId)           // Full snapshot: host + team + world (msg type 1)
  → Send other connected farmers             // Each existing player's farmer data
```

### Disconnection Flow

```
Client disconnects
  → Multiplayer.saveFarmhand(farmer)
    → NetWorldState.SaveFarmhand(farmhandRoot)
      → farmhandRoot.CloneInto(worldState)   // Full farmer state saved to world state
      → ResetFarmhandState(farmer)           // Clears transient state:
        - farmName synced from host
        - position set to bed
        - mount cleared
        - buffs cleared
        - swim state cleared
        - temporaryItem cleared
```

### Save/Load Cycle

```
Save:
  farmhands stored in SaveGame.farmhands list
  → Full XML serialization of Farmer objects including inventory, mail, modData, quests

Load:
  SaveGame.Load() → farmhands loaded into netWorldState.farmhandData
  → loadDataToFarmer(farmer):
    - Items.OverwriteWith(Items)    // Copy to net-sync list
    - Pad inventory to maxItems
    - Position set to mostRecentBed
    - Null quests removed
  → ResetFarmhandState() for each farmhand
```

**Key insight**: There is no inventory validation during save or load. Whatever state was on the farmer at disconnect is exactly what gets saved and restored. Server-side modData writes persist across the full save/load cycle.

## Appendix D: Chat System Details

The chat system provides two communication paths from server to clients.

### Player Chat (Message Type 10)

```csharp
// Multiplayer.cs:1649
case 10:
    long recipientID = msg.Reader.ReadInt64();
    LanguageCode language = msg.Reader.ReadEnum<LanguageCode>();
    string message = msg.Reader.ReadString();
    receiveChatMessage(msg.SourceFarmer, recipientID, language, message);
```

Chat messages support color via `ChatBox.addMessage(string message, Color color)`. Internal types:
- Type 0: `chatMessage` (white)
- Type 1: `errorMessage` (red)
- Type 2: `userNotificationMessage` (yellow)
- Type 3: `privateMessage` (dark cyan)

### System Chat Info (Message Type 15)

```csharp
// Multiplayer.cs:1657
case 15:
    string messageKey = msg.Reader.ReadString();
    string[] args = new string[msg.Reader.ReadByte()];
    // ... read args
    receiveChatInfoMessage(msg.SourceFarmer, messageKey, args);
```

The `messageKey` is prefixed with `"Strings\\UI:Chat_"` and looked up in translations. Args pass through `TokenParser.ParseText()`. Invalid keys are silently dropped (all exceptions caught empty). The server can use known keys like `"PlayerJoinedGame"`, `"PlayerLeftGame"`, etc.

## Appendix E: Mail Queue Internals

The party-wide mail system uses internal prefixes to track which queue a mail was delivered to, preventing duplicate delivery:

| Prefix | Queue | Constant |
|--------|-------|----------|
| `%&MFT&%` | `MailForTomorrow` | `PartyWideMessageQueue.MailForTomorrow` |
| `%&SM&%` | `SeenMail` | `PartyWideMessageQueue.SeenMail` |
| `%&NL&%` | (suffix) | No letter; sets flag without showing mail |

These prefixes are appended to the mail key and stored in `FarmerTeam.broadcastedMail` to prevent re-sending. When using `broadcastPartyWideMail()`:

```csharp
// _performPartyWideMail flow:
1. Adds to player's queue (mailForTomorrow or mailReceived)
2. If no_letter: appends "%&NL&%" suffix to key
3. Prepends queue prefix ("%&MFT&%" or "%&SM&%")
4. Adds prefixed key to team.broadcastedMail (dedup set)
```

## Code References

All file paths are relative to `decompiled/sdv-1.6.15-24356/`.

### Core Networking

| Reference | File | Line(s) |
|-----------|------|---------|
| Message dispatch (client) | `StardewValley/Multiplayer.cs` | 1565–1714 |
| Client broadcast types | `StardewValley/Multiplayer.cs` | 216–238 |
| Farmer delta broadcast | `StardewValley/Multiplayer.cs` | 287–300 |
| Server introduction | `StardewValley/Network/GameServer.cs` | 396–406 |
| Farmhand request validation | `StardewValley/Network/GameServer.cs` | 522 |
| Global message broadcast | `StardewValley/Multiplayer.cs` | 811–847 |
| Global message receive | `StardewValley/Multiplayer.cs` | 1522–1537 |
| Party-wide mail receive | `StardewValley/Multiplayer.cs` | 1475–1511 |
| Chat info message receive | `StardewValley/Multiplayer.cs` | 1209–1236 |
| Server-to-client string msgs | `StardewValley/Multiplayer.cs` | 1238–1265 |
| Force kick | `StardewValley/Multiplayer.cs` | 1513–1520 |

### Farmer and Inventory

| Reference | File | Line(s) |
|-----------|------|---------|
| Inventory field (`NetRef<Inventory>`) | `StardewValley/Farmer.cs` | 214–215 |
| Items accessor | `StardewValley/Farmer.cs` | 1647 |
| Max inventory size (36) | `StardewValley/Farmer.cs` | 117 |
| Mail fields (mailbox, mailForTomorrow, mailReceived) | `StardewValley/Farmer.cs` | 253–259 |
| NetFields initialization | `StardewValley/Farmer.cs` | 2087–2224 |
| `hasOrWillReceiveMail` check | `StardewValley/Farmer.cs` | 3914 |
| `addQuest` | `StardewValley/Farmer.cs` | 7993 |
| `OnItemReceived` (local player only) | `StardewValley/Farmer.cs` | 4840 |
| modData (from Character) | `StardewValley/Character.cs` | 515 |
| Farmhand save/reset | `StardewValley/Network/NetWorldState.cs` | 748–776 |
| Load farmhand data | `StardewValley/SaveGame.cs` | 860–869, 1325 |

### Item System

| Reference | File | Line(s) |
|-----------|------|---------|
| `ItemRegistry.Create()` | `StardewValley/ItemRegistry.cs` | 349 |
| Item modData | `StardewValley/Item.cs` | 87 |
| Item NetFields init | `StardewValley/Item.cs` | 356 |
| Object synced fields | `StardewValley/Object.cs` | 788–820 |
| Object signText (tokenized) | `StardewValley/Object.cs` | 448, 836 |

### Mail System

| Reference | File | Line(s) |
|-----------|------|---------|
| `HandleItemCommand` | `StardewValley/Menus/LetterViewerMenu.cs` | 296–470 |
| `HandleActionCommand` | `StardewValley/Menus/LetterViewerMenu.cs` | 267–293 |
| `FarmerTeam.RequestSetMail` | `StardewValley/FarmerTeam.cs` | 921 |

### Events and Triggers

| Reference | File | Line(s) |
|-----------|------|---------|
| Forced event handling | `StardewValley/Multiplayer.cs` | 1607–1647 |
| `Event.DefaultCommands.AddItem` | `StardewValley/Event.cs` | 1523 |
| `Event.DefaultCommands.Action` | `StardewValley/Event.cs` | 133 |
| Trigger action list | `StardewValley/Triggers/TriggerActionManager.cs` | 178+ |

### Dialogue

| Reference | File | Line(s) |
|-----------|------|---------|
| `$action` command | `StardewValley/Dialogue.cs` | 624–642 |
| Item grant bracket syntax | `StardewValley/Dialogue.cs` | 1001–1047 |
| Fallback on parse error | `StardewValley/Dialogue.cs` | 383–394 |

### Reflection and Type Resolution

| Reference | File | Line(s) |
|-----------|------|---------|
| `StaticDelegateBuilder` | `StardewValley/Internal/StaticDelegateBuilder.cs` | 71 |
| Location type instantiation | `StardewValley/Game1.cs` | 7373 |

### Token System

| Reference | File | Line(s) |
|-----------|------|---------|
| `TokenParser.ParseText()` | `StardewValley/TokenizableStrings/TokenParser.cs` | N/A |
| Debug command gate | `StardewValley/ChatCommands.cs` | 530 |
