# Living world: end-goal overview

Date: 2026-11-24. Status: draft for the user's review. Follows project 2 (plate history simulation, built).
Scope: the end goal of resources (geological, wild, crops, livestock), land, households, money, caravans, news and knowledge (guarded and written), road danger, technology, the government system (to be defined), and the deferred magic, literacy and espionage systems, and how they connect. Each system gets its own design and plan when its project starts. This document sets what those designs must fit; it does not set build details.

Marking used below:
- **(user)**: decided by the user in the definition conversation.
- **(proposal, not contested)**: proposed by Claude and not corrected by the user. Each one is open to change in the project's design.

## Contents

1. [Intent](#1-intent)
2. [Principles](#2-principles)
3. [How the systems connect](#3-how-the-systems-connect)
4. [Resources](#4-resources)
5. [Land, plots and rings](#5-land-plots-and-rings)
6. [Households, work and happiness](#6-households-work-and-happiness)
7. [Money](#7-money)
8. [Caravans and migration](#8-caravans-and-migration)
9. [News and knowledge](#9-news-and-knowledge)
10. [Road danger](#10-road-danger)
11. [Technology](#11-technology)
12. [Government](#12-government)
13. [Magic (deferred)](#13-magic-deferred)
14. [What the code has today](#14-what-the-code-has-today)
15. [Build order](#15-build-order)
16. [Open items](#16-open-items)
17. [Weather](#17-weather)
17a. [DM tools](#17a-dm-tools-user)
17b. [Price chart](#17b-price-chart-user)
18. [Next steps](#18-next-steps)

## 1. Intent

Make the world's resources geologically accurate within reason and let them drive where towns form, how towns live, and how they fail. The original request: realistic salt, iron, copper, oil, coal, forest density and soil fertility, later used to found towns and cities (user).

The worked example that shaped the design (user): a town's iron mine empties in campaign year 30. If the town has other reasons to exist (trade hub, port, forestry, grain), it continues, depending on its economy and its people's happiness. If the mine was its only reason, people lose their needs, leave, and the town is marked abandoned once nobody is left.

## 2. Principles

- **Geologically accurate within reason (user).** Deposits come from the tectonic record, not random placement. Deliberate departures from geology are switches the dungeon master (DM) turns on (the campaign cycle in 4.6, play-time exposure events in 4.5).
- **The world never runs out; a place can (user).** Single sites deplete. World and landmass floors keep each resource available somewhere. A landmass can lack a resource entirely, which creates conflict and story points (user).
- **DM control over natural growth (user).** The DM can let systems run (research, discovery, exposure) or step in (grant technology, mark locations, trigger events, later force resources to exist through world editing).
- **End goal first, build order second (user).** This document describes the end goal. Section 15 says what is built first; simple first versions are noted there, not in the system sections.
- **Open to extension (user).** Weighted decisions (for example migrant destination) take further measures over time.
- **Simulation speed (user).** The goal is about 1 simulated year per minute of real time, give or take a few seconds (equivalently about 1 simulated week per second), with every system in this document running. At 60 s per year: 8,760 one-minute ticks per second (about 114 µs per tick on average) and about 164 ms per simulated day; 1 week per second gives 52 s per year and 143 ms per day. The world size it must hold at, and the hardware, are not yet set. Approach under discussion (proposal, not contested): discrete-event scheduling (entities wake when due, not every minute), groups of nearby cities processed in parallel inside time windows set by the shortest link between groups (light bridge booking lead time, 13), with SQLite written at checkpoints rather than after every boundary.
- **Several DMs (user).** The user's group rotates DMs, with the user as the main one, so the world will later need to be shared by several DMs. Postgres is the likely database then (user: "eventually"). Until then a world stays a SQLite file; simulation state is held in memory during a run and saved at checkpoints (user), so the database choice does not set simulation speed.
- **Checkpoints (user).** A run is a play session (until pause) or a jump to a date. A crash may lose the whole run, but a run saves every 4 simulated months while it goes, so a long jump loses at most 4 months (user). A step button (1 minute, 1 day, 1 week, 1 month, 1 year, or any increment) is its own run and saves when it finishes (user).
- **What must be saved (user):** the state on exit and every DM change; the simulation is meant to be deterministic, so anything else can be recomputed. Today it is not bit-for-bit repeatable: rows created during a run get random ids and phases visit some rows in id order (`docs/capabilities-and-roadmap.md`, "Seeded entity IDs"; 3,490 against 3,520 log events after 60 days). Determinism is not set up now (user): saves hold the full state, and the pieces that allow it later stay in place (seeded random streams per subsystem, stable visiting order); new code does not add sources of randomness outside the seeded streams (proposal, not contested).

## 3. How the systems connect

```
 Tectonic record ──► Resources (deposits, basins, forest, fertility)
 Climate, biome ───► Wild populations, native ranges of crops and livestock
                          │
                          ▼
         Plots and land use (on demand, rings around towns)
                          │
                          ▼
   Households ◄── wages ── Producers (mines, farms, forests, crafts)
     │   ▲                     ▲
 needs│   │ goods               │ technology unlocks depth bands,
     ▼   │                     │ farming practice, new production
 Happiness ── unhappy > X ──► Caravans (board, groups, speed)
                                │        │
                     arrive: population  │ carry
                                ▼        ▼
                              Towns ◄── News (records, headlines, reports)
                                ▲            │
                Road danger ────┘            ▼
             (risk, encounters)        Technology spread
                                       Guarded and written knowledge
                                       (holders, leaks, sales, letters)
 Money: minted from mined gold and silver; ruler spending and taxes (government)
```

## 4. Resources

### 4.1 Deposit types (research report, section 5.10; not yet reviewed by the user)

Deposits are generated from the tectonic record by type. The type sets which ores occur together, replacing random ore weighting (the user withdrew random mixtures as inconsistent with the simulation). Every geological resource in the list in 4.9 (user) has at least one deposit type below. Types come from the research report (`docs/research/reports/Resource generation research.md`, sections 5.1–5.10); rule thresholds there are tuning start values, not sourced figures.

| Deposit type | Forms where (tectonic record) | Main output | Usual by-products | Model (4.3) | Needs a crust record change |
|---|---|---|---|---|---|
| Porphyry | above subduction zones (Andean and island arcs) | copper | molybdenum, gold, silver | point | no |
| Epithermal vein | young volcanic arcs, shallow (≤ 1.5 km), so erosion removes it first | silver, gold | lead, zinc, copper; tin at Potosí | point | no |
| Massive sulphide (felsic, bimodal) | island arcs, accreted arc terranes | copper, zinc | lead, silver, gold | point | no |
| Massive sulphide (mafic, Cyprus type) | ocean crust pushed onto land (ophiolite) | copper | — | point | yes: ophiolite marker |
| Sediment-hosted copper | rift basins with red beds and salt | copper | silver, cobalt | point | no |
| Lead–zinc in carbonate rock | carbonate platform later caught near an orogeny | zinc, lead | silver | point (districts) | yes: event order |
| Orogenic gold | sutures and accreted terranes | gold | — | point | no |
| Tin vein and greisen | granites late in or after a collision | tin | tungsten, arsenic, copper, lead, zinc | point | no |
| Banded iron | oldest continental crust, inside its formation window (4.1a) | iron | almost none | point (very large) | yes: older cratons |
| Oolitic ironstone | shallow marine basins | iron | — | basin | no |
| Bog iron | swamp and wetland tiles | iron | — | renewable, per tile | no |
| Kimberlite pipe | oldest cratons with no orogeny; rare random pipes | diamond | — | point | yes: older cratons |
| Marble-hosted ruby | carbonate margin, then a collision | ruby | — | point | yes: event order |
| Basalt sapphire | young rifts over mantle plumes | sapphire | — | point | partial: hotspot marker for full coverage |
| Emerald | collision granite beside a suture; or salt basin, then an orogeny | emerald | — | point | yes for the second kind: event order |
| Placer | river gravels and beaches downstream of an exposed source above | gold, tin, ruby, sapphire, diamond (no emerald: too fragile) | — | per river tile, following hydrology | no |
| Coal seam | buried swamp sediment | coal | — | basin | yes: burial condition for exposure |
| Salt beds | dry basins (evaporite exposure); brine springs where cover is thin | salt | potash, gypsum | basin | no |
| Oil seep and pitch | deeply buried source rock (oil window) with young faulting | mineral oil, pitch | — | basin, seeps at the margin | yes: burial depth separate from sediment |
| Amber | young coastal sediment near coal-forming swamps; washed onto beaches | amber | — | basin, plus beach collection | no |
| Marble | carbonate platform, then any later orogeny | marble | — | point (quarry) | yes: event order |
| Good stone: granite | collision granites and arc plutons exposed by erosion | granite | — | tile area | no |
| Stone in general: limestone, sandstone | sedimentary cover | building stone | — | tile area | no |

- Lead and zinc are not in the user's resource list; they come as by-products of the types above and are tradeable goods.
- The crust record changes in the last column are the gaps the research found (research report, section 5.11; section 16 of this overview): event order, an ophiolite marker, a hotspot marker, craton ages old enough for banded iron and diamonds, burial depth that is not equal to sediment thickness, and a burial condition on coal exposure. Project 1a fixes them or places those types by best guess (4.1a).

### 4.1a Simulated time (user)

- The tectonic simulation runs for a longer period at generation so that old deposits (banded iron, diamonds) come from simulated history. A slider sets the simulated time, up or down.
- Each deposit type has a required simulated time. If the slider is set below it, the DM is alerted, and that deposit type is placed by best guess instead of simulated data.
- Default about 3,000 million years (My), against 250 My today (user confirmed). Estimated tectonics time about 32 s if cost scales with steps (not measured). Continent tuning is measured again at the new default and adjusted later as needed (user).
- Deposit types that depend on ocean or atmosphere chemistry (banded iron: on Earth mostly 2.5–2.8 billion years ago, around the rise of atmospheric oxygen) get a formation window on the simulated timeline that the DM can move or remove (user).
- The best-guess fallback uses the assigned craton age (today: simulated time + 500 + random 0–1,500 My) (proposal, not contested).
- The per-cell event log keeps the most important events, not the most recent (user). Importance (proposal, not contested): events that form deposits first (subduction arc, collision, rift, deep burial, evaporite and coal exposure), then strength, then recency.

### 4.2 Deposit properties (user)

- **Quantity:** the true total in the ground, set at world generation. The world knows only the discovered part.
- **Grade:** ore share of each load (50% grade: half ore, half waste or other ores). Real ranges for reference (research report `docs/research/reports/Resource generation research.md`, section 3): copper depends on deposit type (porphyry median 0.44%, most 0.3–2.0%; volcanogenic massive sulphide medians 1.2–1.7%; sediment-hosted 1.08–2.59%); iron depends on ore type (raw banded iron formation 15–35%, enriched hematite 60–65%, oolitic ironstone 28–34%, bog ore up to 62%); coal ash averages 5.3% (subbituminous) to 12.6% (lignite), 10.3% for bituminous, so about 87–95% non-ash; coal above 33% ash is not counted as a resource (research report, section 5.8). Gold: orogenic veins 8–16 g/t; epithermal 0.1 to over 30 g/t; placer median 0.63 g/m³. Silver: epithermal under 1 to over 1,000 g/t. Diamonds: kimberlite 5–400 carats per hundred tonnes, typically 50–100 (sections 5.4, 5.6, 5.7).
- **Depth profile:** how much ore lies in each depth band. Depth sets work per load and the technology needed for each band. Pre-industrial depth was set by the power available to lift water: adit-drained, water-pumped mines reached about 500 m (Kutná Hora, about 1400) and 886 m (Röhrerbühel, 1618); drainage adits alone reached 80–100 m (research report, section 6).
- Grade and depth are separate numbers (user).

### 4.3 Point deposits and basins (user)

- **Point deposits** (copper, iron and other ore bodies): one record with a footprint. A porphyry ore body covers a median 0.6 km² (about 0.9 km across if round; maximum 28 km²), inside an alteration zone of median 5.1 km²; ore extends up to about 1–1.5 km deep (research report, section 5.1).
- **Basins** (coal, oil, salt beds): one basin record holding the seam's thickness, grade and depth surface, split into a quantity per tile. A mine depletes only its own tile's piece. The seam shape comes from stored sediment thickness and burial depth; outcrops are where the seam is easiest to dig.
- All deposits live at tile level, so world and landmass totals include deposits far from any town. When plots are generated, a deposit's footprint is placed on plots, the same way every time from the seed.
- Mining needs a plotted location (proposal, not contested). Unplotted deposits stay at their tile totals.

### 4.4 Depletion and rediscovery (user)

- A deposit empties as it is mined; the land around it loses value.
- Rediscovery happens two ways:
  - **Technology:** opens a deeper depth band of the hidden quantity.
  - **Luck:** a rare random prospecting event at an emptied site finds unknown quantity in a band already reachable.

### 4.5 Exposure and floors (user)

- **Two levers, both used:**
  - generation-time tuning of geological activity sets the starting stock;
  - play-time events (earthquakes and other geological events) expose new deposits.
- Events place only deposit types the local tectonic record allows.
- Events are random, or triggered when a resource falls below a floor.
- **Floors** per resource, configurable both per world and per landmass. Floors for single locations wait for DM world editing.

### 4.6 Campaign cycle (user)

An optional per-site cycle keeps a site from running dry for good: production falls, a find restores it, repeat. Implemented as topping up the site's hidden quantity (proposal, not contested). It is a deliberate departure from geology, switched on by the DM.

### 4.7 Forests (user)

- Each forest cell has **standing stock** and **annual growth**; growth comes from the biome.
- Harvest at or below growth keeps stock steady. Harvest above growth draws stock down (boom, then bust).
- Stock at zero: the cell becomes cleared land, farmable, or left to regrow.
- A cleared cell left alone becomes forest again (user), where the biome supports forest; in grassland or desert it returns to grassland or scrub (proposal, not contested).
- Clearing for farmland removes the stock at once as a single harvest.
- Ranges from the research report (section 7):

| Forest | Standing stock (m³ per hectare) | Growth (m³ per hectare per year) | Years to regrow |
|---|---|---|---|
| Boreal | 50–150 | 0.5–3 (north), 3–5 (south) | 60–120 to harvestable stock |
| Temperate | 150–350 | 3–8 | 80–150 |
| Tropical | 200–400 | 2.5–4 (mature), 6–12 (young regrowth) | median 66 to 90% of old-growth biomass (about 40% after 20) |

- **Wood types (user):** each forest holds a mix of wood types (for example oak, pine, cedar, ebony), set at world generation from biome and climate in the same way as native ranges (4.11). Cutting does not track the type yet: every harvest becomes one good, lumber. The types exist in the world so a later project can make them separate goods (oak for ships, pine for pitch) without regenerating forests.
- Each wood type's share of a forest's stock is stored per cell (and per plot where plots exist) (proposal, not contested).
- Not yet covered: firewood and charcoal as products (charcoal fuelled iron smelting before coal).

### 4.8 Soil fertility (user)

- **Base fertility** per cell from geology, floodplain and climate. Floodplains and volcanic soils rich; leached boreal and wet tropical soils poor. Loess is not modelled.
- **Working fertility** per farmed cell starts at base. Cropping lowers it; fallow and manure (from pasture animals) restore it toward base.
- Farming practice (technology) sets the drain and recovery rates.
- The town farmland cap (6.2) uses working fertility, so over-farming shrinks a town's own cap.

### 4.9 Resource list by source

Resources the world must provide (user), sorted by source (proposal, not contested):

| Source | Resources | Behaves like |
|---|---|---|
| Geological deposits | gold, silver, gems, tin, copper, iron, salt, marble, good stone, stone in general, amber, mineral oil | quantity, grade, depth; depletes (4.2–4.6) |
| Wild populations | ivory (elephants), fur (beaver, sable), exotic animals, coral | standing stock and growth, like forests (4.7); can be hunted or cut out (4.10) |
| Crops | sugar, spices, cacao, fruit, tobacco and other drugs, vegetable oil (olive), mulberry for silk | grown on farmland inside a climate range (4.11) |
| Livestock | horse and other breeds, wool, animal fat, wax (bees), silk (silkworms) | a herd that breeds and needs pasture or feed (4.11) |

- Gold and tin also occur as placer deposits in river gravels downstream of the source rock (a deposit type following hydrology). Amber is fossil resin in sedimentary layers (basin model). Gems need one deposit type per kind (diamonds on old cratons; rubies and sapphires in metamorphic belts) (proposal, not contested).
- Mineral oil and vegetable oil are separate goods (proposal, not contested).
- "Exotic" is distance from the native range, shown through price, not a property of the animal (proposal, not contested).
- Specific woods are wood types inside forests (4.7): recorded per forest, harvested as lumber for now (user).
- Fruit spoils through the existing `PerishabilityPhase`.
- Drugs and tobacco may be banned by a country and traded on a black market: a government system requirement (user).
- Stone and marble are costly to move through the dimensional-weight haulage cost (8.3) (user).

### 4.10 Wild populations (user)

- Wild populations recover by regrowth and by animals moving in from neighbouring cells that still have stock, at a rate per species (user). A toggle (user):
  - **on:** a region keeps at least one tile of remnant stock that regrows and spreads from there; "region" is each connected part of the species' range on a landmass (proposal, not contested);
  - **off:** a species can be wiped out completely without DM intervention.
- Recovery speeds differ by orders of magnitude (research report, section 9): beaver 11% a year long-run, 15–30% while colonising empty habitat; savanna elephant up to 7% a year; branching coral 0.5–20.6 cm a year, massive coral 0.7–2.5 cm a year, reef recovery 7–12+ years.

### 4.11 Native ranges and spread (user)

- Crops and animals have native ranges set at world generation from climate and biome.
- People carry seeds, cuttings and breeding animals by caravan to any climate that fits, so ranges grow over a campaign (sugar cane spreading, silkworms smuggled out of a monopoly).
- A town can start producing a new crop or breed only when all three hold (user): the stock has arrived (cargo), the town knows the method, and the climate fits. Each method is a tech tree node (for example sericulture, requiring mulberry growing) that spreads as news and that the DM can lock, unlock or grant (11). A government can ban export of stock (12).

## 5. Land, plots and rings

### 5.1 Plots (user)

- A level below tiles. Tiles are about 21.6 km² (about 5 km across). The plot size is set so a plot can be a building block, road, mine, farm or other town feature; 250 m is an example, not fixed.
- At 250 m: 0.0625 km² per plot, about 346 plots per tile.
- One land use per plot: buildings, farmland, pasture, forest, mine, wild (proposal, not contested; first proposed for kilometre cells, carried to plots). Tiles show the totals of their plots. Forest stock and growth (4.7) and fertility (4.8) are held per plot where plots exist, and as tile totals elsewhere.
- Uses change over time: farmland cleared from forest, abandoned farmland returning to forest.
- Open (project 2): whether a plot holds buildings or is one building. A medieval town house plot was about 10 m × 60 m; a farm holding about 12–48 hectares.

### 5.2 On-demand generation (user)

- Plots exist only at special locations, generated by a deliberate action: the DM marks a location, a city is created, or an event needs it (for example a mine opens).
- Clicking or viewing a tile does not generate plots.
- Plot content comes from the seed and the tile's data, so the same tile always produces the same plots. Plots are stored once generated.

### 5.3 Rings (user)

- The plotted area grows in rings of tiles, sized by the land the town works (built-up area plus its fields), and shrinks with the town.
- The UI will draw the rings so city sizes can be compared on the map.
- Land need, for scale: about 1–1.5 hectares of arable land (including fallow) per person; England had 0.99 ha in 1300 and 1.5–1.6 ha in 1380–1450, of which 0.6–0.95 ha was sown; pasture is extra (research report, section 8):

| Population | Farmland | Tiles |
|---|---|---|
| 500 | 5–7.5 km² | under 1 |
| 5,000 | 50–75 km² | 2–4 (1 ring) |
| 50,000 | 500–750 km² | 23–35 (about 3 rings) |

- Storage for 1,000 locations at 250 m plots (assuming 16 bytes per plot): 5.5 MB if each location is one tile, 39 MB at 1 ring, 105 MB at 2 rings, 205 MB at 3 rings. Mostly villages and small towns: about 10–40 MB.

### 5.4 Shared land (user)

- Now: a plot belongs to the town that claimed it first, and becomes free when that town is abandoned. A ghost town's fields return to forest.
- Later (government system): plots have owners and move between towns by sale, inheritance or conquest.
- Owners (user): named people (heroes, nobles, merchants), governments, and household groups (owning plot shares in proportion to their size). Institutions such as temples and guilds come later.
- The land a household works for its own output is land it owns; wages come from working someone else's plot or mine. Mineral rights follow the plot owner unless the government claims that resource (12.3a) (proposal, not contested).
- Inheritance rule (eldest inherits everything, or land split among heirs) is a government value the DM can adjust and lock (proposal, not contested). The first sends younger children to towns, soldiering or migration; the second shrinks farms each generation.

## 6. Households, work and happiness

### 6.1 Own output and wages (user)

- Households sit on a spectrum from living entirely on their own output (a farmer) to wages only (a townsperson); most mix both.
- A household meets its needs from its own output first and trades its surplus for coin.
- Wages are paid by producers to their workers (user chose wages over a scaled allowance).

### 6.2 The mix (user)

- **Ceiling:** usable land around the town (area × working fertility, excluding built-up plots) caps how much of the town can live on its own output.
- **Movement:** inside the ceiling, households shift time toward whichever pays more, their own output or wages.
- When wages fall (the mine closes), households fall back on their plots as far as land allows; the remainder become unhappy.

### 6.3 Jobs (user)

- Jobs belong to skill groups (for example mining, farming, crafts, trade, soldiering).
- Moving within a group is immediate. Moving to another group takes training, with lower output while training.
- Any household can farm at low skill.

### 6.4 Happiness (user)

- End goal: full happiness and prosperity mechanics (to be designed).
- Basic rule: needs met means happy; not met means unhappy; unhappy for longer than X means the household leaves by caravan.
- No separate "reason to exist" score: a town exists while people live there. Happiness statistics are shown to the DM as information.
- A town with no people is marked abandoned (ghost town).
- Literacy adds to happiness because a reader has more options (user). That needs happiness with degrees, so the full design treats happiness as a score fed by needs and options (literacy, access to several skill groups, nearby markets); until then literacy has no effect on happiness (proposal, not contested).

## 7. Money (user)

- Money enters when a ruler mints coin from gold and silver mined in the realm. It leaves when coin is lost, hoarded or melted down.
- Ruler spending (soldiers, building) and taxes move money through the economy; these come with the government system.
- So gold and silver deposits set the money supply, and an emptied silver mine is a realm-level problem.
- **Coin is cargo (user).** Coin is counted like other goods, with mass and volume under the dimensional-weight rules; a large sum needs special haulage (a million gold coins at about 3.5 g each is about 3.5 tonnes). It is the one good every merchant accepts in trade.
- **Earnings travel (user).** A merchant's earnings are not instant: coin is carried from the point of sale to where it is stored. Taxes are carried coin: the collector travels a circuit and brings coin back as cargo, subject to road danger (12.3).
- **One world currency (user):** gold, silver and copper pieces at fixed ratios. Each denomination is its own good, so a sum's mass depends on how it is paid. Per-government coins and debasement were considered and set aside.
- **Storage (proposal, not contested):** coin sits in stockpiles at physical places (a shop's strongbox, a merchant's house, a treasury) and can be stolen in events there. Banks and vaults come later.
- **Later (proposal, not contested):** bills of exchange, sealed written promises of payment (9.6), so coin does not have to travel.

## 8. Caravans and migration

### 8.1 Caravans (user)

- A caravan is a group of travellers going from A to B at a set departure time: merchants, soldiers, heroes, royalty, migrants, or any mix.
- It carries members (each a kind with a count) and cargo of several goods.

### 8.2 Caravan board (user)

- Each town has a board. A traveller posts a route (A → B) and a departure time; others join; the group leaves together.
- A traveller going A → B → C can join an A → B caravan for safety, then use B's board.
- A posted departure time never changes. A merchant in a hurry posts an earlier departure or travels alone.
- If the poster drops out, the remaining members keep the caravan at the same time and route; a posting with no members is removed (proposal, not contested).

### 8.3 Speed (user)

- A caravan has a minimum speed. Each member brings their own vehicle, which must be at least that fast (a caravan that won't go slower than a hand cart turns away a wagon).
- Capacity and haulage cost use the existing dimensional-weight system (user): `VehicleClass.MaxMassGrams`, `MaxVolumeCm3` and `VolumetricDivisor`; `CargoFit.MaxUnits` takes the smaller of the weight and volume limits; `Haulage.Cost` charges by dimensional weight × distance. Heavy goods (stone, marble) are expensive to move through that cost. Each member's cargo fits their own vehicle (proposal, not contested).

### 8.4 Migration (user)

- Unhappy households pick a destination with a weighted score that can take more measures over time: start with nearest town, then add news-based prospects, wages, links between towns.
- Once the destination is set, the household looks for a caravan heading toward it, or leaves on its own if none is posted before it gives up waiting (proposal, not contested).
- Arrivals join the destination's population.

### 8.5 Naval trade (user)

- Builds on the existing ports and voyages (`docs/superpowers/specs/2026-11-23-ports-and-travel-knowledge-design.md`): river and coastal ports; a voyage per pair of water-connected ports, shortest water path, fixed per-direction speed factor; settlement travel-time tables; merchant `RiskAversion` gambles.
- A voyage works like a caravan: someone needs to go from A to B and owns or commissions a boat. A voyage can be one ship, many ships, or several fleets.
- Ships are military, merchant or civilian. Military ships lower sea dangers (pirates, monsters). Most merchant ships are armed by technology level: swords, bows, cannon, or armour (ironclad).
- Ships have cargo capacity, food stores, required crew and maintenance costs.
- Ship classes set where a ship can travel and its power: oars, wind, its own power, or magic. Land vehicles use the same classes.
- The most common voyage is a merchant going port to port, dropping off and picking up goods. Merchants try never to leave empty, on land or sea, because travelling empty is lost cost.
- Some regions are closed for part of the year (polar ice, passable only by icebreakers).
- **Speculative voyages:** as on land, a merchant may go anyway on a risk factor, because news from distant places rarely arrives, and a route nobody travels may have exotic goods not available locally. Extends the existing `RiskAversion` gamble.
- **Getting space at sea:** ship owners (merchants, shipping houses, factions, governments) sail their own ships, hire out whole ships (charter), and sell cargo space (freight priced by dimensional weight) and passage for travellers.
- **Shipyards:** ships are produced at a shipyard. Some are seeded at world creation; after that, new ships are ordered and built.
  - Inputs per ship class (proposal, not contested): lumber, pitch, iron, sailcloth, shipwright labour and build time. Ships use lumber, one good for now (user); forests already record wood types (4.7), so a later project can require a type (oak hulls, pine masts).
  - Build time per ship class and shipyard size, shown on the calendar from order to delivery; each ship class needs a technology the shipyard's town knows (proposal, not contested).
- **All vehicles are built (user):** land vehicles too (wagons, carts, carriages) at workshops such as cartwrights, from inputs (lumber, iron, labour); some are seeded at world creation.
- **Pirates (user chose outlaws and privateers):**
  - Outlaw pirates are bandits at sea (proposal, not contested): pirate havens are bandit camps on coasts and islands with hidden anchorages to discover; raids, per-leg loss records, fences and moving havens work as on land (10); military escorts and armed merchant ships lower raid chance and losses.
  - Privateers: ship owners licensed by a government (letter of marque) to raid an enemy's shipping and keep a share. A privateer whose licence ends, or whose government makes peace, can turn outlaw. Privateers come with the war project, because they need relations between governments; outlaw pirates come first (proposal, not contested).
- Proposals, not contested:
  - One travel-group model for caravans and convoys: a port has a board where voyages post a route, departure and minimum speed; ships join; military ships can escort.
  - Each ship class has daily food use per crew member; a voyage must reach a port before stores run out.
  - Crew are households in the sailing skill group, paid in coin carried aboard; unpaid crews desert.
  - Hull quality decays like road quality (higher falls faster); shipyards repair hulls.
  - Each ship class has an ice rating; sea ice comes from the weather project.
  - The best sea route depends on season and wind; voyages minimising distance (today) become routes minimising time for the season.

## 9. News and knowledge

### 9.1 Records (user)

- Each town holds records about other places. Each record is one fact (a price, an event, a population figure), stamped with the tick it was observed.
- When a traveller arrives with a newer record than the town holds, the town takes the traveller's version.
- Records are current or out of date; none are false. Rumour and distortion are a later addition (proposal, not contested).

### 9.2 What a traveller carries (user)

- On leaving a town: 3–5 headline facts, 1–5 random records, and at least 1 record relevant to them (a coal merchant knows the coal price).
- A merchant carries the departure town's possibly stale copy of the destination's price, and trades on that (proposal, not contested).
- Travellers forget records over time, down to a minimum of 1; longer journeys lose more. The record kept last is the relevant one (proposal, not contested).
- Caravans carry more records and forget less, because they have more people.

### 9.3 Headlines (user)

- A town's headlines are events first, then the largest changes, ranked by size. The DM can pin a headline (for example a dragon sighting) and it spreads with travellers.

### 9.4 Caravan reports (user)

- On arrival, a caravan reports its journey as a record (for example "3 encounters, 50 gp lost on the last leg"). Travellers who hear it are less likely to take that road.

### 9.5 Guarded knowledge (user)

- A knowledge record (a technology method, a supplier location, a trade route) has a list of holders; each holder marks it shared or guarded.
- Value of guarding: the extra profit of being the only one who knows (a price gap, production others can't match) (proposal, not contested).
- Cost of guarding: using the knowledge at scale means telling workers or partners; each extra holder raises the yearly chance the record leaks (a worker moves away, an apprentice gossips, a rival bribes) (proposal, not contested).
- Holders sell knowledge when an offer beats the value of guarding it; guarded records also leak by that chance (user chose both).
- Guarded records are never forgotten while travelling (user). They sit outside the 3–5 headline, 1–5 random and 1 relevant travel limits (proposal, not contested). A guarded record held by nobody else is lost when its last holder dies without telling anyone, as with technologies (11.2) (proposal, not contested).
- A holder is a named entity (merchant, specialist, noble) or a share of a household group (proposal, not contested).
- Later: theft and espionage as events; government secrecy oaths lower the leak chance.

### 9.6 Written records (user)

- Information can be written down as a physical good: given, sold, carried, and lost in events (raids, war, fire). It is not lost through time.
- A written record is guarded (sealed) or unguarded.
- A guarded written record can be opened only by the person it is being delivered to, or by whoever buys it (user). A seal shows whether the record was opened. Opening one without breaking the seal is an event of the later espionage system (user).
- An unguarded written record a traveller carries is extra information, outside the travel memory limits, and is never forgotten while they hold the record (user). Once it is handed over, sold or lost, they no longer hold it; anything they read from it before then becomes an ordinary memory record that can be forgotten (proposal, not contested).
- Literacy (user, deferred): each household group has a literate share; specialists, merchants and nobles are more often literate. Only a literate holder reads an unguarded record; to an illiterate carrier it is cargo. Literacy also adds to happiness (6.4).
- The government post system carries written records (12).
- The facts in a written record still carry their observation tick, so a record never forgotten still goes out of date (proposal, not contested).
- Towns can keep written records in an archive, a store that war or fire can destroy (proposal, not contested).

### 9.7 Later

- Government post system: a town pays taxes to receive news regularly.

## 10. Road danger (user)

- Each road segment has a risk value (region, patrols, recent raids). Risk sets how often encounters fire; the encounter (raid, washed-out bridge) decides what is lost: cargo, people or time.
- Group size and guards lower both how often encounters happen and how much they cost.
- **Loss records (user):** when an encounter loses goods (a wagon destroyed, coin lost, cargo taken), the leg of travel records what was lost, so the DM can check whether players find anything. Each record holds the goods, quantity, road segment, tile and tick (proposal, not contested).
- **Destroyed, dropped or stolen (proposal, not contested):** destroyed goods are gone; dropped goods lie at the recorded tile and spoil by the existing perishability rules; stolen goods go with the bandits.
- **Bandit camps (user):** stolen goods are taken to a bandit camp, a settlement like a town but much smaller.
  - Dropped goods can also be found by passing caravans, a small chance per pass, so loot near busy roads disappears sooner (user).
  - Camps sell loot through black-market fences in nearby towns (user). Their own coin and goods sit in the camp and can be recovered when it is raided (proposal, not contested).
  - Camps emerge where bandits gather (unhappy households and unpaid soldiers who turn bandit, on low perceived-control tiles near roads), and the DM can also place, move, lock or remove them (user).
  - A camp has people like a small town and sends out its own groups that "patrol" (raid) the roads (user). These raiding parties are a source of bandit encounters, adding to the base road danger (proposal, not contested).
  - Camps have routes to them, hidden: not real roads, and they must be discovered (user).
  - A hidden route is a guarded record (9.5) held by the bandits and their fences: it leaks the way guarded records leak and can be sold (a robbed merchant who escaped, a deserting bandit). Patrols also search nearby roads, with the marshal's abilities changing the chance, and the DM can always reveal it. Once found, it appears on the discovering government's map (user chose all three).
  - Camps are temporary (user): when bandits judge a route depleted (risk too high, so no traffic), they move to other routes. They judge by their raid takings per period (proposal, not contested). The move is a bandit caravan; the new camp's route starts undiscovered, and the old site is left as an abandoned location the DM can use (proposal, not contested).
  - Camp members are households with needs; a camp that cannot feed itself loses members, so a road that travellers avoid (9.4) starves the camp that made it dangerous (proposal, not contested).
- Travellers act on the risk they have heard (9.4), not the real risk. Consequences allowed to happen (proposal, not contested): a road whose bandits left stays avoided until someone reports a safe trip; a newly dangerous road looks safe until the first victims arrive; a feared road brings less news, so towns along it drift out of contact.

## 11. Technology

### 11.1 Tech tree and DM control (user)

- Individual technologies in a tree with prerequisites; each technology unlocks specific things (depth bands, farming practices, production).
- The DM can:
  - **lock** a technology: nobody can discover it;
  - **unlock** it: someone can discover it by research (natural route);
  - **grant** it: inspire someone in a chosen city, who discovers it at once (god-given route).

### 11.2 Spread (user)

- Known technologies travel as news records.
- A town that hears of a technology adopts it only if it has the prerequisites.
- A lock stops new research; it does not remove knowledge.
- A technology no one holds (no town, no traveller's record) is lost.

### 11.3 Research (user)

- Each technology defines its own need metric (for example drainage pumps: mines at their deepest workable band; crop rotation: working fertility below base).
- **Specialist research:** technologies that need education (mathematics) are researched only by specialists. Specialists are a job and do not farm. They generate research points; need decides which technology gets them.
- **Practical discovery:** technologies that need no specialists (better irrigation) have a yearly discovery chance that rises with need, and with the number of people doing the related work (proposal, not contested).
- Specialists are paid from the town treasury (user), so specialist research depends on the government system.

## 12. Government

### 12.1 Structure (user)

- Governments nest at any depth (country > duchy > town). Each has a leader and a small tree of roles, collects its own taxes, and passes a share upward. Follows the existing `Country` → `PoliticalRegion` → `Settlement` chain.
- A town is a small government with few role slots; it gains slots as it grows into a city.
- The leader is a hero (a named character) with goals (to be defined). Example goal: make a road safer, so more patrols go to it.
- Role holders give benefits, downsides, or both to the government.
- The DM can place specific people into a government and lock them so they cannot change roles (story events).
- Government values (tax rate, bans) can be adjusted and locked by the DM.
- Towns join or leave governments. For now this is DM-controlled; later it leads to civil unrest.

### 12.2 Territory and control (user)

- **Claimed territory:** what a government thinks it owns but does not guard. Two governments can claim the same land and be at odds over it (existing `TerritorialClaim`).
- **Official control:** what the government says it holds and its maps show, per political region. Only one government can officially control a place.
- **Perceived control:** what people believe, per tile, updated on a set cadence from the patrols seen there in the period, and fading between periods. A town that saw 1 patrol in a year has reason to doubt its government (input to leaving, later).
- Controlled territory is usually near the government's towns and cities, strongest near the capital and in areas it considers dangerous. The government spends taxes to hold it.
- Patrol movements are the one input to three numbers: real road risk (patrols there now), perceived control (patrols seen per period), and travellers' known risk (caravan reports, 9.4). They can disagree. A tile can be perceived as patrolled by two governments at once (border war) (user confirmed).
- Official control and perceived control are separate map layers, so the DM sees where a government overstates its reach (user confirmed).
- Control changes hands by: army conquest; the holder withdrawing or failing to pay for patrols; the town switching sides; one country buying it from another (user). Land nobody controls is lawless, with higher road risk (proposal, not contested).

### 12.3 Soldiers and taxes (user)

- Patrols are soldiers paid from taxes; patrols lower road risk. Armies capture territory; war is its own later project (section 15), started once the rest of the system is stable. Its first step: armies are soldier caravans under the existing road and speed rules, and the DM decides battles; automatic resolution from heroes' abilities and army sizes comes later (user).
- A tax collector has to go and collect taxes; the coin is cargo carried back over the roads (7). Collection rounds go on the calendar (12.6) (proposal, not contested).
- A town can pay more tax for more patrols coming to it.
- Unpaid soldiers are households with needs like any other: unpaid, they become unhappy and leave, or turn to banditry, which raises road risk where they go (user). This adds to the base bandit and danger value of a road; it does not replace it (user).

### 12.3a Mineral rights (user)

- Each government sets, per resource, whether a deposit belongs to the government or to the owner of the plot above. Defaults: precious metals to the government; coal, stone and wood to the landowner. A government value the DM can adjust and lock.
- A government that claims a resource mints or sells it directly, but private prospectors have less reason to search there; claiming a new resource can cause unrest among owners (proposal, not contested).

### 12.3b Bans and black markets (user)

- A banned good still trades, through fences, at a higher price. Anyone caught carrying it loses it and pays a fine. The chance of being caught depends on the chancellor's abilities and on patrols (inspections on roads, checks at town gates).
- Officials can be bribed, depending on their honesty trait; corruption lets black markets grow.
- Fines go to the treasury as coin; seized goods go to government stores, which a dishonest official may resell (proposal, not contested).
- A ban covers three kinds of thing, set together by category (user approved). Example: a country that bans magic.
  - **Goods** (potions, magical minerals, reagents): the rules above.
  - **Practices and technologies** (casting, researching or using magic technologies): cannot be researched or adopted inside the government's territory; people who already know them keep the knowledge (a DM lock limited to one government's territory). Practitioners work in secret, with their knowledge as guarded records (9.5); unhappy practitioners may migrate abroad.
  - **Roles** (the Archmage): the role type is disabled in that government.
  - Enforcement can use roles such as an Inquisitor or Spellwarden (Appendix A).

### 12.3c Taxes up the chain (user)

- Each overlord sets the terms for each subject government: a share, a fixed amount, or a mix. Terms are a government value the DM can adjust and lock. The default is a share (proposal, not contested).
- A fixed amount covers story setups such as a conquered town paying tribute; terms against patrols seen give a measurable grievance for later civil unrest; coin passed up travels as cargo (proposal, not contested).

### 12.4 Post (user)

- A government provides post to its towns. A town collects a post tax, paid to the government, for a delivery frequency (example: small town 1 a year, large town 4 a year, huge city 10 a year).
- A postman is usually a single traveller sent by the government. He carries more information than an ordinary traveller, still limited, mostly as written messages (9.6).
- Post matters most to remote towns; a busy city already gets news from its traffic.

### 12.5 Succession and elections (user)

- Each government type has a succession rule; claims can be contested; the DM can always override.
  - **Monarchy:** the heir is already named and can be contested.
  - **Republic:** an election in steps; each step can cause unrest or events for the DM. Who votes (role holders, property holders, all adult residents) is set per republic as a government value the DM can adjust and lock.
- Elections run on a fixed term per republic, plus an early election when the leader dies (proposal, not contested).

### 12.6 Calendar (user)

- An app view of upcoming events, built on the existing `CalendarSystem` and world `CalendarDefinition`.
- Entries (proposal, not contested): scheduled (elections and their steps, post deliveries, tax rounds, caravan departures, harvests); expected (an heir coming of age, a mine near its last depth band, a treasury running out); DM-made story events, which can trigger rules.

### 12.7 Roles and heroes (user)

- A role's effect comes from both the role and its holder: the role sets what can be affected; the holder's stats modify how much and how well.
- Each role lists the stats it uses and what each affects (proposal, accepted). Example, treasurer:
  - INT (Intelligence): the highest tax rate they can manage, and whether they find the lowest rate that still raises enough (user);
  - WIS (Wisdom): how early they notice unhappiness caused by taxes (proposal, accepted).
- Role slots by government size (user):

| Size | Roles |
|---|---|
| Small town | mayor |
| Medium town | mayor, treasurer, sheriff |
| Large town | mayor, treasurer, sheriff |
| Small city | mayor, treasurer, sheriff, record keeper |
| Medium city | mayor, treasurer, sheriff, record keeper |
| Large city | mayor, treasurer, sheriff, record keeper, guildmaster, gate |
| Federal | king or president, council or advisor, treasurer, marshal, admiral, judge, spymaster, guildmaster, record keeper |

- Each role has a **type** set by the system and a **title** that is generated and configurable, so all governments share role types but name them differently (user). Example titles (user):

| Title | Type |
|---|---|
| The Sovereign | ruler (king or queen) |
| The Hand of the Crown | head of government |
| The Keeper of the Treasury | finance |
| The High Marshal | military |
| The Lord Justiciar | law and justice |
| The Master of Envoys | foreign relations |
| The Master of Whispers | intelligence |
| The Lord Warden | internal security and territorial administration |
| The Master of Works | infrastructure and construction |
| The High Guildmaster | trade and industry |
| The High Priest / Divine Voice | religion |
| The Archmage | magic |

- Earlier example effects (proposal, not contested): treasurer (taxes; bad holder embezzles), marshal (patrols, armies), postmaster (post reliability; bad holder loses or opens letters), mint master (minting; bad holder skims metal), judge or chancellor (laws, bans; bad holder lets black markets grow), spymaster (espionage, later), gate (checks at town gates for banned goods).
- A hero (named person) has (user):
  - **ability scores:** Strength, Dexterity, Constitution, Intelligence, Wisdom, Charisma, matching a Dungeons & Dragons (D&D) character sheet so the DM can copy stats across (proposal, not contested);
  - **skills:** trained skills from the skill groups (6.3);
  - **personality traits:** for example honesty, loyalty, ambition. They decide what a holder does with a role (embezzle, stay loyal, reach for power) and drive leader goals (deferred) and contested successions.
- Minor people get generated stats; story characters get DM-set stats (proposal, not contested).
- A DM-locked role keeps its holder, so the DM also picks whether that role helps or harms the government.

### 12.8 Institutions and factions (user)

- **Institutions** sit under a government: for example a city's trade guilds or a local church body. Built from the same parts as a government (leader, roles, treasury), with authority over their own area, and able to appoint or elect seats in the civil government (proposal accepted).
- **Factions** stand outside any government and can cross borders. A person can be a member of a faction and hold a role in a government at the same time (user). Examples from the user's campaign:
  - **The White Tower:** a group of mages that works with all the major governments except a small handful.
  - **The Inquisition:** the enforcement arm of a religious organization, effectively controlled by no one, under no government, with influence in all lands.
- **Several factions (user):** a person can belong to more than one faction, with a loyalty value toward each body they belong to (proposal accepted). A faction can require exclusive membership; people can still break that rule, with consequences if it is found out (user).
  - A hidden membership is a guarded record (9.5) held by the person and whoever they told: it leaks like any guarded record and can be sold or uncovered by espionage (proposal, not contested).
  - Each faction sets its consequences for a broken exclusivity rule (expulsion, punishment, being hunted), as a value the DM can adjust and lock (proposal, not contested).
- **Influence (user):** a faction's influence in a place comes from its members holding that government's roles (weighted by role), its members and halls present, coin it spends there (donations, bribes, loans), and its standing with that government. The DM can adjust or lock it. Hidden members count toward real influence but are not visible to the government (proposal, not contested). Influence weighs on government decisions (bans, taxes, who fills a role).
- **Standing (proposal, not contested):** each faction or institution has a standing with each government (allied, tolerated, banned), DM-adjustable and lockable. A banned body operates in secret.
- **Autonomy (proposal, not contested):** every nesting link, for governments, institutions and factions, has an autonomy value from "the parent directs everything" to "the parent is nominal" (the Inquisition under its religious organization; a vassal town that pays and otherwise ignores its duke).
- **Presence (proposal, not contested):** a body's presence in a town is its hall (a building on plots) plus its members there.
- **Split loyalty:** when the interests of two bodies a person belongs to clash, the higher loyalty wins; honesty decides whether they hide it (proposal, not contested).
- **Trade guilds** work inside governments and cities (institutions); some work between governments (user). A cross-border guild can be its city institutions linked by a faction, as the Hanseatic League linked merchant towns (proposal, not contested).
- Institutions are the temples and guilds that own land later (5.4); overlapping courts (Appendix A) follow from bodies holding authority in the same place as a government (proposal, not contested).

### 12.9 What other systems expect





| Expected by | What |
|---|---|
| Money (7) | rulers mint coin from mined gold and silver; ruler spending on soldiers and building; taxes |
| Technology (11.3) | town treasury pays specialists |
| News (9.6, 9.7) | post system: a town pays to receive news regularly; it carries written records |
| News (9.5) | secrecy oaths that lower the leak chance of guarded knowledge |
| Land (5.4) | plot ownership; transfer by sale, inheritance, conquest |
| Road danger (10) | patrols lower road risk |
| Resources (4.9) | banning goods (drugs, tobacco); black markets |

## 13. Magic (deferred)

End goal (user): magical minerals with geological formation rules (deposit types, so grade, depth, depletion, exposure and floors apply), plus non-geological sources placed by the DM (ley lines, sacred groves). Deferred until the systems underneath work, because it shifts the foundation (user).

- **Teleportation (user):** magic includes teleportation.
- **Light bridges (user):** a road-like link that moves travellers between two cities at once, but is not always available. In the user's world only 2 cities have one (two capitals), linked only to each other. A bridge works one way at a time, is active for a short period, and each activation consumes Arcanite, a rare material not yet defined (a magical mineral, so a deposit type under the rules above).
- **Network (user):** other cities join the light bridge network later; two capitals is the starting state, not a limit.
- **Schedule (user):** a bridge usually opens on a schedule set by the resources available (Arcanite on hand). Openings are frequent: in larger cities several times a day (morning and evening travellers), and at least once a week, unless resources run out.
- **Use (user):** travel is instant and very expensive for merchants and travellers. Each opening has size limits: total goods, and the largest cart that fits through.
- **Arcanite (user):** very rare and very dangerous, like a magical cross of uranium and unstable nitroglycerin.
- Proposals, not contested:
  - Openings are calendar entries (12.6) made from Arcanite stock; an opening outside the schedule (an emergency, a DM event) is the exception.
  - The size limits use the dimensional-weight system (8.3): an opening has a total mass and volume, and the largest cart is a `VehicleClass` limit.
  - The fare is coin paid to the bridge's owner, collected and carried like tolls (17.6).
  - A light bridge is a route with its own surface type, so route choice weighs fare and wait for the next opening against road time.
- Consequence for running the simulation in parallel (proposal, not contested): a link with no travel time removes the time margin between the two places it joins, so any grouping of the world must account for light bridges and teleportation (see the distribution discussion). Scheduled openings give that margin back: groups run freely up to the next opening and exchange travellers there. With openings several times a day, bridged cities exchange travellers at least every half day; today's phases run daily or weekly (`CadenceTicks`), so that margin is shorter than one daily phase step.

## 14. What the code has today

| Area | Today | End goal needs |
|---|---|---|
| `ResourceEndowment` | fixed `Abundance` per production cycle, per settlement, never depletes | deposits at map locations with quantity, grade, depth; depletion |
| `ProductionPhase` | extraction cost = good base value | wages, work per load from depth |
| `Money` and holdings | integer copper units; denominations are display only; held as numbers in `RepresentativeConsumer.Budget`, `Shop.Till` and merchant capital | coin as goods per denomination, stored at physical places |
| `ConsumerIncomePhase` | `AllowanceIncome`: flat grant per tick, a money faucet | wages from producers; own output |
| `Settlement.Population` | set at creation, never changes | migration |
| `Settlement.State` | `Active`, `Ruined`, `Abandoned` (set by hand) | `Abandoned` set when population reaches zero |
| Political geography | `Country` → `PoliticalRegion` → `Settlement`; `TerritorialClaim` (`Controls` or `Disputes` a settlement or region), described in code as the precursor to a faction system | input to the government system (12) |
| Money ledger | every money flow tagged faucet or sink by `MoneyChannel` (`ConsumerAllowance`, `MerchantPurchase`, `MerchantSale`, `MerchantHaulage`) | new channels: minting, wages, taxes, ruler spending |
| `Caravan` | one merchant owner, one good | members with kinds and counts, several goods, minimum speed |
| `PinStage` | town suitability from temperature and river | suitability from deposits and fertility |
| Tectonic record | crust age, orogeny kind and age, sediment, coal and evaporite exposure, collision granite flags | input to deposit types |
| Grid | tiles about 21.6 km² | plot level below tiles |
| `VehicleClass`, `CargoFit`, `Haulage` | dimensional-weight capacity and haulage cost per vehicle class | kept; caravan capacity is the sum of members' vehicles |
| `PerishabilityPhase` | goods spoil | kept; covers fruit |
| Price history | none: `Stockpile.MarketPrice` is overwritten daily; a retail sale leaves only a log line | daily price bars per city and good (17b) |
| `Merchant` | named merchant entity owning caravans | a holder of guarded knowledge; one of several caravan member kinds |

## 15. Build order

| # | Project | Contains | Needs |
|---|---|---|---|
| S | In-memory simulation state (being built first) | world held in memory while open; no SQL during a step; saves every 4 simulated months, at run end and on DM changes; GUI on one open-world session. Design `2026-11-25-in-memory-simulation-state-design.md`, plan `2026-11-25-in-memory-simulation-state.md` (Tasks 1–13) | built engine |
| P | Price chart (built inside the S plan) | daily price bars per city and good; candles by day, week, month, year; box selection on the map; chart window; `prices` CLI command. Design `2026-11-25-price-chart-design.md`, plan Tasks P1–P5 in the S plan | S |
| 1a | Geology, forests and fertility | tectonic record fixes (event history, burial, coal conditions, craton ages); deposit types (including placers, amber, gems); point deposits and basins; grade, depth bands, hidden quantity; forest stock and growth; base fertility; generation-time floors; town suitability from resources | tectonics (built) |
| W | Seasons and weather (moved up by the user) | axial tilt; monthly temperature, rainfall and winds; weather systems; tornado events; DM weather tools; weather history; climate change during play. Effects on other systems are connected as each of those systems is built | 1a |
| 1b | Species and native ranges | wild populations; native ranges of crops and livestock with climate rules (coldest-month temperature for frost) | 1a, W |
| 2 | Plots and land use | on-demand plots; land use per plot; rings; first-claim land | 1 |
| 3 | Households and work | own output and wages; skill groups and training; needs and basic happiness; extraction with depletion, rediscovery, working fertility, forest and wild harvest, wild recovery and remnant toggle; crop and livestock production; play-time exposure events and floors | 1, 2 |
| 4 | Caravans and migration | groups, board, minimum speed; weighted destination (nearest town first); abandonment | 3 |
| 5 | News and road danger | records, headlines, memory; guarded knowledge (holders, leaks, sales); written records and archives; encounters; caravan reports | 4 |
| 6 | Technology | tree, need metrics, practical discovery, DM lock, unlock, grant; spread as news; crop and breed methods; spread of stock to new climates | 5 |
| Later | Heroes | named people: ability scores, skills, personality traits; generated or DM-set | 3 |
| Later | Government | nested governments and role trees; succession and elections; official and perceived control; treasury, taxes as carried coin, minting, specialists, post system, ownership, patrols, banned goods and black markets, secrecy oaths; calendar | 3–6, Heroes |
| Later | War | armies as soldier caravans; privateers and letters of marque; DM-decided battles first, automatic resolution later; conquest of territory | Government; started once the rest is stable |
| Later | Magic | magical minerals, DM-placed sites | 1, 2 |
| Later | Literacy | literate share per household group; reading written records; happiness from reading | 3, 5 |
| Later | Espionage | opening seals unseen; stealing guarded knowledge | 5 |

Effects of the order (noted, not changes):
- Until government exists, specialist research does not run; technology advances by practical discovery and DM grants only.
- Until a prospects measure is added, migrants go to the nearest town, which may also be failing.
- Until government exists, money enters only by the existing allowance or by minting rules project 3 sets out.

## 16. Open items

- Full deposit type list and formation rules: researched (research report, sections 5.1–5.10). Filled in a second pass: gold, silver and diamond grades; placer grade; placer gold changes shape fastest in the first 3–5 km and proximal placers lie 1–10 km downstream; deposit density per area for porphyry copper, massive sulphide, orogenic gold, gold placers and kimberlites (section 5.12, converted to deposits per cell and per tile). Still unsourced: median epithermal grade and tonnage; an upper limit for placer travel; densities for lead–zinc in carbonate, tin, sediment-hosted copper, epithermal, iron, gem and basin deposits; coal parting share; coal basin areas.
- Crust record gaps found by the research (report sections 4 and 5.11): `MaxBurialDam` always equals `SedimentDam` because sediment is never removed; `CoalExposureMy` has no subsidence or burial condition; craton ages (750–2,250 My) do not reach banded iron (2.5–2.8 billion years) or diamond (over 2.5 billion years) thresholds; the last-orogeny-only record loses event order (needed for lead–zinc in carbonate, marble, marble-hosted ruby, emerald); no erosion depth, hotspot field or ophiolite flag. Largest fix option: a 4-event log per cell (655 KB at 40,962 cells).
- Climate gap: crop frost rules need a coldest-month temperature; tiles hold only an annual mean (solved by the seasons in 17.3).
- Plot size and whether a plot is a building or holds buildings (project 2).
- Value of X (unhappy duration before leaving) and the list of needs (project 3).
- How long a migrant waits for a caravan (project 4).
- Event tables and how encounters resolve (project 5).
- The technology list and what each unlocks (project 6).
- Wood types as separate goods (the types already exist in forests, 4.7); firewood and charcoal (project 1 or 3).
- Where money enters before the government system exists (project 3).
- Recovery rates and movement rates per wild species (project 1 research).
- Which crops, breeds and wild species the world has, and their climate ranges (project 1).
- How a holder values guarding a record, and leak chance per holder (project 5).
- Copying written records (scribe labour; a printing technology lowering the cost per copy) (proposal, not contested; project 5 or 6).
- The full happiness score (later happiness design).
- Leader and faction goals, and how personality traits drive them (government, deferred by the user).
- Role list per government size, and how many slots a town gains as it grows (government).
- Bandit camp size limits and how raiding parties choose targets (project 5 or government).
- Civil unrest: towns deciding to leave, using perceived control and tax terms (later).

## 17. Weather

### 17.1 Intent (user)

- Weather is simulated during play: tropical storms, hurricanes, tornadoes, droughts and ordinary rain storms.
- The DM can see where cloud cover is likely and where the sky is clear.
- The DM can see whether it is safe for boats to travel to a place; this feeds the later naval trade system. Sailing ships depend on seasonal trade winds, rivers or rowing, so they cannot go directly from port to port.

### 17.2 What the generator has today

- `WindStage`: one fixed direction and strength per cell from its latitude band (trade winds 0–30°, westerlies 30–60°, polar easterlies 60–90°); no seasons.
- `ClimateStage`: yearly mean temperature and rainfall; no axial tilt, so no seasons.

### 17.3 Approach: hybrid (user)

- Seasons first: axial tilt gives monthly climate; the tropical rain belt follows the sun (on Earth it moves about 10–15° of latitude), so winds shift by season and monsoons appear.
- Weather systems (fronts, storms, hurricanes) are moving objects: they start where conditions allow (hurricanes over ocean warmer than about 26 °C), follow the circulation, carry moisture, and leave cloud and rain along their tracks. Storm rates and tracks are settings, so the system stays tunable.
- Tornadoes are rare events inside strong storms (under 1 km wide, smaller than a tile). Hurricanes (about 300–1,000 km) span 3–9 globe cells; thunderstorm cells (about 10–20 km) span a few tiles.
- Droughts happen when storms keep missing an area.
- Sailing needs wind direction, wind speed and storm risk per sea cell per period; a square-rigged ship cannot sail closer than about 60–70° to the wind (proposal, not contested).
- Climate patterns (when the monsoon turns) are common knowledge; actual storms reach people as news (9) (proposal, not contested).
- Weather is deterministic from the seed, tick and DM edits (proposal, not contested).

### 17.4 Weather history (user)

- Stored: monthly summaries per globe cell (about 3.9 MB per simulated year), storm tracks (under 1 MB per year), and daily weather for plotted locations and sea-route cells.
- A yearly weather checkpoint lets exact weather for any past day be recomputed from the nearest checkpoint, the seed and DM edits (cost not measured; up to one simulated year of weather per lookup).
- Storing daily weather for every cell would be about 120 MB per simulated year (about 6 GB over 50 years); not chosen.

### 17.5 Effects on other systems (user approved)

| System | Weather effect |
|---|---|
| Harvests | growing-season rain and temperature against each crop's needs set yield; drought or late frost gives a bad harvest and unmet needs |
| Soil fertility | river floods leave silt (raising working fertility on floodplains); heavy rain on bare farmland erodes it |
| Forests | drought slows growth; dry, hot spells give a wildfire chance that removes standing stock |
| Rivers | floods damage floodplain plots; low water stops river boats |
| Roads and caravans | rain turns unpaved roads to mud (slower); snow closes mountain passes for the season; storms add encounter risk |
| Sea travel | wind direction and speed set sailing time; storms set ship-loss chance, recorded as a lost leg (10) |
| Calendar | seasons, expected monsoon change and hurricane season as expected entries; an incoming storm once known |
| News | storm damage and bad harvests become headlines (events rank first) |

### 17.6 Road damage, types and upkeep (user)

- A storm over a road can damage it without closing it, slowing travel unexpectedly. Travellers learn of damage only through news (caravan reports), so caravans plan on the old condition until a report arrives (proposal, not contested).
- Builds on the existing `RouteSurface` (`Track`, `DirtRoad`, `Paved`), `Route.Danger` and `Route.TrafficCount`.
- **Unpaved roads (track, dirt):** slowest; not maintained. Damage makes travellers divert and wear new paths, modelled as slower travel time, not as repair.
- **Paved roads:** maintained by a controlling body (a local or federal government, or a faction; factions have wealth); fastest; have a quality level that sets speed.
  - Quality depends on how often the road is maintained, which depends on rates the controlling body sets.
  - Roads near high-traffic places are more likely to be maintained, because damage complaints pressure the controlling body.
  - Quality always falls, and falls faster the higher it is.
- Proposals, not contested:
  - Decay loses a fixed fraction of quality per period; traffic wears it; storms knock it down in steps. Steady maintenance settles where repair equals decay, so funding sets long-run quality.
  - Complaints reach the controlling body as news (slowed caravans, towns paying more tax) and are weighed against its leader's goals.
  - A paved road below a quality threshold is no faster than a dirt road.
  - New paved roads are construction projects run by a role such as Master of Works.
- **Funding (user):** treasury spending plus optional tolls. The controlling body sets the toll rate (a value the DM can adjust and lock). Tolls are coin, collected and carried like taxes. Travellers weigh tolls against speed when choosing routes; toll houses hold coin and are bandit targets (proposal, not contested).

### 17.7 DM control and magical storms (user)

- The user's world has magical storms that are much larger than natural ones, or persistent.
- DM actions on weather systems (proposal, not contested): create one with custom size, strength, duration and movement (including stationary); edit, pin, lock or remove an existing one; override a region (force a drought); adjust a season's severity; tag a system as magical for the later magic system.
- A persistent storm is a weather system like any other: it draws in moisture, rains out downwind, blocks winds and sailing routes, and over years changes the land around it (biomes, forests, fertility). The DM can switch that off per storm so it is a hazard only (user).
- This means climate, biomes, forest growth and fertility can change during play, which also lets long natural droughts change the land.

## 17a. DM tools (user)

- Two kinds of tool:
  1. **Direct modification:** a brush or editor; select something, enter or pick a value, and the change happens (for example change a tile from mountain to plains and set its height). **Built first; this is the user's need now.**
  2. **Indirect influence:** eventual consistency; the DM changes a cause or states an outcome, and the simulation gets there its own way. More limited, wider effects. Examples: warm an ocean for a period so tropical storms are likelier; paint a new tectonic plate; ask for an archipelago in an area without saying what it looks like. Most wanted for initial world building, so the DM can build their campaign world and see what comes of it.
- **After a direct edit (user):** the edit is saved as an override on its stage; every later stage reruns (the existing rerun-from-stage feature) and reapplies the other saved edits. Any edit can be pinned, so it stays exactly as painted even if a later stage would compute something else. Edits are a stored list, so they survive regeneration and can be undone (proposal, not contested).
- **The user's campaign world (Eirdrinolath; detail maps of Praxus and the Isles of Remeiron, in `/mnt/nas/Hobbies/D&D/Main Campaign/World Maps/`):** the user will not import or trace it. They will generate new worlds until one suits, then adapt the story to it: same places, continents and placements in lore, different continent shapes (user). Planet stays Earth-sized (radius 6,371 km) with Earth-like proportions (user). Map import, colour conversion and geology derived from drawn shapes are not needed now; steering generation toward drawn shapes stays a later indirect-influence tool.
- **Finding a world (user):** a batch seed browser with filters. Many seeds are generated to the end of tectonics and shown side by side as small maps with their measurements (landmasses, largest landmass share, spread); filters from the lore (for example at least 6 landmasses, one holding 35–50% of land, an archipelago) skip seeds that fail; chosen seeds are finished. Reuses stage checkpoints and `WorldMetrics`; "has an archipelago" is a new measure (proposal, not contested).
- **World browser (user):**
  1. Two views of the same world with different layers or filters, with rotation and zoom either linked or independent.
  2. Two different worlds side by side for comparison: keep one you like, compare it with a new one, and swap if the new one is better.
  - Comparing two different worlds is limited to the early globe stages, so each world is small: land shape and, ideally, biomes; no weather, tiles or cities (user). At the globe level that is 40,962 cells per world. Globe-level biomes are coarse (about 112 km) and can change once tiles and seasons run (proposal, not contested).
  - Two views of the same world have no such limit.
- **Keeping a world (user):** a kept list where each entry stores seed, settings and generator version (regenerated on open), with an optional saved checkpoint for worlds under serious consideration (opens immediately) and notes.
- First direct edits after that (proposal, not contested): naming generated features (landmasses, seas, ranges, regions) and placing a settlement on a chosen tile, pinned, with roads rerun around it. Remeiron's rings cannot be generated: accept a generated archipelago or paint the rings later, marked magical.
- **Creating and triggering (user):** the DM can create cities and bandit camps, and trigger events such as an attack or a wagon breaking for a merchant.
  - A city or camp created during play gets its people either at once (the DM sets the population) or as settlers arriving by caravan from nearby towns over time, drawn by its prospects; the DM picks per city, and settlers arriving is the default (user). A created bandit camp either starts with members or draws unhappy households and unpaid soldiers from the area (proposal, not contested).
  - Proposals, not contested: an event targets either a specific caravan or merchant, or a place (the next caravan through a road segment); the DM either sets the outcome (lose these goods) or lets the normal rules resolve it; triggered events produce the same loss records, news and reactions as natural ones, and are marked DM-triggered for the DM only; events can be scheduled on the calendar.
- Indirect influence end goal (user chose both, on one model): setting causes (cratons, plates, motions, hotspots, rifts) is the base layer; sketching outcomes turns each intent into causes the DM can see and edit.
- Proposals, not contested:
  - Each outcome the DM can ask for has a short list of real causes (an archipelago: island arc, hotspot chain, drowned coast); the system picks one from what the area suits or from the seed, says which, and can be told to choose differently. The chosen cause fills the geological record (an arc brings arc deposits).
  - Each outcome has a measurable test (archipelago: at least N islands of a size within the area); the system retries a bounded number of times, then paints the feature directly with a plausible record and alerts the DM.
  - Region locks: the DM locks regions they are happy with; an indirect change is accepted only if locked regions still pass their tests (the tectonic simulation is global, so one inserted plate moves continents everywhere).
  - Direct edits backfill a cause in the record (painted mountains get a collision event) so ores and other readers stay consistent.

## 17b. Price chart (user)

Full design: `docs/superpowers/specs/2026-11-25-price-chart-design.md` (approved 2026-11-25). Built as Tasks P1–P5 of the in-memory simulation state plan, with checkpoints C2 (CLI) and D2 (GUI).

- A candlestick chart of one good's price over time: open, high, low, close per candle (user).
- Daily candles are the smallest; week, month and year candles are built from the daily bars; the time axis zooms and pans over months or years; candle size follows the zoom or is set by hand (user). Hourly candles wait until trading happens at more than one tick a day (light-bridge openings, section 13).
- Open and close are the market price at the start and end of the day; the drawn wick covers the day's sale low and high and the open and close; hover shows both pairs (user).
- Scope: a box selection on the map (Shift+drag replaces the selection, Ctrl+Shift+drag toggles cities), political regions, countries (primary country plus regions and towns controlled by claim; disputed ones excluded), towns in no country, or the whole world (user).
- Sale kinds: retail, merchant buy, merchant sell, or all (proposal, not contested).
- The chart opens only while no run is active, shows the state as of the last pause, and has a Refresh button; it does no work during a run (user). Recording bars during a run is measured and reported; no slowdown limit is set.
- Later: totals per government for long views on large worlds; a bulk save path for bars near 1,000 settlements; lasso selection; several goods on one chart.

## 18. Next steps

As of 2026-11-25:

1. **Approve this overview.** Each project design must fit it. Items marked "(proposal, not contested)" were filled in by Claude and not decided by the user; review those first.
2. **Build the in-memory simulation state plan, including the price chart (Tasks P1–P5)**, stopping at checkpoints A, B, C, C2, D, D2 and E for the user to try each.
3. **Design project 1a** (geology, forests and fertility), section by section, with a builder check before hand-over. Questions the design must settle (research report `docs/research/reports/Resource generation research.md`):
   - Tectonic record fixes: event log keeping the most important events (4.1a); uplift so burial differs from sediment; a burial condition for coal exposure; older craton ages.
   - Placing deposits from a 112 km cell into specific 5 km tiles.
   - Deposit density targets (report section 5.12).
   - Formation windows, the simulated-time slider and the best-guess fallback (4.1a).
   - Forest and fertility values per tile.
   - Town placement (`PinStage`) using resources.
   - Re-tuning continents at the 3,000 My default.
4. **Plan, then build project 1a.**
4. **Seasons and weather project (W)**, moved up to follow 1a.

Not blocking:
- **Naval trade:** not yet defined; uses the seasonal winds from W. Next definition session, with DM tools.
- **DM tools:** defined in 17a; direct modification first.
- `docs/research/reports/Plate history calibration 2026-11-24.md` is out of date; project 1a re-tuning replaces its figures.
- Nothing in this design work is committed: this overview, both research reports and their notes. The user commits them.

## Appendix A. Role title examples (reference, not decisions)

Examples supplied by the user to draw titles and role types from; not decided structure. Grouped by area:

| Area | Example titles |
|---|---|
| Ruling authority and nobility | King or Queen, Prince or Princess, Regent, Lord Protector, Lord Paramount, Grand Duke, Duke, Marquess, Count or Earl, Viscount, Baron, Lord or Lady. Simple chain: King → Duke → Count → Baron → Knight → Commoner |
| Council | Hand of the King or Lord Chancellor (coordinates government), Lord Treasurer or Master of Coin, Lord Marshal, Lord Admiral, Lord Justiciar, Master of Envoys, Master of Whispers or Spymaster, Master of Works, Master of Trade or Guildmaster, Master of Harvest, High Priest, Lord Chamberlain (royal household), Keeper of the Seal (records) |
| Military | General, Constable, Warden (frontier), Castellan, Captain, Knight Commander, Quartermaster, Master of Arms, Banneret, Sheriff, Watch Captain, Gate Captain |
| Justice | High Judge, Magistrate, Judge, Bailiff, Coroner, Advocate, Notary, Prosecutor, Jailer, Inquisitor. Possible courts: royal, noble, city, ecclesiastical, military, arcane tribunal |
| Town and city | Mayor, Lord Mayor, Alderman, Town Councillor, Reeve, Bailiff, Town Clerk, City Treasurer, Guildmaster, Market Master, Port Master, Watch Commander, Fire Warden, Master of Roads, Sanitation Officer, Master Builder, City Engineer. Example large city: mayor, deputy mayor, 12 aldermen, clerk, treasurer, watch captain, master of markets, master of guilds, master of works |
| Village | Village Elder, Reeve, Bailiff, Headman, Steward, Forester, Miller, Village Constable, Herald, Grain Warden, Water Warden. Minimal chain: Lord → Reeve → Village Elder |
| Economy | Tax Collector, Customs Master, Mintmaster, Comptroller (audits), Royal Factor, Portmaster, Grain Master, Master of Mines. Possible Council of Guilds approving taxes or vetoing policy |
| Religion | High Priest, Patriarch, Archbishop, Bishop, Abbot, Prior, Priest, Temple Keeper, Inquisitor, Confessor, Keeper of Relics, Holy Knight Commander |
| Intelligence and diplomacy | Keeper of Secrets, Ambassador, Envoy, Herald, Royal Negotiator, Master of Protocol, Border Commissioner, Hostage Warden, Censor |
| Magic | Archmage of the Realm, Minister of Arcane Affairs, Magistrate of Sorcery, Royal Wizard, Spellwarden, Keeper of the Arcane Registry, Master of Enchantments, Alchemist-General, Warden of Magical Creatures, Director of the Royal Academy, Royal Diviner |
| Regional level | Duke or Governor, Regional Steward (administration, taxes), Regional Sheriff, Regional Marshal, Regional Magistrate |

Structures raised by the examples and not yet decided: parallel institutions appointing officials over the same city (crown appoints the duke, church the bishop, guilds the mayor); councils with many seats (12 aldermen); overlapping courts.
