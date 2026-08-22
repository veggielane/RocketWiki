namespace RocketWiki.Importer.Tests;

/// <summary>Shared setup: a converter over a fresh fake resolver, and a default page context.</summary>
public abstract class ConverterTestBase
{
    protected readonly FakePageIdResolver Resolver = new();

    protected ConfluenceStorageConverter CreateConverter() => new(Resolver);

    protected static readonly ConfluencePageContext DefaultContext =
        new(SpaceKey: "ENG", PageTitle: "Test Page", ContentId: "12345");

    protected ConversionResult Convert(string storageXhtml, ConfluencePageContext? context = null) =>
        CreateConverter().Convert(storageXhtml, context ?? DefaultContext);
}
