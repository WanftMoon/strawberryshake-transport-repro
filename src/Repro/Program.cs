using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Repro.Client;
using StrawberryShake.Serialization;

// Minimal repro for StrawberryShake.Transport.Http 16.7.5; modes are described in README.md.
switch (args.FirstOrDefault())
{
    case "thread":
        ThreadMode.Run();
        break;
    case "alloc":
        await AllocMode.Run();
        break;
    case "internal-error":
        await InternalErrorMode.Run();
        break;
    case "hash":
        await HashMode.Run();
        break;
    default:
        Console.WriteLine("usage: dotnet run -c Release -- thread | alloc | internal-error | hash");
        break;
}

internal static class Client
{
    public static IReproClient Create(Func<byte[]> body, bool delayHeaders = false, IServiceCollection? services = null)
    {
        services ??= new ServiceCollection();
        services
            .AddReproClient()
            .ConfigureHttpClient(
                c => c.BaseAddress = new Uri("http://repro.local/graphql"),
                b => b.ConfigurePrimaryHttpMessageHandler(() => new StubHandler(body, delayHeaders)));
        return services.BuildServiceProvider().GetRequiredService<IReproClient>();
    }

    public static byte[] Points(int count)
    {
        var random = new Random(1);
        var sb = new StringBuilder(count * 64).Append("{\"data\":{\"points\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                sb.Append(',');
            var x = (1_700_000_000 + i * 0.125).ToString("R", CultureInfo.InvariantCulture);
            var y = (15_000 * (0.5 + random.NextDouble())).ToString("G6", CultureInfo.InvariantCulture);
            sb.Append("{\"__typename\":\"Point\",\"x\":").Append(x).Append(",\"y\":\"").Append(y).Append("\"}");
        }

        return Encoding.UTF8.GetBytes(sb.Append("]}}").ToString());
    }
}

/// <summary>Answers every request with a buffered application/json body; optionally sends the headers asynchronously.</summary>
internal sealed class StubHandler(Func<byte[]> body, bool delayHeaders) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (delayHeaders)
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);

        var content = new ByteArrayContent(body());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

/// <summary>A String serializer that records the thread the result builder runs on.</summary>
internal sealed class RecordingStringSerializer() : ScalarSerializer<string, string>("String")
{
    public static readonly ConcurrentDictionary<int, int> Threads = new();

    public override string Parse(string serializedValue)
    {
        Threads.AddOrUpdate(Environment.CurrentManagedThreadId, 1, (_, n) => n + 1);
        return serializedValue;
    }

    protected override string Format(string runtimeValue) => runtimeValue;
}

internal static class ThreadMode
{
    public static void Run()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISerializer>(new RecordingStringSerializer());
        var client = Client.Create(() => Client.Points(1_000), delayHeaders: true, services);

        // a single-threaded context, like a UI thread
        using var context = new SingleThreadContext();
        var uiThread = context.Run(async () =>
        {
            var result = await client.Points.ExecuteAsync();
            return (Environment.CurrentManagedThreadId, result.Data!.Points.Count);
        });

        foreach (var (thread, count) in RecordingStringSerializer.Threads)
            Console.WriteLine($"result builder parsed {count} values on thread {thread}{(thread == context.ThreadId ? "  <-- the UI-like context thread" : "")}");
        Console.WriteLine($"context thread {context.ThreadId}; caller resumed on {uiThread.Item1}; points {uiThread.Item2}");
    }
}

internal static class AllocMode
{
    public static async Task Run()
    {
        var body = Client.Points(30_000);
        var client = Client.Create(() => body);

        Console.WriteLine($"response body: {body.Length:N0} bytes, 30,000 points");
        Console.WriteLine($"ExecuteAsync:                              {await Measure(async () => (await client.Points.ExecuteAsync()).Data!.Points.Count):N0} KB/call, {Time(async () => (await client.Points.ExecuteAsync()).Data!.Points.Count):F2} ms");
        Console.WriteLine($"JsonDocument.ParseAsync, then disposed:   {await Measure(async () => { using var d = await JsonDocument.ParseAsync(new MemoryStream(body)); return 0; }):N0} KB/call");
        Console.WriteLine($"JsonDocument.ParseAsync, never disposed:  {await Measure(async () => { var d = await JsonDocument.ParseAsync(new MemoryStream(body)); return 0; }):N0} KB/call");
        Console.WriteLine($"one JsonDocument.Parse (time only):        {Time(() => { using var d = JsonDocument.Parse(body); return Task.FromResult(0); }):F2} ms");
    }

    private static async Task<double> Measure(Func<Task<int>> run)
    {
        for (var i = 0; i < 10; i++)
            await run();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        const int n = 50;
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < n; i++)
            await run();
        return (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)n / 1024d;
    }

    private static double Time(Func<Task<int>> run)
    {
        for (var i = 0; i < 10; i++)
            run().GetAwaiter().GetResult();
        var times = new double[50];
        for (var i = 0; i < times.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            run().GetAwaiter().GetResult();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        return times[times.Length / 2];
    }
}

internal static class InternalErrorMode
{
    public static async Task Run()
    {
        var client = Client.Create(() => Encoding.UTF8.GetBytes("{\"data\":null,\"errors\":[{\"message\":\"Internal Execution Error\"}]}"));
        try
        {
            var result = await client.Points.ExecuteAsync();
            Console.WriteLine($"no exception; errors: {string.Join("; ", result.Errors.Select(e => e.Message))}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex.GetType().FullName}: {ex.Message}");
            Console.WriteLine(ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim());
        }
    }
}

internal static class HashMode
{
    public static async Task Run()
    {
        var body = Client.Points(30_000);
        var client = Client.Create(() => body);
        var result = await client.Points.ExecuteAsync();
        var sb = new StringBuilder();
        foreach (var p in result.Data!.Points)
            sb.Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(p.Y).Append(';');
        Console.WriteLine($"points={result.Data.Points.Count} sha256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))}");

        var errorClient = Client.Create(() => Encoding.UTF8.GetBytes("{\"data\":null,\"errors\":[{\"message\":\"boom\",\"path\":[\"points\"],\"extensions\":{\"code\":\"X\"}}],\"extensions\":{\"k\":1}}"));
        var error = await errorClient.Points.ExecuteAsync();
        Console.WriteLine($"error case: data={(error.Data is null ? "null" : "set")} errors=[{string.Join("; ", error.Errors.Select(e => $"{e.Message} code={e.Code} path={string.Join('.', e.Path ?? [])}"))}] extensions=[{string.Join(",", error.Extensions?.Keys ?? [])}]");
    }
}

/// <summary>Runs async code on one dedicated thread with its own SynchronizationContext, like a UI thread.</summary>
internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
    private readonly Thread _thread;
    private int _threadId;

    public SingleThreadContext()
    {
        _thread = new Thread(Pump) { IsBackground = true };
    }

    public int ThreadId => _threadId;

    public T Run<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>();
        _thread.Start();
        Post(async _ =>
        {
            try
            {
                done.SetResult(await work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task.GetAwaiter().GetResult();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    private void Pump()
    {
        _threadId = Environment.CurrentManagedThreadId;
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            callback(state);
    }

    public void Dispose() => _queue.CompleteAdding();
}
