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

using System.Text;
using System.IO.Compression;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Data.Market;
using QuantConnect.Lean.DataSource.ThetaData.Models.Rest;

namespace QuantConnect.Lean.DataSource.ThetaData
{
    /// <summary>
    /// Writes the daily option data of a whole chain in LEAN's file format: the universe file of each day, and the
    /// yearly trade and quote zip files (<c>option/usa/daily/&lt;root&gt;_&lt;year&gt;_{trade|quote}_&lt;style&gt;.zip</c>).
    /// </summary>
    /// <remarks>
    /// LEAN's own path is too slow for a whole chain. <c>DownloaderDataProvider</c> asks for the yearly file of all contracts,
    /// and <c>LeanDataWriter</c> then rewrites that zip once per contract: 1,710 of 5,163 SPY contracts took 11 minutes.
    /// This writer sends 1 request per trading day and writes each zip once.
    /// </remarks>
    public class ThetaDataOptionDataWriter
    {
        private readonly ThetaDataRestApiClient _restApiClient;
        private readonly ThetaDataSymbolMapper _symbolMapper;
        private readonly ThetaDataOptionEndOfDayLoader _endOfDayLoader;
        private readonly string _dataFolder;

        /// <summary>
        /// What a <see cref="Write"/> call wrote.
        /// </summary>
        public record Summary(int Days, int UniverseFiles, int Contracts, int TradeBars, int QuoteBars, IReadOnlyList<string> ZipFiles);

        public ThetaDataOptionDataWriter(ThetaDataRestApiClient restApiClient, string dataFolder)
        {
            _restApiClient = restApiClient;
            _symbolMapper = new ThetaDataSymbolMapper();
            _endOfDayLoader = new ThetaDataOptionEndOfDayLoader(restApiClient);
            _dataFolder = dataFolder;
        }

        /// <summary>
        /// Writes the data of each trading day from <paramref name="firstDay"/> to <paramref name="lastDay"/>, both inclusive.
        /// A day with no data, such as a day ThetaData has not reported yet, writes nothing.
        /// </summary>
        /// <param name="canonical">The canonical option symbol, for example ?SPY or ?SPXW.</param>
        /// <param name="firstDay">The first trading day.</param>
        /// <param name="lastDay">The last trading day.</param>
        /// <param name="exchangeHours">The exchange hours of the option.</param>
        /// <remarks>
        /// A bar that the zip already holds for the same contract and day is replaced. Other bars of the zip stay.
        /// </remarks>
        public Summary Write(Symbol canonical, DateTime firstDay, DateTime lastDay, SecurityExchangeHours exchangeHours)
        {
            var timeZone = exchangeHours.TimeZone;
            var underlying = canonical.Underlying;
            IReadOnlyList<EndOfDayRow> underlyingRows;
            try
            {
                underlyingRows = _restApiClient.GetRows<EndOfDayRow>(ThetaDataEndpoints.EndOfDay(underlying.SecurityType), new Dictionary<string, string>
                {
                    [RequestParameters.Symbol] = _symbolMapper.GetBrokerageSymbol(underlying),
                    [RequestParameters.StartDate] = firstDay.ConvertToThetaDataDateFormat(),
                    [RequestParameters.EndDate] = lastDay.ConvertToThetaDataDateFormat()
                });
            }
            catch (ThetaDataRequestException e) when (e.IsEntitlementError)
            {
                // The option files do not need the underlying. Only the universe files lose their underlying line.
                Log.Error($"{nameof(ThetaDataOptionDataWriter)}: Your ThetaData plan does not include {underlying.SecurityType} end-of-day data, so the universe files have no underlying line. {e.Message}");
                underlyingRows = Array.Empty<EndOfDayRow>();
            }
            var underlyingBars = underlyingRows
                .Select(row => ThetaDataBarConverter.ToDailyTradeBar(row, underlying, timeZone))
                .Where(bar => bar != null)
                .GroupBy(bar => bar!.Time.Date)
                .ToDictionary(x => x.Key, x => x.First());

            var bars = new List<BaseData>();
            var days = 0;
            var universeFiles = 0;
            var includeOpenInterest = true;
            foreach (var day in Time.EachTradeableDay(exchangeHours, firstDay.Date, lastDay.Date))
            {
                var rows = _endOfDayLoader.GetRows(canonical.ID.Symbol, day);
                if (rows.Count == 0)
                {
                    Log.Trace($"{nameof(ThetaDataOptionDataWriter)}: no data for {canonical.ID.Symbol} on {day:yyyy-MM-dd}");
                    continue;
                }
                days++;

                // After 1 denial the files are written without open interest, so the Free plan sends no denied request each day.
                List<BaseData>? openInterest = null;
                if (includeOpenInterest)
                {
                    try
                    {
                        openInterest = ThetaDataProvider.GetChainOpenInterest(_restApiClient, _symbolMapper, canonical, day, timeZone);
                    }
                    catch (ThetaDataRequestException e) when (e.IsEntitlementError)
                    {
                        includeOpenInterest = false;
                        Log.Trace($"{nameof(ThetaDataOptionDataWriter)}: Your ThetaData plan does not include option open interest, so the universe files have none. {e.Message}");
                    }
                }

                var csv = ThetaDataOptionUniverseCsv.Build(canonical, rows, underlyingBars.GetValueOrDefault(day), _symbolMapper, timeZone, openInterest?.Cast<OpenInterest>());
                if (csv != null)
                {
                    var universeFile = Path.Combine(LeanData.GenerateUniversesDirectory(_dataFolder, canonical), $"{day:yyyyMMdd}.csv");
                    Directory.CreateDirectory(Path.GetDirectoryName(universeFile)!);
                    File.WriteAllText(universeFile, csv);
                    universeFiles++;
                }

                foreach (var row in rows)
                {
                    var contract = _symbolMapper.GetLeanOptionSymbol(row.Contract!, canonical);
                    if (ThetaDataBarConverter.ToDailyTradeBar(row, contract, timeZone) is TradeBar trade)
                    {
                        bars.Add(trade);
                    }
                    if (ThetaDataBarConverter.ToDailyQuoteBar(row, contract, timeZone) is QuoteBar quote)
                    {
                        bars.Add(quote);
                    }
                }
                Log.Trace($"{nameof(ThetaDataOptionDataWriter)}: {canonical.ID.Symbol} {day:yyyy-MM-dd}: {rows.Count} contracts");
            }

            var zipFiles = WriteZipFiles(bars);
            return new Summary(days, universeFiles, bars.Select(x => x.Symbol).Distinct().Count(),
                bars.Count(x => x is TradeBar), bars.Count(x => x is QuoteBar), zipFiles);
        }

        /// <summary>
        /// Writes the bars into their yearly zip files, opening and saving each zip once.
        /// </summary>
        private List<string> WriteZipFiles(List<BaseData> bars)
        {
            var written = new List<string>();
            var byZip = bars.GroupBy(bar =>
            {
                var tickType = bar is QuoteBar ? TickType.Quote : TickType.Trade;
                return LeanData.GenerateZipFilePath(_dataFolder, bar.Symbol, bar.Time, Resolution.Daily, tickType);
            });

            foreach (var zip in byZip)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(zip.Key)!);
                using var archive = ZipFile.Open(zip.Key, ZipArchiveMode.Update);
                foreach (var entry in zip.GroupBy(bar => LeanData.GenerateZipEntryName(bar.Symbol, bar.Time, Resolution.Daily, bar is QuoteBar ? TickType.Quote : TickType.Trade)))
                {
                    // Lines keyed by their date, so a bar of the same day replaces the old one.
                    var lines = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    var existing = archive.GetEntry(entry.Key);
                    if (existing != null)
                    {
                        using (var reader = new StreamReader(existing.Open()))
                        {
                            string? line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.Length > 0)
                                {
                                    lines[line[..line.IndexOf(',')]] = line;
                                }
                            }
                        }
                        existing.Delete();
                    }
                    foreach (var bar in entry)
                    {
                        var line = LeanData.GenerateLine(bar, bar.Symbol.SecurityType, Resolution.Daily);
                        lines[line[..line.IndexOf(',')]] = line;
                    }

                    var newEntry = archive.CreateEntry(entry.Key);
                    using var writer = new StreamWriter(newEntry.Open(), new UTF8Encoding(false));
                    writer.Write(string.Join("\n", lines.Values));
                }
                written.Add(zip.Key);
            }
            return written;
        }
    }
}
