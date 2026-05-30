using System.Text.Json;
using System.Text.Json.Nodes;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Pure slicing of the global <c>STEAM_ACCOUNTS</c> array across the declared
/// host fleet. Same inputs always produce the same outputs — both
/// <see cref="ServerConfigDiscovery"/> (validation) and <see cref="TestResourceBroker"/>
/// (placement + bring-up) call this with identical arguments and rely on
/// matching answers.
///
/// <para>Algorithm: walk hosts in declared order. For each host, take the next
/// <c>min(1 + ClientSlots, accountsRemaining)</c> accounts from the global pool —
/// 1 server + up to <c>ClientSlots</c> client accounts. A host receiving only 1
/// account can't serve any Steam test (needs server + ≥1 client), so the slicer
/// gives it 0 instead of wasting the lone account; the next host in declared
/// order may pick it up.</para>
///
/// <para>Hosts that get 0 accounts still appear in the output (so callers can
/// look up by host id without nullables); their <see cref="SteamAccountSlice.SliceJson"/>
/// is null and <see cref="SteamAccountSlice.IsSteamCapable"/> is false.</para>
/// </summary>
public static class SteamAccountSlicer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        // Slice JSON is consumed by the steam-auth container which deserializes
        // case-insensitively; emit the canonical lowercase form to keep wire
        // format stable across runs.
    };

    // Latches first-call emission of steam_account_slicing events. The slicer
    // is pure and called from multiple sites (ServerConfigDiscovery validation,
    // StartPrestart placement, defensive fallback in PrestartAsync) — without a
    // latch the same slicing decision would emit one event per host per call
    // site, polluting the diagnostic log.
    private static int _slicingEventsEmitted;

    /// <summary>
    /// Total function: returns one slice per host, in declared order. Emits one
    /// <c>steam_account_slicing</c> event per host (per
    /// <see cref="JunimoServer.Tests.Helpers.InfrastructureEventLog"/>).
    /// </summary>
    /// <param name="steamAccountsJson">
    /// Raw <c>STEAM_ACCOUNTS</c> JSON from <c>.env.test</c>. May be null/whitespace
    /// (treated as N=0, no Steam-capable hosts).
    /// </param>
    /// <param name="hostsInDeclaredOrder">
    /// All configured hosts in their declared (operator-specified) order. Slices
    /// are assigned greedily in this order; declared-order priority puts capable
    /// slices on the coordinator first when N is small.
    /// </param>
    public static IReadOnlyList<SteamAccountSlice> Slice(
        string? steamAccountsJson,
        IReadOnlyList<DockerHost> hostsInDeclaredOrder)
    {
        var globalAccounts = UserConfigJson.ParseArrayStrict(
            "STEAM_ACCOUNTS", steamAccountsJson, "{user, pass[, refreshToken]}");
        var n = globalAccounts.Count;

        var result = new List<SteamAccountSlice>(hostsInDeclaredOrder.Count);
        var consumed = 0;
        var firstCall = Interlocked.CompareExchange(ref _slicingEventsEmitted, 1, 0) == 0;

        foreach (var host in hostsInDeclaredOrder)
        {
            var remaining = n - consumed;
            // 1 server + ClientSlots client accounts is the maximum a host
            // can productively use. Cap by remaining global pool.
            var desired = Math.Min(1 + host.ClientSlots, remaining);

            // Don't waste the last account: a 1-account slice can't serve any
            // Steam test. Give the host 0 instead so the next host (or none)
            // picks it up. With N=1 the very first host falls into this branch.
            if (remaining < 2)
                desired = 0;

            int sliceSize;
            IReadOnlyList<int> indices;
            string? sliceJson;

            if (desired <= 0)
            {
                sliceSize = 0;
                indices = Array.Empty<int>();
                sliceJson = null;
            }
            else
            {
                sliceSize = desired;
                var idxList = new List<int>(sliceSize);
                var sliceArray = new JsonArray();
                for (var i = 0; i < sliceSize; i++)
                {
                    var globalIdx = consumed + i;
                    idxList.Add(globalIdx);
                    // DeepClone detaches the node from its parent so we can
                    // attach it to a fresh array without InvalidOperationException.
                    sliceArray.Add(globalAccounts[globalIdx].DeepClone());
                }
                indices = idxList;
                sliceJson = sliceArray.ToJsonString(JsonOpts);
                consumed += sliceSize;
            }

            var slice = new SteamAccountSlice(host.Id, sliceSize, indices, sliceJson);
            result.Add(slice);

            if (firstCall)
            {
                InfrastructureEventLog.Emit("steam_account_slicing", new
                {
                    host_id = slice.HostId,
                    sliceSize = slice.SliceSize,
                    globalIndices = slice.GlobalIndices,
                    isSteamCapable = slice.IsSteamCapable
                });
            }
        }

        return result;
    }

}
