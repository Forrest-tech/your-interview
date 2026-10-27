# Vendored logo data (ticket 249, R1 local layer)

Sources (both CC0 1.0 Universal; the datasets' files are CC0, the underlying
brand marks remain their owners' trademarks — display here is nominative use
next to the company's own job listing):

- Simple Icons (`simple-icons-index.json` = verbatim `data/simple-icons.json`,
  `simple-icons-svgs.json` = packed `icons/*.svg` keyed by slug):
  https://github.com/simple-icons/simple-icons — 3,467 icons at vendoring
  (2026-10-27). Lookup normalizes company names with Simple Icons'
  `titleToSlug` rule (see `CompanyLogoRules.TitleToSlug`).
- gilbarbara/logos (`gilbarbara-index.json` = verbatim `logos.json`,
  `gilbarbara-svgs.json` = packed `logos/*.svg` keyed by filename):
  https://github.com/gilbarbara/logos — 1,499 entries at vendoring
  (2026-10-27). Lookup keys are `shortname` (preferred) and the slug of
  `name`; when an entry lists several files, the `*-icon.svg` variant is
  preferred (designed for small tiles), else the first file.

Re-vendoring: re-clone the two repos, regenerate the two `*-svgs.json` maps
with the same key rules, and update the counts/dates above. No network call
is made at runtime for this layer.
