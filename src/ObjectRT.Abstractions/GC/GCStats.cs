namespace ObjectRT.Abstractions.GC;

public sealed class GCStats
{
    public long CollectionCount { get; set; }
    public TimeSpan TotalPause { get; set; }
    public TimeSpan LastPause { get; set; }
    public long AllocatedBytes { get; set; }
    public long LiveBytes { get; set; }
    public long HeapCapacityBytes { get; set; }
    public int HeapCapacitySlots { get; set; }
    public int FreeSlots { get; set; }
    public long ReclaimedBytesLast { get; set; }
    public int ReclaimedSlotsLast { get; set; }
    public GCReason LastReason { get; set; }
    public DateTime LastCollectionUtc { get; set; }

    public GCStats Clone() => (GCStats)MemberwiseClone();
}
