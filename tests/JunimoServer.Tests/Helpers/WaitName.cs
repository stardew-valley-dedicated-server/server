namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Wire-stable identifier for blocking-wait primitives instrumented via
/// <see cref="WaitTrace"/> and <see cref="InfrastructureEventLog.EmitWait"/>.
///
/// <para>
/// <b>Don't rename.</b> Variants are serialized as <c>name.ToString()</c> into
/// <c>infrastructure.jsonl</c>, so consumers (correlation tooling, dashboards,
/// AI debuggers) match by string. Add new variants; deprecate, don't repurpose.
/// </para>
///
/// <para>
/// Naming convention is <c>Subsystem_Operation</c>. <c>Polling_*</c> variants
/// correspond 1:1 to a <see cref="PollingHelper"/> call site.
/// </para>
/// </summary>
public enum WaitName
{
    // ---- ClientPool — multi-wait method (LeaseClientAsync) ----
    /// <summary>_returnSignal at MaxContainers (container-cap loop).</summary>
    ClientPool_LeaseAtCap,

    /// <summary>_returnSignal awaiting a Steam-capable returned client.</summary>
    ClientPool_LeaseSteamWait,

    /// <summary>Awaiting an in-flight pre-warm task.</summary>
    ClientPool_PrewarmInProgress,

    /// <summary>_returnSignal under-cap deadline; waits briefly for a return before creating a new client.</summary>
    ClientPool_LeasePatienceWait,

    // ---- HostCapacityQueue — per-host server-slot and client-slot priority queue ----
    // (Names retained for backwards-compatible JSONL/event-log keys; the queue is
    // per-host now, not global as the names suggest.)
    ClientCapacity_Acquire,
    ClientCapacity_ReleaseAndReacquire,

    // ---- DockerStartLimiter — per-host Docker-start concurrency ----
    // Snapshot carries `host_id` so per-host saturation is visible in diagnostics.
    DockerStartLimiter_StartSlot,

    // ---- DockerExtractLimiter — per-host video-extraction concurrency ----
    // Snapshot carries `host_id` so per-host saturation is visible in diagnostics.
    DockerExtractLimiter_ExtractSlot,

    // ---- ManagedServer — server-scoped coordination ----
    ManagedServer_JoinGate,
    ManagedServer_PriorExclusiveDrain,
    ManagedServer_ExclusiveClassTurn,
    ManagedServer_RefDrain,
    ManagedServer_GateOnlyRefPoll,
    ManagedServer_EnsureInitialized,

    // ---- ServerQueue — per-key TCS for at-least-one-server-ready ----
    ServerQueue_WaitUntilReady,

    // ---- SessionGate — per-class KeepConnected turn lock ----
    SessionGate_TurnLock,

    // ---- Custom polling sites (not using PollingHelper) ----
    ServerApi_WaitForServerOnline,

    // ---- PollingHelper migrations (one variant per call site) ----
    // TestBase.cs (5)
    Polling_TestBase_ServerReady,
    Polling_TestBase_WaitForCabinAssignedById,
    Polling_TestBase_WaitForCabinAssignedByName,
    Polling_TestBase_WaitForChatMessageAfter,
    Polling_TestBase_PostTransitionSettle,

    // ConnectionHelper.cs (2)
    Polling_ConnectionHelper_LoadFarmhandSlotsCoop,
    Polling_ConnectionHelper_LoadFarmhandSlotsLan,

    // GameTestClient.cs (4)
    Polling_GameTestClient_WaitForChatHistoryKeyword,
    Polling_GameTestClient_SendAndExpectChatResponse,
    Polling_GameTestClient_WaitForLocation,
    Polling_GameTestClient_WaitForAuthWarp,

    // ServerApiClient.cs (7)
    Polling_ServerApi_WaitForPlayerByName,
    Polling_ServerApi_WaitForPlayerById,
    Polling_ServerApi_WaitForPlayersRemovedByName,
    Polling_ServerApi_WaitForPlayersRemovedById,
    Polling_ServerApi_WaitForFarmhandByName,
    Polling_ServerApi_WaitForFarmhandDeletedByName,
    Polling_ServerApi_WaitForFarmerServerTile,

    // SharedSteamAuth.cs (1)
    Polling_SharedSteamAuth_AccountsReady,

    // CabinStrategyTests.cs (3)
    Polling_CabinStrategy_OurCabinAssigned,
    Polling_CabinStrategy_FarmerSyncedCabinAndFarmhand,
    Polling_CabinStrategy_FarmerDeletionReflected,

    // CabinPositionPersistenceTests.cs — !cabin reset (1)
    Polling_CabinReset_CabinHidden,

    // CabinPositionPersistenceTests.cs — dummy cabin at shared stack (1)
    Polling_DummyCabin_VisibleInClientFarm,

    // CabinPositionPersistenceTests.cs — same-pass sweep reflected in snapshot (1)
    Polling_CabinSweep_PostReloadSettled,

    // AbandonedClaimTests.cs (3)
    Polling_AbandonedClaim_StuckStateReproduced,
    Polling_AbandonedClaim_DisconnectHealConfirmed,
    Polling_AbandonedClaim_SweptOnReload,

    // SaveImportTests.cs (10)
    Polling_SaveImport_SwapFinalized,
    Polling_SaveImport_ContentsMoved,
    Polling_SaveImport_ContentsMovedUpgraded,
    Polling_SaveImport_PetRelocated,
    Polling_SaveImport_CellarMoved,
    Polling_SaveImport_AsIsPreserved,
    Polling_SaveImport_PartialThenStable,
    Polling_SaveImport_SecondReloadNoop,
    Polling_SaveImport_MasterGatedState,
    Polling_SaveImport_ForceReloadKicksAndFinalizes,

    // FarmhandManagementTests.cs (1)
    Polling_FarmhandManagement_FarmhandGone,

    // HostAutomationTests.cs (5)
    Polling_HostAutomation_NoPlayers,
    Polling_HostAutomation_PauseConfirmed,
    Polling_HostAutomation_TimeAdvanced,
    Polling_HostAutomation_UnpauseConfirmed,
    Polling_HostAutomation_TimeAdvancedSecond,

    // LobbyCommandsTestBase.cs (1)
    Polling_LobbyCommands_AdminGranted,

    // NavigationTests.cs (3)
    Polling_Navigation_HasInviteCode,
    Polling_Navigation_HasGalaxyInviteCode,
    Polling_Navigation_HealthyOk,

    // NoPasswordTests.cs (2)
    Polling_NoPassword_AuthMessageAppeared,
    Polling_NoPassword_HelloWorldAppeared,

    // PasswordProtectionTests.cs (1)
    Polling_PasswordProtection_KickedDisconnect,

    // PasswordProtectionDisruptiveTests.cs (1)
    Polling_PasswordProtectionDisruptive_KickedDisconnect,

    // RenderingTests.cs (3)
    Polling_Rendering_OverlayDark,
    Polling_Rendering_OverlayVisible,
    Polling_Rendering_OverlayWentDarkAgain,

    // ServerApiTests.cs (2)
    Polling_ServerApi_NoPlayersConnected,
    Polling_ServerApi_ChatMessageDelivered,

    // SteamAppIdTests.cs (1)
    Polling_SteamAppId_SdrStatusLine,

    // CropSaverTests.cs (1)
    Polling_CropSaver_AwaitWatcher,

    // CabinPlacementValidationTests.cs (2)
    Polling_CabinPlacement_Moved,
    Polling_CabinPlacement_Rejected,

    // FestivalTests.cs (8)
    Polling_Festival_DayConfirmed,
    Polling_Festival_ClientWindowSynced,
    Polling_Festival_BecameActive,
    Polling_Festival_StillActiveAfterSettle,
    Polling_Festival_EndedAfterLeave,
    Polling_Festival_EndedNoPlayers,
    Polling_Festival_NextFestivalEndedOnLeave,
    Polling_Festival_MainEventStillActive,
}
