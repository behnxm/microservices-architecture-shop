using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Shop.ServiceDefaults;

/// <summary>
/// Lightweight CLI health-check client for container environments.
/// Enables minimal runtime container images without requiring curl or a shell.
/// </summary>
public static class HealthCheckCli
{
    public const string Flag = "--healthcheck";

    public static async Task<int> RunAsync(string[] args, int defaultPort = 8080, CancellationToken cancellationToken = default)
    {
        var port = defaultPort;

        // Optional port argument after --healthcheck (e.g. --healthcheck 8080)
        var flagIndex = Array.IndexOf(args, Flag);
        if (flagIndex >= 0 && flagIndex + 1 < args.Length && int.TryParse(args[flagIndex + 1], out var parsedPort))
        {
            port = parsedPort;
        }
        else if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var envPort))
        {
            port = envPort;
        }

        var url = $"http://127.0.0.1:{port}/alive";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                Console.Out.WriteLine($"Healthcheck PASSED: {url} -> {(int)response.StatusCode}");
                return 0;
            }

            Console.Error.WriteLine($"Healthcheck FAILED: {url} -> {(int)response.StatusCode}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Healthcheck ERROR: {url} -> {ex.Message}");
            return 1;
        }
    }
}
