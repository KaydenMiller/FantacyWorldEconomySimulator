# Autonomous Build — Decisions Log

This log records educated guesses and design-gap resolutions made while building the World Economy Simulation autonomously (per the user's directive to build all phases to a functioning simulation, fixing/guessing as needed and noting choices). Each entry: what was ambiguous, the choice, and why.

## Operating mode
- Build each phase → test (warning-clean build + green tests) → commit → merge to `master` → next phase.
- A **CLI** (`WorldEcon.Cli`) is built for exercising the simulation headlessly (no UI), per the user's instruction.
- Reviews are lighter in autonomous mode (self-verify + tests; deeper review only at risky integration points) to maintain velocity.

## Decisions
- **2026-06-21 — Cost-basis overflow policy.** Resolved the deferred `Money`/`FixedMath` overflow-policy question by making cost-basis accumulation **loud** (`checked`) — consistent with `FixedMath`. Silent wrong cost basis would corrupt the deterministic economy. (Plan 2 T3.)
