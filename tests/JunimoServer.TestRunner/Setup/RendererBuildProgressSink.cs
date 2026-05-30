using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.TestRunner.Rendering;

namespace JunimoServer.TestRunner.Setup;

/// <summary>
/// Parent-side <see cref="IBuildProgressSink"/>: forwards
/// <see cref="DockerImageBuilder"/> progress to the in-process renderer
/// directly, with no IPC hop. Used by the runner's parent-side image build
/// (before <c>SDVD_SETUP_PIPE</c> is exported to the xUnit child).
/// </summary>
public sealed class RendererBuildProgressSink : IBuildProgressSink
{
    private readonly ITestRenderer _renderer;
    private readonly string _category;
    private readonly string? _collectionName;

    public RendererBuildProgressSink(
        ITestRenderer renderer,
        string category,
        string? collectionName = null)
    {
        _renderer = renderer;
        _category = category;
        _collectionName = collectionName;
    }

    public void PhaseStarted(string phaseName)
        => _renderer.OnSetupPhaseStarted(new SetupPhaseStartedEvent(_category, phaseName, _collectionName));

    public void Step(string stepName, SetupStepStatus status, string? details = null)
        => _renderer.OnSetupStep(new SetupStepEvent(_category, stepName, status, details, _collectionName));

    public void PhaseCompleted(string phaseName, bool success, string? errorMessage = null)
        => _renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(_category, phaseName, success, errorMessage, _collectionName));
}
