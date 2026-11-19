using System.Text.Json;

namespace Serval.Transport.Benchmarks;

public static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["server", var transport, var port])
            {
                await BenchmarkServer.RunAsync(transport, int.Parse(port));
            }
            else
            {
                await BenchmarkRunner.RunAsync(args);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
