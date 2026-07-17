using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tracer.Domain;

/// <summary>
/// Signs outgoing payloads so a receiver can tell an event from this product
/// apart from anything else that found the URL.
///
/// <para>
/// <b>The timestamp is signed with the body, not sent beside it.</b> Signing the
/// body alone produces a signature that stays valid forever: anyone who captures
/// one request can replay it, unchanged and perfectly signed, for as long as the
/// secret lives — "issue closed" delivered again next year, still verifying. So
/// the signed material is <c>{timestamp}.{payload}</c>, and the timestamp travels
/// inside the signed header. A receiver rejects anything older than its tolerance
/// and replay stops working; if the timestamp were unsigned, an attacker would
/// simply rewrite it.
/// </para>
/// <para>
/// The scheme is Stripe's, deliberately. Webhook verification is written by
/// people who are not thinking about webhooks, and a familiar
/// <c>t=…,v1=…</c> header is one they may already have code for. <c>v1</c> is a
/// version tag, so the algorithm can be replaced later by sending both.
/// </para>
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Tracer-Signature";

    /// <summary>Builds the <c>t=…,v1=…</c> header value.</summary>
    public static string Header(string secret, DateTimeOffset timestamp, string payload)
    {
        var unix = timestamp.ToUnixTimeSeconds();
        return $"t={unix},v1={Compute(secret, unix, payload)}";
    }

    /// <summary>The v1 signature: HMAC-SHA256 over <c>{unixSeconds}.{payload}</c>, hex encoded.</summary>
    public static string Compute(string secret, long unixSeconds, string payload)
    {
        var signed = $"{unixSeconds.ToString(CultureInfo.InvariantCulture)}.{payload}";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies a header the way a receiver should. Not used to send anything —
    /// it exists so the documented contract is executable rather than prose, and
    /// so the tests check the signature the same way a consumer would.
    /// </summary>
    /// <param name="tolerance">
    /// How old a signature may be. This is the entire replay defence: with no
    /// bound, a captured request is valid forever.
    /// </param>
    public static bool Verify(
        string secret,
        string header,
        string payload,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (!TryParse(header, out var unixSeconds, out var provided))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (age > tolerance || age < -tolerance)
        {
            return false;
        }

        var expected = Compute(secret, unixSeconds, payload);

        // Constant-time: a plain string comparison returns as soon as two bytes
        // differ, so how long it takes leaks how much of a guess was right, one
        // character at a time.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }

    private static bool TryParse(string header, out long unixSeconds, out string signature)
    {
        unixSeconds = 0;
        signature = string.Empty;

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var (key, value) = (part[..separator], part[(separator + 1)..]);
            switch (key)
            {
                case "t" when long.TryParse(value, CultureInfo.InvariantCulture, out var parsed):
                    unixSeconds = parsed;
                    break;
                case "v1":
                    signature = value;
                    break;
            }
        }

        return unixSeconds > 0 && signature.Length > 0;
    }
}
