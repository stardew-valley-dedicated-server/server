/*
 * steamclient_stub.c — Minimal stub for steamclient.so on Alpine/musl.
 *
 * The real steamclient.so is glibc-only and segfaults on musl. This stub
 * provides the minimum exported symbols so that Steamworks.NET's P/Invoke
 * calls don't crash. GameServer.Init() will fail gracefully (return false)
 * and the mod will skip Steam GameServer features.
 *
 * Compile: gcc -shared -fPIC -o steamclient.so steamclient_stub.c
 */

#include <stddef.h>
#include <stdint.h>

/* Main entry point called by Steamworks.NET during init */
void *SteamInternal_CreateInterface(const char *ver) {
    return NULL;
}

void *SteamInternal_FindOrCreateUserInterface(int hSteamUser, const char *ver) {
    return NULL;
}

void *SteamInternal_FindOrCreateGameServerInterface(int hSteamUser, const char *ver) {
    return NULL;
}

void *SteamInternal_ContextInit(void *pContextInitData) {
    return NULL;
}

void *SteamInternal_GameServer_Init(
    uint32_t unIP, uint16_t usPort, uint16_t usGamePort,
    uint16_t usQueryPort, int eServerMode, const char *pchVersionString) {
    return NULL;
}

/* SteamAPI_Init and related — all return failure (0) */
int SteamAPI_Init(void) { return 0; }
int SteamAPI_InitSafe(void) { return 0; }
void SteamAPI_Shutdown(void) {}
void SteamAPI_RunCallbacks(void) {}
void SteamAPI_RegisterCallback(void *pCallback, int iCallback) {}
void SteamAPI_UnregisterCallback(void *pCallback) {}
void SteamAPI_RegisterCallResult(void *pCallback, uint64_t hAPICall) {}
void SteamAPI_UnregisterCallResult(void *pCallback, uint64_t hAPICall) {}
int SteamAPI_RestartAppIfNecessary(uint32_t unOwnAppID) { return 0; }

/* GameServer API stubs */
int SteamGameServer_Init(
    uint32_t unIP, uint16_t usSteamPort, uint16_t usGamePort,
    uint16_t usQueryPort, int eServerMode, const char *pchVersionString) {
    return 0; /* failure */
}

void SteamGameServer_Shutdown(void) {}
void SteamGameServer_RunCallbacks(void) {}
int SteamGameServer_BSecure(void) { return 0; }
uint64_t SteamGameServer_GetSteamID(void) { return 0; }

/* HSteamPipe / HSteamUser */
int SteamAPI_GetHSteamPipe(void) { return 0; }
int SteamAPI_GetHSteamUser(void) { return 0; }
int SteamGameServer_GetHSteamPipe(void) { return 0; }
int SteamGameServer_GetHSteamUser(void) { return 0; }

/* ISteamClient stubs */
void *SteamClient(void) { return NULL; }
void *SteamGameServerClient(void) { return NULL; }

/* Callback dispatcher */
int SteamAPI_ManualDispatch_Init(void) { return 0; }
void SteamAPI_ManualDispatch_RunFrame(int hSteamPipe) {}
int SteamAPI_ManualDispatch_GetNextCallback(int hSteamPipe, void *pCallbackMsg) { return 0; }
void SteamAPI_ManualDispatch_FreeLastCallback(int hSteamPipe) {}
