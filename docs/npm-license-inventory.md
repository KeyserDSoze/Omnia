# npm declared-license inventory and triage (#216)

## Purpose and boundaries

This report is a **lockfile metadata inventory**, not a legal opinion, a
license-text scanner, a choice of license, or clearance to distribute Omnia.
It complements the existing npm/NuGet SCA and lock-derived CycloneDX SBOM.
The initial npm lock has 615 dependency occurrences with 14 distinct
declared license expressions. Nested occurrences and platform-optional
packages are deliberately retained; the npm flags dev, optional and
devOptional are install metadata, not a determination of what ships.

The exact classifier recognizes only expressions seen in the reviewed
lock. It never parses an arbitrary SPDX expression, strips whitespace,
chooses one branch of OR, or silently substitutes a permissive license.

| Triage | Meaning |
|---|---|
| notice-review | MIT, ISC, Apache-2.0, BSD-3-Clause, BSD-2-Clause, 0BSD: still verify actual notices and applicable obligations. |
| additional-review | Compound declarations or other terms needing explicit assessment: (MIT OR CC0-1.0), Python-2.0, Unlicense, CC-BY-4.0, (MIT OR Apache-2.0), BlueOak-1.0.0, MPL-2.0, (BSD-3-Clause OR GPL-2.0). |
| unclassified | Missing, new or unrecognized exact declaration; blocks CI until assessed. |

Examples from the initial lock: lightningcss and its platform variants
declare MPL-2.0; caniuse-lite declares CC-BY-4.0; node-forge declares
(BSD-3-Clause OR GPL-2.0). These are **review candidates**, not proof
that packages are incompatible or that obligations have been satisfied.

## Reproduction

```sh
python3 -m unittest discover -s tests/quality -p test_npm_license_inventory.py
python3 tests/quality/npm_license_inventory.py \
  --lock src/mobile/package-lock.json \
  --output /tmp/omnia-npm-license-inventory.json
```

CI runs this in the existing read-only security-audit job **after**
the live npm/NuGet SCA and publication of its evidence. The inventory
also runs if a preceding step failed (unless the job is canceled);
it cannot turn a prior failure green. A completed report is uploaded
as the separate 14-day artifact omnia-npm-license-inventory even
if unknown license metadata blocks the job. A malformed lock fails
without a completed inventory. Inspect the step result and report
status, never treat artifact presence as approval.

The JSON contains the source commit, SHA-256 of the actual lock bytes,
sorted occurrence rows, original license strings (null if missing),
name/version/path, npm flags and counts. It has no timestamps,
credentials, user data or installed-code claims. Identical input bytes
and commit produce identical JSON. The earlier lock integrity and
locked-install gates remain mandatory; this is not a replacement.

## Next steps

The project owner must review actual license files/notices in verified
tarballs and distributed Web/native bundles, including copyright,
patents, attribution and source obligations, plus all NuGet packages
and container components. Link the evidence in #22 and #131; open
separate bounded tasks for remaining coverage. No legal acceptance,
release approval or risk waiver is created by this triage.

References:
- https://docs.npmjs.com/cli/v11/configuring-npm/package-json/
- https://docs.npmjs.com/cli/v11/configuring-npm/package-lock-json/
- https://spdx.org/licenses/
- https://spdx.org/licenses/MPL-2.0.html
