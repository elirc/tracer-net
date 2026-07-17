using System.Security.Cryptography;
using System.Text;

namespace Tracer.Domain;

/// <summary>
/// Minting and hashing of API tokens.
///
/// <para>
/// <b>Why SHA-256 and not bcrypt/argon2.</b> Slow password hashes exist to make
/// guessing a *human-chosen* secret expensive: passwords carry maybe 30 bits of
/// entropy, so an attacker with the hashes brute-forces the keyspace and the only
/// defence is making each guess cost something. A token from <see cref="Mint"/>
/// carries 192 bits from a CSPRNG. There is no keyspace to search, so the work
/// factor buys nothing — while costing an expensive hash on *every authenticated
/// request*, which is a real denial-of-service lever.
/// </para>
/// <para>
/// What does matter for a high-entropy secret is that verification is a single
/// deterministic hash, so the token can be found by an indexed lookup rather than
/// by loading every key in the table and comparing one at a time.
/// </para>
/// </summary>
public static class ApiKeyToken
{
    /// <summary>Marks a token as this product's, so a leaked one is recognisable in logs and scanners.</summary>
    public const string Scheme = "trk_";

    /// <summary>Characters of the raw token stored in the clear to identify a key in a list.</summary>
    public const int PrefixLength = 12;

    private const int EntropyBytes = 24; // 192 bits

    /// <summary>A fresh token. The caller must show this to the user once and then forget it.</summary>
    public static string Mint() =>
        Scheme + Convert.ToHexString(RandomNumberGenerator.GetBytes(EntropyBytes)).ToLowerInvariant();

    /// <summary>Stable hash of a raw token, used both to store a new key and to look up a presented one.</summary>
    public static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    /// <summary>The identifiable leading fragment of a raw token, e.g. <c>trk_a1b2c3d4</c>.</summary>
    public static string PrefixOf(string rawToken) =>
        rawToken.Length <= PrefixLength ? rawToken : rawToken[..PrefixLength];
}
