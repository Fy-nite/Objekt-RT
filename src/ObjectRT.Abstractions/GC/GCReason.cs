namespace ObjectRT.Abstractions.GC;

public enum GCReason
{
    Explicit = 0,
    Threshold = 1,
    AllocationFailure = 2,
    OOMProbe = 3,
}
