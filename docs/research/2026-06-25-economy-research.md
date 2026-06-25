# Economy research — survey & findings (2026-06-25)

A four-stream survey of how other simulations/tools model economies, run to find blind spots before extending WorldEcon. Distilled to the actionable points; sources kept for follow-up. The conclusions feed the "Refined economic direction" section of `../capabilities-and-roadmap.md`.

---

## Stream 1 — Commercial simulation/strategy games

**What they model (condensed):**
- **Victoria 3** — order-based price clearing (price swings ~25–175% of base on buy/sell imbalance); money created/destroyed per trade; per-region **Market Access %** (infrastructure) blends local vs. national prices; shortage→throughput-penalty cascades that self-correct; selectable production methods that substitute inputs; pop stratification & standard-of-living-driven consumption; tariffs, taxes, minting, GDP-tied credit.
- **Workers & Resources: Soviet Republic** — moneyless internal economy; large trades move global prices against you; **import cost = value + frontier distance**; labor is physically transported; utilities (power/water/heat) are required inputs.
- **Anno 1800** — escalating residential **need tiers** (houses upgrade only after lower needs met); fixed per-resident consumption; **per-building maintenance regardless of output** (idle to save upkeep); ship routes with load/unload friction.
- **Capitalism Lab/2** — product = price + **quality + brand**; brand awareness/loyalty; **market share per city**; vertical/horizontal integration; R&D.
- **Mount & Blade: Bannerlord** — village raw output scaled by "hearths"; workshops convert raw→finished (profit depends on cheap adjacent inputs); per-town supply pricing; **prosperity = purchasing power**; food gates prosperity; autonomous caravans; war/raid disruption.
- **Port Royale / Patrician** — production chains w/ efficiency; population-proportional consumption; price = base × local-supply-depletion; trade routes with **priority reservations**; warehouse upkeep; town growth gated by needs satisfaction (~20-day buffer).
- **EVE Online** — true player **order book** (partial fills, buy-order jump-range); fragmented **regional markets** + arbitrage hubs (Jita liquidity ~80× others); blueprint manufacturing; broker fees + sales tax; **tracked ISK faucets vs. sinks** with published indices; **destruction-driven demand**.
- **Dwarf Fortress** — value = form × material × **quality(skill)** + decorations (huge spread for the "same" good); processing-step arbitrage; seasonal caravans by civ; **cultural trade taboos**; negotiated margins; **forward-contract trade agreements**.
- **Distant Worlds / X4** — **autonomous private sector** owns/decides freighters & routing; **freighter count/cargo size is an explicit throughput lever**; fuel as consumed/transported commodity; every ware physically hauled; station price moves continuously with stock level toward min/max bounds; attackable supply lines.
- **Offworld Trading Company** — **prices move only on actual trades, not on your own stockpiling**; geographic scarcity; paid black-market shortage/surplus manipulation.

**Biggest gaps it flagged for WorldEcon (excl. planned items):** transport friction as a real binding constraint (finite carrier capacity); distance-scaled landed cost that doesn't fully equalize; a money-supply ledger (faucets/sinks); standing trade contracts/reservations; "trades move prices, stockpiling doesn't"; per-building upkeep; comparative-advantage specialization; autonomous profit-seeking agents. Cheap wins: in-tier goods substitution, utilities-as-inputs, cultural buy-refusals.

---

## Stream 2 — RPG-economy & agent-based-economics literature

**Doran & Parberry, "Emergent Economies for Role Playing Games"** (IJIGS 2012 / tech report 2010) — the canonical paper. PDF: https://ianparberry.com/pubs/econ.pdf
- Population of single-profession trader agents; each round runs a **production rule** (basket→basket) then posts bids/asks to a central **clearing house**; imperfect info, learn from own experience only.
- **Price belief = a [low, high] interval per commodity.** Offer a price drawn within it; **on success narrow** (toward mean, ~1/10 of upper); **on failure widen** (upper +~1/10) **and translate toward the round's clearing price** (with 110–120% over-correction when clearly mispriced). Quantity scaled by a "favorability" factor (where the mean sits in the observed range).
- **Clearing = distributed double auction:** shuffle, sort bids desc / asks asc, match top-to-top, **clearing price = midpoint of bid & ask**, leftovers rejected.
- **Design thesis:** "interesting behavior > accurate modeling." Minimum viable economy = prices that are reactive, event-consistent, and visibly variable. **Never compute equilibrium — run a belief/feedback loop that orbits it.** Bankrupt agents respawn into the currently most-profitable role (auto-allocation + answers "how many woodcutters can this town support?"). Cheap: ~200k agent-updates/sec (2010 core), 2 floats/commodity/agent.

**Agent-based econ foundations:**
- **Sugarscape** (Epstein & Axtell 1996) — decentralized bilateral trade does **not** converge to one price; realized prices form a **distribution with non-zero variance**. A single global clearing price reads as "planned."
- **Gode & Sunder "Zero-Intelligence Traders" (1993)** — random bids/offers reach ~100% allocative efficiency **if** a budget/reservation constraint stops trading past value. → invest in **market mechanism + reservation prices**, not agent IQ.
- **Quantity theory of money (MV=PQ)** — use as a **validation invariant**: inject money in a test, confirm the aggregate price level rises proportionally without distorting real quantities.

**Postmortems (failure modes):**
- **EVE** — sinks (esp. destruction) are the master inflation control; intervene on inputs/sinks, **not prices**; "Scarcity Era" overreach drove disengagement. Spatial friction (haul cost + gank risk) is what creates arbitrage. (Official Monthly Economic Reports publish money supply, faucet/sink delta, price indices.)
- **Diablo III auction house** — a frictionless global market made "buy, don't farm" optimal and collapsed the core loop → removed. **Players take the path of least resistance.**
- **Inflation/mudflation** — faucets outrunning sinks inflate within ~months; Path of Exile resists it structurally (**currency *is* consumable crafting items** — a built-in sink). Value comes from engineered artificial scarcity (Castronova).

**Lessons for WorldEcon:** adopt belief-interval price discovery; embrace price **dispersion** + perpetual disequilibrium; build a **faucet/sink ledger**; an **economist's dashboard** (money supply, delta, CPI basket, regional spreads) is high-leverage; add **expectations/speculation** (news moves price before goods move) and **cobweb cycles** (production lag → oscillation); add **demand elasticity**; keep **friction** deliberate; reservation prices/light haggling fall out of the belief band.

---

## Stream 3 — TTRPG systems & GM tools (what a DM actually wants)

**System mechanics (recurring):** settlement size → a single **wealth cap** gating the priciest item (D&D 3.5 GP Limit; Pathfinder **Base Value**); **buy/sell asymmetry** (PF **Purchase Limit** — towns pay less than they charge; half-price selling); **probabilistic availability** (3.5 "75% rule," 5e d100 tables) not fixed stock; crafting ≈ ½ price + time; income as periodic table/test; cost-of-living/upkeep drains; **failure spirals** (Kingmaker Unrest, GURPS/BW Resources "tax"); supply/demand via local specialization (**Traveller** trade codes → arbitrage; **Harn** bottom-up manor budget); trade routes/caravans as discrete income objects; taxes/tariffs as Economy↔Loyalty sliders; a separate **domain currency** layer (Build Points, Gold Bars).

**Tool landscape:** city/shop generators (Watabou, donjon, World Anvil, Eigengrau's), VTT modules (**Item Piles** best-in-class merchant/loot; no restock/economy), domain trackers (pf2e-kingmaker-tools), trade sims (ACKS Mercantile Ventures — real arbitrage but "accountant's nightmare"). **No tool delivers ACKS-depth with plug-and-play usability.**

**What GMs most value / biggest recurring gaps:**
1. **"Pointless gold"** is the #1 economy grievance — wealth accumulates with no sinks (training/stronghold sinks were dropped). → money sinks matter.
2. **"Shopping is tedious"** → abstraction by default, roleplay on demand.
3. **Over-simulation warning:** a deep engine invites player arbitrage, breaks immersion when seams show, risks hyperinflation when PCs dump wealth, and adds tedium. → **deep DM-side sim, simple player-side answers.**
4. Most-requested missing feature everywhere: **persistent shops that auto-restock**, then **dynamic/regional pricing**, **anti-inflation guardrails**, **settlement→shop→economy linkage**, low bookkeeping.
- _Note for WorldEcon: it's a personal tool, so "usability for others" is moot — but the "deep sim → simple legible answers" framing still applies to the **author's** session-time use, and the snapshot/branch feature is genuinely novel vs. all surveyed tools._

---

## Stream 4 — Open-source economy sims (implementation patterns)

**Projects:**
- **BazaarBot** (Lars Doucet, Haxe) — canonical reference impl of Doran-Parberry (price-belief intervals + double auction). https://github.com/larsiusprime/bazaarBot · writeup: https://www.gamedeveloper.com/design/bazaarbot-an-open-source-economics-engine · Python port: https://github.com/Enerccio/py-bazaarbot
- **EconSim** (omikun, Unity/C#) — modernized BazaarBot; adds **cost-of-inputs + margin** pricing, dependency chains (forest fire cascades scarcity downstream), bankruptcy-respawn growth, a gov/tax layer. Closest to WorldEcon's stack. https://github.com/omikun/EconSim
- **Prosperity-Wars / EconomicSimulation** (Nashet, Unity/C#) — pops + factories + country markets. https://github.com/Nashet/Prosperity-Wars
- **Eigengrau's Generator** — town-wealth scaling adjusts wages/prices coherently; clientele-matched pricing. https://www.randroll.com/economy-of-the-generator-from-eigengraus/
- **Factorio Black Market 2** — minimal robust rule: every buy nudges price up, every sell down. https://github.com/djmango/BlackMarket2

**Design patterns / pitfalls:**
- Price discovery via **belief intervals + double auction**, not formulas; the confidence band gives memory/hysteresis → oscillation-around-equilibrium, not thrashing.
- **The zero-profit death spiral is the most-cited failure:** when a profession dies out its profit metric reads zero forever → permanent shortage. Fix: bias respawn/production toward goods where **demand>supply**, not just past profit.
- **Stability via small adjustment fractions** (the paper's 1/10, 1/5 constants are deliberately gentle; feedback coefficients >1 → oscillation/bubbles).
- **No equilibrium is ever reached** — expect ~2% per-round drift; design displays around orbiting.
- **Closed economy mandatory** — any good consumed-but-never-produced (or vice versa) gives ugly, unstable results.
- **Inflation = sinks vs. faucets** (track a running money delta).
- **RNG for shuffle/tie-break breaks determinism** — substitute a seeded deterministic tie-break (stable sort on agent id / seeded key). The paper itself notes "chaotic sensitivity to initial conditions" — any nondeterminism diverges runs (hard conflict with WorldEcon's determinism requirement; design around it).

**Top adoptable ideas:** per-shop price-belief intervals; favorability-scaled trade quantities (damps whiplash); double-auction with `clearing = avg(bid,ask)` + leftover-rejection (deterministic tie-break); demand-aware production to prevent death spirals; idleness tax / forced bankruptcy; sinks-vs-faucets bookkeeping in the event log; town-wealth tier multipliers.

---

## Cross-stream synthesis — the biggest blind spots (all four converge)

1. **Price discovery** — move from aggregate supply/demand formulas to **per-shop price-belief intervals** (prices emerge from real quantity flows). _Locked in._
2. **Money-supply discipline** — explicit **faucet/sink ledger** + dashboard; prerequisite for labor/wages. _Locked in._
3. **Legibility / session surface** — "what's buyable, cost, restock" + queryable impact-ranked events; deep sim → simple answers. _Locked in (query & travel surfaces)._
4. **Stability guardrails** — shortage-aware production (anti-death-spiral) + per-facility upkeep/idleness. _Locked in._
5. **Demand elasticity** — quantity bends with price, per good. _Locked in._
6. **Friction/specialization** — weight/dim-weight + transport modes keep price spreads from flattening; emergent comparative advantage. _Locked in._
7. **Determinism caveat for all of the above** — belief-price draws + clearing tie-breaks need a seeded, deterministic RNG surface. _Flagged as the key architectural decision._
