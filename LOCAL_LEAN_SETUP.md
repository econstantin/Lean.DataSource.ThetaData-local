# Use the ThetaData connector with a local LEAN CLI

This guide builds the connector from this checkout, adds it to a local LEAN
engine image, and runs a download, a research notebook, and a backtest on
ThetaData end-of-day data. Every command here ran on 2026-10-28 on Windows 11,
in PowerShell 7, on the Free plan.

## What you need

- Theta Terminal v3 running and logged in. It listens on port 25503. Check it:

  ```powershell
  Invoke-RestMethod 'http://127.0.0.1:25503/v3/stock/history/eod?symbol=SPY&start_date=20261020&end_date=20261020&format=json'
  ```

  The answer holds 1 row with `open`, `high`, `low`, and `close`.
- Docker Desktop running.
- The .NET 10 SDK.
- The `lean` CLI installed from the `local` branch of the `lean-cli` fork. That
  branch has the `--no-auth-needed` option. Without it, the CLI installs
  QuantConnect's own ThetaData package, which needs a QuantConnect module
  license and still uses API v2.
- A LEAN engine image and research image. This guide extends
  `lean-cli/engine:local` and `lean-cli/research:local`. The official
  `quantconnect/lean:latest` and `quantconnect/research:latest` work the same way.
- `Lean`, the LEAN source checkout, next to this checkout. The project file
  links `..\..\Lean\Common\Properties\SharedAssemblyInfo.cs`.

## Keep the ThetaData login secret

The terminal holds the ThetaData login. The connector never sees it.

- Give the terminal an API key with the `THETADATA_API_KEY` environment
  variable, or put the email and password in `creds.txt` next to
  `ThetaTerminalv3.jar` (https://thetadata.net/docs/Articles/Getting-Started/Getting-Started.html).
- Keep `creds.txt` out of every git repository.
- Do not add `--verbose` to a `lean` command on a machine that is logged in to
  QuantConnect: the CLI then prints the account's organization data.

## 1. Build the connector

```powershell
$root = 'C:\Users\econs\Dev\Lean-Local'
$connector = "$root\Lean.DataSource.ThetaData-local"
dotnet build -c Release "$connector\QuantConnect.ThetaData\QuantConnect.DataSource.ThetaData.csproj"
dotnet build -c Release "$connector\QuantConnect.ThetaData.Tests\QuantConnect.DataSource.ThetaData.Tests.csproj"
dotnet test "$connector\QuantConnect.ThetaData.Tests\bin\Release\QuantConnect.Lean.DataSource.ThetaData.Tests.dll"
```

The test run ends with `Passed!  - Failed:     0, Passed:    92` (2026-10-28, commit `75cc2dd`). These tests
need no terminal. The tests that call a real terminal are marked `Explicit`.
Run the ones that need only end-of-day data like this:

```powershell
dotnet test "$connector\QuantConnect.ThetaData.Tests\bin\Release\QuantConnect.Lean.DataSource.ThetaData.Tests.dll" --filter "FullyQualifiedName~ThetaDataTerminalV3Tests"
```

To test against a terminal on another address, set `QC_THETADATA_REST_URL`.

## 2. Add the connector to the images

The Dockerfile copies the connector DLL into `/Lean/Launcher/bin/Debug`, the
folder from which the engine loads plugins. It changes no other file of the
base image.

```powershell
$output = "$connector\QuantConnect.ThetaData\bin\Release"
docker build -f "$connector\local-lean\Dockerfile" --build-arg BASE_IMAGE=lean-cli/engine:local -t lean-cli/engine:thetadata $output
docker build -f "$connector\local-lean\Dockerfile" --build-arg BASE_IMAGE=lean-cli/research:local -t lean-cli/research:thetadata $output
```

Check that the engine image holds the new build. The 2 hashes must be equal:

```powershell
docker run --rm --entrypoint sha256sum lean-cli/engine:thetadata /Lean/Launcher/bin/Debug/QuantConnect.Lean.DataSource.ThetaData.dll
(Get-FileHash "$output\QuantConnect.Lean.DataSource.ThetaData.dll" -Algorithm SHA256).Hash.ToLower()
```

Build the 2 images again after each change to the connector.

## 3. Use a separate LEAN workspace

**Warning:** a ThetaData run downloads into the workspace's data folder and
replaces files there. For example, it writes `equity/usa/daily/spy.zip` with
data from 2023-06-01 only, the first date of the Free plan. Use a workspace
whose data folder holds no data you need.

```powershell
$workspace = "$root\thetadata-smoke"
New-Item -ItemType Directory $workspace | Out-Null
Copy-Item "$root\Lean\Data" "$workspace\data" -Recurse
Copy-Item 'C:\LeanWork\lean.json.example' "$workspace\lean.json"
```

`lean.json.example` is a `lean.json` with every secret empty and
`"data-folder": "data"`. Any such file works.

Every `lean` command below runs in the workspace and takes these options.
`host.docker.internal` is the address of the Windows host inside a container.

```powershell
Set-Location $workspace
$theta = @(
    '--thetadata-subscription-plan', 'Free',
    '--thetadata-rest-url', 'http://host.docker.internal:25503',
    '--thetadata-ws-url', 'ws://host.docker.internal:25520/v1/events',
    '--no-update', '--no-auth-needed'
)
```

**Caution:** without `--thetadata-rest-url`, the CLI uses its built-in default
`http://host.docker.internal:25510`, the retired v2 port. The connector then
logs a warning and every request fails to connect.

## 4. Write the option data

A daily option backtest reads 2 kinds of files:

- 1 universe file per day, `data/option/usa/universes/spy/<yyyyMMdd>.csv`, for
  the option chain.
- 1 zip file per year and data type,
  `data/option/usa/daily/spy_<year>_{trade|quote}_american.zip`, with the daily
  bars of every contract.

LEAN downloads a missing file by itself, but for the yearly zip that does not
work in practice. LEAN asks for the whole chain from January 1 to yesterday,
then rewrites the zip once per contract: about 30,000 SPY contracts a year.
LEAN also downloads the zip again when it is older than 7 days
(`downloader-data-update-period`).

So write both kinds of files ahead with the writer in this repository. It sends
1 request per trading day and writes each zip once:

```powershell
dotnet build -c Release "$connector\QuantConnect.ThetaData.DataWriter\QuantConnect.DataSource.ThetaData.DataWriter.csproj"
& "$connector\QuantConnect.ThetaData.DataWriter\bin\Release\QuantConnect.Lean.DataSource.ThetaData.DataWriter.exe" --data-folder "$workspace\data" --ticker SPY --start 20261016 --end 20261023
```

- `--start` and `--end` are both inclusive.
- The run above took 46 s and printed
  `SPY 2026-10-16 to 2026-10-23: 6 days, 6 universe files, 14400 contracts, 32950 trade bars, 76842 quote bars`.
- For SPXW, pass `--security-type IndexOption --ticker SPX --target-option SPXW`.
- A second run over the same days replaces those bars and keeps the others.
- A whole year of SPY is about 250 requests. On 2026-10-28 a day took 2 to 20 s.

To write only the universe files, the CLI also works:

```powershell
lean data download --data-provider-historical ThetaData --data-type Universe --resolution Daily --security-type Option --market usa --ticker SPY --start 20261019 --end 20261024 --image lean-cli/engine:thetadata @theta
```

- For `Universe`, LEAN writes a file for each trading day from `--start` to `--end`, both included. The command above writes 2026-10-19 to 2026-10-23 (2026-10-24 is a Saturday). A run with `--start 20261022 --end 20261023` wrote 2 files. The CLI rejects an `--end` that is not after `--start`.
- Each day takes 1 request of about 5 MB and about 10 to 20 seconds.
- The log line `Generated for SPY ... on 2026/10/20 with 12727 entries` is 1
  row for SPY and 12,726 contract rows.
- On the Free plan, the log has 1 error for open interest (HTTP 403). The files
  are still written, with an empty open interest column.

**Caution:** the universe rows have 0 for delta, gamma, vega, theta, and rho,
and no implied volatility, because LEAN's universe writer does not compute
them. A universe filter by Greeks or implied volatility selects nothing on this
data. The rows have open interest only when the plan includes it, so on the
Free plan an open interest filter also selects nothing. Filter by strike and
expiration.

Avoid `--data-type Trade` or `Quote` with `--security-type Option` for a whole
chain. The connector returns the 5,000 or more contracts of a day in about 20
seconds, but LEAN's file writer then rewrites the yearly zip file once per
contract. On 2026-10-28 it wrote 1,710 of 5,163 contracts in 11 minutes.

## 5. Run a backtest

`examples/ThetaDataOptionSmoke/main.py` adds SPY and its options, selects 1
call, and logs the chain size and the call's prices each day. It places no
order.

```powershell
New-Item -ItemType Directory "$workspace\ThetaDataOptionSmoke" -Force | Out-Null
Copy-Item "$connector\examples\ThetaDataOptionSmoke\main.py" "$workspace\ThetaDataOptionSmoke\"
lean backtest ThetaDataOptionSmoke --data-provider-historical ThetaData --image lean-cli/engine:thetadata @theta
```

The run took about 10 s. The log of the run,
`ThetaDataOptionSmoke/backtests/<timestamp>/log.txt`, held these lines on
2026-10-28:

```text
2026-10-19 16:00:00 2026-10-19 chain=60 underlying=766.48
2026-10-19 16:00:00 selected SPY   261023C00766000 expiry=2026-10-23 strike=766.0
2026-10-19 16:00:00 2026-10-19 SPY   261023C00766000 bid=5.41 ask=5.46 last=5.48 volume=1865
2026-10-20 16:00:00 2026-10-20 chain=60 underlying=769.05
2026-10-20 16:00:00 2026-10-20 SPY   261023C00766000 bid=6.35 ask=6.45 last=6.34 volume=5289
...
2026-10-23 16:00:00 2026-10-23 SPY   261023C00766000 bid=0.0 ask=0.01 last=0.01 volume=180101
```

- The log also holds `ThetaDataProvider.ValidateSubscription(): skipped, no-auth-needed is set.`
  That line shows that the engine loaded the local build.
- On the Free plan it holds 1 error for open interest (HTTP 403).
- LEAN stamps a daily bar at the 16:00 close. The quote in it is the NBBO of
  ThetaData's report at about 17:15 ET.
- `bid=0.0` means no bid: the connector leaves a missing side empty, and LEAN
  shows an empty side as 0.
- LEAN selects the chain of a day from the universe of the day before. On
  2026-10-22 the 766 call was outside that chain, so the log has no line for it.

## 6. Run research

`examples/ThetaDataOptionSmoke/research.ipynb` loads SPY bars, the chain of
2026-10-23, and the daily history of 1 call.

```powershell
Copy-Item "$connector\examples\ThetaDataOptionSmoke\research.ipynb" "$workspace\ThetaDataOptionSmoke\"
lean research ThetaDataOptionSmoke --data-provider-historical ThetaData --image lean-cli/research:thetadata @theta
```

The command opens Jupyter Lab. Open `research.ipynb` and run all cells. On
2026-10-28 the notebook printed the SPY bars of 2026-10-19 to 2026-10-23
(close 766.48 to 759.97), `contracts: 12396, expirations: 32`, and the daily bid
and ask of the 2026-10-30 760 call.

**Caution:** a chain from `option_chain` comes from the universe file. Its bid
and ask are both the universe price (the mid quote), with sizes of 0. Use
`qb.history` of a contract for its real bid and ask.

To run the notebook without a browser, start Jupyter detached and run the
notebook in the container:

```powershell
lean research ThetaDataOptionSmoke --data-provider-historical ThetaData --image lean-cli/research:thetadata @theta --detach --no-open --port 8899
$container = docker ps --filter ancestor=lean-cli/research:thetadata --format '{{.Names}}'
docker exec -w /Lean/Launcher/bin/Debug/Notebooks $container jupyter nbconvert --to notebook --execute research.ipynb --output /tmp/research-out.ipynb
docker stop $container
```

## 7. Run again without ThetaData

With `--data-provider-historical ThetaData`, LEAN reads a file that exists and
downloads a missing one. It also downloads a yearly or daily file again when
the file is older than 7 days (`downloader-data-update-period`). A universe
file has a date in its name and is never downloaded again. To run only from
local files, use the `Local` data provider:

```powershell
lean backtest ThetaDataOptionSmoke --data-provider-historical Local --image lean-cli/engine:thetadata --no-update --no-auth-needed
```

The engine log names `DefaultDataProvider`. On 2026-10-28 this run printed the
same 10 log lines as the ThetaData run above, and sent no request to the
terminal.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `cannot connect to Theta Terminal at http://host.docker.internal:25510/v3/` | the CLI default is the v2 port | pass `--thetadata-rest-url http://host.docker.internal:25503` |
| `The 'thetadata-rest-url' value ... names the ThetaData v2 API` | the address ends in `/v2` | remove `/v2`; the connector adds `/v3` |
| `cannot connect to Theta Terminal at http://127.0.0.1:25503/v3/` inside a run | `127.0.0.1` is the container itself | use `host.docker.internal` |
| `Your ThetaData plan does not include Option OpenInterest data` | the plan denies the endpoint (HTTP 403) | expected on the Free plan; the run goes on without that data |
| `The current user's subscription plan does not support the requested resolution: Minute` | the Free plan has daily data only | use `Resolution.DAILY`, or a paid plan and its `--thetadata-subscription-plan` |
| The engine log has no `ThetaDataProvider.ValidateSubscription(): skipped, no-auth-needed is set.` | the image lacks the local build, or `--no-auth-needed` is missing | build the images again (step 2); check the hash |
| An empty option chain, with `DirectoryNotFoundException` for `universes/<root>` in the log | an older connector build: LEAN does not create the universe folder | rebuild; this build creates the folder |
| Requests answer HTTP 429 | more concurrent requests than the plan allows | lower `thetadata-max-concurrent-requests` in `lean.json` |
| A request for data before 2023-06-01 returns nothing on the Free plan | the plan starts at 2023-06-01 (HTTP 403 before it) | expected |
