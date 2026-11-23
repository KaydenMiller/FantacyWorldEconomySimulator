using WorldEcon.SharedKernel.Globe;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.Gui.ViewModels;

/// <summary>One natural area in the Areas list.</summary>
public sealed record AreaItem(int Index, GeographicRegionKind Kind, string Name, int CellCount)
{
    public string Display => $"{Name} — {Kind}, {CellCount:N0} cells";
}

/// <summary>
/// The Areas tab: every natural area, filtered by kind (index 0 = all kinds) and a case-insensitive name search.
/// Selecting an area calls the owner back (the main view-model highlights it on the globe).
/// </summary>
public sealed class AreasViewModel : ObservableObject
{
    public const string AllKinds = "All kinds";

    private readonly Action<AreaItem?> _onSelected;
    private int _kindIndex;
    private string _search = "";
    private AreaItem? _selected;
    private IReadOnlyList<AreaItem> _filtered;

    public AreasViewModel(IReadOnlyList<GeneratedArea> areas, Action<AreaItem?> onSelected)
    {
        _onSelected = onSelected;
        Items = areas.OrderBy(a => a.Kind).ThenBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => new AreaItem(a.Index, a.Kind, a.Name, a.Cells.Count)).ToList();
        KindNames = [AllKinds, .. Items.Select(i => i.Kind).Distinct().Order().Select(k => k.ToString())];
        _filtered = Items;
    }

    public static AreasViewModel Empty { get; } = new([], _ => { });

    public IReadOnlyList<AreaItem> Items { get; }

    public List<string> KindNames { get; }

    public IReadOnlyList<AreaItem> Filtered
    {
        get => _filtered;
        private set => Set(ref _filtered, value);
    }

    public int KindIndex
    {
        get => _kindIndex;
        set
        {
            if (value < 0 || value >= KindNames.Count) return;
            if (Set(ref _kindIndex, value)) Refilter();
        }
    }

    public string SearchText
    {
        get => _search;
        set
        {
            if (Set(ref _search, value ?? "")) Refilter();
        }
    }

    public AreaItem? SelectedArea
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) _onSelected(value);
        }
    }

    private void Refilter()
    {
        var kind = _kindIndex == 0 ? null : KindNames[_kindIndex];
        var text = _search.Trim();
        Filtered = Items.Where(i => (kind is null || i.Kind.ToString() == kind)
                && (text.Length == 0 || i.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
