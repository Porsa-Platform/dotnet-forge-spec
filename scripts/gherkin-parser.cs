#!/usr/bin/dotnet-run
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property PublishAot=false

// gherkin-parser — converts a Gherkin feature file into the canonical JSON IR.
//
// Usage:
//   dotnet run scripts/gherkin-parser.cs -- <feature-file> <json-output>
//
// Exit codes:
//   0  parse succeeded and JSON IR was written
//   1  input/output/parsing error
//   2  command-line usage error

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: gherkin-parser <feature-file> <json-output>");
    Console.Error.WriteLine("Exit codes: 0=success, 1=error, 2=usage error");
    return 2;
}

var featureFilePath = args[0];
var jsonOutputPath  = args[1];

try
{
    if (!File.Exists(featureFilePath))
        throw new IOException($"Feature file not found: {featureFilePath}");

    var feature = GherkinParser.Parse(featureFilePath);

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    var json = JsonSerializer.Serialize(feature, jsonOptions);

    var outputDir = Path.GetDirectoryName(jsonOutputPath);
    if (!string.IsNullOrEmpty(outputDir))
        Directory.CreateDirectory(outputDir);

    File.WriteAllText(jsonOutputPath, json);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------------------
// Parser
// ---------------------------------------------------------------------------

static class GherkinParser
{
    private static readonly Regex ParameterRegex =
        new(@"<([A-Za-z_][A-Za-z0-9_]*)>", RegexOptions.Compiled);

    private static readonly string[] StepKeywords =
        ["Given", "When", "Then", "And", "But"];

    public static FeatureIR Parse(string filePath)
    {
        var lines = File.ReadAllLines(filePath);

        string? featureName = null;
        var background   = new List<StepIR>();
        var scenarios    = new List<ScenarioIR>();
        ScenarioIR? currentScenario = null;

        var state = ParserState.Initial;
        List<string>? exampleHeaders = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Skip blank lines and comment lines
            if (line.Length == 0 || line[0] == '#')
                continue;

            // Feature declaration
            if (line.StartsWith("Feature:"))
            {
                featureName = line["Feature:".Length..].Trim();
                state = ParserState.Feature;
                continue;
            }

            // Require Feature: before anything else
            if (featureName is null)
                continue;

            // Background section
            if (line == "Background:" || line.StartsWith("Background: "))
            {
                state = ParserState.Background;
                currentScenario = null;
                exampleHeaders  = null;
                continue;
            }

            // Scenario or Scenario Outline
            if (line.StartsWith("Scenario Outline:") || line.StartsWith("Scenario:"))
            {
                var scenarioName = line.StartsWith("Scenario Outline:")
                    ? line["Scenario Outline:".Length..].Trim()
                    : line["Scenario:".Length..].Trim();

                currentScenario = new ScenarioIR { Name = scenarioName };
                scenarios.Add(currentScenario);
                state = ParserState.Scenario;
                exampleHeaders = null;
                continue;
            }

            // Examples keyword (only valid inside a scenario)
            if (line == "Examples:" || line.StartsWith("Examples:"))
            {
                if (state != ParserState.Scenario)
                    throw new FormatException("Examples found outside a scenario");
                state = ParserState.Examples;
                exampleHeaders = null;
                continue;
            }

            // Table rows
            if (line.StartsWith('|'))
            {
                if (state != ParserState.Examples)
                    continue; // ignore stray table rows

                var cells = SplitTableRow(line);

                if (exampleHeaders is null)
                {
                    exampleHeaders = cells;
                }
                else
                {
                    if (cells.Count != exampleHeaders.Count)
                        throw new FormatException(
                            $"Example data row has {cells.Count} cells but header has {exampleHeaders.Count}");

                    var row = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < exampleHeaders.Count; i++)
                        row[exampleHeaders[i]] = cells[i];

                    currentScenario!.Examples.Add(row);
                }
                continue;
            }

            // Step lines
            var step = TryParseStep(line);
            if (step is not null)
            {
                switch (state)
                {
                    case ParserState.Background:
                        background.Add(step);
                        break;
                    case ParserState.Scenario:
                        if (currentScenario is null)
                            throw new FormatException($"Step '{line}' found outside a background or scenario");
                        currentScenario.Steps.Add(step);
                        break;
                    default:
                        throw new FormatException($"Step '{line}' found outside a background or scenario");
                }
            }
        }

        if (featureName is null)
            throw new FormatException("Feature declaration not found");

        return new FeatureIR
        {
            Name       = featureName,
            Background = background,
            Scenarios  = scenarios
        };
    }

    private static StepIR? TryParseStep(string line)
    {
        foreach (var kw in StepKeywords)
        {
            if (line.StartsWith(kw + " "))
            {
                var text       = line[(kw.Length + 1)..].Trim();
                var parameters = ParameterRegex.Matches(text)
                    .Select(m => m.Groups[1].Value)
                    .ToList();
                return new StepIR { Keyword = kw, Text = text, Parameters = parameters };
            }
        }
        return null;
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|'))  trimmed = trimmed[..^1];
        return [..trimmed.Split('|').Select(c => c.Trim())];
    }
}

// ---------------------------------------------------------------------------
// Data model
// ---------------------------------------------------------------------------

enum ParserState { Initial, Feature, Background, Scenario, Examples }

class FeatureIR
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("background")]
    public List<StepIR> Background { get; set; } = [];

    [JsonPropertyName("scenarios")]
    public List<ScenarioIR> Scenarios { get; set; } = [];
}

class ScenarioIR
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("steps")]
    public List<StepIR> Steps { get; set; } = [];

    [JsonPropertyName("examples")]
    public List<Dictionary<string, string>> Examples { get; set; } = [];
}

class StepIR
{
    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("parameters")]
    public List<string> Parameters { get; set; } = [];
}
