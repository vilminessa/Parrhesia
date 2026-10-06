namespace Parrhesia.Core.Graph;

public enum GraphChangeKind
{
    NodeAdded,
    NodeRemoved,
    NodeChanged,
    RouteAdded,
    RouteRemoved,
    RouteChanged,
}

/// <summary>Изменение графа, о котором нужно узнать UI и движку.</summary>
public sealed record GraphChange(GraphChangeKind Kind, AudioNode? Node = null, Route? Route = null);
