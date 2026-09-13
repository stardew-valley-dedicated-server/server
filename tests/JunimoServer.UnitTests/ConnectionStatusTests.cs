using System;
using System.Collections.Generic;
using System.Linq;
using JunimoServer.Util;
using Xunit;

namespace JunimoServer.UnitTests;

public class ConnectionStatusTests
{
    private static readonly ConnectionStatusCode[] AllCodes =
        Enum.GetValues<ConnectionStatusCode>();

    // Every derivation row of the contract; `null` galaxy = Steam auth not configured.
    [Theory]
    [InlineData(
        true,
        true,
        GalaxyLobbyState.Connected,
        SteamSessionState.Connected,
        ConnectionStatusCode.Ready
    )]
    [InlineData(
        true,
        false,
        GalaxyLobbyState.Connected,
        SteamSessionState.Connected,
        ConnectionStatusCode.SteamRelayPending
    )]
    [InlineData(
        true,
        false,
        GalaxyLobbyState.Connected,
        SteamSessionState.Lost,
        ConnectionStatusCode.SteamRelayPending
    )]
    [InlineData(
        true,
        false,
        GalaxyLobbyState.Recovering,
        SteamSessionState.Connected,
        ConnectionStatusCode.Reconnecting
    )]
    [InlineData(
        true,
        false,
        GalaxyLobbyState.Down,
        SteamSessionState.Lost,
        ConnectionStatusCode.Reconnecting
    )]
    [InlineData(
        true,
        true,
        GalaxyLobbyState.Recovering,
        SteamSessionState.Connected,
        ConnectionStatusCode.Reconnecting
    )]
    [InlineData(
        true,
        true,
        GalaxyLobbyState.Connected,
        SteamSessionState.Lost,
        ConnectionStatusCode.Reconnecting
    )]
    [InlineData(false, false, null, SteamSessionState.Lost, ConnectionStatusCode.InviteUnavailable)]
    [InlineData(
        false,
        false,
        null,
        SteamSessionState.Connected,
        ConnectionStatusCode.InviteUnavailable
    )]
    [InlineData(
        false,
        false,
        GalaxyLobbyState.Down,
        SteamSessionState.Lost,
        ConnectionStatusCode.SteamSessionDown
    )]
    [InlineData(
        false,
        false,
        GalaxyLobbyState.Recovering,
        SteamSessionState.Lost,
        ConnectionStatusCode.SteamSessionDown
    )]
    [InlineData(
        false,
        false,
        GalaxyLobbyState.Recovering,
        SteamSessionState.Connected,
        ConnectionStatusCode.Reconnecting
    )]
    [InlineData(
        false,
        false,
        GalaxyLobbyState.Down,
        SteamSessionState.Connected,
        ConnectionStatusCode.Starting
    )]
    [InlineData(
        false,
        false,
        GalaxyLobbyState.Connected,
        SteamSessionState.Connected,
        ConnectionStatusCode.Starting
    )]
    public void ComputeDerivesTheContractRow(
        bool codePresent,
        bool relayReady,
        GalaxyLobbyState? galaxy,
        SteamSessionState steam,
        ConnectionStatusCode expected
    )
    {
        Assert.Equal(expected, ConnectionStatus.Compute(codePresent, relayReady, galaxy, steam));
    }

    [Fact]
    public void ComputeIsTotalOverEverySignalCombination()
    {
        GalaxyLobbyState?[] lobbies =
        [
            null,
            .. Enum.GetValues<GalaxyLobbyState>().Cast<GalaxyLobbyState?>(),
        ];
        var combos =
            from codePresent in new[] { false, true }
            from relayReady in new[] { false, true }
            from galaxy in lobbies
            from steam in Enum.GetValues<SteamSessionState>()
            select (codePresent, relayReady, galaxy, steam);
        foreach (var (codePresent, relayReady, galaxy, steam) in combos)
        {
            var code = ConnectionStatus.Compute(codePresent, relayReady, galaxy, steam);
            Assert.Contains(code, AllCodes);
            // Ready and SteamRelayPending claim the code is usable, which needs a code to exist.
            if (code is ConnectionStatusCode.Ready or ConnectionStatusCode.SteamRelayPending)
            {
                Assert.True(codePresent);
            }
        }
    }

    [Fact]
    public void EveryCodeHasWireFormAndText()
    {
        var wires = new HashSet<string>();
        foreach (var code in AllCodes)
        {
            var wire = code.ToWire();
            Assert.True(wires.Add(wire), $"duplicate wire form '{wire}'");
            Assert.Equal(char.ToLowerInvariant(wire[0]), wire[0]);
            Assert.DoesNotContain(' ', wire);

            var text = ConnectionStatus.Text(code);
            if (code == ConnectionStatusCode.Ready)
            {
                Assert.Null(text);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
        }
    }

    [Fact]
    public void WireFormIsCamelCase()
    {
        Assert.Equal("steamRelayPending", ConnectionStatusCode.SteamRelayPending.ToWire());
        Assert.Equal("inviteUnavailable", ConnectionStatusCode.InviteUnavailable.ToWire());
        Assert.Equal("ready", ConnectionStatusCode.Ready.ToWire());
    }

    [Fact]
    public void RenderAppliesTheDisplayRule()
    {
        Assert.Equal("SABC", ConnectionStatus.Render(ConnectionStatusCode.Ready, "SABC"));
        Assert.Equal(
            "SABC (GOG ready - Steam connecting...)",
            ConnectionStatus.Render(ConnectionStatusCode.SteamRelayPending, "SABC")
        );
        Assert.Equal(
            "SABC (reconnecting...)",
            ConnectionStatus.Render(ConnectionStatusCode.Reconnecting, "SABC")
        );
        Assert.Equal(
            "starting up...",
            ConnectionStatus.Render(ConnectionStatusCode.Starting, null)
        );
        Assert.Equal(
            "connecting to Steam...",
            ConnectionStatus.Render(ConnectionStatusCode.SteamSessionDown, null)
        );
        Assert.Equal(
            "not used on this server",
            ConnectionStatus.Render(ConnectionStatusCode.InviteUnavailable, null)
        );
    }
}
