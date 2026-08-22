using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.GraphQL;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// Enforces design.md §8: "the SDL is exported to schema.graphql... and a CI
/// check fails if code and file drift." Builds the schema straight from
/// <see cref="GraphQLConfiguration.AddRocketWikiGraphQL"/> — the exact same
/// call Program.cs makes — so this test and the running API can never quietly
/// diverge from each other, only (which is the point) from the checked-in file.
/// </summary>
public class SchemaExportTests
{
    private static async Task<string> BuildCurrentSchemaSdlAsync()
    {
        var services = new ServiceCollection();
        IRequestExecutorBuilder executorBuilder = services.AddGraphQLServer().AddRocketWikiGraphQL();
        var schema = await executorBuilder.BuildSchemaAsync();
        return schema.ToString();
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Trim();

    [Fact]
    public async Task CheckedInSchema_MatchesCodeGeneratedSchema()
    {
        var repoRoot = RepoRoot.Find();
        var schemaPath = Path.Combine(repoRoot, "schema.graphql");

        Assert.True(File.Exists(schemaPath),
            $"schema.graphql not found at '{schemaPath}'. Generate it with:\n" +
            "  cd src/RocketWiki.Api && dotnet run schema export --output ../../schema.graphql");

        var checkedIn = NormalizeLineEndings(await File.ReadAllTextAsync(schemaPath));
        var current = NormalizeLineEndings(await BuildCurrentSchemaSdlAsync());

        if (checkedIn == current)
        {
            return;
        }

        var checkedInLines = checkedIn.Split('\n');
        var currentLines = current.Split('\n');
        var onlyInFile = checkedInLines.Except(currentLines).ToArray();
        var onlyInCode = currentLines.Except(checkedInLines).ToArray();

        var message = new System.Text.StringBuilder();
        message.AppendLine("schema.graphql is out of sync with the code-first GraphQL schema (design.md §8).");
        message.AppendLine();
        message.AppendLine("Regenerate it with:");
        message.AppendLine("  cd src/RocketWiki.Api && dotnet run schema export --output ../../schema.graphql");
        message.AppendLine();

        if (onlyInFile.Length > 0)
        {
            message.AppendLine("Lines only in the checked-in schema.graphql (removed/renamed in code):");
            foreach (var line in onlyInFile)
            {
                message.AppendLine($"  - {line}");
            }

            message.AppendLine();
        }

        if (onlyInCode.Length > 0)
        {
            message.AppendLine("Lines only in the code-generated schema (missing from schema.graphql):");
            foreach (var line in onlyInCode)
            {
                message.AppendLine($"  + {line}");
            }

            message.AppendLine();
        }

        message.AppendLine("--- full code-generated schema ---");
        message.AppendLine(current);

        Assert.Fail(message.ToString());
    }
}
