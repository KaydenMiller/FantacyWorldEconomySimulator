# Price chart design

Date: 2026-11-25. Status: draft for review (builder check done). Builds on the in-memory simulation state design (`2026-11-25-in-memory-simulation-state-design.md`) and is planned as extra tasks in its plan.

## Contents

1. [Intent](#1-intent)
2. [Goals, non-goals, anti-goals](#2-goals-non-goals-anti-goals)
3. [Where the code is today](#3-where-the-code-is-today)
4. [Approaches](#4-approaches)
5. [Data: daily price bars](#5-data-daily-price-bars)
6. [Building candles](#6-building-candles)
7. [Map selection](#7-map-selection)
8. [Chart window](#8-chart-window)
9. [CLI command](#9-cli-command)
10. [Where it fits in the in-memory plan](#10-where-it-fits-in-the-in-memory-plan)
11. [Left to the builder](#11-left-to-the-builder)
12. [Later work](#12-later-work)

Marking: **(user)** is a decision the user made; **(proposal)** is Claude's call, shown to the user in the playback and not contested.

## 1. Intent

The DM wants to see how the price of a good moves over months and years, in one city, a group of cities, one or more governments, or the whole world: trends, the cheapest and most expensive sales across a region, and where a period started and ended. The form is a candlestick chart: each candle shows open, high, low and close for its period. **(user)**

## 2. Goals, non-goals, anti-goals

Goals:

- Daily candles are the smallest; week, month and year candles are built from them. **(user)**
- The time axis pans and zooms over months or years; the candle size follows the zoom by default and can be set by hand. **(user)**
- Scope: a box selection on the map, one or more political regions, one or more countries, settlements in no country, or the whole world. **(user)**
- The chart opens only while no run is active, shows the state as of the last pause, and has a Refresh button; it never reads or redraws during a run. **(user)**
- Filter by sale kind: retail, merchant buy, merchant sell, or all. **(proposal)**
- The data is recorded during the simulation and kept in the world file, so a chart covers the whole history of the world from the day recording starts.

Non-goals (now):

- Hourly candles: every phase runs once a day at one tick, so there is no trading inside a day to divide. **(user: "likely less so")**
- Totals stored per government or for the world (section 12).
- Several goods on one chart.
- Bars for days before this feature exists: older worlds start their chart on the first day simulated after the upgrade.

Anti-goals:

- The chart slows the simulation. **(user)** It does no work during a run (goal above). Recording bars during a run cannot be avoided if the data is to exist; its cost is measured separately and reported at the price-chart checkpoint and in the after measurement (plan Task 13), and the user judges it. No threshold is set.
- Phases issue SQL to record bars (breaks the in-memory design's rule 5.2).
- The chart shows a value the simulation never had (an interpolated price for a day with no market).

## 3. Where the code is today

- No price history exists. `Stockpile.MarketPrice` is overwritten each day; a retail sale leaves only a log line ("Sold N … to townsfolk for … at …").
- Daily phase order: Production 10, PriceDiscovery 20, Perishability 30, Pricing 40, Trade 50.
- Sales and their prices:
  - Retail: `PriceDiscoveryPhase`, one unit at a time at the shop's ask; the last cleared ask becomes the market price of every stockpile of that good in the settlement.
  - Merchant buy: `TradePhase`, at the home city, `quantity × chosen.SeatPrice`.
  - Merchant sell: `TradePhase`, on delivery, `caravan.Quantity × destPrice` (`SeatRefPrice`).
- Raw and industrial goods are priced by `PricingPhase` (formula, one price per settlement and good); they reach the chart through their market price and merchant trades only.
- Governments: `Settlement.PoliticalRegionId`; `PoliticalRegion.CountryId` (optional).
- Calendar: `CalendarSystem.ToDate(Tick)` (year, month, day), `WeekdayIndex(Tick)`.
- GUI: Avalonia 12.1.6; the map draws with Skia (`SkiaDrawOperation`, `MapView`); plain left-drag pans, right-click is used; pop-out windows exist (`PanelWindow`).

## 4. Approaches

Drawing the chart (the data side has one sensible way, section 5):

| Approach | Trade-off | Outcome |
|---|---|---|
| A. Own Skia chart control | No dependency; the map already uses Skia; full control of zoom, candle-size switching and rollups. Axes, date labels, tooltip and pan/zoom are written by us (estimate: a few hundred lines). | **Chosen (user)** |
| B. ScottPlot 5 | Candlestick plot and pan/zoom built in. `ScottPlot.Avalonia` 5.1.58 declares Avalonia ≥ 11.3.4; no source found confirming Avalonia 12; possible SkiaSharp 3.119.4 clash. | Not chosen |
| C. LiveCharts2 | Candlestick series. 2.0.1 declares Avalonia ≥ 11.0.0; no source found confirming Avalonia 12. | Not chosen |

Avalonia's own candlestick chart needs a paid Avalonia Pro licence and was not considered.

## 5. Data: daily price bars

### 5.1 The row (proposal)

One `DailyPriceBar` per (settlement, good, day), an append-only domain entity:

| Field | Meaning |
|---|---|
| `WorldId`, `SettlementId`, `GoodId` | Keys |
| `Day` | `tick / Tick.DefaultMinutesPerDay` (day number since tick 0) |
| `Open`, `Close` | Market price at the start and end of the day, in base currency units; null when the settlement had no market price for the good |
| Per sale kind (retail, merchant buy, merchant sell): `Low`, `High`, `Volume`, `Turnover` | Lowest and highest unit price of that kind's sales that day; units sold; total money of those sales. `Low`/`High` null when `Volume` is 0 |

A row is written for a day when the settlement has a market price for the good at the start or end of the day, or any sale of it happened. Otherwise no row: the chart shows a gap.

Size at the measured world (6 settlements × 13 goods): at most 28,080 rows per simulated year. At 1,000 settlements × 30 goods: at most 10.8 million rows per simulated year.

### 5.2 Open and close (user)

- **Open** is the settlement's market price for the good before the day's first phase; **close** is the market price after the day's last phase. With no change between days, open equals the previous day's close.
- **Market price of a settlement for a good** (proposal): the rule `TradePhase.SeatRefPrice` uses today, the first positive `MarketPrice` among the settlement's shop stockpiles of that good in id order; none means no market price (null). One helper returns `Money?`; `TradePhase` keeps its own fallback to `good.BaseValue` on null, so merchants trade as today, and the chart records null Open/Close for that day. A merchant sale made at `BaseValue` is still recorded with that unit price, so such a day draws a wick with no body (Claude's call after the builder check: no shop posted `BaseValue`, so the body stays empty).

### 5.3 High, low and volume (user, details proposal)

Each sale is recorded with its sale kind, unit price and quantity, at the place it happens:

- Retail: each cleared unit in `PriceDiscoveryPhase` (unit price = the ask).
- Merchant buy: the purchase in `TradePhase` (unit price = `chosen.SeatPrice`, quantity = units withdrawn). When the purchase is capped at the merchant's capital (`TradePhase` line 220 today), High/Low use `SeatPrice` and Turnover adds the money actually paid, so Turnover can be below Volume × price on those days (Claude's call after the builder check; the cap itself is existing behaviour and unchanged).
- Merchant sell: the delivery in `TradePhase` (unit price = `destPrice`, quantity = `caravan.Quantity`), recorded against the destination settlement.

### 5.4 Recording (proposal)

- A `PriceBarRecorder` held by `WorldState` keeps the current day's open bars in memory, keyed by (settlement, good). Phases call `Record(settlement, good, kind, unitPrice, quantity)`; no SQL.
- Two engine hooks per day capture open (before the first daily phase) and close (after the last), then hand the finished day's rows to `WorldState` as added entities. Whether the hooks are phases with extreme `Order` values or calls in `EngineRun` is left to the builder (section 11).
- Finished rows are saved with the rest at each save (in-memory design, section 6) and dropped from memory after a successful save, like log events (plan Task 9).
- Money is integer base units throughout; no floating point in stored values.

## 6. Building candles

A query in `WorldEcon.Application` (proposal: `PriceSeriesQuery`) takes a good, a set of settlements, a sale-kind filter, a candle size and a day range, and returns candles:

`Candle(StartDay, EndDay, Open?, High?, Low?, Close?, SaleLow?, SaleHigh?, Volume, Turnover, CityCount)`

Step 1, across settlements for each day (proposal):

- Sale High = the highest `High` of the chosen kinds across the settlements; sale Low = the lowest `Low`. These are returned as recorded (`SaleHigh`, `SaleLow`).
- Drawn High and Low (user): the wick covers the sale extremes and the candle's Open and Close, so `High = max(SaleHigh, Open, Close)` and `Low = min(SaleLow, Open, Close)` over the values present. A body therefore never sticks out past its wick. Hover shows both pairs.
- Volume and Turnover = sums.
- Open and Close = average of the settlements' opens (closes), weighted by each settlement's volume of the chosen kinds that day; when the total volume is 0, the plain average of settlements that have a price. Integer division rounded half away from zero (`FixedMath.DivRound`).
- "All" kinds: High and Low over all three kinds, Volume and Turnover summed.

Step 2, over time for each candle period:

- Periods: day; calendar week (starts where `WeekdayIndex` is 0); calendar month; calendar year, all from `CalendarSystem`.
- Open = the first day in the period with an open; Close = the last day with a close; High = highest; Low = lowest; Volume, Turnover = sums; CityCount = distinct settlements with a row in the period.
- A period with no rows is not returned (gap).

Sources: bars in the world file, read only while no run is active (user). Every run ends with a save, so between runs the file holds every bar and the chart needs no in-memory read (Claude's call while planning). Step 2 for High and Low applies to sale and drawn values separately, then the drawn rule above is re-applied to the rolled-up Open and Close.

Scope resolution (user, details proposal), shared by the GUI and the CLI:

- Political region: settlements with that `PoliticalRegionId`.
- Country: settlements in regions whose `CountryId` is the country, plus regions and single settlements the country holds by a `TerritorialClaim` of kind `Controls`. `Disputes` claims are not counted.
- No country: settlements that no country includes by the rule above.
- Several picks are a union: a settlement counts once in a chart even when two picked countries include it.

## 7. Map selection

- Plain drag pans, unchanged. **(user)**
- **Shift+drag** draws a box; the selection becomes exactly the settlements inside it; anything selected before is unselected. A Shift+click with no drag therefore clears the selection. **(user)**
- **Ctrl+Shift+drag** toggles each settlement inside the box: selected ones are removed, unselected ones added. **(user)**
- The modifier keys held when the drag starts decide the mode. Alt is not used: KDE and GNOME take Alt+drag to move windows.
- Esc clears the selection when one exists; with no selection, Esc keeps its current meaning in `MainWindow` (back to the globe). **(proposal; Esc conflict found in the builder check)**
- "Inside the box" means the settlement's on-screen position, for settlements on the visible side of the globe. **(proposal)**
- Selected settlements are highlighted on the map; a counter shows how many are selected; a **Price chart** button opens the chart window for the selection. **(proposal)**

## 8. Chart window

All proposal, confirmed in playback:

- A pop-out window (as `PanelWindow` today), several may be open at once.
- Opens only while no run is active: the **Price chart** button is disabled during a run. A chart window left open when a run starts stays open, keeps showing what it had, and its Refresh button is disabled until the run ends. **(user)**
- Refresh button re-reads the data for the current scope, good and kind. **(user)**
- Scope picker: "Map selection (N cities)", political regions (multi-select), countries (multi-select), "No country", whole world. A chart opened from the map selection keeps the settlements it opened with; changing the map selection afterwards does not change an open chart.
- Good: one dropdown. Sale kind: All / Retail / Merchant buy / Merchant sell.
- Candle size: Auto (default) / Day / Week / Month / Year. Auto picks the smallest size that keeps the visible candles at or under 200.
- Mouse wheel zooms the time axis around the pointer; drag pans; double-click fits all data. The price axis fits the visible candles.
- Candles: body from open to close (rising and falling in two colours from the theme), wick from low to high. A candle with no sales draws its body with no wick. A candle with no open or close draws the wick only.
- Volume bars in a strip under the candles.
- Hover shows: period dates (world calendar), open, close, wick high and low, sale high and low (world currency format), volume, turnover, city count.
- Drawn with Skia in a custom Avalonia control. **(user)**

## 9. CLI command

`prices <db> <good> [--city <name>]… [--region <name>]… [--country <name>]… [--no-country] [--kind all|retail|buy|sell] [--candle day|week|month|year] [--from <date>] [--to <date>]` prints the candles as a table (period, open, high, low, close, sale low, sale high, volume, cities). No scope option means the whole world. It lets the user check the recorded data before the GUI exists. **(proposal)**

## 10. Where it fits in the in-memory plan

- Recording, the candle query and the CLI command: after Checkpoint C (phases on `WorldState`, saves every 4 months), so the recorder lives in memory and saves with the rest.
- Map selection and the chart window: after Checkpoint D (GUI on the session).
- A new checkpoint before E, so E's after measurement includes recording cost.

## 11. Left to the builder

- Whether the open and close hooks are phases or `EngineRun` calls, as long as open is taken before the first daily phase and close after the last.
- EF mapping of the per-kind columns (owned types or flat columns), and the indexes, as long as a query by (world, good, day range) and by (settlement, good, day) does not scan the table.
- Colours, fonts and spacing of the chart, from the existing theme.
- Chart control internals (layout, hit testing, caching).

## 12. Later work

- A bulk insert path for bars outside EF (Entity Framework), if worlds near 1,000 settlements × 30 goods are simulated: a 4-month save there inserts up to 3.6 million bar rows. Planned work measures only the seed-42 world.
- Totals per political region, per country and for the world (weekly and monthly), if long views on large worlds read too many rows (5 years at 1,000 settlements × 30 goods is up to 54 million daily rows).
- Hourly candles, once trading happens at more than one tick a day (for example light-bridge openings in the overview, section 13).
- Freehand (lasso) selection.
- Several goods, or a price index, on one chart.
