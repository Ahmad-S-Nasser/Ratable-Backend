# Coon.Payment

Provider-neutral payment checkout and webhook verification.

It knows how to start a hosted payment and how to prove that a notification really came from
the provider. It knows nothing about subscriptions, plans, add-ons, invoices or tax — those
belong to whatever system is using it, which is what makes it reusable rather than a copy-paste
of one product's billing code.

## Reusing it in another system

Reference the project directly:

```xml
<ProjectReference Include="..\..\Coon.Payment\Coon.Payment.csproj" />
```

or `dotnet pack` it and consume the package. It targets `net8.0` and depends only on the
Microsoft.Extensions abstractions for options, DI and logging — no ASP.NET Core, no database,
so a worker service or a console tool can use it as-is.

## Wiring

```csharp
builder.Services.AddPayments(builder.Configuration);
```

```jsonc
{
  "Payments": {
    "Provider": "kashier",        // "" or "none" registers the null object
    "Kashier": {
      "MerchantId": "MID-....",
      "PaymentApiKey": "...",     // NEVER in appsettings.json
      "WebhookSecret": "",        // falls back to PaymentApiKey
      "Mode": "test"              // "live" to take real money
    }
  }
}
```

With no provider configured, `IPaymentGateway` still resolves — to `UnconfiguredPaymentGateway`,
which reports `IsConfigured == false`, throws on checkout, and **rejects** every verification.
The host stays startable and can fall back to whatever manual path it has; what it cannot do is
mistake "no provider" for "payment succeeded".

## Using it

```csharp
if (!gateway.IsConfigured) { /* offer your fallback */ }

var session = await gateway.CreateCheckoutAsync(new CheckoutRequest
{
    OrderId = order.Id,          // YOUR reference; persist it BEFORE redirecting
    Amount = order.Amount,
    Currency = "EGP",
    SuccessUrl = "...",
    WebhookUrl = "https://api.example.com/api/payments/webhook/kashier",
});

return Redirect(session.RedirectUrl);
```

Then, in the webhook endpoint:

```csharp
var notification = gateway.ReadNotification(headers, rawBody);
if (!notification.IsAuthentic) return BadRequest();
```

## Five rules the caller must follow

The library cannot enforce these from the outside, and each one is a real way to lose money.

1. **Fulfil on the webhook, not the redirect.** The redirect is missing entirely when the payer
   closes the tab, and a URL is trivially forged. `ReadRedirect` is for showing a result page.
2. **Verify over the raw body.** Deserialize *after* verifying. A body that has been through a
   JSON round-trip no longer has the bytes that were signed.
3. **Check the amount yourself.** A valid signature proves the message is genuine, not that it
   is for the amount you raised. Compare against your stored order, currency included.
4. **Be idempotent.** Gateways retry. Close your order conditionally — a filtered update that
   only matches an open order — so two concurrent deliveries cannot both provision.
5. **Answer 2xx to anything authentic, including a repeat.** A non-2xx makes the provider retry
   forever over something you already handled.

## Kashier: verify the signing formats before going live

`Kashier/KashierSignature.cs` holds the only two byte sequences that are signed:

- checkout hash — HMAC-SHA256 of `/?payment={mid}.{order}.{amount}.{currency}`
- notification signature — HMAC-SHA256 of the payload's own `signatureKeys`, **sorted
  alphabetically**, joined as `k=v&k=v` with each value URL-encoded

Both were checked directly against Kashier's published documentation
(developers.kashier.io/payment/webhook/ for the notification signature; the hosted-checkout
guide for the hash). The checkout hash matched a published worked example byte-for-byte. The
notification signature did not at first: an earlier version of this library signed the fields in
payload order, unencoded, on the assumption that order was itself part of the signed message -
Kashier's docs say the opposite ("order only elements of the signatureKeys array... alphabetically
... generate signature payload by URL-encoding the selected key-value pairs"), so every real
webhook would have failed verification. That has been fixed.

Documentation can still lag an API, and **a wrong byte fails closed and identically to a wrong
key** - so even with both formats now matching what Kashier publishes, **run one payment in
`Mode: "test"` and confirm the webhook verifies before switching to `live`.** That is the only
check that fully proves it against your actual merchant account.

## Tests

```
dotnet run --project ..\Coon.Payment.Tests
```

45 assertions, no framework required, non-zero exit on failure. They cover URL construction, an
independently recomputed HMAC, invariant amount formatting under a comma-decimal locale, replay
with an edited amount, and the rule that no unrecognised status is ever read as `Paid`.

They do **not** prove the two Kashier formats above are right — the tests sign with the same
formats they verify, so they would pass even if both were wrong.
