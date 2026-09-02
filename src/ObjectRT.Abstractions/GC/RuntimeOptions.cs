namespace ObjectRT.Abstractions.GC;

public sealed record RuntimeOptions
{
    public HeapOptions Heap { get; init; } = new();
    public GCOptions GC { get; init; } = new();
}
