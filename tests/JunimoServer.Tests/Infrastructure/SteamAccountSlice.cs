namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// One host's slice of the global Steam-accounts pool. Disjoint by construction:
/// each global account index appears in at most one slice.
///
/// <para>
/// <see cref="GlobalIndices"/> are the operator-facing account positions (0..N-1
/// inside the original <c>STEAM_ACCOUNTS</c> JSON). Containers and the wire format
/// (<c>?account=N</c>, <c>SDVD_TEST_STEAM_ACCOUNT_INDEX</c>) carry slice-local indices
/// (0..k-1, where k = <see cref="SliceSize"/>): the host's steam-auth sees only its
/// renumbered sub-array via <see cref="SliceJson"/>. Slice-local 0 is the host's
/// server account; 1..k-1 are client accounts.
/// </para>
/// </summary>
public sealed record SteamAccountSlice(
    string HostId,
    int SliceSize,
    IReadOnlyList<int> GlobalIndices,
    string? SliceJson
)
{
    /// <summary>
    /// True iff this host's slice can serve any Steam test (≥1 server + ≥1 client account).
    /// </summary>
    public bool IsSteamCapable => SliceSize >= 2;

    /// <summary>
    /// Number of client accounts (= <see cref="SliceSize"/> - 1, floored at 0).
    /// Mirrors <see cref="SteamAccountAllocator.ClientPoolSize"/>.
    /// </summary>
    public int ClientPoolSize => Math.Max(0, SliceSize - 1);
}
