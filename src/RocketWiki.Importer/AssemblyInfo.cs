using System.Runtime.CompilerServices;

// Lets the test project exercise internal implementation details directly (Slugifier,
// EntityGraph) where a black-box test through the public surface would be indirect or
// awkward, without making those types part of this library's public API.
[assembly: InternalsVisibleTo("RocketWiki.Importer.Tests")]
