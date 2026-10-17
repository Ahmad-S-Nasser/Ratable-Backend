// Verification for Coon.Payment. Run it:  dotnet run --project Coon.Payment.Tests
//
// WHAT THIS DOES AND DOES NOT PROVE
//
// It proves everything that does not depend on Kashier being reachable: that the checkout URL
// carries the right fields, that the hash is an HMAC-SHA256 of the documented path (recomputed
// here independently rather than by calling the library back), that amounts survive a
// comma-decimal locale, that a tampered amount with a replayed signature is rejected, and that
// no unrecognised status is ever read as Paid.
//
// It does NOT prove the two signing FORMATS in KashierSignature match the merchant account.
// That cannot be known without real credentials - even now that both formats have been checked
// directly against Kashier's published documentation (the checkout hash matched a worked example
// byte-for-byte; the notification signature did not at first - see KashierSignature.cs's remarks
// and the fix applied there), documentation can still lag the live API, and this file would still
// pass if a format were subtly wrong in a way its own docs don't reveal, because it signs with
// the same formats it verifies.
//
// The fastest real check: run one payment in TEST mode and confirm the webhook verifies.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Coon.Payment;
using Coon.Payment.Kashier;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : "  -> " + detail)}"); }
}

const string Mid = "MID-1234-567";
const string Key = "super-secret-payment-api-key";

var options = new KashierOptions
{
    MerchantId = Mid,
    PaymentApiKey = Key,
    Mode = "test",
    DefaultCurrency = "EGP",
};

var gateway = new KashierGateway(Options.Create(options), NullLogger<KashierGateway>.Instance);

Console.WriteLine("\n=== configuration ===");
Check("configured when creds present", gateway.IsConfigured);
Check("provider name is stable", gateway.Name == "kashier");

var bare = new KashierGateway(Options.Create(new KashierOptions()), NullLogger<KashierGateway>.Instance);
Check("not configured when creds absent", !bare.IsConfigured);

Console.WriteLine("\n=== amount formatting ===");
Check("2dp invariant", KashierSignature.FormatAmount(12.5m) == "12.50",
    KashierSignature.FormatAmount(12.5m));
Check("integer gets 2dp", KashierSignature.FormatAmount(300m) == "300.00");

// The bug this guards: on a comma-decimal locale the default formatter yields "12,50", which
// changes the signed bytes and fails every payment on the server but not on a dev machine.
var previous = CultureInfo.CurrentCulture;
CultureInfo.CurrentCulture = new CultureInfo("de-DE");
Check("survives a comma-decimal locale", KashierSignature.FormatAmount(12.5m) == "12.50",
    KashierSignature.FormatAmount(12.5m));
CultureInfo.CurrentCulture = previous;

Console.WriteLine("\n=== checkout url ===");
var session = await gateway.CreateCheckoutAsync(new CheckoutRequest
{
    OrderId = "order-abc-1",
    Amount = 300m,
    Currency = "EGP",
    Description = "Extra seats",
    CustomerEmail = "buyer@example.com",
    SuccessUrl = "https://app.example.com/billing/done",
    FailureUrl = "https://app.example.com/billing/failed",
    WebhookUrl = "https://api.example.com/api/payments/webhook/kashier",
    Metadata = new Dictionary<string, string> { ["orgId"] = "org-9", ["addOnId"] = "extra_seats" },
});

var uri = new Uri(session.RedirectUrl);
var q = HttpUtility.ParseQueryString(uri.Query);

Check("redirect is absolute https", uri.Scheme == "https");
Check("carries merchantId", q["merchantId"] == Mid);
Check("carries orderId", q["orderId"] == "order-abc-1");
Check("amount on wire is 2dp", q["amount"] == "300.00", q["amount"]);
Check("currency uppercased", q["currency"] == "EGP");
Check("mode is test", q["mode"] == "test");
Check("success url survives encoding", q["merchantRedirect"] == "https://app.example.com/billing/done");
Check("webhook url survives encoding", q["serverWebhook"] == "https://api.example.com/api/payments/webhook/kashier");
Check("session echoes order id", session.OrderId == "order-abc-1");

// Independently recompute the hash rather than calling the library's own helper, so this
// actually checks the signed string rather than agreeing with itself.
static string Hmac(string message, string key)
{
    using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
    return Convert.ToHexString(mac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
}

var expectedHash = Hmac($"/?payment={Mid}.order-abc-1.300.00.EGP", Key);
Check("hash matches independent HMAC of the documented path", q["hash"] == expectedHash,
    $"got {q["hash"]}, expected {expectedHash}");

var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(q["metaData"] ?? "{}");
Check("metadata round-trips", meta is not null && meta["orgId"] == "org-9");

Console.WriteLine("\n=== checkout refuses bad input ===");
try { await gateway.CreateCheckoutAsync(new CheckoutRequest { OrderId = "x", Amount = 0m, Currency = "EGP" }); Check("zero amount rejected", false); }
catch (ArgumentException) { Check("zero amount rejected", true); }

try { await gateway.CreateCheckoutAsync(new CheckoutRequest { OrderId = "x", Amount = -5m, Currency = "EGP" }); Check("negative amount rejected", false); }
catch (ArgumentException) { Check("negative amount rejected", true); }

try { await gateway.CreateCheckoutAsync(new CheckoutRequest { OrderId = " ", Amount = 10m, Currency = "EGP" }); Check("blank order id rejected", false); }
catch (ArgumentException) { Check("blank order id rejected", true); }

try { await bare.CreateCheckoutAsync(new CheckoutRequest { OrderId = "x", Amount = 10m, Currency = "EGP" }); Check("unconfigured refuses checkout", false); }
catch (PaymentConfigurationException) { Check("unconfigured refuses checkout", true); }

Console.WriteLine("\n=== webhook verification ===");

// Build a payload the way the provider does, and sign it the way the provider documents:
// signatureKeys sorted alphabetically, each value URL-encoded, joined as k=v&k=v.
// developers.kashier.io/payment/webhook/.
var data = new Dictionary<string, object>
{
    ["merchantOrderId"] = "order-abc-1",
    ["transactionId"] = "TX-99",
    ["status"] = "SUCCESS",
    ["amount"] = "300.00",
    ["currency"] = "EGP",
};
var signedKeys = new[] { "merchantOrderId", "amount", "currency", "status" };

string SignPayload(Dictionary<string, object> d, string[] keys, string secret) =>
    Hmac(string.Join("&", keys
        .OrderBy(k => k, StringComparer.Ordinal)
        .Select(k => $"{k}={Uri.EscapeDataString(d[k]?.ToString() ?? string.Empty)}")), secret);

string BodyFor(Dictionary<string, object> d, string[] keys) =>
    JsonSerializer.Serialize(new { data = d, signatureKeys = keys });

var body = BodyFor(data, signedKeys);
var goodSig = SignPayload(data, signedKeys, Key);

var ok = gateway.ReadNotification(
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = goodSig },
    body);

Check("valid signature accepted", ok.IsAuthentic, ok.Rejection);
Check("order id extracted", ok.OrderId == "order-abc-1", ok.OrderId);
Check("status mapped to Paid", ok.Status == PaymentStatus.Paid);
Check("amount parsed", ok.Amount == 300.00m, ok.Amount?.ToString());
Check("provider reference extracted", ok.ProviderReference == "TX-99");
Check("raw payload retained for audit", ok.Raw.ContainsKey("currency"));

var bad = gateway.ReadNotification(
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = Hmac("nonsense", Key) },
    body);
Check("wrong signature rejected", !bad.IsAuthentic);

// Kashier's docs say signatureKeys must be sorted alphabetically before signing - this is the
// one case the rest of this file can't catch, since it signs the same way it verifies. Feeding
// the keys out of order here proves the implementation actually sorts them, rather than trusting
// (or requiring) the order the array happens to arrive in.
var scrambledKeys = new[] { "status", "merchantOrderId", "currency", "amount" };
var scrambledSig = SignPayload(data, scrambledKeys, Key);
Check("scrambled-order signatureKeys still produces the sorted-order signature",
    scrambledSig == goodSig, $"got {scrambledSig}, expected {goodSig}");

var scrambledResult = gateway.ReadNotification(
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = scrambledSig },
    BodyFor(data, scrambledKeys));
Check("gateway verifies a webhook whose signatureKeys arrived out of alphabetical order",
    scrambledResult.IsAuthentic, scrambledResult.Rejection);

// A value containing '&' or a space would corrupt the k=v&k=v join itself if left unencoded -
// URL-encoding is what keeps the delimiter unambiguous.
var trickyData = new Dictionary<string, object>(data) { ["status"] = "SUCCESS & CONFIRMED" };
var trickyResult = gateway.ReadNotification(
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = SignPayload(trickyData, signedKeys, Key) },
    BodyFor(trickyData, signedKeys));
Check("a value containing '&' is URL-encoded before signing", trickyResult.IsAuthentic, trickyResult.Rejection);

var wrongKey = gateway.ReadNotification(
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = SignPayload(data, signedKeys, "attacker-key") },
    body);
Check("signature from a different key rejected", !wrongKey.IsAuthentic);

// Tamper with the amount but keep the original signature: the classic replay-with-edit.
var tampered = new Dictionary<string, object>(data) { ["amount"] = "1.00" };
var tamperedResult = gateway.ReadNotification(
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = goodSig },
    BodyFor(tampered, signedKeys));
Check("edited amount rejected", !tamperedResult.IsAuthentic);

Check("missing header rejected",
    !gateway.ReadNotification(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), body).IsAuthentic);
Check("empty body rejected",
    !gateway.ReadNotification(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = goodSig }, "").IsAuthentic);
Check("non-json body rejected",
    !gateway.ReadNotification(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = goodSig }, "not json").IsAuthentic);

var noKeys = JsonSerializer.Serialize(new { data });
Check("payload without signatureKeys rejected",
    !gateway.ReadNotification(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = goodSig }, noKeys).IsAuthentic);

Check("unconfigured verifier rejects rather than accepts",
    !bare.ReadNotification(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = goodSig }, body).IsAuthentic);

Console.WriteLine("\n=== unknown status never grants ===");
foreach (var status in new[] { "SOMETHING_NEW", "", "pending", "FAILED", "REFUNDED" })
{
    var d = new Dictionary<string, object>(data) { ["status"] = status };
    var r = gateway.ReadNotification(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-kashier-signature"] = SignPayload(d, signedKeys, Key) },
        BodyFor(d, signedKeys));

    Check($"status '{status}' is not Paid", r.IsAuthentic && r.Status != PaymentStatus.Paid, r.Status.ToString());
}

Console.WriteLine("\n=== constant-time compare ===");
Check("equal matches", KashierSignature.Matches("abcd", "abcd"));
Check("case-insensitive hex", KashierSignature.Matches("ABCD", "abcd"));
Check("different lengths do not match", !KashierSignature.Matches("abcd", "abcde"));
Check("null does not match", !KashierSignature.Matches(null, "abcd"));
Check("empty does not match", !KashierSignature.Matches("", ""));

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
