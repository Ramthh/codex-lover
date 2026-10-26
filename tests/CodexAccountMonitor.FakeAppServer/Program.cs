using System.Text.Json;

namespace CodexAccountMonitor.FakeAppServer;

public static class FakeAppServerMarker
{
}

internal static class Program
{
    public static void Main(string[] args)
    {
        if (args is ["--probe-saved-account-environment", var monitorAssembly])
        {
            ProbeSavedAccountEnvironment(monitorAssembly);
            return;
        }
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            var hasId = root.TryGetProperty("id", out var id);

            switch (method)
            {
                case "initialize" when hasId:
                    WriteResult(id, new
                    {
                        codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")
                    });
                    break;

                case "account/read" when hasId:
                    var refreshToken = root.TryGetProperty("params", out var parameters)
                                       && parameters.TryGetProperty("refreshToken", out var refresh)
                                       && refresh.ValueKind == JsonValueKind.True;
                    WriteResult(id, new
                    {
                        account = new
                        {
                            type = "chatgpt",
                            email = "fixture@example.test",
                            planType = "plus"
                        }
                    });
                    if (!refreshToken)
                    {
                        // Exit immediately after flushing the response. On Windows, Process.Exited
                        // can be observed before the parent's redirected stdout loop reaches this line.
                        Environment.Exit(0);
                    }

                    break;

                case "account/rateLimits/read" when hasId:
                    if (Path.GetFileName(Environment.GetEnvironmentVariable("CODEX_HOME")) == "nullable")
                    {
                        WriteResult(id, new
                        {
                            rateLimits = new
                            {
                                primary = new { usedPercent = (decimal?)null, windowDurationMins = (int?)300, resetsAt = (long?)null },
                                secondary = new { usedPercent = (decimal?)28, windowDurationMins = (int?)10080, resetsAt = (long?)null }
                            },
                            rateLimitResetCredits = new { availableCount = (int?)null }
                        });
                        break;
                    }
                    WriteNotification("fixture/reentrant", new { });
                    Thread.Sleep(25);
                    WriteResult(id, new
                    {
                        rateLimits = new
                        {
                            limitId = "codex",
                            primary = new
                            {
                                usedPercent = 28,
                                windowDurationMins = 10_080,
                                resetsAt = 1_800_200_000
                            }
                        },
                        rateLimitsByLimitId = new
                        {
                            codex_test_model = new
                            {
                                limitId = "codex_test_model",
                                limitName = "Test model",
                                primary = new
                                {
                                    usedPercent = 0,
                                    windowDurationMins = 300,
                                    resetsAt = 1_800_000_000
                                },
                                secondary = new
                                {
                                    usedPercent = 0,
                                    windowDurationMins = 10_080,
                                    resetsAt = 1_800_100_000
                                }
                            },
                            codex_other_model = new
                            {
                                limitId = "codex_other_model",
                                limitName = "Other test model",
                                primary = new
                                {
                                    usedPercent = 45,
                                    windowDurationMins = 300,
                                    resetsAt = 1_800_300_000
                                },
                                secondary = new
                                {
                                    usedPercent = 55,
                                    windowDurationMins = 10_080,
                                    resetsAt = 1_800_400_000
                                }
                            },
                            malformed = (object?)null
                        },
                        rateLimitResetCredits = new { availableCount = 2 }
                    });
                    break;

                default:
                    break;
            }
        }
    }

    private static void ProbeSavedAccountEnvironment(string monitorAssembly)
    {
        string[] credentialNames = ["OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN"];
        var inheritedFixtureCredentials = credentialNames.All(name =>
            Environment.GetEnvironmentVariable(name) == "fixture-inherited-credential");
        // Exercise the public production entry point in this disposable child without
        // adding a diagnostic mode to the shipped app or loading any saved account.
        var bootstrap = System.Reflection.Assembly.LoadFrom(monitorAssembly)
            .GetType("CodexAccountMonitor.Infrastructure.MonitorStartupBootstrap", throwOnError: true)!;
        bootstrap.GetMethod("PrepareSavedAccountEnvironment", Type.EmptyTypes)!.Invoke(null, null);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            inheritedFixtureCredentials,
            openAiKeyRemoved = Environment.GetEnvironmentVariable("OPENAI_API_KEY") is null,
            codexApiKeyRemoved = Environment.GetEnvironmentVariable("CODEX_API_KEY") is null,
            accessTokenRemoved = Environment.GetEnvironmentVariable("CODEX_ACCESS_TOKEN") is null,
            unrelatedEnvironmentPreserved = Environment.GetEnvironmentVariable("CODEX_MANAGED_CONFIG_PATH") == "fixture-managed-config"
                && Environment.GetEnvironmentVariable("HTTPS_PROXY") == "http://fixture.invalid:8080"
                && Environment.GetEnvironmentVariable("OPENAI_WORKLOAD_IDENTITY_CONTEXT") == "fixture-audit-context"
        }));
    }

    private static void WriteResult(JsonElement id, object result)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt64(), result }));
        Console.Out.Flush();
    }

    private static void WriteNotification(string method, object parameters)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { method, @params = parameters }));
        Console.Out.Flush();
    }
}
