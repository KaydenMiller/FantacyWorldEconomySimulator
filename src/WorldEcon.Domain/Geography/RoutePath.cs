namespace WorldEcon.Domain.Geography;

/// <summary>A resolved route through the graph: ordered settlements and total edge distance.</summary>
public sealed record RoutePath(IReadOnlyList<SettlementId> Nodes, long TotalDistance)
{
    public bool Equals(RoutePath? other)
        => other is not null
            && TotalDistance == other.TotalDistance
            && Nodes.SequenceEqual(other.Nodes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TotalDistance);
        foreach (var node in Nodes)
            hash.Add(node);
        return hash.ToHashCode();
    }
}

public sealed record ReachableSettlement(SettlementId Settlement, long Distance);
