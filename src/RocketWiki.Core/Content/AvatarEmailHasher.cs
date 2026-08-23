using System.Security.Cryptography;
using System.Text;

namespace RocketWiki.Core.Content;

/// <summary>
/// The Gravatar-protocol email normalization and digests, in one place so the upload
/// path and the JIT email-drift refresh can never disagree on what a hash means.
/// Normalization is the protocol's: trim, then lowercase (invariant). Both digests are
/// computed because consumers disagree — historical Gravatar is MD5, the current spec
/// is SHA-256, Libravatar accepts both — and stored as lowercase hex, which is also
/// the form incoming <c>/avatar/{hash}</c> segments are folded to before comparison,
/// making the case-insensitive match an exact ordinal one.
///
/// MD5 here is an identifier scheme mandated by the protocol being implemented, not a
/// security control — nothing verifies integrity or secrecy with it. (The inherent
/// disclosure is the protocol's, stated in the design: anyone who can reach the
/// endpoint and knows an email can compute its hash and probe for an avatar.)
/// </summary>
public static class AvatarEmailHasher
{
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Both digests of the normalized form, or (null, null) for a null/empty
    /// email — a hash of an empty string would be a real, probeable value that means
    /// nobody, which is exactly the wrong direction to fail.</summary>
    public static (string? Md5Hex, string? Sha256Hex) Compute(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return (null, null);
        }

        var bytes = Encoding.UTF8.GetBytes(Normalize(email));
        return (Convert.ToHexStringLower(MD5.HashData(bytes)),
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
