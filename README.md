# blp-net

A C# client for the Bloomberg API. It runs on Bloomberg's managed .NET SDK
and talks to the terminal on the local host.

The library covers reference data (BDP) and historical data (BDH).
Date-time values come back in the request's time zone, and `ForceString`
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

A response keys its values by the security string Bloomberg echoes. A
rejected security or field appears in `SecurityErrors` or `FieldErrors`
and leaves no value. A failed request raises `ResponseException`. A dead
or missing session raises a `SessionException`. A request that waits
longer than `BlpClientOptions.IdleTimeout` for its next event raises
`RequestTimeoutException`.

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
