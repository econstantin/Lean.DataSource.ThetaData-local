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
using NodaTime;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Lean.DataSource.ThetaData.Models.Rest;

namespace QuantConnect.Lean.DataSource.ThetaData
{
    /// <summary>
    /// Builds 1 day of an option universe file, <c>option/usa/universes/&lt;root&gt;/&lt;yyyyMMdd&gt;.csv</c>,
    /// from the end-of-day rows of the chain. The rows match those of LEAN's <c>UniverseExtensions.RunUniverseDownloader</c>.
    /// </summary>
    /// <remarks>
    /// The rows have open interest only when the caller passes it, and no implied volatility or greeks: LEAN writes 0 for each greek.
    /// </remarks>
    public static class ThetaDataOptionUniverseCsv
    {
        /// <summary>
        /// Builds the CSV text, with its header line.
        /// </summary>
        /// <param name="canonical">The canonical option symbol, for example ?SPY.</param>
        /// <param name="rows">The end-of-day rows of the chain on 1 day.</param>
        /// <param name="underlyingBar">The daily bar of the underlying on that day, or null.</param>
        /// <param name="symbolMapper">Maps ThetaData contracts to LEAN symbols.</param>
        /// <param name="exchangeTimeZone">The time zone of the option exchange.</param>
        /// <param name="openInterest">The open interest of the contracts on that day, or null when the plan does not include it.</param>
        /// <returns>The CSV text, or null when no row holds a quote or a trade.</returns>
        public static string? Build(Symbol canonical, IEnumerable<EndOfDayRow> rows, TradeBar? underlyingBar, ThetaDataSymbolMapper symbolMapper, DateTimeZone exchangeTimeZone,
            IEnumerable<OpenInterest>? openInterest = null)
        {
            var universeDataBySymbol = new Dictionary<Symbol, DerivativeUniverseData>();
            foreach (var row in rows)
            {
                var contract = symbolMapper.GetLeanOptionSymbol(row.Contract!, canonical);
                var quoteBar = ThetaDataBarConverter.ToDailyQuoteBar(row, contract, exchangeTimeZone);
                var tradeBar = ThetaDataBarConverter.ToDailyTradeBar(row, contract, exchangeTimeZone);
                // Same order as RunUniverseDownloader: the quote sets the prices, the trade adds the volume.
                if (quoteBar != null)
                {
                    universeDataBySymbol[contract] = new DerivativeUniverseData(quoteBar);
                }
                if (tradeBar != null)
                {
                    if (universeDataBySymbol.TryGetValue(contract, out var universeData))
                    {
                        universeData.UpdateByTradeBar(tradeBar);
                    }
                    else
                    {
                        universeDataBySymbol[contract] = new DerivativeUniverseData(tradeBar);
                    }
                }
            }
            if (universeDataBySymbol.Count == 0)
            {
                return null;
            }
            // As in RunUniverseDownloader: open interest updates a contract's row, or adds a row for a contract with no quote or trade.
            foreach (var point in openInterest ?? Enumerable.Empty<OpenInterest>())
            {
                if (universeDataBySymbol.TryGetValue(point.Symbol, out var universeData))
                {
                    universeData.UpdateByOpenInterest(point);
                }
                else
                {
                    universeDataBySymbol[point.Symbol] = new DerivativeUniverseData(point);
                }
            }
            if (underlyingBar != null)
            {
                universeDataBySymbol[underlyingBar.Symbol] = new DerivativeUniverseData(underlyingBar);
            }

            var csv = new StringBuilder();
            csv.AppendLine($"#{OptionUniverse.CsvHeader(canonical.SecurityType)}");
            foreach (var universeData in universeDataBySymbol
                .OrderBy(x => x.Key.Underlying != null)
                .ThenBy(x => x.Key.SecurityType.IsOption() ? x.Key.ID.OptionRight : 0)
                .ThenBy(x => x.Key.SecurityType.IsOption() ? x.Key.ID.StrikePrice : 0)
                .ThenBy(x => x.Key.ID.Date)
                .ThenBy(x => x.Key.ID))
            {
                csv.AppendLine(universeData.Value.ToCsv());
            }
            return csv.ToString();
        }
    }
}
