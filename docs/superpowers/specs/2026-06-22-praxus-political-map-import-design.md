# Praxus Political-Map Import — Conversion Plan

**Date:** 2026-06-22
**Status:** Plan (no code changes; execution deferred until the import schema settles)
**Source data:** `~/Downloads/eirdrinolath_20260622_063800/` (Kanka export, v3.11) + 5 campaign map images
**Scope:** The primary continent **Praxus** only. Political layout (countries → regions →
settlements) with role/description-based population estimates and description-backed goods.

> This document is the *plan* for converting the Kanka export + maps into a hand-curated
> `SeedWorld` JSON. It deliberately produces **no code and no DTO changes** — another agent is
> active in the codebase. The actual JSON is authored later (see §9 Execution Checklist), once the
> import schema is stable. Everything an author needs to write that JSON quickly lives here.

---

## 1. Goal & guardrails

- Produce a single hand-curated import file (later) — working name `world-praxus.json` — that loads
  through the existing `JsonSeedSource` / CLI `import` path and represents Praxus's political map.
- **Interpretation, not 1:1 import.** We read the Kanka graph + maps and build *our* representative
  model. We do not build a generic Kanka import system.
- **Stay inside today's schema** (`src/WorldEcon.Seeding/SeedModel.cs`). Where the world doesn't fit,
  we approximate within the schema and record the approximation (§3, §8). No code changes now.
- Populations and distances need only be **reasonable**, grounded in D&D demographics (§4) and the
  map scale (§6) — not exact.

## 2. Source-of-truth split

Each input is authoritative for different facts; we never guess what a source already states.

| Need | Authoritative source |
|---|---|
| Political hierarchy + names (continent, kingdoms, regions, cities) | **Kanka location tree** (`parent_id` chain + `type`) — fully extracted in §7 |
| Role / character of each place (drives population + goods) | **Kanka `entry` descriptions** |
| Settlement coordinates | **Map images** (pixel → grid) |
| Road network (routes) + distances | **Map images** (lines between cities) + the labeled mile distances for scale calibration |
| Territory groupings (cross-check) | **Map image 5** colored political polygons |
| Goods that exist | **Kanka `entry` descriptions + `items/`** (Archanite family, etc.) |

The `SeedWorld.Seed` value and ruleset version are author-chosen (a fixed default, e.g. `1`), not
sourced from Kanka.

## 3. Fitting Praxus into today's strict schema

`SeedWorld` requires strict nesting **Continent → Country → Region → Settlement**. The Kanka tree
nests cities *directly* under kingdoms and has regions with no country. In-schema interpretation:

1. **Each kingdom = a `Country` with one `Region`** (the kingdom's core territory, same name) that
   holds its cities. Finer regional subdivision is deferred until the schema grows region metadata.
2. **Threykadia** (the fallen Threykadian Empire — "dissolved but not gone") is its **own `Country`**.
   Its cities sit in a region named **Threykadian Desolation** *within* Threykadia (leaving room for
   other Threykadian regions later). The empire retains an **(undead) population but is isolated**:
   model as settlements with population but **no routes crossing the Threykadia boundary** (§6).
3. **Independent** = a synthetic `Country` representing self-governing places with **no government
   above the city level** — only each city's own internal rules, nothing greater than itself. It
   houses the unaligned locations that sit *directly under the continent* rather than under any
   kingdom: the **Wastelands** region (Dragons Spire) and the free village **Boldersdoor** (a direct
   child of Praxus in the Kanka tree). This replaces giving each unaligned place its own one-off
   country. (Threykadia is *not* Independent — it is a fallen empire that still has a state above its
   cities, so it stays its own country.)
4. **Non-settlement locations** (mountain ranges, buildings, shops, taverns, mines, landmarks, ships)
   are **not** settlements. Notable ones are recorded as points of interest (§7 notes) and inform
   goods, but do not become map nodes under the current schema.

### Schema re-check (verified 2026-06-25 — the API has moved since the plan was drafted)
Re-read against the current `SeedModel.cs` + the new canonical sample `samples/aerthos.seed.json`.
**Geography is unchanged** — still strict Continent→Country→Region→Settlement; countryless regions,
region kind, settlement state, and claims are **still not exposed** in the import DTO, so §3's fitting
and the deferrals below stand. Two new *optional* fields now exist and we adopt them:
- `SeedGood.needTier` (`Essential` | `Standard` | `Comfort`; omitted ⇒ Essential) — assigned per good in §5.
- `SeedSettlement.consumers` (`[{ size, budget }]`) — pre-seeds demand from day 1; **deferred to the
  economic pass** (the political pass leaves it empty). When used, seed at the engine's
  DefaultConsumerSize (1000) and a budget ≈ one week's allowance.

**Authoring convention:** the canonical sample uses **camelCase** keys (`name`, `continents`,
`fromSettlement`, `needTier`, …) — mirror it. The sample's coords/distances are small abstract integers;
we instead use map-pixel coords and map-mile distances (§6), which is fine as long as it stays internally
consistent. **Re-verify the schema again at execution** — it has changed once and may change again.

### Deferred to "needs schema support" (record now, encode later — see §8)
- **Countryless regions** (the domain already supports `Region.CountryId == null`) — if the importer
  exposes it by execution time, model Threykadian Desolation / Wastelands as true countryless regions
  directly under Praxus instead of synthetic countries.
- **Territorial claim:** **Threykadia *Disputes* Lesser Threykadia** (successor-state dispute). The
  domain has `TerritorialClaim` (Controls/Disputes); the import DTO does not yet.
- **Region kind / settlement state** (e.g. Threykadian Desolation = `Other`/ruined, Neihrendal =
  `Ruined`). Domain has `RegionKind` + `SettlementState`; not in the import DTO yet.

## 4. Population baselines (D&D demographics)

Baselines blend the 3.5e DMG settlement bands with 5e ranges, then nudge by description. **Reasonable,
not exact.**

| Kanka type | D&D band | Baseline | Nudge |
|---|---|---|---|
| Capital | Metropolis (25k+) | ~22,000–25,000 | seat of power / major trade → higher |
| City | Small city (5–12k) | ~6,000–8,000 | major hub → 12–18k; minor/remote → 3–5k; ruined → <1k |
| Town | Large town (2–5k) | ~2,000 | — |
| Village | Village (400–900) | ~600 | — |
| Outpost | Hamlet / thorp | ~150 | — |
| Ruined / isolated city | — | floored | undead remnant / cursed → 500–3,000 |

## 5. Goods rule & description-backed catalog

Include a good **only if** a description or `items/` entry supports it. No invented economies. This
first pass defines the catalog and tags which settlements are *known for* each good; full production
nodes / endowments / shop inventories are a later economic pass (not this political pass).

| Good | `GoodCategory` | Description/source basis | Tied to |
|---|---|---|---|
| Archanite | Material (Raw) | `items/`: Gemstone storing magical energy | Draydon (research) |
| Refined Archanite | Material | `items/` | Draydon |
| Corrupted Archanite | Material | `items/` | — |
| Arcane Engine | Tool | `items/`: Construct using Archanite | Draydon |
| Grain / Corn | Food | Ackber "corn exports", Polting, Westron farming | Ackber, Polting, Westron |
| Livestock / Animal goods | Food | Polting, Westron animal exports | Polting, Westron |
| Fish | Food | Huffdale fishing/whaling, Seacaster, Inkwel | Huffdale, Seacaster |
| Ink | Material | Inkwel "cultivated fish… ink for pens and dyes" | Inkwel |
| Textiles / Luxury goods | Luxury | Hiton "specialty of luxury goods and textiles" | Hiton, Ozmouth |
| Ships / Naval vessels | Tool | Seacaster "largest shipyards… naval products" | Seacaster |
| Emeralds / Gems | Luxury | Smaragd Stadt "Emerald City"; Emerald Caverns mine | Smaragd Stadt |
| Iron ore | Raw | Deloriea "large mine"; dwarven mines | Deloriea, Dhunmin |
| Weapons | Weapon | Hammerfell "largest set of forges… weapons", Klolis | Hammerfell, Deloriea |
| Armor | Armor | Hammerfell "armor and weapons" | Hammerfell |
| Stone / Masonry | Material | Ironhelm "quality of its construction… grand architecture" | Ironhelm |
| Gold | Luxury (Raw) | Zeigelith "City of Gold" | Zeigelith |
| Spirits (burning alcohol) | Misc | Syrusburn "burning alcohol… magical fire" | Syrusburn |
| Bows / Arrows | Weapon | Ezorath "City of Arrows… ancient ways… druids" | Ezorath |

**NeedTier** (the new optional `SeedGood` field): staples (Grain/Corn, Fish, Livestock, Iron ore,
Stone) → `Essential`; manufactured / utility goods (Tools, Weapons, Armor, Ink, Textiles, Ships,
Arcane Engine, Bows/Arrows, the Archanite family) → `Standard`; indulgences (Luxury goods, Emeralds,
Gold, Spirits) → `Comfort`.

(Magic artifacts in `items/` — Morgal Blade, Lens of Focusing, Demonomicon, etc. — are *not* trade
goods and are excluded from the catalog.)

## 6. Coordinates & routes — method

**Coordinates.** Read each named settlement's pixel position from the labeled Praxus map (image 1)
and store on an integer grid matching the image (~2000 × ~1300). Off-map Kanka cities (those without
a visible label — e.g. Erowind, Ezorath, Cassiro, Zeigelith, Fulstead, Klolis, Osidia, Wimborne,
Elin, Kirodor, Morgaliat, Threyokad) are placed within their kingdom's bounds at authoring time. The
starter table in §7 lists estimated coords for the locatable cities; treat them as approximate.

**Distance scale.** The maps show measured distances (e.g. **793.76 mi** and **406.75 mi** for the
red dashed Syrusburn→Illithiador measurement in image 4). Calibrate **pixels → miles** from those
labeled segments, then compute each route's `Distance` from its endpoints' pixel coords × scale.

**Routes.** The lines between cities on the map are the road network → `SeedRoute` entries.
- `Terrain` / `Category`: infer from the terrain the line crosses — `Land`/`Plains`/`Forest`/
  `Mountain` for overland; `ShippingLane` + `Sea`/`Coast` for sea legs to island/coastal cities
  (Seacaster, Ozmouth, Xia, the offshore Duran cities).
- `SeedRoute` is **directed**; list both directions for two-way roads (per `SeedModel` semantics).
- **Threykadia isolation:** author **no routes** crossing the Threykadia / Threykadian Desolation
  boundary (Kelthorad, Threyokad, Morgaliat, Kirodor are internally reachable only) — reflects the
  "isolated, can't trade outside the region" lore. Internal routes among its cities are optional.

The full edge trace is done against the map at authoring time; §9 captures it as an execution step.

## 7. Praxus catalog (authoritative structure + draft values)

Continent: **Praxus**. `Pop` = D&D-baseline estimate (§4) with the description cue that set it.
`(x,y)` = approximate map pixels (§6); "—" = off-map, place within region at authoring.

### Country: Thaloria — Region: Thaloria (capital: Markain)
| Settlement | Type | Pop | Cue | (x,y) | Goods |
|---|---|---|---|---|---|
| Markain | City | 10,000 | capital, "well-regarded hub for merchants", not largest | (420,640) | trade |
| Seacaster | City | 9,000 | "largest exporters of naval products", largest shipyards | (160,715) | Ships, Fish |
| Polting | City | 7,000 | farming, "reasonably large exporter of food/animals" | (390,870) | Grain, Livestock |
| Hiton | City | 5,000 | minor port, luxury goods + textiles, minor military | (355,495) | Textiles, Luxury |
| Syrusburn | City | 5,000 | known for magical "burning alcohol" | (540,648) | Spirits |
| Inkwel | City | 4,000 | niche ink industry (cultivated ink-fish) | (385,388) | Ink, Fish |
| Zeigelith | City | 4,000 | "City of Gold", ancient mountain city | — | Gold |
| Ozmouth | City | 3,500 | island, "city of the rich", little production, trade-reliant | (305,645) | Luxury |
| Xia | City | 3,000 | former elven port, diminished after elves left | (410,728) | — |
| *Nailine Heights* | *(Mountain Range — not a settlement)* | — | tallest peak Rostou Summit | — | — |

### Country: Thierovania — Region: Thierovania (capital: Draydon)
| Settlement | Type | Pop | Cue | (x,y) | Goods |
|---|---|---|---|---|---|
| Draydon | City | 12,000 | capital, "advanced sciences and magic", Archanite research | (575,462) | Archanite, Arcane Engine |
| Ackber | City | 8,000 | "large farming community", corn exports + all foodstuffs | (790,365) | Grain, Corn |
| Huffdale | City | 7,000 | northernmost major city, fishing/whaling, academy campus | (660,192) | Fish |
| Damridge | City | 6,000 | major river city with a large bridge (trade crossing) | (595,328) | — |
| Klolis | City | 5,000 | "minor… reasonably sized", merchants, farming, forges | — | Weapons, Grain |
| Fulstead | City | 5,000 | (no description) — baseline | — | — |
| Slab Ridge | City | 4,000 | lighthouse town with unidentified arcane runes | (470,258) | — |
| Edinburgh | Town | 2,000 | (no description) | (540,175) | — |
| Wimborne | Town | 2,000 | (no description) | — | — |
| Osidia | Town | 1,800 | "town at the bottom of the mountain" | — | — |
| Murkwell | Outpost | 150 | "small outpost just north of…" | — | — |

### Country: Kingdom of Dhunmin — Region: Dhunmin (capital: Ironhelm) — *dwarven*
| Settlement | Type | Pop | Cue | (x,y) | Goods |
|---|---|---|---|---|---|
| Ironhelm | City | 18,000 | capital, "by far the largest city of the dwarves", architecture | (880,470) | Stone |
| Kuldohr | City | 12,000 | "most visible and traveled" dwarven city, public face | (840,678) | trade |
| Smaragd Stadt | City | 8,000 | "Emerald City", emerald + other mining (Emerald Caverns) | (945,418) | Emeralds, Gems |
| Tharimdar | City | 6,000 | hidden ravine fortress, "one of the most well-defended" | (790,488) | — |
| Westron | City | 5,000 | farming/animal city, friendly with dwarves | (965,228) | Grain, Livestock |
| Thulkuldor | City | 5,000 | remote deep-underground city, easy to get lost | (1130,325) | — |

### Country: Kingdom of Duran — Region: Duran (capital: Lexingtara)
| Settlement | Type | Pop | Cue | (x,y) | Goods |
|---|---|---|---|---|---|
| Lexingtara | Capital | 22,000 | capital; magic prohibited (punishable by death) | (1430,612) | — |
| Emerstead | City | 6,000 | (no description) | (1545,543) | — |
| Greendale | City | 6,000 | (no description) | (1620,378) | — |
| Stonecaster | City | 6,000 | (no description) | (1700,525) | — |
| Elin | Town | 2,000 | (no description) | — | — |

### Country: Lesser Threykadia — Region: Lesser Threykadia (capital: Hammerfell)
| Settlement | Type | Pop | Cue | (x,y) | Goods |
|---|---|---|---|---|---|
| Hammerfell | City | 14,000 | capital, "largest set of forges in the world", armor/weapons | (1575,752) | Weapons, Armor |
| Deloriea | City | 6,000 | "basic forges and a large mine" | (1490,762) | Iron ore, Weapons |
| Stonedome | City | 5,000 | secretive; rumored gladiator fights to the death | (1390,738) | — |

### Country: Kel Fabrel — Region: Kel Fabrel (magical/elven realm)
| Settlement | Type | Pop | Cue | (x,y) | Goods |
|---|---|---|---|---|---|
| Illithiador | City | 9,000 | most well-known elven city, outsiders rarely admitted | (730,748) | — |
| Illsaserine | City | 6,000 | self-sufficient magical realm, "mostly untouched" | (820,890) | — |
| Erowind | City | 6,000 | "City of Starlight" | — | — |
| Thieladianel | City | 6,000 | (no description) | (1110,858) | — |
| Ezorath | City | 4,000 | "City of Arrows", ancient elven/druid ways | — | Bows, Arrows |
| Cassiro | Town | 1,500 | minor staging town between elven realm and beyond | — | — |
| Neihrendal | City | 500 | once-great trade capital, **fell to a curse, lost to the forest** | (585,748) | — *(ruined)* |

### Country: Threykadia — Region: Threykadian Desolation — *fallen empire; undead; isolated (no external routes)*
| Settlement | Type | Pop | Cue | (x,y) | Goods |
|---|---|---|---|---|---|
| Kelthorad | City | 3,000 | "Ancient Capital… largest city ever built", now lost/ruined | (1245,520) | — |
| Morgaliat | City | 2,000 | "city of the dead", home to the undead armies of Threy | — | — |
| Threyokad | City | 1,500 | former imperial capital, location lost to time | — | — |
| Kirodor | City | 1,500 | SW of the region, "once a proud member" | — | — |

### Country: Independent — *self-governing; no rule above the city level*
A non-state umbrella for unaligned places sitting directly under Praxus (not in any kingdom).

**Region: Wastelands**
| Settlement | Type | Pop | Cue | (x,y) | Goods |
|---|---|---|---|---|---|
| Dragons Spire | City | 2,000 | "city of fire located in the caldera of an active shield volcano" | — | — |

**Region: Boldersdoor** *(free settlement; direct child of Praxus in the Kanka tree)*
| Settlement | Type | Pop | Cue | (x,y) | Goods |
|---|---|---|---|---|---|
| Boldersdoor | Village | 600 | (no description) — independent village under Praxus | — | — |

**Totals:** 1 continent · 8 countries (6 kingdoms + Threykadia + Independent) · 9 regions ·
47 settlements (excludes mountain ranges, buildings, shops, taverns, mines, landmarks, ships).

## 8. Not in this pass (recorded for later)

- **Other 5 continents** — out of scope; Praxus only.
- **Territorial claim:** Threykadia *Disputes* Lesser Threykadia — encode when the importer exposes
  claims; until then it lives in this doc.
- **Countryless regions / region kind / settlement state** — encode when the importer exposes them;
  until then Threykadia & Wastelands are synthetic countries and ruined/desolate status is implied by
  low population only.
- **Economic layer** (production nodes, endowments, shop inventories, merchant capital, recipes) —
  this pass defines the goods catalog and "known for" tags only; wiring production/trade is a later
  pass.
- **Points of interest** dropped as settlements but worth re-attaching when sub-locations exist:
  Inteceda Academy, Emerald Caverns (Smaragd Stadt), Spark & Hammer Forge (Klolis), Hall of Mages
  (Neihrendal), Construction of Magic School + Institution of Ancient Understanding (Markain), the
  Sky Bridge/Canals (Draydon), Norngar Maw/Underpass.

## 9. Execution checklist (when the import schema is stable)

1. **Confirm the target schema** — re-read `src/WorldEcon.Seeding/SeedModel.cs` and mirror the canonical
   sample `samples/aerthos.seed.json` (camelCase keys). As of 2026-06-25 geography is strict-nested with
   optional `needTier`/`consumers`; the API has already shifted once, so re-verify and adopt any newly
   exposed fields (countryless region, region kind, settlement state, claims — see §3/§8) if present,
   else use the strict-schema fallback.
2. **Author geography** — emit Praxus → 8 countries → 8 regions → 45 settlements from §7 (name, type,
   population, X/Y).
3. **Place off-map settlements** — assign coords within each kingdom's map bounds for the cities
   marked "—" in §7.
4. **Calibrate scale** — derive pixels→miles from the labeled map distances (§6).
5. **Trace routes** — read the road lines from the map; emit directed `SeedRoute` pairs with computed
   `Distance`, `Terrain`, `Category`. **Omit all routes crossing the Threykadia boundary.**
6. **Goods catalog** — emit the §5 goods; tag "known for" settlements (lightweight; full economy
   later).
7. **Validate** — load through `JsonSeedSource` + the CLI `import` command; confirm it imports with no
   errors and the geography/route counts match §7.
8. **Record deferrals** — note in the import file (or a sidecar) the §8 items that couldn't be encoded
   (esp. the Threykadia↔Lesser Threykadia dispute and Threykadia's isolation) so they're not lost.
