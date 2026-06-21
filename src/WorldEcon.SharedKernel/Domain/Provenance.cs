namespace WorldEcon.SharedKernel.Domain;

/// <summary>Whether a value is DM canon or simulation-evolved (spec §4.8).</summary>
public enum Provenance
{
    Authored = 0,
    Derived = 1,
}
