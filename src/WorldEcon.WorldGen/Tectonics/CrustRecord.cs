using WorldEcon.Simulation.Random;

namespace WorldEcon.WorldGen.Tectonics;

public enum CrustKind : byte
{
    Oceanic,
    Continental,
    AccretedArc,
}

public enum OrogenyKind : byte
{
    None,
    Andean,
    Himalayan,
    Arc,
}

[Flags]
public enum CrustFlags : byte
{
    None = 0,
    Suture = 1,
    CollisionGranite = 2,
    FailedRift = 4,
}

/// <summary>
/// The geological record (design 2026-11-24, section 4), as parallel arrays indexed by slot. During the simulation the
/// slots are a pool that plates carry in their frames (<see cref="PlateFrames"/>); the stage result is a per-cell record
/// (<see cref="Gather"/>, slot = cell). Ages are million years (My) before present, 0 = active now,
/// <see cref="Never"/> = never happened. Thicknesses are decametres (dam).
/// </summary>
public sealed class CrustRecord
{
    public const ushort Never = ushort.MaxValue;

    /// <summary>Thickness of new oceanic crust (7 km).</summary>
    public const ushort OceanicThicknessDam = 700;

    /// <summary>Thickness of continental crust without mountains (35 km).</summary>
    public const ushort ContinentalThicknessDam = 3_500;

    private readonly Stack<int> _free = new();

    public CrustRecord(int capacity)
    {
        Resize(Math.Max(1, capacity));
    }

    /// <summary>Slots in use or freed (the next fresh slot index).</summary>
    public int Count { get; private set; }

    public int Capacity => Kind.Length;

    public CrustKind[] Kind { get; private set; } = [];
    public ushort[] ThicknessDam { get; private set; } = [];
    public ushort[] FormedAgeMy { get; private set; } = [];
    public ushort[] LastOrogenyAgeMy { get; private set; } = [];
    public OrogenyKind[] Orogeny { get; private set; } = [];
    public byte[] OrogenyCount { get; private set; } = [];
    public ushort[] ArcMagmaAgeMy { get; private set; } = [];
    public CrustFlags[] Flags { get; private set; } = [];
    public ushort[] RiftAgeMy { get; private set; } = [];
    public ushort[] PassiveMarginAgeMy { get; private set; } = [];
    public ushort[] SedimentDam { get; private set; } = [];
    public ushort[] MaxBurialDam { get; private set; } = [];
    public ushort[] CoalExposureMy { get; private set; } = [];
    public ushort[] EvaporiteExposureMy { get; private set; } = [];

    /// <summary>A slot for new crust (a freed one first), with every field reset to "never" and zero.</summary>
    public int Allocate()
    {
        int slot;
        if (_free.Count > 0) slot = _free.Pop();
        else
        {
            if (Count == Capacity) Resize(Capacity * 2);
            slot = Count++;
        }

        Reset(slot);
        return slot;
    }

    /// <summary>Returns a slot whose crust was destroyed (subducted, merged).</summary>
    public void Free(int slot) => _free.Push(slot);

    /// <summary>New oceanic crust at a spreading ridge (age 0); a passive margin starts when it opens beside rifted continent.</summary>
    public void GapFill(int slot, bool besideRiftedContinent)
    {
        Reset(slot);
        Kind[slot] = CrustKind.Oceanic;
        ThicknessDam[slot] = OceanicThicknessDam;
        FormedAgeMy[slot] = 0;
        if (besideRiftedContinent) PassiveMarginAgeMy[slot] = 0;
    }

    /// <summary>A per-cell record: cell c gets a copy of slot <paramref name="slotOfCell"/>[c].</summary>
    public CrustRecord Gather(ReadOnlySpan<int> slotOfCell)
    {
        var r = new CrustRecord(slotOfCell.Length) { Count = slotOfCell.Length };
        for (var c = 0; c < slotOfCell.Length; c++) CopySlot(slotOfCell[c], r, c);
        return r;
    }

    /// <summary>Copies every field of <paramref name="from"/> into slot <paramref name="toSlot"/> of <paramref name="to"/>.</summary>
    public void CopySlot(int from, CrustRecord to, int toSlot)
    {
        to.Kind[toSlot] = Kind[from];
        to.ThicknessDam[toSlot] = ThicknessDam[from];
        to.FormedAgeMy[toSlot] = FormedAgeMy[from];
        to.LastOrogenyAgeMy[toSlot] = LastOrogenyAgeMy[from];
        to.Orogeny[toSlot] = Orogeny[from];
        to.OrogenyCount[toSlot] = OrogenyCount[from];
        to.ArcMagmaAgeMy[toSlot] = ArcMagmaAgeMy[from];
        to.Flags[toSlot] = Flags[from];
        to.RiftAgeMy[toSlot] = RiftAgeMy[from];
        to.PassiveMarginAgeMy[toSlot] = PassiveMarginAgeMy[from];
        to.SedimentDam[toSlot] = SedimentDam[from];
        to.MaxBurialDam[toSlot] = MaxBurialDam[from];
        to.CoalExposureMy[toSlot] = CoalExposureMy[from];
        to.EvaporiteExposureMy[toSlot] = EvaporiteExposureMy[from];
    }

    public CrustRecord Clone()
    {
        var r = new CrustRecord(Capacity) { Count = Count };
        for (var s = 0; s < Count; s++) CopySlot(s, r, s);
        foreach (var f in _free.Reverse()) r._free.Push(f);
        return r;
    }

    /// <summary>Order-sensitive hash of every field of the slots in use.</summary>
    public ulong Hash()
    {
        var h = 0x43525553545245UL; // "CRUSTRE"
        for (var s = 0; s < Count; s++)
        {
            var a = (ulong)Kind[s] | ((ulong)ThicknessDam[s] << 8) | ((ulong)FormedAgeMy[s] << 24) | ((ulong)LastOrogenyAgeMy[s] << 40) | ((ulong)Orogeny[s] << 56);
            var b = (ulong)OrogenyCount[s] | ((ulong)ArcMagmaAgeMy[s] << 8) | ((ulong)Flags[s] << 24) | ((ulong)RiftAgeMy[s] << 32) | ((ulong)PassiveMarginAgeMy[s] << 48);
            var c = (ulong)SedimentDam[s] | ((ulong)MaxBurialDam[s] << 16) | ((ulong)CoalExposureMy[s] << 32) | ((ulong)EvaporiteExposureMy[s] << 48);
            h = DeterministicHash.Combine(h, a, b, c);
        }

        return h;
    }

    private void Reset(int s)
    {
        Kind[s] = CrustKind.Oceanic;
        ThicknessDam[s] = OceanicThicknessDam;
        FormedAgeMy[s] = 0;
        LastOrogenyAgeMy[s] = Never;
        Orogeny[s] = OrogenyKind.None;
        OrogenyCount[s] = 0;
        ArcMagmaAgeMy[s] = Never;
        Flags[s] = CrustFlags.None;
        RiftAgeMy[s] = Never;
        PassiveMarginAgeMy[s] = Never;
        SedimentDam[s] = 0;
        MaxBurialDam[s] = 0;
        CoalExposureMy[s] = 0;
        EvaporiteExposureMy[s] = 0;
    }

    private void Resize(int capacity)
    {
        T[] Grow<T>(T[] a) { var b = new T[capacity]; Array.Copy(a, b, Math.Min(a.Length, capacity)); return b; }
        Kind = Grow(Kind);
        ThicknessDam = Grow(ThicknessDam);
        FormedAgeMy = Grow(FormedAgeMy);
        LastOrogenyAgeMy = Grow(LastOrogenyAgeMy);
        Orogeny = Grow(Orogeny);
        OrogenyCount = Grow(OrogenyCount);
        ArcMagmaAgeMy = Grow(ArcMagmaAgeMy);
        Flags = Grow(Flags);
        RiftAgeMy = Grow(RiftAgeMy);
        PassiveMarginAgeMy = Grow(PassiveMarginAgeMy);
        SedimentDam = Grow(SedimentDam);
        MaxBurialDam = Grow(MaxBurialDam);
        CoalExposureMy = Grow(CoalExposureMy);
        EvaporiteExposureMy = Grow(EvaporiteExposureMy);
    }
}
