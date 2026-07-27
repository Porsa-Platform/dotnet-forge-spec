#!/usr/bin/dotnet-run
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property PublishAot=false

// gherkin-mutator — mutates Gherkin example values in the parser-defined JSON IR
// and runs generated acceptance test entry points to determine whether those
// tests detect the changed specification data.
//
// Usage:
//   dotnet scripts/gherkin-mutator.cs -- [options]
//
// Required:
//   --runner-worker <command>   persistent runner adapter command
//
// Options:
//   --feature <path>                    Gherkin feature file (default: features/a-feature.feature)
//   --work-dir <path>                   mutation work directory (default: build/acceptance-mutation)
//   --generated-dir <path>              generated test directory (default: <work-dir>/generated)
//   --workers <count>                   max mutation workers (default: 1)
//   --timeout <duration>                full run timeout (e.g. 60s, 5m)
//   --status-interval <duration>        periodic status interval (default: 30s, 0 to disable)
//   --level <full|hard|soft>            differential mutation level (default: hard)
//   --implementation-hash <hash>        override generated metadata hash
//   --json                              emit JSON report instead of text
//
// Exit codes:
//   0  all executed mutations were killed and no errors occurred
//   1  at least one mutation survived, or at least one mutation produced an error
//   2  command-line usage or option parsing error

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// =========================================================================
// CLI Entry Point
// =========================================================================

var argsList = new List<string>(args);
string? featurePath = null;
string workDir = "build/acceptance-mutation";
string? generatedDir = null;
int workers = 1;
string? timeoutText = null;
string statusIntervalText = "30s";
string level = "hard";
string? runnerWorkerText = null;
string? implementationHashOverride = null;
bool jsonReport = false;

for (int i = 0; i < argsList.Count; i++)
{
    switch (argsList[i])
    {
        case "--feature":
            featurePath = CliHelper.GetArgValue(argsList, ref i);
            break;
        case "--work-dir":
            workDir = CliHelper.GetArgValue(argsList, ref i);
            break;
        case "--generated-dir":
            generatedDir = CliHelper.GetArgValue(argsList, ref i);
            break;
        case "--workers":
            workers = int.Parse(CliHelper.GetArgValue(argsList, ref i));
            break;
        case "--timeout":
            timeoutText = CliHelper.GetArgValue(argsList, ref i);
            break;
        case "--status-interval":
            statusIntervalText = CliHelper.GetArgValue(argsList, ref i);
            break;
        case "--level":
            level = CliHelper.GetArgValue(argsList, ref i);
            break;
        case "--runner-worker":
            runnerWorkerText = CliHelper.GetArgValue(argsList, ref i);
            break;
        case "--implementation-hash":
            implementationHashOverride = CliHelper.GetArgValue(argsList, ref i);
            break;
        case "--json":
            jsonReport = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown option: {argsList[i]}");
            Console.Error.WriteLine(CliHelper.Usage());
            return 2;
    }
}

if (string.IsNullOrEmpty(runnerWorkerText))
{
    Console.Error.WriteLine("--runner-worker is required");
    Console.Error.WriteLine(CliHelper.Usage());
    return 2;
}

if (level != "full" && level != "hard" && level != "soft")
{
    Console.Error.WriteLine("--level must be full, hard, or soft");
    return 2;
}

featurePath ??= "features/a-feature.feature";

try
{
    if (!File.Exists(featurePath))
        throw new FileNotFoundException($"Feature file not found: {featurePath}");

    var feature = GherkinParser.Parse(featurePath);

    var effectiveGeneratedDir = generatedDir ?? Path.Combine(workDir, "generated");

    var implementationHash = GeneratedMetadataHelper.ResolveImplementationHash(
        effectiveGeneratedDir, featurePath, implementationHashOverride);

    var statusIntervalMs = CliHelper.ParseDurationMs(statusIntervalText);

    var runnerCommand = CliHelper.SplitCommand(runnerWorkerText);

    var report = MutationEngine.Run(new MutationConfig
    {
        Feature = feature,
        FeaturePath = featurePath,
        WorkDir = workDir,
        GeneratedDir = effectiveGeneratedDir,
        Workers = workers,
        Level = level,
        ImplementationHash = implementationHash,
        StatusIntervalMs = statusIntervalMs,
        RunnerCommand = runnerCommand,
        TimeoutText = timeoutText
    });

    var writeStamp = report.Summary.Survived == 0 && report.Summary.Errors == 0;
    ManifestManager.WriteMutationMetadata(featurePath, feature, report, implementationHash, level, writeStamp);

    if (jsonReport)
    {
        ReportWriter.WriteJsonReport(report);
    }
    else
    {
        ReportWriter.WriteTextReport(report);
    }

    return report.Summary.Survived > 0 || report.Summary.Errors > 0 ? 1 : 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

// =========================================================================
// Helpers
// =========================================================================

static class CliHelper
{
    public static string GetArgValue(List<string> args, ref int i)
    {
        if (i + 1 >= args.Count)
            throw new ArgumentException($"Option {args[i]} requires a value");
        return args[++i];
    }

    public static string Usage()
    {
        return "Usage: gherkin-mutator --runner-worker <command> [--feature <path>] [--work-dir <dir>] [--generated-dir <dir>] [--workers <n>] [--timeout <duration>] [--status-interval <duration>] [--level full|hard|soft] [--implementation-hash <hash>] [--json]";
    }

    public static long ParseDurationMs(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        text = text.Trim().ToLowerInvariant();
        if (text.EndsWith("ms"))
            return long.Parse(text[..^2]);
        if (text.EndsWith('s'))
            return long.Parse(text[..^1]) * 1000;
        if (text.EndsWith('m'))
            return long.Parse(text[..^1]) * 60000;
        return long.Parse(text);
    }

    public static string[] SplitCommand(string command)
    {
        var parts = new List<string>();
        bool inQuote = false;
        var current = new StringBuilder();
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
            }
            else if (ch == ' ' && !inQuote)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return [.. parts];
    }
}

// =========================================================================
// Gherkin Parser (same as gherkin-parser.cs)
// =========================================================================

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
        var background = new List<StepIR>();
        var scenarios = new List<ScenarioIR>();
        ScenarioIR? currentScenario = null;

        var state = ParserState.Initial;
        List<string>? exampleHeaders = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line.StartsWith("Feature:"))
            {
                featureName = line["Feature:".Length..].Trim();
                state = ParserState.Feature;
                continue;
            }

            if (featureName is null)
                continue;

            if (line == "Background:" || line.StartsWith("Background: "))
            {
                state = ParserState.Background;
                currentScenario = null;
                exampleHeaders = null;
                continue;
            }

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

            if (line == "Examples:" || line.StartsWith("Examples:"))
            {
                if (state != ParserState.Scenario)
                    throw new FormatException("Examples found outside a scenario");
                state = ParserState.Examples;
                exampleHeaders = null;
                continue;
            }

            if (line.StartsWith('|'))
            {
                if (state != ParserState.Examples)
                    continue;

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
            Name = featureName,
            Background = background,
            Scenarios = scenarios
        };
    }

    private static StepIR? TryParseStep(string line)
    {
        foreach (var kw in StepKeywords)
        {
            if (line.StartsWith(kw + " "))
            {
                var text = line[(kw.Length + 1)..].Trim();
                var parameters = ParameterRegex.Matches(text)
                    .Select(m => m.Groups[1].Value)
                    .ToList();
                return new StepIR { Keyword = kw, Text = text };
            }
        }
        return null;
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return [.. trimmed.Split('|').Select(c => c.Trim())];
    }
}

enum ParserState { Initial, Feature, Background, Scenario, Examples }

// =========================================================================
// IR Data Model
// =========================================================================

class FeatureIR
{
    public string Name { get; set; } = "";
    public List<StepIR>? Background { get; set; }
    public List<ScenarioIR> Scenarios { get; set; } = [];

    public FeatureIR DeepClone()
    {
        return new FeatureIR
        {
            Name = Name,
            Background = Background?.Select(s => s.DeepClone()).ToList(),
            Scenarios = Scenarios.Select(s => s.DeepClone()).ToList()
        };
    }
}

class ScenarioIR
{
    public string Name { get; set; } = "";
    public List<StepIR> Steps { get; set; } = [];
    public List<Dictionary<string, string>> Examples { get; set; } = [];

    public ScenarioIR DeepClone()
    {
        return new ScenarioIR
        {
            Name = Name,
            Steps = Steps.Select(s => s.DeepClone()).ToList(),
            Examples = Examples.Select(e => new Dictionary<string, string>(e, StringComparer.Ordinal)).ToList()
        };
    }
}

class StepIR
{
    public string Keyword { get; set; } = "";
    public string Text { get; set; } = "";

    public StepIR DeepClone()
    {
        return new StepIR { Keyword = Keyword, Text = Text };
    }
}

// =========================================================================
// Mutation Engine
// =========================================================================

static class MutationEngine
{
    public static Report Run(MutationConfig cfg)
    {
        if (cfg.Workers < 1) cfg.Workers = 1;
        cfg.GeneratedDir ??= Path.Combine(cfg.WorkDir, "generated");
        cfg.Level ??= "hard";

        var mutations = Discover(cfg.Feature);
        var skip = AcceptedSkips(cfg, mutations);
        var executableIndexes = new List<int>();
        for (int i = 0; i < mutations.Count; i++)
        {
            if (!skip.Contains(mutations[i].Scenario))
                executableIndexes.Add(i);
        }

        var report = new Report
        {
            Summary = new Summary { Total = executableIndexes.Count }
        };
        report.Summary.SkippedScenarios = skip.Count;
        report.Summary.SkippedMutations = mutations.Count - executableIndexes.Count;
        report.Results = new Result[executableIndexes.Count];

        if (executableIndexes.Count == 0)
            return report;

        Directory.CreateDirectory(Path.Combine(cfg.WorkDir, "base"));
        WriteFeatureJson(Path.Combine(cfg.WorkDir, "base", "feature.json"), cfg.Feature);

        var startedAt = Stopwatch.StartNew();
        int running = 0;
        var mu = new object();

        StatusReporting.ReportStatus(cfg, startedAt, report, running, skip.Count,
            mutations.Count - executableIndexes.Count, mu);

        using var workerPool = new WorkerPool(cfg.RunnerCommand, cfg.Workers);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = cfg.Workers
        };

        var timeoutCts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(cfg.TimeoutText))
        {
            var timeoutMs = CliHelper.ParseDurationMs(cfg.TimeoutText);
            if (timeoutMs > 0)
                timeoutCts.CancelAfter((int)timeoutMs);
        }

        var statusTimerCts = new CancellationTokenSource();
        var statusIntervalMs = cfg.StatusIntervalMs;

        Task? statusTask = null;
        if (statusIntervalMs > 0)
        {
            statusTask = Task.Run(async () =>
            {
                while (!statusTimerCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay((int)statusIntervalMs, statusTimerCts.Token);
                        lock (mu)
                        {
                            StatusReporting.ReportStatus(cfg, startedAt, report, running, skip.Count,
                                mutations.Count - executableIndexes.Count, mu);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        Parallel.For(0, executableIndexes.Count, parallelOptions, resultIndex =>
        {
            var mutation = mutations[executableIndexes[resultIndex]];
            var mutationWorkDir = Path.Combine(cfg.WorkDir, "mutations", mutation.ID);
            var featureJson = Path.Combine(mutationWorkDir, "feature.json");

            lock (mu) { running++; }

            try
            {
                Directory.CreateDirectory(mutationWorkDir);
                var cloned = cfg.Feature.DeepClone();
                Apply(cloned, mutation);
                WriteFeatureJson(featureJson, cloned);

                var runnerResult = workerPool.RunJob(timeoutCts.Token, mutation, featureJson,
                    cfg.GeneratedDir, mutationWorkDir);

                var result = Classify(mutation, runnerResult);
                lock (mu)
                {
                    report.Results[resultIndex] = result;
                    switch (result.Status)
                    {
                        case "killed": report.Summary.Killed++; break;
                        case "survived": report.Summary.Survived++; break;
                        default: report.Summary.Errors++; break;
                    }
                    running--;
                }
            }
            catch (OperationCanceledException)
            {
                lock (mu)
                {
                    report.Results[resultIndex] = new Result
                    {
                        Mutation = ToMutationView(mutation),
                        Status = "error",
                        Error = "mutation cancelled due to timeout"
                    };
                    report.Summary.Errors++;
                    running--;
                }
            }
            catch (Exception ex)
            {
                lock (mu)
                {
                    report.Results[resultIndex] = new Result
                    {
                        Mutation = ToMutationView(mutation),
                        Status = "error",
                        Error = ex.Message
                    };
                    report.Summary.Errors++;
                    running--;
                }
            }
        });

        statusTimerCts.Cancel();
        statusTask?.Wait(1000);

        lock (mu)
        {
            StatusReporting.ReportStatus(cfg, startedAt, report, running, skip.Count,
                mutations.Count - executableIndexes.Count, mu);
        }

        return report;
    }

    public static List<Mutation> Discover(FeatureIR feature)
    {
        var mutations = new List<Mutation>();
        for (int si = 0; si < feature.Scenarios.Count; si++)
        {
            var scenario = feature.Scenarios[si];
            for (int ei = 0; ei < scenario.Examples.Count; ei++)
            {
                var example = scenario.Examples[ei];
                var keys = example.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                foreach (var key in keys)
                {
                    var original = example[key];
                    var path = $"$.scenarios[{si}].examples[{ei}].{key}";
                    var mutated = MutateValue(path, original);
                    if (mutated == original) continue;
                    var id = $"m{mutations.Count + 1}";
                    mutations.Add(new Mutation
                    {
                        ID = id,
                        Path = path,
                        Description = $"{path}: {original} -> {mutated}",
                        Original = original,
                        Mutated = mutated,
                        Scenario = si,
                        Example = ei,
                        Key = key
                    });
                }
            }
        }
        return mutations;
    }

    public static void Apply(FeatureIR feature, Mutation mutation)
    {
        feature.Scenarios[mutation.Scenario]
            .Examples[mutation.Example][mutation.Key] = mutation.Mutated;
    }

    public static string MutateValue(string path, string value)
    {
        var trimmed = value.Trim();
        var lower = trimmed.ToLowerInvariant();

        if (trimmed.Contains(','))
        {
            var parts = trimmed.Split(',').Select(p => p.Trim()).ToArray();
            var index = (int)(Seed(path, value) % (ulong)parts.Length);
            parts[index] = MutateValue($"{path}[]", parts[index]);
            return string.Join(", ", parts);
        }
        if (lower == "true") return "false";
        if (lower == "false") return "true";
        if (lower is "null" or "nil" or "none") return "value";
        if (long.TryParse(trimmed, out var i))
        {
            var delta = (long)(Seed(path, value) % 9) + 1;
            if (Seed(path, value) % 2 == 0) delta = -delta;
            return (i + delta).ToString();
        }
        if (trimmed.Contains('.') && double.TryParse(trimmed, out var f))
        {
            if (!double.IsInfinity(f) && !double.IsNaN(f))
            {
                var delta = ((double)(Seed(path, value) % 900) + 100) / 100;
                if (Seed(path, value) % 2 == 0) delta = -delta;
                return (f + delta).ToString("G");
            }
        }
        return Dither(path, value);
    }

    private static string Dither(string path, string value)
    {
        if (string.IsNullOrEmpty(value)) return "x";
        var index = (int)(Seed(path, value) % (ulong)value.Length);
        var chars = value.ToCharArray();
        if (chars[index] >= 'a' && chars[index] <= 'z')
        {
            chars[index] = (char)(chars[index] - 'a' + 'A');
            return new string(chars);
        }
        if (chars[index] >= 'A' && chars[index] <= 'Z')
        {
            chars[index] = (char)(chars[index] - 'A' + 'a');
            return new string(chars);
        }
        chars[index] = 'x';
        return new string(chars);
    }

    private static ulong Seed(params string[] parts)
    {
        const ulong fnvOffsetBasis = 14695981039346656037;
        const ulong fnvPrime = 1099511628211;
        ulong hash = fnvOffsetBasis;
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= fnvPrime;
            }
            hash ^= (byte)0;
            hash *= fnvPrime;
        }
        return hash;
    }

    private static HashSet<int> AcceptedSkips(MutationConfig cfg, List<Mutation> mutations)
    {
        if (cfg.Level == "full" || string.IsNullOrEmpty(cfg.FeaturePath))
            return [];

        var metadata = ManifestManager.ReadMutationMetadata(cfg.FeaturePath);
        if (metadata is null)
            return [];

        var skip = new HashSet<int>();

        if ((metadata.Manifest?.Scenarios?.Count ?? 0) == 0 && ManifestManager.FeatureStampValid(cfg.FeaturePath))
        {
            for (int i = 0; i < cfg.Feature.Scenarios.Count; i++)
                skip.Add(i);
            return skip;
        }

        var current = ManifestManager.NewManifest(cfg.FeaturePath, cfg.Feature, new Report(), cfg.ImplementationHash);
        foreach (var entry in metadata.Manifest?.Scenarios ?? [])
        {
            if (ManifestEntryReusable(metadata.Manifest, current, entry, cfg.Level,
                    cfg.Feature, mutations))
            {
                skip.Add(entry.Index);
            }
        }
        return skip;
    }

    private static bool ManifestEntryReusable(
        Manifest old, Manifest current, ScenarioManifestEntry entry,
        string level, FeatureIR feature, List<Mutation> mutations)
    {
        if (old.Version != 1) return false;
        if (old.FeatureName != current.FeatureName || old.FeaturePath != current.FeaturePath)
            return false;
        if (old.BackgroundHash != current.BackgroundHash) return false;
        if (level == "hard" && old.ImplementationHash != current.ImplementationHash)
            return false;
        if (entry.Index < 0 || entry.Index >= feature.Scenarios.Count) return false;
        var scenario = feature.Scenarios[entry.Index];
        if (entry.Name != scenario.Name || entry.ScenarioHash != ManifestManager.HashJson(scenario))
            return false;
        if (entry.Result.Survived != 0 || entry.Result.Errors != 0) return false;
        if (entry.MutationCount != MutationCountForScenario(mutations, entry.Index))
            return false;
        return true;
    }

    private static int MutationCountForScenario(List<Mutation> mutations, int scenarioIndex)
    {
        return mutations.Count(m => m.Scenario == scenarioIndex);
    }

    private static Result Classify(Mutation mutation, RunnerResult runnerResult)
    {
        string status;
        switch (runnerResult.Outcome)
        {
            case "test_failure": status = "killed"; break;
            case "test_success": status = "survived"; break;
            default: status = "error"; break;
        }
        return new Result
        {
            Mutation = ToMutationView(mutation),
            Status = status,
            Output = runnerResult.Output ?? "",
            Error = runnerResult.Error ?? "",
            Duration = runnerResult.Duration
        };
    }

    private static MutationView ToMutationView(Mutation m)
    {
        return new MutationView
        {
            ID = m.ID,
            Path = m.Path,
            Description = m.Description,
            Original = m.Original,
            Mutated = m.Mutated
        };
    }

    private static void WriteFeatureJson(string path, FeatureIR feature)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.Serialize(feature, options);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}

// =========================================================================
// Worker Pool
// =========================================================================

class WorkerPool : IDisposable
{
    private readonly string[] _command;
    private readonly int _count;
    private readonly List<WorkerInstance> _workers;
    private int _next;

    public WorkerPool(string[] command, int count)
    {
        _command = command;
        _count = Math.Max(1, count);
        _workers = new List<WorkerInstance>();
        _next = 0;
        StartWorkers();
    }

    private void StartWorkers()
    {
        for (int i = 0; i < _count; i++)
        {
            var worker = new WorkerInstance(_command);
            _workers.Add(worker);
        }
    }

    public RunnerResult RunJob(CancellationToken ct, Mutation mutation,
        string featureJson, string generatedDir, string workDir)
    {
        var worker = _workers[Interlocked.Increment(ref _next) % _workers.Count];
        return worker.Execute(ct, new WorkerRequest
        {
            Id = mutation.ID,
            FeatureJson = featureJson,
            GeneratedDir = generatedDir,
            WorkDir = workDir
        });
    }

    public void Dispose()
    {
        foreach (var w in _workers)
            w.Dispose();
    }
}

class WorkerInstance : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;

    public WorkerInstance(string[] command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command[0],
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8
        };
        for (int i = 1; i < command.Length; i++)
            psi.ArgumentList.Add(command[i]);

        _process = new Process { StartInfo = psi };
        _process.Start();
        _stdin = new StreamWriter(_process.StandardInput.BaseStream, Encoding.UTF8) { AutoFlush = true };
        _stdout = new StreamReader(_process.StandardOutput.BaseStream, Encoding.UTF8);
    }

    public RunnerResult Execute(CancellationToken ct, WorkerRequest request)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var requestJson = JsonSerializer.Serialize(request);
            _stdin.WriteLine(requestJson);

            if (ct.IsCancellationRequested)
                return new RunnerResult { Outcome = "infrastructure_error", Error = "cancelled", Duration = started.ElapsedTicks };

            var line = _stdout.ReadLine();
            if (line is null)
                return new RunnerResult { Outcome = "infrastructure_error", Error = "worker exited without response", Duration = started.ElapsedTicks };

            var response = JsonSerializer.Deserialize<WorkerResponse>(line);
            if (response is null)
                return new RunnerResult { Outcome = "infrastructure_error", Error = "invalid worker response", Duration = started.ElapsedTicks };

            if (response.Id != request.Id)
                return new RunnerResult
                {
                    Outcome = "infrastructure_error",
                    Error = $"worker response id '{response.Id}' does not match request id '{request.Id}'",
                    Duration = started.ElapsedTicks
                };

            return new RunnerResult
            {
                Outcome = response.Outcome,
                Output = response.Output ?? "",
                Error = response.Error ?? "",
                Duration = response.Duration > 0 ? response.Duration : started.ElapsedTicks / (Stopwatch.Frequency / 1_000_000_000)
            };
        }
        catch (Exception ex)
        {
            return new RunnerResult { Outcome = "infrastructure_error", Error = ex.Message, Duration = started.ElapsedTicks };
        }
    }

    public void Dispose()
    {
        try { _stdin.Close(); } catch { }
        try
        {
            if (!_process.HasExited)
            {
                _process.WaitForExit(100);
                if (!_process.HasExited) _process.Kill();
            }
        }
        catch { }
        _process.Dispose();
    }
}

// =========================================================================
// Status Reporting
// =========================================================================

static class StatusReporting
{
    public static void ReportStatus(MutationConfig cfg, Stopwatch startedAt,
        Report report, int running, int skippedScenarios, int skippedMutations, object mu)
    {
        lock (mu)
        {
            var completed = report.Summary.Killed + report.Summary.Survived + report.Summary.Errors;
            var elapsed = startedAt.ElapsedMilliseconds;
            var line = $"status elapsed={elapsed}ms total={report.Summary.Total} completed={completed}" +
                       $" running={running} killed={report.Summary.Killed} survived={report.Summary.Survived}" +
                       $" errors={report.Summary.Errors}";
            if (skippedScenarios > 0 || skippedMutations > 0)
                line += $" skipped_scenarios={skippedScenarios} skipped_mutations={skippedMutations}";
            Console.Error.WriteLine(line);
        }
    }
}

// =========================================================================
// Manifest Management
// =========================================================================

static class ManifestManager
{
    public static string HashJson(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return HashString(json);
    }

    public static string HashString(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static Manifest NewManifest(string featurePath, FeatureIR feature, Report report, string implementationHash)
    {
        var now = DateTime.UtcNow.ToString("O");
        var m = new Manifest
        {
            Version = 1,
            TestedAt = now,
            FeatureName = feature.Name,
            FeaturePath = featurePath,
            BackgroundHash = HashJson(feature.Background),
            ImplementationHash = implementationHash,
            Scenarios = []
        };

        var allMutations = MutationEngine.Discover(feature);
        var scenarioSummaries = ScenarioSummaries(feature, report);

        for (int i = 0; i < feature.Scenarios.Count; i++)
        {
            var summary = scenarioSummaries.GetValueOrDefault(i);
            if (summary is null || summary.Survived != 0 || summary.Errors != 0)
                continue;
            m.Scenarios.Add(new ScenarioManifestEntry
            {
                Index = i,
                Name = feature.Scenarios[i].Name,
                ScenarioHash = HashJson(feature.Scenarios[i]),
                MutationCount = allMutations.Count(mut => mut.Scenario == i),
                Result = summary,
                TestedAt = now
            });
        }
        return m;
    }

    public static void WriteMutationMetadata(string featurePath, FeatureIR feature,
        Report report, string implementationHash, string level, bool writeStamp)
    {
        var content = File.ReadAllText(featurePath);
        var previous = ParseMutationMetadata(content);
        var cleaned = StripMutationMetadata(content);
        var stamp = HashString(cleaned);
        var m = NewManifest(featurePath, feature, report, implementationHash);

        if (previous is not null)
        {
            MergeReusablePreviousScenarios(m, previous.Manifest!, feature, level);
        }

        var manifestJson = JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = false });

        var sb = new StringBuilder();
        if (writeStamp)
        {
            sb.AppendLine($"# mutation-stamp: sha256={stamp}");
        }
        sb.AppendLine("# acceptance-mutation-manifest-begin");
        sb.Append("# ");
        sb.AppendLine(manifestJson);
        sb.AppendLine("# acceptance-mutation-manifest-end");
        sb.AppendLine();
        sb.Append(cleaned.TrimStart('\n'));

        File.WriteAllText(featurePath, sb.ToString());
    }

    public static bool FeatureStampValid(string featurePath)
    {
        try
        {
            var content = File.ReadAllText(featurePath);
            var metadata = ParseMutationMetadata(content);
            if (metadata is null || string.IsNullOrEmpty(metadata.Stamp))
                return false;
            return metadata.Stamp == HashString(StripMutationMetadata(content));
        }
        catch { return false; }
    }

    public static MutationMetadata? ReadMutationMetadata(string featurePath)
    {
        try
        {
            var content = File.ReadAllText(featurePath);
            return ParseMutationMetadata(content);
        }
        catch { return null; }
    }

    private static MutationMetadata? ParseMutationMetadata(string content)
    {
        var metadata = new MutationMetadata();
        var lines = content.Split('\n');
        var manifestLines = new List<string>();
        bool inManifest = false;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith("# mutation-stamp: sha256="))
            {
                metadata.Stamp = trimmed["# mutation-stamp: sha256=".Length..];
                continue;
            }
            if (trimmed == "# acceptance-mutation-manifest-begin")
            {
                inManifest = true;
                continue;
            }
            if (trimmed == "# acceptance-mutation-manifest-end")
            {
                inManifest = false;
                continue;
            }
            if (inManifest)
            {
                manifestLines.Add(trimmed.TrimStart('#').TrimStart());
            }
        }

        if (manifestLines.Count == 0)
            return metadata.Stamp is not null ? metadata : null;

        var manifestJson = string.Join("", manifestLines);
        metadata.Manifest = JsonSerializer.Deserialize<Manifest>(manifestJson);
        return metadata;
    }

    private static string StripMutationMetadata(string content)
    {
        var lines = content.Split('\n');
        var result = new List<string>();
        bool inManifest = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# mutation-stamp:"))
                continue;
            if (trimmed == "# acceptance-mutation-manifest-begin")
            {
                inManifest = true;
                continue;
            }
            if (trimmed == "# acceptance-mutation-manifest-end")
            {
                inManifest = false;
                continue;
            }
            if (inManifest)
                continue;
            result.Add(line);
        }

        return string.Join("\n", result).TrimStart('\n');
    }

    private static void MergeReusablePreviousScenarios(Manifest current, Manifest previous,
        FeatureIR feature, string level)
    {
        var existing = new HashSet<int>(current.Scenarios.Select(e => e.Index));
        var allMutations = MutationEngine.Discover(feature);

        foreach (var entry in previous.Scenarios)
        {
            if (existing.Contains(entry.Index)) continue;
            if (ManifestEntryReusable(previous, current, entry, level, feature, allMutations))
            {
                current.Scenarios.Add(entry);
                existing.Add(entry.Index);
            }
        }
    }

    private static bool ManifestEntryReusable(Manifest old, Manifest current,
        ScenarioManifestEntry entry, string level, FeatureIR feature, List<Mutation> mutations)
    {
        if (old.Version != 1) return false;
        if (old.FeatureName != current.FeatureName || old.FeaturePath != current.FeaturePath)
            return false;
        if (old.BackgroundHash != current.BackgroundHash) return false;
        if (level == "hard" && old.ImplementationHash != current.ImplementationHash)
            return false;
        if (entry.Index < 0 || entry.Index >= feature.Scenarios.Count) return false;
        var scenario = feature.Scenarios[entry.Index];
        if (entry.Name != scenario.Name || entry.ScenarioHash != HashJson(scenario))
            return false;
        if (entry.Result.Survived != 0 || entry.Result.Errors != 0) return false;
        if (entry.MutationCount != mutations.Count(m => m.Scenario == entry.Index))
            return false;
        return true;
    }

    private static Dictionary<int, Summary> ScenarioSummaries(FeatureIR feature, Report report)
    {
        var summaries = new Dictionary<int, Summary>();
        if (report.Results.Length == 0 && feature.Scenarios.Count == 1 && report.Summary.Total > 0)
        {
            summaries[0] = report.Summary;
            return summaries;
        }
        foreach (var result in report.Results)
        {
            var si = ScenarioIndexFromPath(result.Mutation.Path);
            if (si < 0) continue;
            if (!summaries.TryGetValue(si, out var s))
                s = new Summary();
            s.Total++;
            switch (result.Status)
            {
                case "killed": s.Killed++; break;
                case "survived": s.Survived++; break;
                case "error": s.Errors++; break;
            }
            summaries[si] = s;
        }
        return summaries;
    }

    private static int ScenarioIndexFromPath(string path)
    {
        const string prefix = "$.scenarios[";
        if (!path.StartsWith(prefix)) return -1;
        var remainder = path[prefix.Length..];
        var end = remainder.IndexOf(']');
        if (end < 0) return -1;
        if (int.TryParse(remainder[..end], out var index))
            return index;
        return -1;
    }
}

// =========================================================================
// Generated Metadata Helpers
// =========================================================================

static class GeneratedMetadataHelper
{
    public static string ResolveImplementationHash(string generatedDir, string featurePath, string? overrideHash)
    {
        if (!string.IsNullOrEmpty(overrideHash))
            return overrideHash;

        try
        {
            var metadataPath = GeneratedMetadataPath(generatedDir, featurePath);
            if (!File.Exists(metadataPath)) return "unknown";
            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<GeneratedMetadata>(json);
            if (metadata is null || metadata.FeaturePath != featurePath)
                return "unknown";
            return metadata.ImplementationHash ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    public static string GeneratedMetadataPath(string generatedDir, string featurePath)
    {
        return Path.Combine(generatedDir, "metadata", FeatureMetadataSlug(featurePath) + ".json");
    }

    public static string FeatureMetadataSlug(string featurePath)
    {
        var sb = new StringBuilder();
        bool previousHyphen = false;
        foreach (var ch in featurePath.ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                previousHyphen = false;
            }
            else if (!previousHyphen && sb.Length > 0)
            {
                sb.Append('-');
                previousHyphen = true;
            }
        }
        return sb.ToString().Trim('-');
    }
}

// =========================================================================
// Reports
// =========================================================================

static class ReportWriter
{
    public static void WriteTextReport(Report report)
    {
        var s = report.Summary;
        var line = $"total={s.Total} killed={s.Killed} survived={s.Survived} errors={s.Errors}";
        if (s.SkippedScenarios > 0 || s.SkippedMutations > 0)
            line += $" skipped_scenarios={s.SkippedScenarios} skipped_mutations={s.SkippedMutations}";
        Console.WriteLine(line);

        foreach (var result in report.Results)
        {
            Console.WriteLine($"{result.Status,-8} {result.Mutation.Description}");
            if (result.Status == "survived" || result.Status == "error")
            {
                if (!string.IsNullOrEmpty(result.Error))
                    Console.WriteLine($"  error: {result.Error}");
                if (!string.IsNullOrEmpty(result.Output))
                    Console.WriteLine($"  output:\n{result.Output}");
            }
        }
    }

    public static void WriteJsonReport(Report report)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        Console.WriteLine(JsonSerializer.Serialize(report, options));
    }
}

// =========================================================================
// Data Types
// =========================================================================

class MutationConfig
{
    public FeatureIR Feature { get; set; } = new();
    public string FeaturePath { get; set; } = "";
    public string WorkDir { get; set; } = "build/acceptance-mutation";
    public string? GeneratedDir { get; set; }
    public int Workers { get; set; } = 1;
    public string Level { get; set; } = "hard";
    public string ImplementationHash { get; set; } = "";
    public long StatusIntervalMs { get; set; } = 30000;
    public string[] RunnerCommand { get; set; } = [];
    public string? TimeoutText { get; set; }
}

class Mutation
{
    public string ID { get; set; } = "";
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public string Original { get; set; } = "";
    public string Mutated { get; set; } = "";
    public int Scenario { get; set; }
    public int Example { get; set; }
    public string Key { get; set; } = "";
}

class RunnerResult
{
    public string Outcome { get; set; } = "";
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public long Duration { get; set; }
}

class Report
{
    public Summary Summary { get; set; } = new();
    public Result[] Results { get; set; } = [];
}

class Summary
{
    public int Total { get; set; }
    public int Killed { get; set; }
    public int Survived { get; set; }
    public int Errors { get; set; }
    public int SkippedScenarios { get; set; }
    public int SkippedMutations { get; set; }
}

class Result
{
    public MutationView Mutation { get; set; } = new();
    public string Status { get; set; } = "";
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public long Duration { get; set; }
}

class MutationView
{
    public string ID { get; set; } = "";
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public string Original { get; set; } = "";
    public string Mutated { get; set; } = "";
}

class WorkerRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("feature_json")]
    public string FeatureJson { get; set; } = "";

    [JsonPropertyName("generated_dir")]
    public string GeneratedDir { get; set; } = "";

    [JsonPropertyName("work_dir")]
    public string WorkDir { get; set; } = "";

    [JsonPropertyName("timeout")]
    public string? Timeout { get; set; }
}

class WorkerResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "";

    [JsonPropertyName("output")]
    public string? Output { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("duration")]
    public long Duration { get; set; }
}

class Manifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("tested_at")]
    public string TestedAt { get; set; } = "";

    [JsonPropertyName("feature_name")]
    public string FeatureName { get; set; } = "";

    [JsonPropertyName("feature_path")]
    public string FeaturePath { get; set; } = "";

    [JsonPropertyName("background_hash")]
    public string BackgroundHash { get; set; } = "";

    [JsonPropertyName("implementation_hash")]
    public string ImplementationHash { get; set; } = "";

    [JsonPropertyName("scenarios")]
    public List<ScenarioManifestEntry> Scenarios { get; set; } = [];
}

class ScenarioManifestEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("scenario_hash")]
    public string ScenarioHash { get; set; } = "";

    [JsonPropertyName("mutation_count")]
    public int MutationCount { get; set; }

    [JsonPropertyName("result")]
    public Summary Result { get; set; } = new();

    [JsonPropertyName("tested_at")]
    public string TestedAt { get; set; } = "";
}

class MutationMetadata
{
    public string? Stamp { get; set; }
    public Manifest? Manifest { get; set; }
}

class GeneratedMetadata
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("feature_path")]
    public string FeaturePath { get; set; } = "";

    [JsonPropertyName("ir_path")]
    public string IRPath { get; set; } = "";

    [JsonPropertyName("implementation_hash")]
    public string? ImplementationHash { get; set; }

    [JsonPropertyName("hash_scope")]
    public string HashScope { get; set; } = "";

    [JsonPropertyName("generated_files")]
    public List<string>? GeneratedFiles { get; set; }
}
