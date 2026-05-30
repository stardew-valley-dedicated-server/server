namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Declares an explicit ordering priority for a test collection.
/// Place on the <c>[CollectionDefinition]</c> class. Lower values run first.
/// Collections without this attribute are ordered automatically by the
/// <see cref="JunimoServer.Tests.Fixtures.TestCollectionOrderer"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CollectionPriorityAttribute(int priority) : Attribute
{
    public int Priority { get; } = priority;
}
