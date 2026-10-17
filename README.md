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

## Hosting on IIS
See `docs/iis.md`.
