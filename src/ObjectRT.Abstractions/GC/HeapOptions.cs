namespace ObjectRT.Abstractions.GC;

/// <summary>V1 heap sizing. Heap is slots (List capacity), not byte-addressable memory.</summary>
public sealed record HeapOptions
{
    /// <summary>Initial List capacity in slots (2048 ≈ 32 KiB slot reservation). Not memory.</summary>
    public int InitialHeapCapacitySlots { get; init; } = 2048;

    /// <summary>Hard cap on logical heap bytes (sum live buffer lengths). 0 = uncapped.</summary>
    public long MaximumHeapSizeBytes { get; init; } = 0;
}
