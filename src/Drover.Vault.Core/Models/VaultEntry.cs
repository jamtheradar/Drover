namespace Drover.Vault.Core.Models;

// Plain-data DTOs returned by VaultIo / VaultStore. Kept in the Core
// library so the future Drover.VaultMcp.exe (spec 0005) returns the
// same shapes without re-declaring them.
public sealed record VaultEntry(
    string RelativePath,
    System.DateTime LastModifiedUtc,
    long Bytes,
    string? Summary);

public sealed record VaultSearchHit(
    string RelativePath,
    int LineNumber,
    string Snippet);

public enum VaultScope
{
    Project,
    Global,
}
