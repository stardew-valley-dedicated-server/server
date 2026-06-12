using System.Security.Cryptography;
using System.Text;
using JunimoServer.Tests.Containers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Immutable record derived from attribute. Used as server pooling key.
/// The server key includes isolation context, not just config.
/// </summary>
public sealed record ResourceRequirements(
    string? Password,
    FarmTypeSetting FarmType,
    bool WithSteam,
    string? SharedGroup,
    int StartingCabins,
    int MaxPlayers,
    int Clients,
    string CabinStrategy,
    bool AllowIpConnections,
    IsolationMode Isolation,
    string? TestClassName,
    string? TestMethodName,
    bool Exclusive = false,
    string ExistingCabinBehavior = "KeepExisting",
    bool FixtureFarmMod = false
)
{
    private static int _perTestCounter;

    /// <summary>
    /// Computes the server key used for pooling. Includes isolation scope + config hash.
    /// </summary>
    public string GetServerKey()
    {
        var configHash = ComputeConfigHash();

        return Isolation switch
        {
            IsolationMode.SharedGroup => $"group-{SharedGroup}-{configHash}",
            IsolationMode.SharedClass => $"config-{configHash}",
            IsolationMode.SharedAssembly => $"config-{configHash}",
            IsolationMode.PerTest => $"test-{TestClassName}.{TestMethodName}-{NextPerTestId()}",
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    /// <summary>
    /// Returns a human-readable display label for logging and UI.
    /// </summary>
    public string GetDisplayLabel()
    {
        var connection = WithSteam ? "steam" : "lan";
        var pw = Password != null ? "+pw" : "";
        var fixture = FixtureFarmMod ? "+fixturemod" : "";
        return $"{connection}{pw}{fixture}-farm{FarmType}-{CabinStrategy}-c{StartingCabins}";
    }

    /// <summary>
    /// Hash of server-affecting config fields (not Clients, which doesn't affect server identity).
    /// FixtureFarmMod changes which mods load, which is server identity — so it enters the key.
    /// </summary>
    private string ComputeConfigHash()
    {
        var configString =
            $"{Password}|{FarmType}|{WithSteam}|{StartingCabins}"
            + $"|{MaxPlayers}|{CabinStrategy}|{AllowIpConnections}|{ExistingCabinBehavior}"
            + $"|{FixtureFarmMod}";
        var bytes = Encoding.UTF8.GetBytes(configString);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string NextPerTestId() => Interlocked.Increment(ref _perTestCounter).ToString();

    /// <summary>
    /// Creates requirements from a resolved TestServerAttribute.
    /// </summary>
    public static ResourceRequirements FromAttribute(
        TestServerAttribute attr,
        string? testClassName,
        string? testMethodName
    ) =>
        new(
            Password: attr.Password,
            FarmType: attr.FarmType,
            WithSteam: attr.WithSteam,
            SharedGroup: attr.SharedGroup,
            StartingCabins: attr.StartingCabins,
            MaxPlayers: attr.MaxPlayers,
            Clients: attr.Clients,
            CabinStrategy: attr.CabinStrategy,
            // Without Steam there's no invite code path; force IP connections on
            AllowIpConnections: attr.WithSteam ? attr.AllowIpConnections : true,
            Isolation: attr.Isolation,
            TestClassName: testClassName,
            TestMethodName: testMethodName,
            Exclusive: attr.Exclusive,
            ExistingCabinBehavior: attr.ExistingCabinBehavior,
            FixtureFarmMod: attr.FixtureFarmMod
        );

    /// <summary>
    /// Maps requirements to ServerContainerOptions for container creation.
    /// </summary>
    public ServerContainerOptions ToServerOptions()
    {
        var options = new ServerContainerOptions
        {
            FarmType = FarmType,
            StartingCabins = StartingCabins,
            MaxPlayers = MaxPlayers,
            CabinStrategy = CabinStrategy,
            ExistingCabinBehavior = ExistingCabinBehavior,
            AllowIpConnections = AllowIpConnections,
            WithSteam = WithSteam,
            FixtureFarmMod = FixtureFarmMod,
        };
        return options;
    }
}
