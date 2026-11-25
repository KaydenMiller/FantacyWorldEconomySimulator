using System.Globalization;
using ErrorOr;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Economy;
using WorldEcon.Rendering;
using WorldEcon.SharedKernel;

namespace WorldEcon.Gui.ViewModels;

/// <summary>One line of a city panel section: fixed-width text, and an action when the line is a link.</summary>
public sealed record TableLine(string Text, Action? Open = null)
{
    public bool HasLink => Open is not null;

    public bool HasNoLink => Open is null;
}

/// <summary>A settlement offered by the city panel's search.</summary>
public sealed record SettlementChoice(Guid Id, string Name);

/// <summary>
/// The city panel for one settlement (docked City tab, or a pop-out window pinned to one city): market, shops, seated
/// merchants, travel-time table, route entrances and caravans inbound, outbound and returning. Data comes from
/// <see cref="SettlementDetailsQuery"/> on a worker thread; a result for a city that is no longer shown is dropped.
/// Re-queries when the session raises <see cref="WorldSession.DataChanged"/> until <see cref="Detach"/>. Framework-free.
/// </summary>
public sealed class CityPanelViewModel : ObservableObject
{
    public const string NoDatabaseText = "No database: economy views need a world opened from a file.";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly WorldSession _session;
    private readonly Action<MapObject> _selectOnMap;
    private readonly Action<Guid> _highlightRoute;
    private readonly Action<Guid>? _centreOn;
    private readonly Func<Guid, CancellationToken, Task<ErrorOr<SettlementDetails>>>? _query;
    private readonly Func<Task<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>>>? _list;
    private IReadOnlyList<SettlementChoice>? _all;
    private int _request;
    private bool _attached = true;
    private Guid? _settlementId;
    private SettlementDetails? _details;
    private string _error = "";
    private bool _isLoading;
    private string _searchText = "";
    private string _vehicleFilter = "all";
    private bool _fromHereOnly = true;
    private bool _marketExpanded = true, _travelExpanded = true, _shopsExpanded, _merchantsExpanded, _entrancesExpanded, _caravansExpanded;
    private bool _canPopOut;

    public CityPanelViewModel(WorldSession session, Action<MapObject> selectOnMap, Action<Guid> highlightRoute, Action<Guid>? centreOn = null)
        : this(session, selectOnMap, highlightRoute, centreOn, null, null)
    {
    }

    /// <summary>For tests: <paramref name="query"/> and <paramref name="list"/> replace the database reads.</summary>
    internal CityPanelViewModel(WorldSession session, Action<MapObject> selectOnMap, Action<Guid> highlightRoute, Action<Guid>? centreOn,
        Func<Guid, CancellationToken, Task<ErrorOr<SettlementDetails>>>? query, Func<Task<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>>>? list)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _selectOnMap = selectOnMap;
        _highlightRoute = highlightRoute;
        _centreOn = centreOn;
        // The run is looked up at call time: a panel made before Play reads through the run while it is open.
        _query = query ?? (session.Path is { } path
            ? (id, ct) => session.ActiveRun is { } run ? run.SettlementDetailsAsync(id, ct) : SettlementDetailsQuery.RunAsync(path, id, ct)
            : null);
        _list = list ?? (session.Path is { } p
            ? () => session.ActiveRun is { } run ? run.SettlementListAsync() : SettlementDetailsQuery.ListAsync(p)
            : null);
        _session.DataChanged += OnDataChanged;
        _session.ActiveRunChanged += OnActiveRunChanged;
    }

    /// <summary>The Refresh button is enabled: not while a simulation run is open (the time controls re-read the panels).</summary>
    public bool CanRefresh => _session.ActiveRun is null;

    private void OnActiveRunChanged(object? sender, EventArgs e) => Raise(nameof(CanRefresh));

    /// <summary>Raised when the user asks to open this city in its own window.</summary>
    public event EventHandler? PopOutRequested;

    /// <summary>Completes when the last load started finished.</summary>
    public Task Loaded { get; private set; } = Task.CompletedTask;

    public Guid? SettlementId
    {
        get => _settlementId;
        private set => Set(ref _settlementId, value);
    }

    public SettlementDetails? Details => _details;

    public bool HasDetails => _details is not null;

    public string HeaderText => _details?.Header.Name ?? (_settlementId is null ? "No city selected" : "");

    public string SubHeaderText => _details?.Header is { } h
        ? string.Create(Inv, $"{h.Type} · pop {h.Population:N0}{(h.Port == Domain.Geography.PortKind.None ? "" : $" · port {h.Port.ToString().ToLowerInvariant()}")} · tile {(h.TileId is { } t ? t.ToString(Inv) : "none")}{(h.State == Domain.Geography.SettlementState.Active ? "" : $" · {h.State}")}")
        : "";

    public string AsOfText => _isLoading ? "loading…" : _details is { } d ? $"as of {d.Header.ReadDate}" : "";

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool HasError => _error.Length > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value)) Raise(nameof(AsOfText));
        }
    }

    // ---- search ----

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            Raise(nameof(SearchResults));
            Raise(nameof(HasSearchResults));
        }
    }

    /// <summary>Settlements whose name contains <see cref="SearchText"/> (case-insensitive); empty while the text is empty.</summary>
    public IReadOnlyList<SettlementChoice> SearchResults => _searchText.Trim().Length == 0 || _all is null
        ? []
        : _all.Where(s => s.Name.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    public bool HasSearchResults => SearchResults.Count > 0;

    /// <summary>Shows the chosen settlement, centres the map on it and clears the search.</summary>
    public async Task ChooseAsync(SettlementChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        SearchText = "";
        _centreOn?.Invoke(choice.Id);
        await ShowAsync(choice.Id);
    }

    // ---- loading ----

    /// <summary>Shows <paramref name="settlementId"/>: "loading…", then its details (or the error).</summary>
    public Task ShowAsync(Guid settlementId)
    {
        SettlementId = settlementId;
        Loaded = LoadAsync(settlementId);
        return Loaded;
    }

    /// <summary>Re-reads the shown settlement.</summary>
    public Task RefreshAsync() => _settlementId is { } id ? ShowAsync(id) : Task.CompletedTask;

    /// <summary>
    /// The Refresh button: raises <see cref="WorldSession.DataChanged"/> once, so the caravans and every open panel
    /// (this one included) are read again.
    /// </summary>
    public void RefreshAll()
    {
        if (CanRefresh) _session.RaiseDataChanged();
    }

    /// <summary>Stops following the session (the panel is closed or the world replaced).</summary>
    public void Detach()
    {
        _attached = false;
        _request++;
        _session.DataChanged -= OnDataChanged;
        _session.ActiveRunChanged -= OnActiveRunChanged;
    }

    private void OnDataChanged(object? sender, EventArgs e)
    {
        if (_attached) _ = RefreshAsync();
    }

    private async Task LoadAsync(Guid id)
    {
        var request = ++_request;
        if (_query is null)
        {
            SetDetails(null);
            Error = NoDatabaseText;
            return;
        }

        IsLoading = true;
        try
        {
            if (_all is null && _list is not null && await _session.Track(Task.Run(_list)) is { IsError: false } listed)
                _all = listed.Value.Select(x => new SettlementChoice(x.Id, x.Name)).ToList();
            var result = await _session.Track(Task.Run(() => _query(id, CancellationToken.None)));
            if (request != _request || _session.IsClosed) return; // another city was asked for, or the world closed
            if (result.IsError)
            {
                SetDetails(null);
                Error = result.FirstError.Description;
            }
            else
            {
                Error = "";
                SetDetails(result.Value);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (request != _request) return;
            SetDetails(null);
            Error = ex.Message;
        }
        finally
        {
            if (request == _request) IsLoading = false;
        }
    }

    private void SetDetails(SettlementDetails? details)
    {
        _details = details;
        if (details is not null && !details.TravelTimes.Any(t => t.VehicleClass == _vehicleFilter)) _vehicleFilter = "all";
        foreach (var name in new[]
                 {
                     nameof(Details), nameof(HasDetails), nameof(HeaderText), nameof(SubHeaderText), nameof(AsOfText), nameof(MarketTitle),
                     nameof(MarketHeader), nameof(MarketLines), nameof(ShopsTitle), nameof(ShopLines), nameof(MerchantsTitle), nameof(MerchantLines),
                     nameof(VehicleClasses), nameof(VehicleFilter), nameof(EntrancesTitle), nameof(EntranceHeader), nameof(EntranceLines),
                     nameof(CaravansTitle), nameof(CaravanLines), nameof(SearchResults),
                 })
            Raise(name);
        RaiseTravel();
        Raise(nameof(HasError));
    }

    // ---- sections ----

    private string Money(long units) => _session.Data.World.Currency.Format(new Money(units));

    public string MarketTitle => $"Market ({_details?.Market.Count ?? 0} goods)";

    public string MarketHeader => Market.Header;

    public IReadOnlyList<TableLine> MarketLines => Market.Lines;

    private (string Header, IReadOnlyList<TableLine> Lines) Market => TextTable.Format(
        ["Good", "Cat.", "Qty", "Min cost", "Price"], [false, false, true, true, true],
        (_details?.Market ?? []).Select(m => (new[] { m.Good, m.Category.ToString(), m.Quantity.ToString("N0", Inv), Money(m.MinCostBasis), Money(m.Price) }, (Action?)null)));

    public string ShopsTitle => $"Shops ({_details?.Shops.Count ?? 0})";

    public IReadOnlyList<TableLine> ShopLines
    {
        get
        {
            var lines = new List<TableLine>();
            foreach (var s in _details?.Shops ?? [])
            {
                lines.Add(new TableLine(string.Create(Inv, $"{s.Name} · {s.Kind} · markup {s.MarkupBp / 100.0:F2} % · till {Money(s.Till)}")));
                var (header, rows) = TextTable.Format(["  Good", "Qty", "Cost", "Retail", "Margin", "%"], [false, true, true, true, true, true],
                    s.Stock.Select(l => (new[] { "  " + l.Good, l.Quantity.ToString("N0", Inv), Money(l.CostBasis), Money(l.RetailPrice), Money(l.MarginAbs), l.MarginPct.ToString("0.##", Inv) }, (Action?)null)));
                if (rows.Count == 0)
                {
                    lines.Add(new TableLine("  (no stock)"));
                    continue;
                }

                lines.Add(new TableLine(header));
                lines.AddRange(rows);
            }

            return lines;
        }
    }

    public string MerchantsTitle => $"Merchants ({_details?.Merchants.Count ?? 0})";

    public IReadOnlyList<TableLine> MerchantLines
    {
        get
        {
            var lines = new List<TableLine>();
            var caravans = (_details?.Caravans ?? []).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var m in _details?.Merchants ?? [])
            {
                lines.Add(new TableLine(string.Create(Inv,
                    $"Merchant {m.Id.ToString()[..8]} · capital {Money(m.Capital)} · reach {m.Reach:N0} · {m.VehicleClass} · risk aversion {m.RiskAversion}")));
                foreach (var t in m.LastTrips)
                    lines.Add(new TableLine(string.Create(Inv, $"  trip: {t.From} → {t.To} by {t.VehicleClass}, {t.Ticks / 1440.0:F2} d, {t.LengthMetres / 1000.0:F1} km")));
                foreach (var id in m.CaravansOut)
                {
                    var text = caravans.TryGetValue(id, out var c) ? $"  caravan out: {c.Good} × {c.Quantity:N0}, {c.Kind.ToString().ToLowerInvariant()} {c.OtherEnd}" : $"  caravan out: {id.ToString()[..8]}";
                    var cid = id;
                    lines.Add(new TableLine(string.Create(Inv, $"{text}"), () => _selectOnMap(new MapObject(MapObjectKind.Caravan, cid))));
                }
            }

            return lines;
        }
    }

    // ---- travel times ----

    public bool FromHereOnly
    {
        get => _fromHereOnly;
        set
        {
            if (Set(ref _fromHereOnly, value)) RaiseTravel();
        }
    }

    /// <summary>"all" then the vehicle classes in the table (ordinal).</summary>
    public IReadOnlyList<string> VehicleClasses =>
        ["all", .. (_details?.TravelTimes ?? []).Select(t => t.VehicleClass).Distinct().OrderBy(v => v, StringComparer.Ordinal)];

    public string VehicleFilter
    {
        get => _vehicleFilter;
        set
        {
            if (Set(ref _vehicleFilter, string.IsNullOrEmpty(value) ? "all" : value)) RaiseTravel();
        }
    }

    public IReadOnlyList<TravelTimeLine> VisibleTravelTimes => (_details?.TravelTimes ?? [])
        .Where(t => !_fromHereOnly || t.FromId == _details!.Header.Id)
        .Where(t => _vehicleFilter == "all" || t.VehicleClass == _vehicleFilter)
        .ToList();

    public string TravelTitle => _details is null ? "Travel times (0)"
        : VisibleTravelTimes.Count == _details.TravelTimes.Count ? $"Travel times ({_details.TravelTimes.Count})"
        : $"Travel times ({VisibleTravelTimes.Count} of {_details.TravelTimes.Count})";

    public string TravelHeader => Travel.Header;

    public IReadOnlyList<TableLine> TravelLines => Travel.Lines;

    private (string Header, IReadOnlyList<TableLine> Lines) Travel => TextTable.Format(
        ["From → To", "Vehicle", "Days", "Km", "Source"], [false, false, true, true, false],
        VisibleTravelTimes.Select(t => (new[]
        {
            $"{t.From} → {t.To}", t.VehicleClass, (t.Ticks / 1440.0).ToString("0.00", Inv), (t.LengthMetres / 1000.0).ToString("0.0", Inv),
            t.Source == KnowledgeSource.Reported ? $"Reported t{t.UpdatedTick}" : "Estimated",
        }, (Action?)null)));

    private void RaiseTravel()
    {
        Raise(nameof(VisibleTravelTimes));
        Raise(nameof(TravelTitle));
        Raise(nameof(TravelHeader));
        Raise(nameof(TravelLines));
    }

    // ---- route entrances ----

    public string EntrancesTitle => $"Route entrances ({_details?.Entrances.Count ?? 0})";

    public string EntranceHeader => Entrances.Header;

    public IReadOnlyList<TableLine> EntranceLines => Entrances.Lines;

    private (string Header, IReadOnlyList<TableLine> Lines) Entrances => TextTable.Format(
        ["Edge", "Cat.", "Surface", "Other end", "Km", "Traffic"], [false, false, false, false, true, true],
        (_details?.Entrances ?? []).Select<EntranceLine, (string[] Cells, Action? Open)>(e =>
        {
            var id = e.RouteId;
            return (new[]
            {
                e.Edge < 0 ? "?" : $"e{e.Edge}", e.Category.ToString(), e.Surface.ToString(), e.OtherEnd, (e.LengthMetres / 1000.0).ToString("0.0", Inv),
                e.Traffic.ToString("N0", Inv),
            }, () => _highlightRoute(id));
        }));

    // ---- caravans here ----

    public string CaravansTitle => $"Caravans here ({_details?.Caravans.Count ?? 0})";

    public IReadOnlyList<TableLine> CaravanLines
    {
        get
        {
            var (_, lines) = TextTable.Format(["", "Goods", "", "Vehicle", "When"], [false, false, false, false, false],
                (_details?.Caravans ?? []).Select(c =>
                {
                    var id = c.Id;
                    var (where, when) = c.Kind switch
                    {
                        CaravanHereKind.Inbound => ($"from {c.OtherEnd}", $"arrives {DetailFormatter.Date(_session, c.Tick)}"),
                        CaravanHereKind.Outbound => ($"to {c.OtherEnd}", $"arrives {DetailFormatter.Date(_session, c.Tick)}"),
                        _ => ($"back from {c.OtherEnd}", $"home {DetailFormatter.Date(_session, c.Tick)}"),
                    };
                    return (new[] { c.Kind.ToString(), string.Create(Inv, $"{c.Good} × {c.Quantity:N0}"), where, c.VehicleClass, when },
                        c.Kind == CaravanHereKind.Returning ? null : (Action?)(() => _selectOnMap(new MapObject(MapObjectKind.Caravan, id))));
                }));
            return lines;
        }
    }

    // ---- expanders and pop-out ----

    public bool MarketExpanded { get => _marketExpanded; set => Set(ref _marketExpanded, value); }

    public bool TravelExpanded { get => _travelExpanded; set => Set(ref _travelExpanded, value); }

    public bool ShopsExpanded { get => _shopsExpanded; set => Set(ref _shopsExpanded, value); }

    public bool MerchantsExpanded { get => _merchantsExpanded; set => Set(ref _merchantsExpanded, value); }

    public bool EntrancesExpanded { get => _entrancesExpanded; set => Set(ref _entrancesExpanded, value); }

    public bool CaravansExpanded { get => _caravansExpanded; set => Set(ref _caravansExpanded, value); }

    /// <summary>True where separate windows exist (desktop); set by the host.</summary>
    public bool CanPopOut { get => _canPopOut; set => Set(ref _canPopOut, value); }

    public void RequestPopOut() => PopOutRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>Pads table cells into fixed-width lines (for a monospaced font): a header line and one line per row.</summary>
public static class TextTable
{
    public static (string Header, IReadOnlyList<TableLine> Lines) Format(string[] headers, bool[] rightAligned, IEnumerable<(string[] Cells, Action? Open)> rows)
    {
        var list = rows.ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, list.Count == 0 ? 0 : list.Max(r => r.Cells[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => rightAligned[i] ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd();
        return (Line(headers), list.Select(r => new TableLine(Line(r.Cells), r.Open)).ToList());
    }
}
