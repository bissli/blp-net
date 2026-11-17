# blp-net

A C# client for the Bloomberg API. It runs on Bloomberg's managed .NET SDK
and talks to the terminal on the local host.

The library covers reference data (BDP), historical data (BDH),
intraday ticks and bars, saved equity screens (EQS) and live market data
subscriptions. Date-time values come back in the request's time zone, and `ForceString`
gives each number as plain text with trailing zeros dropped.

## Requirements

- .NET 10 SDK.
- A Windows host with a logged-in Bloomberg terminal, for any live
  request. The tests run without one.

## Build

```bash
dotnet build
dotnet test
```

`Directory.Build.props` sends all build output to `blp-net/<hash>` under
the local application data folder: `%LOCALAPPDATA%` on Windows,
`~/.local/share` on Linux. The hash comes from the checkout's path, so
two checkouts never share output. Dropbox locks the files it syncs, so
a build inside a Dropbox checkout fails. On Windows,
`tools\CaptureFixtures\build.cmd` publishes the fixture capture tool to
`%LOCALAPPDATA%\blp-net\CaptureFixtures`.

## Bloomberg SDK package

Bloomberg does not publish its .NET SDK on nuget.org. The repo carries
`Bloomberg.Blpapi` 3.26.10.1 in `packages/`, and `nuget.config` maps that
one package to the folder. Every other package restores from nuget.org.
A newer SDK goes into `packages/` and takes a matching version in
`src/Blp/Blp.csproj`.

## Usage

```csharp
using Blp;
using Blp.HistoricalData;
using Blp.ReferenceData;

using var client = await BlpClient.ConnectAsync(new BlpClientOptions());

var reference = await client.GetReferenceDataAsync(
    new ReferenceDataRequest(["IBM US Equity"], ["PX_LAST", "NAME"]));
object? lastPrice = reference.Values["IBM US Equity"]["PX_LAST"];

var history = await client.GetHistoricalDataAsync(
    new HistoricalDataRequest(
        ["IBM US Equity"], ["PX_LAST"], new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 31))
    {
        Period = Periodicity.Weekly,
    });
foreach (var row in history.Rows["IBM US Equity"])
{
    Console.WriteLine($"{row.Date} {row.Values["PX_LAST"]}");
}
```

`ConnectAsync` starts a session on `localhost:8194` with OS logon and
checks it with one reference data request. One client serves any number
of concurrent requests.

```csharp
using Blp.Eqs;
using Blp.IntradayBars;
using Blp.IntradayTicks;

var start = new DateTimeOffset(2024, 1, 2, 9, 30, 0, TimeSpan.FromHours(-5));
var end = start.AddMinutes(30);

var ticks = await client.GetIntradayTicksAsync(
    new IntradayTickRequest("IBM US Equity", start, end) { IncludeConditionCodes = true });

var bars = await client.GetIntradayBarsAsync(
    new IntradayBarRequest("IBM US Equity", start, end) { Interval = 5 });

var screen = await client.GetScreenerAsync(new EqsRequest("Core Capital Ratios"));
foreach (var (security, columns) in screen.Values)
{
    Console.WriteLine($"{security} {columns.Count}");
}
```

Intraday ticks and bars come back as one name-to-value row per tick or
bar, in time order. An EQS response keys each security's columns by the
names the screen sends. A screen saved under the user's own login takes
`ScreenType = EqsScreenType.Private`.

```csharp
using Blp.Subscriptions;

using var subscription = Subscription.Start(
    ["BID", "ASK"],
    (topic, row) => Console.WriteLine($"{topic} {row.GetValueOrDefault("BID")}"));
subscription.Add("IBM US Equity", "MSFT US Equity");

SubscriptionStatus status = subscription.GetStatus();
```

A `Subscription` runs its own session on `//blp/mktdata`, apart from
`BlpClient`. The callback runs on the SDK's dispatch thread, once per
tick, with each subscribed field the tick carries. `GetStatus` reports
each topic's state and, in `Failure`, why the subscription can no longer
deliver data. `Remove` drops topics, and `Stop` or `Dispose` ends the
session.

A response keys its values by the security string Bloomberg echoes. A
rejected security or field appears in `SecurityErrors` or `FieldErrors`
and leaves no value. A reference or historical request with
`RaiseSecurityError` or `RaiseFieldError` set instead raises
`RequestSecurityException` or `RequestFieldException` once the response
completes, security errors first. A failed request raises
`ResponseException`. A dead or missing session raises a
`SessionException`. A request that waits longer than its `IdleTimeout`
for the next event, or its `TotalTimeout` for the full response, raises
`RequestTimeoutException`. Each limit a request leaves unset takes the
client's: `BlpClientOptions.IdleTimeout`, or `BlpClientOptions.TotalTimeout`,
which defaults to no limit.

## Values

| Bloomberg type          | Value                                       |
| ----------------------- | ------------------------------------------- |
| number                  | the boxed CLR value (`double`, `long`, ...) |
| date                    | `DateOnly`                                  |
| date-time               | `DateTimeOffset` in the request's zone      |
| time                    | `TimeOnly` in the request's zone            |
| bulk field              | a list of name-to-value rows                |
| string, enumeration     | the trimmed string                          |
| null number, date, time | `null`                                      |
| other null              | `""`                                        |

Bloomberg sends date-times and times in UTC. The request's `TimeZone`,
`TimeZoneInfo.Local` by default, sets the zone of the returned values.
A time alone takes this host's current date before conversion.
