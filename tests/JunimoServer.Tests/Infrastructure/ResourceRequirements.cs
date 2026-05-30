using System.Security.Cryptography;
using System.Text;
using JunimoServer.Tests.Containers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Immutable record derived from attribute. Used as server pooling key.
/// The server key includes isolation context, not just config.
/// </summary>
public sealed record ResourceRequirements(
    string? Password, int FarmType, bool WithSteam, string? SharedGroup,
    int StartingCabins, int MaxPlayers, int Clients,
    string CabinStrategy, bool AllowIpConnections,
    IsolationMode Isolation, string? TestClassName, string? TestMethodName,
    bool Exclusive = false)
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
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    /// <summary>
    /// Returns a human-readable display label for logging and UI.
    /// </summary>
    public string GetDisplayLabel()
    {
        var connection = WithSteam ? "steam" : "lan";
        var pw = Password != null ? "+pw" : "";
        return $"{connection}{pw}-farm{FarmType}-{CabinStrategy}-c{StartingCabins}";
    }

    /// <summary>
    /// Hash of server-affecting config fields (not Clients, which doesn't affect server identity).
    /// </summary>
    private string ComputeConfigHash()
    {
        var configString = $"{Password}|{FarmType}|{WithSteam}|{StartingCabins}" +
                          $"|{MaxPlayers}|{CabinStrategy}|{AllowIpConnections}";
        var bytes = Encoding.UTF8.GetBytes(configString);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string NextPerTestId() =>
        Interlocked.Increment(ref _perTestCounter).ToString();

    /// <summary>
    /// Creates requirements from a resolved TestServerAttribute.
    /// </summary>
    public static ResourceRequirements FromAttribute(TestServerAttribute attr,
        string? testClassName, string? testMethodName) => new(
        Password: attr.Password, FarmType: attr.FarmType, WithSteam: attr.WithSteam,
        SharedGroup: attr.SharedGroup, StartingCabins: attr.StartingCabins,
        MaxPlayers: attr.MaxPlayers, Clients: attr.Clients,
        CabinStrategy: attr.CabinStrategy,
        // Without Steam there's no invite code path; force IP connections on
        AllowIpConnections: attr.WithSteam ? attr.AllowIpConnections : true,
        Isolation: attr.Isolation, TestClassName: testClassName,
        TestMethodName: testMethodName,
        Exclusive: attr.Exclusive);

    /// <summary>
    /// Convenience factory for FarmMapTypeTests Theory parameters.
    /// </summary>
    public static ResourceRequirements ForFarmType(int farmType, string testClassName,
        string? cabinStrategy = null, int startingCabins = 1) => new(
        Password: null, FarmType: farmType, WithSteam: false, SharedGroup: null,
        StartingCabins: startingCabins, MaxPlayers: 10, Clients: 1,
        CabinStrategy: cabinStrategy ?? "CabinStack", AllowIpConnections: true,
        Isolation: IsolationMode.PerTest, TestClassName: testClassName,
        TestMethodName: $"FarmType_{farmType}");

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
            AllowIpConnections = AllowIpConnections,
            WithSteam = WithSteam
        };
        return options;
    }
}
