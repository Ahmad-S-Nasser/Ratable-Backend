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

## Moderation and admin (P8)
- Every new or edited review and comment is handed to Moderation (`IModerationIntake`). It runs the behaviour signals (same text again, a burst of reviews for one place, several accounts on one device or network) and the AI pre-screen (abuse, personal data, spam, suspected fake, identifying details for an anonymous author), then `RoutingPolicy` decides who reads it first: anything flagged, an AI that is capped or down, and every review of an individual (`Category.RatesIndividuals`) go to a moderator; otherwise a review with an accepted proof is published, and one without proof is published only for an account with a verified phone. Verification never changes a score. A held review whose only reason was "no proof" is published when a proof is accepted.
- Moderator API (Moderator or Admin): `GET /api/moderation/overview`, `/cases` (+ `POST /cases/{id}/publish|hide`; hiding needs a reason the author sees), `/reports` (+ resolve), `/disputes` (+ uphold or dismiss); entities and proofs have their own queues (`/api/moderation/entities`, `/api/moderation/proofs`). Moderators see content and reasons, never who wrote it.
- Disputes: a business owner disputes a public review; it stays visible with an "under review" label unless a moderator upholds the dispute (hidden, with a reason).
- Admin API: `GET /api/admin/audit` (filter, page), `GET /api/admin/users`, `POST /api/admin/users/{id}/suspend|unsuspend` (a reason is needed, sessions end at once, logged), the audited anonymous-author look-up.
- Sign-in records hashes of the network and the `X-Device-Id` header (apps send it) for the shared-device signal. A background worker queues any pending item that has no case.

## Hosting on IIS
See `docs/iis.md`.

## Photos and video (P9e)
The Media module takes photos and videos for a review. The author asks for a signed upload address, sends the file in pieces (it can resume after a break), and a background worker processes it:
- Checks: the real file type from its first bytes, one video (up to 60 seconds and 100 MB) and up to 4 photos (10 MB each) per review.
- Photos: location, device, time and author data are stripped without re-encoding.
- Videos (ffmpeg): sample pictures and the sound go through `IAiClient` (`video_frames`, `video_audio`); children's faces and number plates are always blurred; for an anonymous author every face is blurred and the voice is changed, otherwise the video is refused; metadata is dropped; an adaptive HLS stream (720p and 480p), cover candidates and Arabic and English captions (stored as text) are produced; the original is deleted.
- A person approves everything. A video the AI could not check is retried later and cannot be approved without it. Nothing is public before approval, and a video that was not protected for an anonymous author is never shown for an anonymous review.
- Settings (environment): `Media__StoragePath` (private folder), `Media__SigningKey` (32 random bytes, base64), `Media__FfmpegPath` and `Media__FfprobePath` (ffmpeg must be installed on the server; without it videos end as "processor unavailable" and photos still work), `Media__WorkerEnabled`.
- The AI contract for the three features is in `docs/ai-integration.md`.
