/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System.Globalization;
using QuantConnect;
using QuantConnect.Logging;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Configuration;
using QuantConnect.Lean.DataSource.ThetaData;
using QuantConnect.Lean.DataSource.ThetaData.Models.Interfaces;
using QuantConnect.Lean.DataSource.ThetaData.Models.SubscriptionPlans;

const string Usage = """
    Writes the daily option data of a whole chain from ThetaData end-of-day reports into a LEAN data folder:
    1 universe file per day, and the yearly trade and quote zip files.

    Usage:
      QuantConnect.Lean.DataSource.ThetaData.DataWriter --data-folder <path> --ticker <SPY> --start <yyyyMMdd> --end <yyyyMMdd>
        [--security-type Option|IndexOption] [--target-option <SPXW>] [--plan Free|Value|Standard|Pro] [--rest-url <http://127.0.0.1:25503>]

    --start and --end are trading days, both inclusive. For an index option, --ticker is the index (SPX) and
    --target-option is the option root (SPXW). The default plan is Free.
    """;

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i + 1 < args.Length; i += 2)
{
    options[args[i].TrimStart('-')] = args[i + 1];
}
if (args.Length % 2 != 0 || !options.ContainsKey("data-folder") || !options.ContainsKey("ticker") || !options.ContainsKey("start") || !options.ContainsKey("end"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

Log.LogHandler = new ConsoleLogHandler();
var dataFolder = Path.GetFullPath(options["data-folder"]);
// LEAN reads the market hours from the data folder, so set it before the first use.
Config.Set("data-folder", dataFolder);
Globals.Reset();
// Symbol.Create of an equity reads its map file to find the first ticker, through the map file provider in Composer.
var mapFileProvider = QuantConnect.Util.Composer.Instance.GetExportedValueByTypeName<IMapFileProvider>("LocalDiskMapFileProvider");
mapFileProvider.Initialize(QuantConnect.Util.Composer.Instance.GetExportedValueByTypeName<IDataProvider>("DefaultDataProvider"));

var start = DateTime.ParseExact(options["start"], "yyyyMMdd", CultureInfo.InvariantCulture);
var end = DateTime.ParseExact(options["end"], "yyyyMMdd", CultureInfo.InvariantCulture);
var isIndexOption = options.GetValueOrDefault("security-type", "Option").Equals("IndexOption", StringComparison.OrdinalIgnoreCase);
var underlying = Symbol.Create(options["ticker"].ToUpperInvariant(), isIndexOption ? SecurityType.Index : SecurityType.Equity, Market.USA);
var canonical = isIndexOption
    ? Symbol.CreateCanonicalOption(underlying, options.GetValueOrDefault("target-option", underlying.Value).ToUpperInvariant(), Market.USA, null)
    : Symbol.CreateCanonicalOption(underlying);

ISubscriptionPlan plan = options.GetValueOrDefault("plan", "Free").ToUpperInvariant() switch
{
    "FREE" => new FreeSubscriptionPlan(),
    "VALUE" => new ValueSubscriptionPlan(),
    "STANDARD" => new StandardSubscriptionPlan(),
    "PRO" => new ProSubscriptionPlan(),
    var other => throw new ArgumentException($"Unknown plan '{other}'.")
};

var exchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, canonical, canonical.SecurityType);
using var client = new ThetaDataRestApiClient(options.GetValueOrDefault("rest-url", ThetaDataRestApiClient.DefaultRestApiUrl), plan.RateGate, plan.MaxConcurrentRequests);
var summary = new ThetaDataOptionDataWriter(client, dataFolder).Write(canonical, start, end, exchangeHours);

Console.WriteLine($"{canonical.ID.Symbol} {start:yyyy-MM-dd} to {end:yyyy-MM-dd}: {summary.Days} days, {summary.UniverseFiles} universe files, " +
    $"{summary.Contracts} contracts, {summary.TradeBars} trade bars, {summary.QuoteBars} quote bars");
foreach (var file in summary.ZipFiles)
{
    Console.WriteLine(file);
}
return 0;
