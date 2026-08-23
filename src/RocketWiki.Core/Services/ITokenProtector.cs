namespace RocketWiki.Core.Services;

/// <summary>
/// Encrypts/decrypts a stored secret at rest. Exists so RocketWiki.Data's
/// credential service can protect tokens without a dependency on ASP.NET Data
/// Protection — the API layer supplies the real implementation
/// (DataProtectionTokenProtector); tests supply their own.
/// </summary>
public interface ITokenProtector
{
    string Protect(string plaintext);

    /// <summary>
    /// Null when the payload cannot be decrypted (key ring lost or rotated away,
    /// tampered row). Callers treat that as "no usable credential" — fail closed
    /// into the degraded/no-credential path, never an exception that breaks the
    /// page; the user re-enters their token.
    /// </summary>
    string? Unprotect(string protectedPayload);
}
