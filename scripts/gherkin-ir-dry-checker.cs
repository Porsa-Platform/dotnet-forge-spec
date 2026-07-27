#!/usr/bin/dotnet-run
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property PublishAot=false

// gherkin-ir-dry-checker — reads a parser-produced JSON IR and reports
// repeated, near-duplicate, and possible-synonym step text so agents can
// normalise and prune Gherkin feature files.
//
// Usage:
//   dotnet scripts/gherkin-ir-dry-checker.cs -- [--include-exact] <json-ir> <report-output>
//
// Exit codes:
//   0  report generation succeeded
//   1  input/output/report generation error
//   2  wrong command usage

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// ---------------------------------------------------------------------------
// Argument parsing
// ---------------------------------------------------------------------------

bool includeExact = false;
string? jsonIrPath     = null;
string? reportPath = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--include-exact")
        includeExact = true;
    else if (jsonIrPath is null)
        jsonIrPath = args[i];
    else if (reportPath is null)
        reportPath = args[i];
    else
    {
        Console.Error.WriteLine("Usage: gherkin-ir-dry-checker [--include-exact] <json-ir> <report-output>");
        return 2;
    }
}

if (jsonIrPath is null || reportPath is null)
{
    Console.Error.WriteLine("Usage: gherkin-ir-dry-checker [--include-exact] <json-ir> <report-output>");
    return 2;
}

try
{
    if (!File.Exists(jsonIrPath))
        throw new IOException($"JSON IR file not found: {jsonIrPath}");

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    var feature = JsonSerializer.Deserialize<FeatureIR>(
        File.ReadAllText(jsonIrPath), jsonOptions)
        ?? throw new InvalidDataException("Failed to deserialise JSON IR");

    var report = DryChecker.Analyse(feature, includeExact);

    var outputOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    var reportJson = JsonSerializer.Serialize(report, outputOptions);

    var dir = Path.GetDirectoryName(reportPath);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(reportPath, reportJson);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------------------
// DRY Checker
// ---------------------------------------------------------------------------

static class DryChecker
{
    private static readonly Regex PlaceholderRegex =
        new(@"<([A-Za-z_][A-Za-z0-9_]*)>", RegexOptions.Compiled);

    // Common English function words excluded from Jaccard comparison
    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "shall",
        "should", "may", "might", "must", "can", "could",
        "to", "of", "in", "on", "at", "by", "for", "with", "from",
        "and", "but", "or", "nor", "not", "no", "if", "as", "that", "which",
        "who", "whom", "this", "these", "those", "its", "it"
    };

    public static DryReport Analyse(FeatureIR feature, bool includeExact)
    {
        // Build a flat list of (location, step) for all steps
        var allSteps = new List<(StepLocation Location, StepIR Step)>();

        if (feature.Background is { Count: > 0 })
        {
            for (int si = 0; si < feature.Background.Count; si++)
            {
                allSteps.Add((
                    new StepLocation
                    {
                        Section       = "background",
                        ScenarioIndex = null,
                        ScenarioName  = null,
                        StepIndex     = si,
                        Keyword       = feature.Background[si].Keyword
                    },
                    feature.Background[si]));
            }
        }

        for (int sci = 0; sci < feature.Scenarios.Count; sci++)
        {
            var sc = feature.Scenarios[sci];
            for (int si = 0; si < sc.Steps.Count; si++)
            {
                allSteps.Add((
                    new StepLocation
                    {
                        Section       = "scenario",
                        ScenarioIndex = sci,
                        ScenarioName  = sc.Name,
                        StepIndex     = si,
                        Keyword       = sc.Steps[si].Keyword
                    },
                    sc.Steps[si]));
            }
        }

        var findings = new List<DryFinding>();

        // 1. duplicate-in-scenario (same text within one background or scenario)
        FindDuplicatesInSection("background", null, null, feature.Background, findings);
        for (int sci = 0; sci < feature.Scenarios.Count; sci++)
        {
            var sc = feature.Scenarios[sci];
            FindDuplicatesInSection("scenario", sci, sc.Name, sc.Steps, findings);
        }

        // 2. exact-duplicate across the whole IR (only with --include-exact)
        if (includeExact)
            FindExactDuplicatesAcrossIR(allSteps, findings);

        // 3. placeholder-variant (same after slot-normalisation)
        FindPlaceholderVariants(allSteps, findings);

        // 4. near-duplicate and possible-synonym (Jaccard similarity)
        FindSimilarSteps(allSteps, findings);

        int totalOccurrences = allSteps.Count;
        var uniqueTexts      = allSteps.Select(s => s.Step.Text).Distinct().Count();

        return new DryReport
        {
            SchemaVersion = 1,
            FeatureName   = feature.Name,
            Summary = new DryReportSummary
            {
                StepOccurrences = totalOccurrences,
                UniqueSteps     = uniqueTexts,
                Findings        = findings.Count
            },
            Findings = findings
        };
    }

    // ------------------------------------------------------------------
    // Duplicate-in-scenario
    // ------------------------------------------------------------------

    private static void FindDuplicatesInSection(
        string section,
        int? scenarioIndex,
        string? scenarioName,
        List<StepIR> steps,
        List<DryFinding> findings)
    {
        var seen = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < steps.Count; i++)
        {
            var text = steps[i].Text;
            if (!seen.TryGetValue(text, out var indices))
                seen[text] = indices = [];
            indices.Add(i);
        }

        foreach (var (text, indices) in seen)
        {
            if (indices.Count < 2) continue;

            var members = indices.Select(i => new DryFindingMember
            {
                Text      = text,
                Locations = [new StepLocation
                {
                    Section       = section,
                    ScenarioIndex = scenarioIndex,
                    ScenarioName  = scenarioName,
                    StepIndex     = i,
                    Keyword       = steps[i].Keyword
                }]
            }).ToList();

            findings.Add(new DryFinding
            {
                Kind              = "duplicate-in-scenario",
                Confidence        = "high",
                CanonicalCandidate = text,
                PatternCandidate  = $"^{Regex.Escape(text)}$",
                Members           = members,
                Reason            = "step text appears more than once in the same background or scenario",
                SuggestedAction   = "Remove the duplicate step or consolidate into a single step."
            });
        }
    }

    // ------------------------------------------------------------------
    // Exact-duplicate across IR
    // ------------------------------------------------------------------

    private static void FindExactDuplicatesAcrossIR(
        List<(StepLocation Location, StepIR Step)> allSteps,
        List<DryFinding> findings)
    {
        var grouped = allSteps
            .GroupBy(s => s.Step.Text, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        foreach (var group in grouped)
        {
            // Skip if already reported as duplicate-in-scenario
            var locs = group.Select(s => s.Location).ToList();
            var sections = locs.Select(l => $"{l.Section}:{l.ScenarioIndex}").Distinct().ToList();
            if (sections.Count == 1 && locs.Count > 1)
                continue; // already captured by duplicate-in-scenario

            findings.Add(new DryFinding
            {
                Kind              = "exact-duplicate",
                Confidence        = "high",
                CanonicalCandidate = group.Key,
                PatternCandidate  = $"^{Regex.Escape(group.Key)}$",
                Members           = [new DryFindingMember { Text = group.Key, Locations = locs }],
                Reason            = "step text appears more than once across the IR",
                SuggestedAction   = "Review whether these steps represent the same intent and normalise if so."
            });
        }
    }

    // ------------------------------------------------------------------
    // Placeholder-variant
    // ------------------------------------------------------------------

    private static string NormalisePlaceholders(string text)
    {
        int slot = 0;
        return PlaceholderRegex.Replace(text, _ => $"<_{++slot}>");
    }

    private static void FindPlaceholderVariants(
        List<(StepLocation Location, StepIR Step)> allSteps,
        List<DryFinding> findings)
    {
        var byNormalised = allSteps
            .GroupBy(s => NormalisePlaceholders(s.Step.Text), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.DistinctBy(x => x.Step.Text, StringComparer.Ordinal).Count() > 1);

        foreach (var group in byNormalised)
        {
            var canonical = group.Key; // normalised form

            var members = group
                .GroupBy(s => s.Step.Text, StringComparer.Ordinal)
                .Select(textGroup => new DryFindingMember
                {
                    Text      = textGroup.Key,
                    Locations = textGroup.Select(s => s.Location).ToList()
                })
                .ToList();

            findings.Add(new DryFinding
            {
                Kind              = "placeholder-variant",
                Confidence        = "high",
                CanonicalCandidate = canonical,
                PatternCandidate  = PlaceholderRegex.Replace(
                    Regex.Escape(members[0].Text),
                    @"([A-Za-z0-9_]+)"),
                Members           = members,
                Reason            = "step text is identical after replacing placeholder names with generic slots",
                SuggestedAction   = "Review the feature wording and normalise the Gherkin if the different forms do not add meaning."
            });
        }
    }

    // ------------------------------------------------------------------
    // Near-duplicate and possible-synonym (Jaccard)
    // ------------------------------------------------------------------

    private static readonly Regex TokenRegex = new(@"[a-z0-9]+", RegexOptions.Compiled);

    private static HashSet<string> Tokenise(string text)
    {
        var normalised = NormalisePlaceholders(text)
            .ToLowerInvariant();
        // Remove placeholder slots for token comparison
        normalised = Regex.Replace(normalised, @"<_\d+>", " ");
        return [..TokenRegex.Matches(normalised)
            .Select(m => m.Value)
            .Where(t => !FunctionWords.Contains(t))];
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        var intersection = a.Count(t => b.Contains(t));
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static void FindSimilarSteps(
        List<(StepLocation Location, StepIR Step)> allSteps,
        List<DryFinding> findings)
    {
        // Deduplicate to unique texts for comparison
        var unique = allSteps
            .GroupBy(s => s.Step.Text, StringComparer.Ordinal)
            .Select(g => (
                Text      : g.Key,
                Tokens    : Tokenise(g.Key),
                Locations : g.Select(s => s.Location).ToList()))
            .ToList();

        var alreadyReported = new HashSet<(string, string)>();

        for (int i = 0; i < unique.Count; i++)
        {
            for (int j = i + 1; j < unique.Count; j++)
            {
                var a = unique[i];
                var b = unique[j];

                // Skip exact duplicates (handled elsewhere)
                if (a.Text.Equals(b.Text, StringComparison.Ordinal)) continue;

                // Skip placeholder-variants (handled elsewhere)
                if (NormalisePlaceholders(a.Text).Equals(
                    NormalisePlaceholders(b.Text), StringComparison.OrdinalIgnoreCase))
                    continue;

                var pairKey = (a.Text.CompareTo(b.Text) < 0)
                    ? (a.Text, b.Text)
                    : (b.Text, a.Text);
                if (!alreadyReported.Add(pairKey)) continue;

                var score = Jaccard(a.Tokens, b.Tokens);

                string? kind       = null;
                string? confidence = null;
                string? reason     = null;
                string? suggestion = null;

                if (score >= 0.72)
                {
                    kind       = "near-duplicate";
                    confidence = "medium";
                    reason     = $"step texts have high token similarity (Jaccard {score:F2})";
                    suggestion = "Inspect whether these steps are semantically equivalent and normalise the Gherkin if so.";
                }
                else if (score >= 0.45)
                {
                    kind       = "possible-synonym";
                    confidence = "low";
                    reason     = $"step texts have moderate token similarity (Jaccard {score:F2})";
                    suggestion = "Review whether these steps overlap in meaning and simplify if appropriate.";
                }

                if (kind is null) continue;

                findings.Add(new DryFinding
                {
                    Kind              = kind,
                    Confidence        = confidence!,
                    CanonicalCandidate = a.Text,
                    Score             = Math.Round(score, 4),
                    Members =
                    [
                        new DryFindingMember { Text = a.Text, Locations = a.Locations },
                        new DryFindingMember { Text = b.Text, Locations = b.Locations }
                    ],
                    Reason          = reason!,
                    SuggestedAction = suggestion!
                });
            }
        }
    }
}

// ---------------------------------------------------------------------------
// JSON IR model
// ---------------------------------------------------------------------------

class FeatureIR
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("background")]
    public List<StepIR>? Background { get; set; }

    [JsonPropertyName("scenarios")]
    public List<ScenarioIR> Scenarios { get; set; } = [];
}

class ScenarioIR
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("steps")]
    public List<StepIR> Steps { get; set; } = [];
}

class StepIR
{
    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Report model
// ---------------------------------------------------------------------------

class DryReport
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("feature_name")]
    public string FeatureName { get; set; } = "";

    [JsonPropertyName("summary")]
    public DryReportSummary Summary { get; set; } = new();

    [JsonPropertyName("findings")]
    public List<DryFinding> Findings { get; set; } = [];
}

class DryReportSummary
{
    [JsonPropertyName("step_occurrences")]
    public int StepOccurrences { get; set; }

    [JsonPropertyName("unique_steps")]
    public int UniqueSteps { get; set; }

    [JsonPropertyName("findings")]
    public int Findings { get; set; }
}

class DryFinding
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "";

    [JsonPropertyName("canonical_candidate")]
    public string CanonicalCandidate { get; set; } = "";

    [JsonPropertyName("pattern_candidate")]
    public string PatternCandidate { get; set; } = "";

    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("members")]
    public List<DryFindingMember> Members { get; set; } = [];

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("suggested_action")]
    public string SuggestedAction { get; set; } = "";
}

class DryFindingMember
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("locations")]
    public List<StepLocation> Locations { get; set; } = [];
}

class StepLocation
{
    [JsonPropertyName("section")]
    public string Section { get; set; } = "";

    [JsonPropertyName("scenario_index")]
    public int? ScenarioIndex { get; set; }

    [JsonPropertyName("scenario_name")]
    public string? ScenarioName { get; set; }

    [JsonPropertyName("step_index")]
    public int StepIndex { get; set; }

    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = "";
}
