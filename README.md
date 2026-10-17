# Ratable Backend

ASP.NET Core (.NET 10) modular monolith with MongoDB. Source of truth for rules and plan: `CLAUDE.md` and `docs/PLAN.md` in the workspace root.

## Structure
- `src/Api`: the HTTP API, the only place with business rules. Publishes OpenAPI at `/openapi/v1.json`.
- `src/Shared`: shared database layer and helpers.
- `src/Modules/<Name>`: Identity, Entities, Reviews, Comments, Verification, Moderation, Businesses, Insights, Contributions, Ai. Each exposes one `Add<Name>Module` method and talks to others only through interfaces.
- `tests/Api`: xUnit tests; tests that need MongoDB use a locally installed server named in RATABLE_TEST_MONGO (no Docker).
- `Coon.Payment`, `Coon.Payment.Tests`: payment library (Kashier), used from phase 2.

## Run locally
```
set Mongo__ConnectionString=mongodb://localhost:27017
dotnet run --project src/Api
```
- `GET /health` liveness, `GET /health/ready` includes MongoDB, `GET /openapi/v1.json` the API contract.

## Test
```
dotnet build
dotnet test tests/Api
dotnet run --project Coon.Payment.Tests
```

## Configuration
Environment variables only; no secrets in the repository. Nested keys use a double underscore: `Mongo__ConnectionString`, `Mongo__Database`. `appsettings.json` holds defaults only.

## Sign-in (P2)
Phone number + one-time code. `POST /api/auth/otp/request`, `POST /api/auth/otp/verify` (`client` is `web` for an httpOnly cookie, `app` for a bearer token), `POST /api/auth/logout`, `GET /api/me`, `PUT /api/me/profile`, `PUT /api/admin/users/{id}/roles` (Admin). Signing in never sets `PhoneVerified`; only the WhatsApp verifier (P9h) does.

Required environment variables (generate each with 32 random bytes, base64):
- `Identity__PhoneEncryptionKey`, `Identity__PhoneHashKey`
- `Identity__OtpSender=console` in development prints codes to the console; without it the sender refuses to send until a real provider is added.
- Optional: `Identity__BlockedPhonePrefixes__0=+1555`, `Identity__BootstrapAdminPhone` (the first admin; clear it once an admin exists).

## AI (P3)
All AI goes through `IAiClient` (rule 2). It logs every call (model, tokens, cost, feature, item id; never the prompt or the answer) to `ai_calls`, enforces `Ai:DailyCapUsd` per UTC day and returns `Deferred` when the cap is hit (the caller sends the item to the human queue and never publishes it). Calls also fail visibly (`Failed`) when no provider is set, the model has no price, the provider errors or the output is invalid.
- The AI service itself is external. Connect it by implementing `IAiProvider` (see `docs/ai-integration.md`); until then every call returns `Failed: provider_not_configured`.
- Tiers map to model names in `Ai:Models`; every model needs a price in `Ai:Prices` (US dollars per million tokens).
- `GET /api/admin/ai/spend` (Admin) shows spend today and this month, by feature.
- Tests use `FakeAiClient` (other modules) or `FakeAiProvider` (the client itself). No test calls a real model.

## Entities and Arabic matching (P4)
- `ArabicNormalizer` and `Slugger` (src/Shared/Text) are the only place text is normalized for matching, search and slugs; originals are never changed.
- Adding an entity: `POST /api/entities` (signed in). It is checked for duplicate candidates (normalized-name score of at least `Entities:CandidateThreshold`, within `Entities:DuplicateRadiusMeters`), the AI judge gives a suggestion, and it always waits in the moderator queue (`GET /api/moderation/entities`; `POST .../{id}/approve`, `/reject`, `/merge`). With the AI capped or failing, a person decides without a suggestion.
- Merging a duplicate into another entity is described on `EntityService.MergeAsync`: other modules move their data through `IEntityMergeParticipant`, a clash stops the merge, every step can be repeated, and the old id redirects (301) to the survivor.
- Public reads: `GET /api/entities/{id}` and `GET /api/entities/search?q=` (active entities only, nothing about the submitter).

## Reviews, history and scores (P5)
- `POST /api/entities/{id}/reviews`, `PUT /api/reviews/{id}`, `DELETE /api/reviews/{id}`, `GET /api/me/reviews`: one review per account per entity; at least one answer (a 1 to 5 tap, or a choice); text optional. A new or edited review is PendingModeration and Unverified until the Moderation and Verification work publishes or verifies it.
- Editing saves the version it replaces in `review_revisions` (dated); deleting hides the review but keeps its revisions; editing a deleted review restores it.
- Public: `GET /api/entities/{id}/reviews`, `GET /api/entities/{id}/score`, `GET /api/reviews/{id}/history`, `GET /api/categories[/{id}]`. Public review responses carry no author at all until the anonymity work (P6) adds one behind the leak test.
- Score rules (`ScoreCalculator`): only Published and Verified reviews count; a review's overall is the average of its answered criteria; the entity's overall is the average of those, every review weighing the same; a per-criterion score appears only after 5 answers.

## Anonymity and comments (P6)
- Every review and comment stores its real author and an anonymous flag. Every public response is built by `IAuthorPresenter` (src/Shared/Authors): an anonymous item gets no author and its author's profile is never looked up. An author who is "named" but has no public display name is shown as anonymous.
- `PUT /api/reviews/{id}/anonymity` and `PUT /api/comments/{id}/anonymity` switch the flag at any time. It is not a content change (no revision, no re-moderation), and because the flag lives on the review and never on a revision, one switch covers the current version and the whole history.
- Comments: `POST/GET /api/reviews/{id}/comments`, `PUT/DELETE /api/comments/{id}`, `GET /api/me/comments`. One level deep, start as PendingModeration, an edit sets an "edited" label and returns them to moderation, they never touch a score.
- `POST /api/reports` (review or comment; one report per reader per item; never hides anything by itself).
- `POST /api/admin/anonymous-author-lookups` (Admin only): needs a written reason, writes the audit log BEFORE revealing anything (if that fails nothing is revealed), and never returns a phone number.
- The leak test (`tests/Api/Anonymity/AnonymityLeakTests.cs`) scans every public endpoint, as a visitor and as a signed-in user, and the log, for the anonymous author's name, avatar, phone and account id. Add any new public endpoint to its `PublicUrls` list.

## Evidence and proof (P7)
- `POST /api/reviews/{id}/proof` (multipart: `file`, `proofType`, `declaration`, optional `retention=keep` with `keepConsent=true`) by the review's author. A clean proof (AI read it, name and date fine, file never seen before) is approved at once and the review becomes Verified; anything else, or an AI that is capped or down, goes to the moderator queue and the review stays Unverified. About 5 percent of approved proofs are also checked by a person.
- `GET /api/moderation/proofs`, `GET .../{id}/file` (every opening is audited), `POST .../{id}/approve|reject` (a rejection needs a reason the author sees). `GET /api/me/proofs`, `DELETE /api/me/proofs/{id}/file` (author deletes a kept file; the badge stands on the recorded result).
- Files are encrypted at rest in private storage (`Verification__StoragePath`, `Verification__StorageKey`), deleted right after the decision unless the author chose to keep them (separate consent, 12 months), deleted with the review, and removed hourly when due. Only the proof type, date, result and a one-way hash are kept for good.
- Presence proof (live photo and location at the place) is an interface only (`IPresenceProofService`); the mobile app fills it in.

## Hosting on IIS
See `docs/iis.md`.
