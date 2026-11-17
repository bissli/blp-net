using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bloomberglp.Blpapi;
using CaptureFixtures;

const string Host = "localhost";
const int Port = 8194;
const string Auth = "AuthenticationType=OS_LOGON";
const string Usage = """
    Captures real Bloomberg responses as test fixtures for blp-net.

    Runs on a Windows host with a logged-in Bloomberg terminal. Sends every request case
    and two subscriptions, and writes manifest.json, one <service>.schema.txt per service
    and cases/<name>.json per case. A case that fails records its error and the run goes on.

    Usage: tools\CaptureFixtures\capture.cmd [options]

    Options:
      --out-dir <path>             Output folder. Default: ./blp-fixtures
      --day <yyyy-MM-dd>           Trading day of the intraday cases. Default: the last
                                   weekday before today.
      --subscribe-seconds <n>      Seconds to collect per subscription. Default: 60
      --eqs-screen <name>          Global or saved EQS screen. Default: Core Capital Ratios
      --help                       Print this text.

    Run it on a weekday during US market hours so the subscriptions receive live ticks.
    Exit code 1 when any capture failed.
    """;

var outDir = Path.Combine(Directory.GetCurrentDirectory(), "blp-fixtures");
var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(-1);
double subscribeSeconds = 60;
var eqsScreen = "Core Capital Ratios";
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--help":
            Console.WriteLine(Usage);
            return 0;
        case "--out-dir" when index + 1 < args.Length:
            outDir = args[++index];
            break;
        case "--day" when index + 1 < args.Length:
            day = DateOnly.ParseExact(args[++index], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            break;
        case "--subscribe-seconds" when index + 1 < args.Length:
            subscribeSeconds = double.Parse(args[++index], CultureInfo.InvariantCulture);
            break;
        case "--eqs-screen" when index + 1 < args.Length:
            eqsScreen = args[++index];
            break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete option: {args[index]}\n\n{Usage}");
            return 2;
    }
}

var casesDir = Path.Combine(outDir, "cases");
Directory.CreateDirectory(casesDir);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

var sessionOptions = new SessionOptions
{
    ServerHost = Host,
    ServerPort = Port,
    AuthenticationOptions = Auth,
};
using var session = new Session(sessionOptions);
if (!session.Start())
{
    Console.Error.WriteLine("Failed to start the Bloomberg session");
    return 1;
}

var recorder = new SessionRecorder(session);
var startupEvents = new JsonArray();
var failedCount = 0;
try
{
    recorder.Collect(startupEvents, TimeSpan.FromSeconds(3));
}
catch (Exception exception)
{
    failedCount++;
    startupEvents.Add(new JsonObject { ["error"] = exception.ToString() });
    Console.WriteLine($"startup events: FAILED ({exception.Message})");
}

void Capture(string name, Action<JsonObject> capture)
{
    var record = new JsonObject { ["case"] = name };
    try
    {
        capture(record);
        Console.WriteLine($"{name}: ok");
    }
    catch (Exception exception)
    {
        failedCount++;
        record["error"] = exception.ToString();
        Console.WriteLine($"{name}: FAILED ({exception.Message})");
    }

    File.WriteAllText(Path.Combine(casesDir, $"{name}.json"), record.ToJsonString(jsonOptions));
}

var correlationId = 1;
foreach (var captureCase in Cases.Build(day, eqsScreen))
{
    var id = correlationId++;
    Capture(captureCase.Name, record => recorder.CaptureRequest(captureCase, id, record));
}

foreach (var uri in new[] { "//blp/refdata", "//blp/bqlsvc", "//blp/mktdata" })
{
    var label = $"{uri["//blp/".Length..]} schema";
    try
    {
        if (!session.OpenService(uri)) throw new InvalidOperationException($"Failed to open {uri}");
        using var schema = new StringWriter();
        session.GetService(uri).Print(schema);
        File.WriteAllText(
            Path.Combine(outDir, $"{uri["//blp/".Length..]}.schema.txt"), schema.ToString());
        Console.WriteLine($"{label}: ok");
    }
    catch (Exception exception)
    {
        failedCount++;
        Console.WriteLine($"{label}: FAILED ({exception.Message})");
    }
}

(string Name, string[] Options, long FirstCorrelationId)[] subscriptions =
[
    ("subscription", [], 1000),
    ("subscription_interval", ["interval=5.0"], 2000),
];
foreach (var (name, options, firstCorrelationId) in subscriptions)
{
    Capture(
        name,
        record => recorder.CaptureSubscription(
            options, firstCorrelationId, TimeSpan.FromSeconds(subscribeSeconds), record));
}

session.Stop();
var manifest = new JsonObject
{
    ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    ["day"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    ["hostLocalZone"] = TimeZoneInfo.Local.Id,
    ["sdkVersion"] = VersionInfo.VersionString,
    ["os"] = RuntimeInformation.OSDescription,
    ["startupEvents"] = startupEvents,
    ["failedCount"] = failedCount,
};
File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest.ToJsonString(jsonOptions));
Console.WriteLine($"{failedCount} failures; fixtures in {outDir}");
return failedCount > 0 ? 1 : 0;
