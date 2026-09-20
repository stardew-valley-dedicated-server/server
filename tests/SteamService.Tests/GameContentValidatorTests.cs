using System.Text.Json;
using Xunit;
using static SteamService.GameContentValidator;

namespace SteamService.Tests;

public class GameContentValidatorTests
{
    [Fact]
    public void StartsPending_SoTheGateHoldsBeforeTheRunBegins()
    {
        var validator = new GameContentValidator();

        Assert.Equal(State.Pending, validator.Current.State);
        Assert.False(validator.Current.IsTerminal);
    }

    [Fact]
    public async Task SuccessfulRunEndsCompleted_WithTheSummaryAsDetail()
    {
        var validator = new GameContentValidator();

        await validator.RunAsync(() =>
            Task.FromResult<string?>("1 repaired, 0 downloaded, 9 unchanged")
        );

        var snapshot = validator.Current;
        Assert.Equal(State.Completed, snapshot.State);
        Assert.True(snapshot.IsTerminal);
        Assert.Equal("1 repaired, 0 downloaded, 9 unchanged", snapshot.Detail);
        Assert.NotNull(snapshot.StartedAt);
        Assert.NotNull(snapshot.FinishedAt);
    }

    [Fact]
    public async Task IsRunningWhileTheValidationIsInFlight()
    {
        var validator = new GameContentValidator();
        var release = new TaskCompletionSource();

        var run = validator.RunAsync(async () =>
        {
            await release.Task;
            return null;
        });

        Assert.Equal(State.Running, validator.Current.State);
        Assert.False(validator.Current.IsTerminal);

        release.SetResult();
        await run;
        Assert.Equal(State.Completed, validator.Current.State);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))] // no auth method (login failure)
    [InlineData(typeof(IOException))] // disk error mid-repair
    [InlineData(typeof(OperationCanceledException))]
    public async Task AnyExceptionEndsFailedWithTheMessage_NeverThrows(Type exceptionType)
    {
        var validator = new GameContentValidator();
        var exception = (Exception)Activator.CreateInstance(exceptionType, "boom")!;

        await validator.RunAsync(() => throw exception);

        var snapshot = validator.Current;
        Assert.Equal(State.Failed, snapshot.State);
        Assert.True(snapshot.IsTerminal);
        Assert.Equal("boom", snapshot.Detail);
        Assert.NotNull(snapshot.FinishedAt);
    }

    [Fact]
    public void DisabledAndSkippedAreTerminalAndCarryTheReason()
    {
        var disabled = new GameContentValidator();
        disabled.MarkDisabled("VALIDATE_ON_BOOT=false");
        Assert.Equal(State.Disabled, disabled.Current.State);
        Assert.True(disabled.Current.IsTerminal);
        Assert.Equal("VALIDATE_ON_BOOT=false", disabled.Current.Detail);

        var skipped = new GameContentValidator();
        skipped.MarkSkipped("no Steam account configured");
        Assert.Equal(State.Skipped, skipped.Current.State);
        Assert.True(skipped.Current.IsTerminal);
    }

    [Fact]
    public async Task WireShapeUsesLowerCaseStatusAndSnakeCaseKeys()
    {
        // startapp.sh's wait_for_content_validation greps `"status":"<lower-case>"` out of the
        // body, so the casing and key name are a contract with the entrypoint.
        var validator = new GameContentValidator();
        await validator.RunAsync(() => throw new Exception("boom"));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(validator.Current.ToJson()));
        var root = doc.RootElement;

        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("boom", root.GetProperty("detail").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("started_at").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("finished_at").ValueKind);
    }

    [Fact]
    public void EveryStateNameIsLowerCase()
    {
        foreach (var state in Enum.GetValues<State>())
        {
            Assert.Equal(StateName(state), StateName(state).ToLowerInvariant());
        }
    }
}
