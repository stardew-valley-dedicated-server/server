namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Surface that <see cref="DockerImageBuilder"/> reports progress to. Two
/// callers in two processes need different transports for the same events:
/// the parent runner has the renderer in-process and forwards directly, while
/// the test child uses <see cref="SetupEventBus"/> to ferry events back over
/// the named pipe. Both implementations live next to their callers.
/// </summary>
public interface IBuildProgressSink
{
    void PhaseStarted(string phaseName);
    void Step(string stepName, SetupStepStatus status, string? details = null);
    void PhaseCompleted(string phaseName, bool success, string? errorMessage = null);
}

/// <summary>
/// Child-side sink: forwards build progress to <see cref="SetupEventBus"/>,
/// which serialises each event onto the named pipe consumed by the parent
/// runner. Used by <c>TestResourceBroker</c> and
/// <c>DownloadValidationFixture</c>.
/// </summary>
public sealed class SetupEventBusBuildProgressSink : IBuildProgressSink
{
    private readonly string _category;
    private readonly string? _collectionName;

    public SetupEventBusBuildProgressSink(string category, string? collectionName)
    {
        _category = category;
        _collectionName = collectionName;
    }

    public void PhaseStarted(string phaseName) =>
        SetupEventBus.EmitPhaseStarted(_category, phaseName, _collectionName);

    public void Step(string stepName, SetupStepStatus status, string? details = null) =>
        SetupEventBus.EmitStep(_category, stepName, status, details, _collectionName);

    public void PhaseCompleted(string phaseName, bool success, string? errorMessage = null) =>
        SetupEventBus.EmitPhaseCompleted(
            _category,
            phaseName,
            success,
            errorMessage,
            _collectionName
        );
}
