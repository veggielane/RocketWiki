using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GitLab;

/// <summary>
/// <see cref="ITokenProtector"/> over ASP.NET Core Data Protection — the standard
/// at-rest encryption for app-held secrets, keyed by this instance's key ring.
/// The purpose string is versioned so a future format change can run both side by
/// side. Key-ring custody is a deployment concern (§15): in k3s the keys must be
/// persisted deliberately (a mounted volume or the DB key repository) or every
/// pod restart silently invalidates every stored token — degraded to
/// "no credential", not broken pages, but a support headache worth preventing.
/// </summary>
public sealed class DataProtectionTokenProtector : ITokenProtector
{
    private const string Purpose = "RocketWiki.GitLab.Token.v1";

    private readonly IDataProtector _protector;

    public DataProtectionTokenProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string protectedPayload)
    {
        try
        {
            return _protector.Unprotect(protectedPayload);
        }
        catch (CryptographicException)
        {
            // Lost/rotated key ring or a tampered row: fail closed into "no usable
            // credential" (the caller degrades to the placeholder) rather than
            // failing the page. Deliberately not logged with any payload detail.
            return null;
        }
    }
}
