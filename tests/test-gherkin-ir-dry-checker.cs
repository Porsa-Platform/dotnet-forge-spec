#!/usr/bin/dotnet-run
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property PublishAot=false

// Tests for scripts/gherkin-ir-dry-checker.cs
// Usage: dotnet run tests/test-gherkin-ir-dry-checker.cs --

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

var passed = 0;
var failed = 0;

Run("reports empty findings for clean feature", () =>
{
    var report = AnalyzeIR(CleanIR());
    AssertEq(0, (int)report["summary"]!["findings"]!);
});

Run("reports duplicate-in-scenario in background", () =>
{
    var ir = CleanIR();
    ir["background"] = new JsonArray(NewStep("Given", "the same step"), NewStep("Given", "the same step"));
    ir["scenarios"] = new JsonArray();
    var findings = GetFindings(AnalyzeIR(ir));
    AssertEq(1, findings.Count);
    AssertEq("duplicate-in-scenario", (string)findings[0]!["kind"]!);
});

Run("reports duplicate-in-scenario in scenario", () =>
{
    var ir = CleanIR();
    ir["background"] = new JsonArray();
    ir["scenarios"] = new JsonArray { NewScenario("S", NewStep("Then", "do it"), NewStep("Then", "do it")) };
    var findings = GetFindings(AnalyzeIR(ir));
    AssertEq(1, findings.Count);
    AssertEq("duplicate-in-scenario", (string)findings[0]!["kind"]!);
});

Run("does not report exact-duplicate across scenarios by default", () =>
{
    var ir = CleanIR();
    ir["background"] = new JsonArray();
    ir["scenarios"] = new JsonArray(NewScenario("S1", NewStep("Then", "shared step")), NewScenario("S2", NewStep("Then", "shared step")));
    AssertEq(0, GetFindings(AnalyzeIR(ir)).Count);
});

Run("reports exact-duplicate with --include-exact", () =>
{
    var ir = CleanIR();
    ir["background"] = new JsonArray();
    ir["scenarios"] = new JsonArray(NewScenario("S1", NewStep("Then", "shared step")), NewScenario("S2", NewStep("Then", "shared step")));
    var findings = GetFindings(AnalyzeIR(ir, "--include-exact"));
    AssertEq(1, findings.Count);
    AssertEq("exact-duplicate", (string)findings[0]!["kind"]!);
});

Run("reports placeholder-variant", () =>
{
    var ir = CleanIR();
    ir["background"] = new JsonArray();
    ir["scenarios"] = new JsonArray { NewScenario("movement",
        NewStep("Then", "the player is in room <destination_room>"),
        NewStep("And", "the player is in room <expected_player_room>"),
        NewStep("And", "the player is in room <transport_room>")) };
    var findings = GetFindings(AnalyzeIR(ir));
    var pv = findings.FirstOrDefault(f => (string)f!["kind"]! == "placeholder-variant");
    if (pv is null) throw new Exception("placeholder-variant finding not found");
    AssertEq("high", (string)pv!["confidence"]!);
});

Run("reports possible-synonym", () =>
{
    var ir = CleanIR();
    ir["background"] = new JsonArray();
    ir["scenarios"] = new JsonArray { NewScenario("S",
        NewStep("Then", "the output contains line Enter command"),
        NewStep("And", "the output contains prompt Enter command")) };
    var findings = GetFindings(AnalyzeIR(ir));
    AssertEq(1, findings.Count);
    AssertEq("possible-synonym", (string)findings[0]!["kind"]!);
});

Run("report includes schema version", () =>
{
    var report = AnalyzeIR(CleanIR());
    AssertEq(1, (int)report["schema_version"]!);
});

Run("exits with 2 on wrong usage", () =>
{
    var (exit, _) = RunChecker();
    AssertExit(2, exit);
});

Run("exits with 1 on non-existent input file", () =>
{
    var (exit, _) = RunChecker("nonexistent.json", "out.json");
    AssertExit(1, exit);
});

Console.WriteLine($"\nResult: {passed} passed, {failed} failed, {passed + failed} total");
if (failed > 0) Environment.Exit(1);

// --- helpers -----------------------------------------------------------

void Run(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}\n      {ex.Message}"); }
}

JsonObject CleanIR() => new()
{
    ["name"] = "Clean",
    ["background"] = new JsonArray { NewStep("Given", "initial state") },
    ["scenarios"] = new JsonArray { NewScenario("S", NewStep("Then", "verify result")) }
};

JsonObject NewStep(string keyword, string text) => new()
{
    ["keyword"] = keyword, ["text"] = text, ["parameters"] = new JsonArray()
};

JsonObject NewScenario(string name, params JsonObject[] steps) => new()
{
    ["name"] = name, ["steps"] = new JsonArray(steps), ["examples"] = new JsonArray()
};

(int ExitCode, string Stdout) RunChecker(params string[] args)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
    };
    psi.ArgumentList.Add("run"); psi.ArgumentList.Add("scripts/gherkin-ir-dry-checker.cs"); psi.ArgumentList.Add("--");
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var proc = new Process { StartInfo = psi };
    proc.Start();
    var stdout = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit(30000);
    return (proc.ExitCode, stdout);
}

JsonObject AnalyzeIR(JsonObject ir, params string[] extraArgs)
{
    var dir = Path.Combine(Path.GetTempPath(), $"dry-test-{Guid.NewGuid()}");
    Directory.CreateDirectory(dir);
    var irPath = Path.Combine(dir, "ir.json");
    var reportPath = Path.Combine(dir, "report.json");
    try
    {
        File.WriteAllText(irPath, ir.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var args = new List<string>(extraArgs) { irPath, reportPath };
        var result = RunChecker([.. args]);
        if (result.ExitCode != 0) throw new Exception($"checker exited with code {result.ExitCode}");
        return JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject();
    }
    finally { TryDeleteDir(dir); }
}

JsonArray GetFindings(JsonObject report) => report["findings"]!.AsArray();

void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }

void AssertEq<T>(T expected, T actual, string? msg = null)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception(msg ?? $"expected {expected} but got {actual}");
}

void AssertExit(int expected, int actual)
{
    if (expected != actual) throw new Exception($"expected exit code {expected} but got {actual}");
}
