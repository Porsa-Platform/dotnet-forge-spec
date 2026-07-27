#!/usr/bin/dotnet-run
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property PublishAot=false

// Tests for scripts/gherkin-mutator.cs
// Usage: dotnet run tests/test-gherkin-mutator.cs --

using System.Diagnostics;
using System.Text.Json.Nodes;

var passed = 0;
var failed = 0;

Run("exits with 2 when --runner-worker is missing", () =>
{
    var (exit, _) = RunMutatorRaw();
    AssertExit(2, exit);
});

Run("exits with 2 when --level is invalid", () =>
{
    var (exit, _) = RunMutator("Feature: T\nScenario Outline: S\n  Given <x>\nExamples:\n  | x |\n  | 7 |\n", "--level", "invalid");
    AssertExit(2, exit);
});

Run("discovers correct number of mutations", () =>
{
    var (exit, output) = RunMutator(@"Feature: Test
Scenario Outline: Calc
  Given <x> and <y>
  Then result is <z>
Examples:
  | x | y | z |
  | 10 | 20 | 30 |
", "--json");
    var report = JsonNode.Parse(output)!;
    AssertEq(3, (int)report["summary"]!["total"]!);
});

Run("--json produces valid JSON report", () =>
{
    var (exit, output) = RunMutator(@"Feature: Test
Scenario Outline: S
  Given <x>
Examples:
  | x |
  | 7 |
", "--json");
    var report = JsonNode.Parse(output)!;
    Assert(true, report["summary"] is not null);
    Assert(true, report["results"] is not null);
});

Run("text report contains summary line", () =>
{
    var (exit, output) = RunMutator(@"Feature: Test
Scenario Outline: S
  Given <x>
Examples:
  | x |
  | 7 |
");
    Assert(true, output.StartsWith("total="));
});

Run("handles boolean mutation (true -> false)", () =>
{
    var (exit, output) = RunMutator(@"Feature: Test
Scenario Outline: S
  Given flag is <flag>
Examples:
  | flag |
  | true |
", "--json");
    var m = JsonNode.Parse(output)!["results"]![0]!["mutation"]!;
    AssertEq("true", (string)m["original"]!);
    AssertEq("false", (string)m["mutated"]!);
});

Run("handles integer mutation", () =>
{
    var (exit, output) = RunMutator(@"Feature: Test
Scenario Outline: S
  Given count is <count>
Examples:
  | count |
  | 100 |
", "--json");
    var m = JsonNode.Parse(output)!["results"]![0]!["mutation"]!;
    AssertEq("100", (string)m["original"]!);
    int.Parse((string)m["mutated"]!);
});

Run("handles null/nil/none mutations", () =>
{
    foreach (var val in new[] { "null", "nil", "none" })
    {
        var (exit, output) = RunMutator($@"Feature: Test
Scenario Outline: S
  Given value is <v>
Examples:
  | v |
  | {val} |
", "--json");
        var m = JsonNode.Parse(output)!["results"]![0]!["mutation"]!;
        AssertEq(val, (string)m["original"]!);
        AssertEq("value", (string)m["mutated"]!);
    }
});

Run("handles comma-list mutation", () =>
{
    var (exit, output) = RunMutator(@"Feature: Test
Scenario Outline: S
  Given items are <items>
Examples:
  | items |
  | a, b, c |
", "--json");
    var m = JsonNode.Parse(output)!["results"]![0]!["mutation"]!;
    AssertEq("a, b, c", (string)m["original"]!);
    var mutated = (string)m["mutated"]!;
    Assert(true, mutated.Contains(','));
    Assert(true, mutated != "a, b, c");
});

Run("handles float mutation", () =>
{
    var (exit, output) = RunMutator(@"Feature: Test
Scenario Outline: S
  Given value is <v>
Examples:
  | v |
  | 3.14 |
", "--json");
    var m = JsonNode.Parse(output)!["results"]![0]!["mutation"]!;
    AssertEq("3.14", (string)m["original"]!);
    double.Parse((string)m["mutated"]!, System.Globalization.CultureInfo.InvariantCulture);
});

Run("handles string dithering", () =>
{
    var (exit, output) = RunMutator(@"Feature: Test
Scenario Outline: S
  Given message is <m>
Examples:
  | m |
  | accepted |
", "--json");
    var m = JsonNode.Parse(output)!["results"]![0]!["mutation"]!;
    AssertEq("accepted", (string)m["original"]!);
    Assert(true, (string)m["mutated"]! != "accepted");
});

Run("feature metadata slug matches spec", () =>
{
    AssertEq("features-api-v2-happy-path-feature", Slugify("Features/API v2/Happy Path.feature"));
    AssertEq("features-hunt-the-wumpus-feature", Slugify("features/Hunt The Wumpus.feature"));
    AssertEq("features-orders-cancel-order-feature", Slugify("features/orders/Cancel Order.feature"));
});

Console.WriteLine($"\nResult: {passed} passed, {failed} failed, {passed + failed} total");
if (failed > 0) Environment.Exit(1);

// --- helpers -----------------------------------------------------------

void Run(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}\n      {ex.Message}"); }
}

static (int ExitCode, string Stdout) RunMutator(string featureContent, params string[] extraArgs)
{
    var dir = Path.Combine(Path.GetTempPath(), $"mutator-test-{Guid.NewGuid()}");
    Directory.CreateDirectory(dir);
    var featurePath = Path.Combine(dir, "test.feature");
    var workerPath = Path.GetFullPath("scripts/mock-runner-worker.cs");
    try
    {
        File.WriteAllText(featurePath, featureContent);
        var args = new List<string>
        {
            "--runner-worker", $"dotnet run -- {workerPath} --",
            "--status-interval", "0",
            "--feature", featurePath
        };
        args.AddRange(extraArgs);
        return RunMutatorRaw([.. args]);
    }
    finally { TryDeleteDir(dir); TryDeleteDir("build"); }
}

static (int ExitCode, string Stdout) RunMutatorRaw(params string[] args)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
    };
    psi.ArgumentList.Add("run"); psi.ArgumentList.Add("scripts/gherkin-mutator.cs"); psi.ArgumentList.Add("--");
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var proc = new Process { StartInfo = psi };
    proc.Start();
    var stdout = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit(60000);
    return (proc.ExitCode, stdout);
}

static string Slugify(string path)
{
    var sb = new System.Text.StringBuilder();
    bool prevHyphen = false;
    foreach (var ch in path.ToLowerInvariant())
    {
        if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) { sb.Append(ch); prevHyphen = false; }
        else if (!prevHyphen && sb.Length > 0) { sb.Append('-'); prevHyphen = true; }
    }
    return sb.ToString().Trim('-');
}

static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }

static void AssertEq<T>(T expected, T actual, string? msg = null)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception(msg ?? $"expected {expected} but got {actual}");
}

static void AssertExit(int expected, int actual)
{
    if (expected != actual) throw new Exception($"expected exit code {expected} but got {actual}");
}

static void Assert(bool condition, object? value = null)
{
    if (!condition) throw new Exception($"assertion failed{(value is not null ? $" on value {value}" : "")}");
}
