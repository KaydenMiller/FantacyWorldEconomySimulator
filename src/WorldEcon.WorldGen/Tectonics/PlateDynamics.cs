using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tectonics;

/// <summary>
/// Plate speeds and directions that change over the history (user request 2026-11-24). Each step a plate's speed eases
/// (e-folding <see cref="TectonicsSettings.SpeedEaseMy"/>) toward a target: its own drawn speed × slab pull (share of its
/// boundary subducting under a neighbour) × continental drag (continental share) × collision drag (while in a continental
/// collision). Every 10 million years each pole moves by a random step of
/// <see cref="TectonicsSettings.PoleDriftDegPer100My"/> / 10 degrees; the plate is rebased first so its crust stays where
/// it is.
/// </summary>
public static class PlateDynamics
{
    private const ulong DriftTag = 0x44524946UL; // "DRIF"

    /// <summary>Pole drift is applied every this many million years.</summary>
    public const int DriftEveryMy = 10;

    /// <summary>Target speed relative to the plate's own speed.</summary>
    public static double TargetFactor(TectonicsSettings t, double subductingShare, double continentalShare, bool inCollision) =>
        (1 + t.SlabPullBp / 10_000.0 * Math.Clamp(subductingShare, 0, 1))
        * (1 - t.ContinentalDragBp / 10_000.0 * Math.Clamp(continentalShare, 0, 1))
        * (inCollision ? 1 - t.CollisionDragBp / 10_000.0 : 1);

    public static void Update(StepState s)
    {
        var t = s.T;
        var plates = s.PlateCount;
        while (s.BaseStepMicroRad.Count < plates) s.BaseStepMicroRad.Add(Math.Abs(s.StepMicroRad[s.BaseStepMicroRad.Count]));
        var cells = new int[plates];
        var continental = new int[plates];
        for (var c = 0; c < s.Grid.CellCount; c++)
        {
            var p = s.PlateOfCell[c];
            cells[p]++;
            if (s.Pool.Kind[s.SlotOfCell[c]] != CrustKind.Oceanic) continental[p]++;
        }

        var ease = 1 - Math.Exp(-t.StepMy / (double)t.SpeedEaseMy);
        for (var p = 0; p < plates; p++)
        {
            var boundary = p < s.LastBoundaryCells.Length ? s.LastBoundaryCells[p] : 0;
            var subducting = p < s.LastSubductingCells.Length ? s.LastSubductingCells[p] : 0;
            var share = boundary > 0 ? subducting / (double)boundary : 0;
            var factor = TargetFactor(t, share, cells[p] > 0 ? continental[p] / (double)cells[p] : 0, s.LastCollided.Contains(p));
            var current = s.StepMicroRad[p];
            var sign = current < 0 ? -1 : 1;
            var target = s.BaseStepMicroRad[p] * factor;
            var next = Math.Abs(current) + (target - Math.Abs(current)) * ease;
            s.StepMicroRad[p] = sign * (long)Math.Round(next);
        }

        var driftSteps = Math.Max(1, DriftEveryMy / t.StepMy);
        if (t.PoleDriftDegPer100My <= 0 || s.StepIndex % driftSteps != 0) return;
        var angle = t.PoleDriftDegPer100My / 100.0 * driftSteps * t.StepMy * Math.PI / 180;
        var rng = new Xoshiro256StarStar(DeterministicHash.Combine(s.Seed, DriftTag, (ulong)s.StepIndex, 0));
        for (var p = 0; p < plates; p++)
        {
            var axis = s.Axis[p];
            var random = new Vector3d(Unit(rng.NextULong()) - 0.5, Unit(rng.NextULong()) - 0.5, Unit(rng.NextULong()) - 0.5);
            var tangent = random - axis * Vector3d.Dot(random, axis);
            if (tangent.Length < 1e-9) continue;
            var moved = (axis * Math.Cos(angle) + tangent.Normalize() * Math.Sin(angle)).Normalize();
            s.Frames.Rebase(p, s.PlateOfCell);
            s.Axis[p] = moved;
            s.Frames.SetAxis(p, moved);
        }
    }

    private static double Unit(ulong v) => (v >> 11) * (1.0 / ((1UL << 53) - 1));
}
