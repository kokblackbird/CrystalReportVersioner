# Changelog

All notable changes to **Crystal Report Versioner** are documented here.  
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).  
Versioning follows [Semantic Versioning](https://semver.org/).

---

## [1.2.0] — 2025-07-xx

### Added
- **Export for AI** button — loads a single `.rpt` file through the Crystal runtime engine
  and serialises the full report definition to a structured JSON file ready to paste into
  an AI prompt (ChatGPT, Copilot, Claude, etc.).  
  The exported JSON includes:
  - Report name, file path, and export timestamp
  - `recordSelectionFormula` and `groupSelectionFormula`
  - `formulaFields` — name + full formula expression text
  - `parameterFields` — name, prompt text, value type, allow-multiple flag, and default values
  - `sortFields` — field formula name and sort direction
  - `groupFields` — field formula name and group condition
  - `runningTotalFields` — name, summarized field, operation, evaluate condition, and reset condition
  - `databaseTables` — table name, location, and all field names with their data types
  - `sections` — every section (Report Header, Page Header, Group Header/Footer, Details,
	Report Footer, Page Footer) with each contained object's kind, data-source formula,
	display text, and subreport name where applicable
  - `subreports` — subreport name, selection formula, and formula fields
- Export degrades gracefully: if a Crystal runtime property is unavailable the field is
  omitted and a note is written to the log; the JSON file is always valid.
- `SaveFileDialog` defaults to `<reportname>_ai.json` in the same directory as the source
  `.rpt` file.

### Changed
- Assembly version bumped from `1.1.2.0` → `1.2.0.0`.

---

## [1.1.2] — 2025

### Added
- Crystal-level diff in archive entries: detects changes to formula fields, parameter
  fields, and database table locations between the Original and Changed `.rpt` files.
- `RecordSelectionFormula` diff included in the Crystal analysis section of the HTML report.
- Registry detection of the SAP Crystal Reports .NET runtime; startup notice with a direct
  link to the SAP download page when the runtime is absent.
- `IsRuntimeAvailable` check shown at startup with a friendly warning when the runtime is
  installed but cannot be loaded in-process.

### Changed
- Binary diff section now shows SHA-256 hash, file size, different-byte count, and
  difference percentage.
- Archive HTML report uses a dark-themed stylesheet (matching the application palette).

---

## [1.1.0] — 2025

### Added
- **Revert to Last Change** button — restores the original `.rpt` from the most recent
  archive entry (supports both `.zip` archives and plain version folders).
- Changelog HTML file (`changelog.html`) appended in the archive root on every push,
  giving a running history of all versions with diff summaries.
- Zip compression of version folders after archiving to keep the archive root tidy.
- Archive root auto-derived from the original report directory (`<dir>\archive`) when the
  field is left blank.
- `Open Archive Folder` button opens the archive root in Windows Explorer.

### Changed
- Version folder naming changed to `v<yyyyMMdd-HHmmss>` UTC timestamps for unambiguous
  chronological sorting.

---

## [1.0.0] — 2025

### Added
- Initial release of Crystal Report Versioner.
- **Push Changes** workflow: copies the Original and Changed `.rpt` files into a
  timestamped version folder, generates an HTML diff report, and writes it alongside
  the `.rpt` copies.
- Binary-level diff (byte-by-byte comparison) with SHA-256 hashes and size comparison.
- Browse buttons for Original, Changed, and Archive Root paths.
- Optional free-text message field stamped into each version entry.
- Dark-themed WPF UI (Segoe UI, dark palette `#0D1B22`).
- Application version displayed in the status bar.
- Window icon loaded from `resources/icon.ico` at runtime.
