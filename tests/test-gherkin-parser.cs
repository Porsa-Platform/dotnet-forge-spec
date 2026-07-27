#!/usr/bin/dotnet-run
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property PublishAot=false

// Tests for scripts/gherkin-parser.cs
// Usage: dotnet tests/test-gherkin-parser.cs --

using System.Diagnostics;
using System.Text.Json.Nodes;

var passed = 0;
var failed = 0;

Run("parses feature with background, scenario outline, and examples", () =>
{
    var feature = @"Feature: Withdrawals

Background:
  Given an account balance of <balance>

Scenario Outline: Withdraw cash
  When the customer withdraws <amount>
  Then the remaining balance is <remaining>

Examples:
  | balance | amount | remaining |
  | 100     | 20     | 80        |
  | 50      | 5      | 45        |
";
    var (exit, json) = RunParser(feature);
    AssertExit(0, exit);
    var doc = JsonNode.Parse(json)!;
    AssertEq("Withdrawals", (string)doc["name"]!);
    var bg = doc["background"]!.AsArray();
    AssertEq(1, bg.Count);
    AssertEq("Given", (string)bg[0]!["keyword"]!);
    AssertEq("an account balance of <balance>", (string)bg[0]!["text"]!);
    var scenarios = doc["scenarios"]!.AsArray();
    AssertEq(1, scenarios.Count);
    AssertEq("Withdraw cash", (string)scenarios[0]!["name"]!);
    var steps = scenarios[0]!["steps"]!.AsArray();
    AssertEq(2, steps.Count);
    AssertEq("When", (string)steps[0]!["keyword"]!);
    var examples = scenarios[0]!["examples"]!.AsArray();
    AssertEq(2, examples.Count);
    AssertEq("20", (string)examples[0]!["amount"]!);
    AssertEq("80", (string)examples[0]!["remaining"]!);
    AssertEq("5", (string)examples[1]!["amount"]!);
});

Run("parses scenario without examples", () =>
{
    var (exit, json) = RunParser("Feature: Simple\n\nScenario: Single\n  Given the input is ready\n  Then output is correct\n");
    AssertExit(0, exit);
    AssertEq(0, JsonNode.Parse(json)!["scenarios"]![0]!["examples"]!.AsArray().Count);
});

Run("rejects missing Feature declaration", () =>
{
    AssertExit(1, RunParser("Scenario: orphan\n  Given something\n").ExitCode);
});

Run("rejects Examples outside a scenario", () =>
{
    AssertExit(1, RunParser("Feature: Bad\n\nExamples:\n  | x |\n  | y |\n").ExitCode);
});

Run("rejects example row cell count mismatch", () =>
{
    AssertExit(1, RunParser("Feature: Bad\nScenario Outline: mismatch\n  Given <x>\nExamples:\n  | x | y |\n  | 1 |\n").ExitCode);
});

Run("rejects non-existent file", () =>
{
    var (exit, _) = RunScriptFile("nonexistent.feature", "out.json");
    AssertExit(1, exit);
});

Run("exits with 2 on wrong number of arguments", () =>
{
    var (exit, _) = RunScriptFile();
    AssertExit(2, exit);
});

Run("records parameters from step text", () =>
{
    var (exit, json) = RunParser("Feature: Params\n\nScenario: Multi\n  Given <a> and <b> then <c>\n");
    AssertExit(0, exit);
    var pars = JsonNode.Parse(json)!["scenarios"]![0]!["steps"]![0]!["parameters"]!.AsArray();
    AssertEq(3, pars.Count);
    AssertEq("a", (string)pars[0]!);
    AssertEq("b", (string)pars[1]!);
    AssertEq("c", (string)pars[2]!);
});

Console.WriteLine($"\nResult: {passed} passed, {failed} failed, {passed + failed} total");
if (failed > 0) Environment.Exit(1);

// --- helpers -----------------------------------------------------------

void Run(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}\n      {ex.Message}"); }
}

static (int ExitCode, string Json) RunParser(string content)
{
    var dir = Path.Combine(Path.GetTempPath(), $"parser-test-{Guid.NewGuid()}");
    Directory.CreateDirectory(dir);
    var featurePath = Path.Combine(dir, "test.feature");
    var jsonPath = Path.Combine(dir, "out.json");
    try
    {
        File.WriteAllText(featurePath, content);
        var (exit, _) = RunScriptFile(featurePath, jsonPath);
        var json = File.Exists(jsonPath) ? File.ReadAllText(jsonPath) : "";
        return (exit, json);
    }
    finally { TryDeleteDir(dir); }
}

static (int ExitCode, string Stdout) RunScriptFile(params string[] args)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
    };
    psi.ArgumentList.Add("run"); psi.ArgumentList.Add("scripts/gherkin-parser.cs"); psi.ArgumentList.Add("--");
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var proc = new Process { StartInfo = psi };
    proc.Start();
    var stdout = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit(30000);
    return (proc.ExitCode, stdout);
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
