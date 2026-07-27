#!/usr/bin/dotnet-run
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property PublishAot=false

// Mock runner worker for mutator tests.
// Reads newline-delimited JSON job requests from stdin, responds over stdout.
// Returns test_failure for m1, test_success for m2, infrastructure_error for
// requests with "error" in the id, and test_success for everything else.
// Usage: dotnet run scripts/mock-runner-worker.cs --

using System.Text.Json;

string? line;
while ((line = Console.ReadLine()) is not null)
{
    try
    {
        var req = JsonDocument.Parse(line);
        var id = req.RootElement.GetProperty("id").GetString() ?? "";
        string outcome;
        string output = "mock worker output";
        string error = "";

        if (id == "m1")
            outcome = "test_failure";
        else if (id == "m2")
            outcome = "test_success";
        else if (id.Contains("error"))
            outcome = "infrastructure_error";
        else
            outcome = "test_success";

        if (outcome == "infrastructure_error")
            error = "mock error";

        var response = new Dictionary<string, object>
        {
            ["id"] = id,
            ["outcome"] = outcome,
            ["output"] = output,
            ["error"] = error,
            ["duration"] = 1
        };
        Console.WriteLine(JsonSerializer.Serialize(response));
    }
    catch
    {
        var errResp = new Dictionary<string, object>
        {
            ["id"] = "unknown",
            ["outcome"] = "infrastructure_error",
            ["output"] = "",
            ["error"] = "invalid request",
            ["duration"] = 0
        };
        Console.WriteLine(JsonSerializer.Serialize(errResp));
    }
}
