using Microsoft.CodeAnalysis;

namespace Reverb.Analysis.Tests;

public class SourceGeneratorTests
{
    internal const string Setup = "using Reverb;\nusing System.Text.Json;\nusing System.Text.Json.Serialization;\nusing System.Text.RegularExpressions;";

    internal const string Types = """
        record Order(int Id);

        [JsonSerializable(typeof(Order))]
        partial class AppJson : JsonSerializerContext;

        static partial class Patterns
        {
            [GeneratedRegex(@"\d+")]
            public static partial Regex Digits();
        }
        """;

    [Fact]
    public void Framework_generators_are_found()
    {
        Assert.Contains(TestNotebooks.References.Generators, g => g.GetGeneratorType().Assembly.GetName().Name == "System.Text.Json.SourceGeneration");
        Assert.Contains(TestNotebooks.References.Generators, g => g.GetGeneratorType().Assembly.GetName().Name == "System.Text.RegularExpressions.Generator");
    }

    [Fact]
    public void Generated_members_resolve_without_errors()
    {
        var analysis = TestNotebooks.Analyze(
            ["var json = JsonSerializer.Serialize(new Order(1), AppJson.Default.Order);", "var n = Patterns.Digits().Count(\"a1b2\");"],
            Types, Setup);
        Assert.Empty(analysis.Diagnostics.Where(d => d.IsError));
    }

    [Fact]
    public void An_edit_keeps_running_the_generators()
    {
        string[] cells = ["var json = JsonSerializer.Serialize(new Order(1), AppJson.Default.Order);"];
        var analysis = TestNotebooks.Analyze(cells, Types, Setup);
        var edited = analysis.Update(Reverb.Format.NotebookDocument.Parse(Reverb.Testing.NotebookText.Build(
            ["var json = JsonSerializer.Serialize(new Order(2), AppJson.Default.Order);"], Types, Setup)));
        Assert.Empty(edited.Diagnostics.Where(d => d.IsError));
    }
}
