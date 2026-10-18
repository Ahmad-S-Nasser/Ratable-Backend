# Seed importer

Loads places for the anchor categories from a file you provide. It normalizes names, matches them against what already exists, and marks what it creates as seeded.

## How to run it
Admin only. Dry run first (the default), read the report, then run for real.

- Web: console, Import page (admin).
- API: `POST /api/admin/import/entities?dryRun=true&format=csv|json&batch=<name>` with the file text as the body. `dryRun=false` writes. `GET /api/admin/import/sample?format=csv|json` returns a sample file.
- A dry run does all the work except writing, so its report is exactly what the real run will do. Every real run is written to the audit log.
- Limits: `Entities:ImportMaxBytes` (8 MB) and `Entities:ImportMaxRows` (20,000).

## File format
CSV (comma or semicolon, one header row, quotes around text that holds a comma) or a JSON array of objects. Column names ignore case, spaces, dashes and underscores (`name_ar`, `nameAr` and `Name AR` are the same).

| Column | Meaning |
| --- | --- |
| `category` | Required. The id of an active category, for example `compounds`. |
| `name_ar`, `name_en` | At least one is required. |
| `alt_names` | Other names, separated by `|` (up to 5). |
| `address`, `city`, `country` | Optional text. |
| `lat`, `lng` | Required. Decimal degrees. |
| `google_place_id` | Optional. The same id is the same place, whatever the name. |
| `phone`, `whatsapp`, `website` | Optional contact details. In a category that gates contact they are stored as contact and stay hidden unless the owner's plan includes contact. Elsewhere the website is a "same as" link. |
| `external_ids` | Optional, `key:value|key:value`, for example `wikidata:Q123`. The same pair is the same place. |
| `fields` | Optional, `key:value|key:value`, the category's own fields (for example `cuisine:Street food`). Unknown keys skip the row. |

## What happens to a row
- **Created**: no similar place. It becomes an active, seeded place with slugs.
- **Merged**: same Google place id or external id, or a name score at or above `Entities:ImportMergeThreshold` (0.9) inside `Entities:DuplicateRadiusMeters` (5 km). Only blanks are filled (names, ids, address, city, contact). Nothing that is already there is overwritten, and an owner's own contact is never replaced.
- **Review**: similar but not certain. It is created as a pending place with its candidates, and a moderator decides in the new-places queue.
- **Skipped**: invalid row (`invalid_category`, `invalid_name`, `invalid_location`, `invalid_field:<key>`). The reason is in the report.

Running the same file again changes nothing (every row merges, unchanged). Chains are safe: the same name far away is its own place. No AI is used.
