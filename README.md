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

## Rewards, places near you, verdicts and lists (P9f and P9g)
- Points ledger (Contributions module): append-only, one payment per (user, action, reference), cancelled together when a review is hidden or deleted, reversible by an admin with a reason. Ranks, badges, missions, point values and daily caps are in the `Rewards` configuration section. Points are an input to nothing: tests prove a score, a verdict and the order of places do not change.
- Verdict (`Reviews:Verdicts`): worked out from verified reviews only, beside the score. Lists are configuration (`src/Modules/Reviews/Lists/*.json`, loaded once into `collection_lists`).
- Location: `ILocationProvider` backed by seeded areas (`src/Modules/Entities/Areas/areas.json`, 2dsphere index). `GET /api/places?lat&lng&radiusKm&area&category` accepts 1, 3, 5, 10 or 25 km. The visitor location can travel in the `X-Visitor-Location: lat,lng` header; it is used for the answer only and is never stored or logged.

## Verify via WhatsApp and social sign-in (P9h)
- Settings: `Identity__WhatsAppNumber`, `Identity__WhatsAppAppSecret`, `Identity__WhatsAppVerifyToken`, optional `Identity__GoogleClientId`, `Identity__AppleClientId`, `Identity__BlockedPhonePrefixes__0..`.
- WhatsApp Cloud API webhook: `GET/POST /api/webhooks/whatsapp` (signed with the app secret; unsigned calls are refused). Flow: `POST /api/me/phone/verify/start` (default reverse route: code shown once, `wa.me` link with the code typed in), the person sends the message, the webhook matches the code and the sender's number, `GET /api/me/phone/verify/status`. The code route (`route: "code"`) needs a provider behind `IVerificationCodeSender`.
- Verification sets only `PhoneVerified`. It never changes a score.

## Business side (P10)
- Businesses module: `POST /api/entities/{id}/claims` (verified phone and a company email, rate limited), `PUT /api/me/claims/{id}/document?type=` (raw PDF, JPG or PNG up to `Businesses:MaxDocumentBytes`, judged by its first bytes), `GET /api/me/claims`. Moderators: `GET /api/moderation/claims`, `GET .../{id}/document` (audit entry first), `POST .../approve`, `POST .../reject {reason}`, `POST /api/moderation/ownerships/{entityId}/revoke {reason}`.
- The claim document lives in private storage (`IPrivateFileStore`, the same encrypted store as proof files), is deleted after the decision and only its hash is kept.
- Approval makes the account the single owner of the place (unique index) and gives it the BusinessOwner role. Ownership unlocks owner replies (`/api/reviews/{id}/owner-reply`, one per review), the dashboard (`/api/business/places`, `/api/business/places/{id}/dashboard`, `.../seen`) and disputes for that place only. It never changes a score, a ranking or a removal.
- A business account cannot review the place it runs (`own_entity`, 403).
- The dashboard numbers come from the same published and Verified reviews as the public score (`IBusinessReviewSource`, implemented by Reviews). A reviewer is shown only through `IAuthorPresenter`, so an anonymous review has no author there.
- Plans are configuration (`Businesses:Plans`); `GET /api/plans` feeds the pricing page. Payment is not part of P10.
- Owner replies are moderated like reviews: every reply and every edit gets a moderation case (kind OwnerReply), is published by the rules when the pre-screen is clean and waits for a moderator otherwise. Hidden or pending replies are never public.
- Company email: a claim sends a one-time 6-digit code to the company address (single use, 10 minutes, kept hashed, 5 tries, resend after 60 seconds). The document can be uploaded only after the email is confirmed. Mail comes from `Email:From` (support@coon.one for now); set `Email__Host`, `Email__Port`, `Email__User`, `Email__Password` in the environment. For development and tests `Email:OutboxPath` writes mail to a folder instead.
- `GET /api/plans` returns the plans with monthly prices per currency (EGP, SAR, AED), the currency list and the annual discount, all from `Businesses:Plans`.

## Contact plan and Request contact (P10a)
- Category flag `contactGated` (off by default). The seeded category `service-providers` turns it on, together with `ratesIndividuals`.
- A profile is locked when its category gates contact and the owner's plan has no `contact` feature (`IContactAccess`, implemented by Businesses). Plans live in `Businesses:Plans`; a subscription is activated by an audited admin grant (`PUT /api/admin/entities/{id}/subscription`) until payments exist (P14, webhook only).
- Public entity and search responses (`PublicEntityBuilder`) never carry phone, WhatsApp or links of a gated category; links are never in them either. For a locked profile the address, fields and summary are stripped of contact details, and so are review text, comments, media captions and the owner reply (`ContactStripper`, at the moment the response is built).
- `POST /api/entities/{id}/contact/reveal` (Show contact): subscribed or open profiles only, rate limited (20 an hour per network, 300 an hour per place), counted per day for the owner's stats. `POST /api/entities/{id}/views` counts page views (once an hour per visitor, robots ignored). Counters are per place and day; there is no visitor in them.
- Request contact: `POST /api/entities/{id}/contact-requests`, `GET /api/me/contact-requests`; provider side `GET /api/business/places/{id}/contact-requests` (counts only without the plan) and `.../{requestId}/reply` (plan required). Limits and life are in `Businesses` options (7 days, 5 a reader a day, 30 a provider a day). A reader's WhatsApp number is read from the account only when the provider with the plan opens a request that chose to share it.
- Brief stats `GET .../stats` (plan only, no AI) and upgrade messages `GET .../nudges` (`IBusinessNudgeService`: top 3, views, waiting requests, closed requests; real counts from the same stats, none for zero, one per trigger per week, dismissible). The words are in the web translation files.
