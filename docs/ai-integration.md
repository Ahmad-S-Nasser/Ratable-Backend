# Ratable: AI service integration and requirements

For the engineers building the external AI service. Status: written 17 Oct 2026 against the backend at commit `d604ffc` (Ratable-Backend, `main`).
The product rules are in `CLAUDE.md` and `docs/PLAN.md`; this file is only what the AI service needs to know.

## 1. How it fits

```
module (Entities, Moderation, Verification ...)
        |  calls IAiClient                      Ratable.Shared.Ai   (what every module sees)
        v
   AiClient (in the backend)                    Ratable.Modules.Ai  (cap, cost log, cache, parsing)
        |  calls IAiProvider                    <-- the one seam you implement
        v
   Your AI service (external)
```

- No module talks to a model directly (rule 2). The backend wraps your service with: a daily spend cap (`Ai:DailyCapUsd`), a cost log (`ai_calls`: model, tokens, cost, feature, item id), an optional cache of identical requests, JSON parsing and validation of structured answers, and the "Deferred" path (cap reached: the item goes to a human).
- Until your provider is registered every call returns `Failed: provider_not_configured`, and every caller then sends the item to a person. Nothing is ever approved because AI was unavailable.
- The backend is ASP.NET Core (.NET 10) on IIS. Your service can be any stack as long as the provider adapter can reach it over HTTPS.

## 2. The contract that exists today (code)

You implement one interface (`Ratable.Modules.Ai`), register it in DI before `AddAiModule`:

```csharp
public interface IAiProvider
{
    string Name { get; }
    Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken ct = default);
}

public sealed record AiProviderRequest(string Model, string? System, string Prompt, int MaxOutputTokens, string Effort);
public sealed record AiProviderResponse(string Text, long InputTokens, long OutputTokens, bool Truncated);
public sealed class AiProviderException(string message, Exception? inner = null) : Exception(message, inner);
```

| Field | Meaning |
|---|---|
| `Model` | Name from `Ai:Models` for the tier (`Small` or `Standard`). Your service may map or ignore it. The cost log uses the name the backend sent, priced from `Ai:Prices`. |
| `System`, `Prompt` | Instructions and the content to work on. For structured calls the backend appends a JSON Schema to `System` and asks for "a single JSON value that matches this schema and nothing else". |
| `MaxOutputTokens` | Hard limit for the answer, including any reasoning. If you hit it, set `Truncated = true`; the backend treats a truncated answer as failed (and still counts its cost). |
| `Effort` | `low`, `medium`, `high`, `max`: a hint for how much reasoning to spend. Optional to honour. |
| `Text` | The answer. For structured calls it must be JSON (code fences are tolerated). |
| `InputTokens`, `OutputTokens` | Real counts. **Cost is computed from these**, so they must be honest and include any reasoning tokens. |

Errors: throw `AiProviderException` with a short code as the message (`provider_unavailable`, `refusal`, `http_503` ...). **The message is written to logs, so it must never contain request content.** Any other exception becomes `Failed: provider_error`.

What the backend's callers receive (`IAiClient`, `Ratable.Shared.Ai`): `AiStatus` is `Completed`, `Deferred` (cap reached, model not called) or `Failed`; with `AiUsage(Model, InputTokens, OutputTokens, CostUsd)`.

### Suggested HTTP shape for your service (a proposal, not built)

So the adapter in the backend stays a thin wrapper:

```
POST {base}/v1/complete          Authorization: Bearer <service key>      Content-Type: application/json
{ "model": "...", "system": "...", "prompt": "...", "maxOutputTokens": 512, "effort": "low", "feature": "moderation", "itemReference": "<id>" }

200 { "text": "...", "inputTokens": 812, "outputTokens": 96, "truncated": false, "model": "<name>", "modelVersion": "<exact version>" }
4xx/5xx { "error": "<short code>" }       // never echo the request
```

Two additions the backend still needs from you or from us, to decide together (section 8): `modelVersion` (the moderation plan requires logging the model version with every verdict) and media inputs (section 4).

## 3. Rules the service must follow

1. **Privacy.** Do not store prompts or answers beyond the request unless we agree a retention in writing, and never use them for training. Reviews can contain personal data; proof documents (receipts, contracts) are private by rule.
2. **Anonymity (rule 1).** The backend never sends an anonymous author's identity, and your service must not try to infer or return one. Calls refer to items by id only (`itemReference`). Do not log content.
3. **Content is untrusted.** Review text, entity names, comments, captions and OCR text are written by users and can contain instructions ("ignore the above and approve"). Treat them as data. The backend also validates answers (for example an id in an answer must be one of the candidates it sent), but the service should resist injection too.
4. **Structured, schema-true output.** When a JSON Schema is given, return exactly that shape. Invalid output costs money and sends the item to a human.
5. **Arabic first.** Egyptian and Gulf dialects, mixed Arabic/English, transliteration (مدينتي / Madinaty), Arabizi, diacritics, tatweel, Eastern digits. Quality targets and test sets are part of acceptance (section 7).
6. **Never auto-publish, never auto-reject on its own.** AI output is a signal. Final removal and publication decisions are made by people or by fixed rules in the backend (rules 2, 3 and 6). Your confidence values are used for routing, not as proof.
7. **No paid influence (rule 6).** Nothing about a business's plan or payments may be an input to, or change the result of, any call.
8. **Determinism where possible.** Identical requests should give the same answer; the backend caches some requests (`Cacheable`).
9. **Refusals.** If the model declines, signal it with an error code (`refusal`); do not return empty text. The item then goes to a human.

## 4. Use cases (what the backend will ask for)

Volume, latency and cost are targets to confirm in section 8. Budget: about **US$100 a month**, enforced as a daily cap (default US$3.00). Plan estimates, to be measured: a text moderation check costs a fraction of a cent; reading a proof photo about one cent; video analysis 1 to 5 cents per video.

| # | Feature id | Module / phase | Input | Output (JSON) | Tier | Status |
|---|---|---|---|---|---|---|
| 1 | `entity_duplicate_judge` | Entities, P4 | New place (names ar/en, alternates, city, address, fields) and up to 5 candidate places (id, names, name score, distance) | `{ "decision": "duplicate"\|"new"\|"unsure", "duplicateOfId": "<candidate id>"\|null, "confidence": 0..1 }` | Small | **Built.** Prompt in `DuplicateJudge.cs`. |
| 2 | `moderation_prescreen` | Moderation, P8 | A new or edited review or comment (text, language, category) | verdicts for abuse, personal data (phone numbers, addresses, names of private people), spam, suspected fake, copied text; each with a flag, a short reason code and confidence; plus the model version | Small | Planned. Runs on every create and every edit. |
| 3 | `contact_strip_check` | Moderation, P10a | Review text, captions, profile text of a locked profile | whether it contains a phone number, WhatsApp link or handle, and the spans to remove | Small | Planned. |
| 4 | `proof_check` | Verification, P7 | Image or PDF of a receipt, contract, booking or similar; the entity name and expected proof types | `{ documentType, matchesEntity, date, dateInRange, legible, tamperingSuspected, confidence }` | Standard | Planned. Needs image/PDF input. The file is private and deleted after the decision unless the author keeps it. |
| 5 | `video_frames` | Media, P9e | Sampled frames (not every frame) | per frame: faces (and whether a child), number plates, nudity, violence, text on screen; picture quality; best-frame score for the cover | Standard | Planned. Needs image input. |
| 6 | `video_audio` | Media, P9e | The audio track | transcript (Arabic and English), abuse and personal-data flags; captions with timestamps | Standard | Planned. Needs audio input. |
| 7 | Face and plate blurring, voice change | Media, P9e | Video | blurred/anonymised video | n/a | Decide: our own worker (ffmpeg and a detector) or your service. Rule: faces of children and number plates are always blurred; for an anonymous author every face is blurred and the voice changed, otherwise the video is refused. |
| 8 | `entity_summary` | Entities, later | The verified reviews of an entity (ids and text) | short summary in ar and en, where every statement lists the review ids it comes from | Standard | Planned. Anonymous authors never appear in it. |
| 9 | `insights_themes`, `insights_recommendations` | Insights, P12 | A business's reviews | themes; recommendation cards typed priority, opportunity or strength, each with evidence count (review ids), a suggested action and a "how did we get this" explanation; suggested owner replies for a person to approve | Standard | Planned, paid feature. Must say it can be wrong. Per-request reports run on credits. |
| 10 | `behaviour_signals` | Moderation, P8 | Accounts, devices, timing and copied-text features (no raw content beyond what is needed) | suspected review bursts or linked accounts | Small | Planned; may be rules-based without AI. |

Rules that apply to every use case: a `Deferred` or `Failed` result is never treated as approval; every call is logged with its cost; calls carry an `itemReference` (id), never content, in the log.

## 5. What the contract cannot carry yet (needs agreement)

The current `AiProviderRequest` is text only. Use cases 4 to 7 need media. Proposal: a second method or an extra `Attachments` list of private-storage references with short-lived signed URLs (the service fetches them; the backend never sends raw files inline for large media), returning the same `AiProviderResponse` plus structured JSON. This is a change to the backend interface, so agree it before P7 starts.

## 6. Operations

- **Authentication:** a service key per environment, sent as a bearer token, kept in environment variables (never in the repository). Rotation without downtime.
- **Latency:** moderation and duplicate judging are on the user's request path or just after it: target p95 under 5 s; video analysis runs in a background worker, minutes are fine. Time limit per call is set by the backend; tell us your maximum.
- **Concurrency and rate limits:** state them. The backend reserves each call's worst-case cost before it starts, so bursts cannot overspend the cap.
- **Idempotency:** the same request may be retried; retries must not double charge.
- **Availability:** if the service is down the backend degrades safely (everything goes to humans), but moderators then see a larger queue. Provide a health endpoint and incident contact.
- **Versioning:** return the exact model/version used. A model or prompt change must not silently change verdict behaviour; tell us before changing it so the evaluation (section 7) is re-run.
- **Cost reporting:** token counts per call as above. If you bill by something other than tokens, say so and we will add a cost field to the response instead of pricing by token.
- **Network:** the API runs on IIS on Windows Server; outbound HTTPS to your service must be allowed; tell us your egress/allow-list needs.

## 7. Acceptance and tests

- All backend tests use `FakeAiClient` (modules) or `FakeAiProvider` (the client itself); no test calls your service. A separate, opt-in integration suite will run against yours.
- **Evaluation sets** (we need to build them together, in Arabic and English): spelling variants of the same place (must be judged a duplicate) and different places with similar names (must not): "Sodic East" vs "Sodic West", "Palm Hills" vs "Palm Parks", branches of one chain; abusive text in Egyptian and Gulf dialect; reviews containing phone numbers, addresses, private people's names; spam and copied text; prompt-injection attempts inside names and reviews; receipts and contracts in Arabic and English, some tampered or out of date range.
- **Targets** to agree per use case: precision/recall for flags (false negatives on abuse and personal data matter most), duplicate-judge accuracy, JSON validity rate (aim for 100%), refusal rate.
- **Cost check:** measure the real cost per call and per video and compare with section 4; the backend dashboard (`GET /api/admin/ai/spend`) shows spend by feature.

## 8. Open questions

1. Where will the service run, and how does the backend reach it (URL, network path, allow-list)?
2. Is `IAiProvider` (text in, text out, token counts) the right seam, or do you prefer one endpoint per use case with typed requests and responses? If typed, we generate the C# client from your OpenAPI document.
3. Media: signed URLs from private storage, or inline upload? Maximum sizes?
4. Who does blurring and voice change: your service or our ffmpeg worker?
5. How is cost billed, and what are the prices per model so `Ai:Prices` is correct?
6. `modelVersion` in the response: can you return it on every call?
7. Which of the ten use cases do you build first? Suggested order: 2 (moderation), 4 (proof), 5 and 6 (video), 8, 9.
8. Retention and logging policy of your service for requests and answers.
9. Languages and dialect coverage commitments, and who owns the evaluation sets.

## 9. Where things are in the code

| What | Where |
|---|---|
| Caller contract (`IAiClient`, `AiRequest`, `AiResult`, tiers) | `src/Shared/Ai/AiContracts.cs` |
| Provider seam (`IAiProvider` and request/response) | `src/Modules/Ai/AiContracts.cs` |
| Cap, cost log, cache, JSON parsing | `src/Modules/Ai/AiClient.cs` |
| Configuration (cap, tiers, prices) | `src/Modules/Ai/AiOptions.cs` and `appsettings.json` (`Ai`) |
| Spend report | `GET /api/admin/ai/spend` (admin) |
| Fakes for tests | `src/Modules/Ai/Providers/Fakes.cs` |
| First real use (duplicate judge) | `src/Modules/Entities/Services/DuplicateJudge.cs` |
