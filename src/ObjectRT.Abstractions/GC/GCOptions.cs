namespace ObjectRT.Abstractions.GC;

public sealed record GCOptions
{
    public GCCollectorKind Collector { get; init; } = GCCollectorKind.MarkSweep;
    public long InitialThresholdBytes { get; init; } = 64 * 1024;
    public double GrowthFactor { get; init; } = 2.0;
    public long MinHeadroomBytes { get; init; } = 16 * 1024;
}
