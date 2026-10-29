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

using NodaTime;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Securities;
using QuantConnect.Configuration;
using System.Collections.Concurrent;

namespace QuantConnect.Lean.DataSource.ThetaData
{
    /// <summary>
    /// The <see cref="IDataDownloader"/> that LEAN's <c>DownloaderDataProvider</c> and <c>lean data download</c> call.
    /// </summary>
    /// <remarks>
    /// LEAN builds an option universe file by calling <see cref="Get"/> with the canonical option symbol (for example ?SPY)
    /// at daily resolution, once for each of Quote, Trade, and OpenInterest (UniverseExtensions.RunUniverseDownloader).
    /// </remarks>
    public class ThetaDataDownloader : IDataDownloader, IDisposable
    {
        /// <summary>
        /// The history provider that sends the requests.
        /// </summary>
        private readonly ThetaDataProvider _historyProvider;

        /// <inheritdoc cref="MarketHoursDatabase" />
        private readonly MarketHoursDatabase _marketHoursDatabase;

        /// <summary>
        /// The most contracts of a canonical intraday download that are requested at the same time.
        /// </summary>
        private readonly int _maxParallelContracts;

        /// <summary>
        /// Initializes a new instance of the <see cref="ThetaDataDownloader"/>
        /// </summary>
        public ThetaDataDownloader()
            : this(new ThetaDataProvider(Config.Get("thetadata-subscription-plan", "Free")))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ThetaDataDownloader"/> on a given provider. Tests use it.
        /// </summary>
        public ThetaDataDownloader(ThetaDataProvider historyProvider)
        {
            _historyProvider = historyProvider;
            _marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
            _maxParallelContracts = Math.Max(1, historyProvider.MaxConcurrentRequests);
        }

        /// <summary>
        /// Gets the data of 1 symbol. A canonical option symbol gets the data of every contract of the chain.
        /// </summary>
        /// <returns>
        /// The data. Null when the request is not supported, or when the plan does not include the data of a contract.
        /// Empty, never null, for an option chain.
        /// </returns>
        public IEnumerable<BaseData>? Get(DataDownloaderGetParameters downloadParameters)
        {
            var symbol = downloadParameters.Symbol;

            var dataType = LeanData.GetDataType(downloadParameters.Resolution, downloadParameters.TickType);
            var exchangeHours = _marketHoursDatabase.GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType);
            var dataTimeZone = _marketHoursDatabase.GetDataTimeZone(symbol.ID.Market, symbol, symbol.SecurityType);

            if (ThetaDataProvider.IsSupportedCanonicalOption(symbol) && downloadParameters.Resolution == Resolution.Daily)
            {
                // UniverseExtensions.RunUniverseDownloader writes data/option/usa/universes/<root>/<date>.csv
                // without creating the folder. For a root with no folder yet the write throws, LEAN logs the error,
                // and the algorithm sees an empty option chain (issue #9).
                Directory.CreateDirectory(LeanData.GenerateUniversesDirectory(Globals.DataFolder, symbol));
            }

            // A daily request for a whole chain uses 1 bulk request per day. Other canonical requests go contract by contract.
            if (symbol.IsCanonical() && downloadParameters.Resolution != Resolution.Daily)
            {
                return EmptyIfSupportedChain(symbol, GetCanonicalOptionHistoryByContract(
                    symbol,
                    downloadParameters.StartUtc,
                    downloadParameters.EndUtc,
                    dataType,
                    downloadParameters.Resolution,
                    exchangeHours,
                    dataTimeZone,
                    downloadParameters.TickType));
            }

            var historyRequest = new HistoryRequest(
                startTimeUtc: downloadParameters.StartUtc,
                endTimeUtc: downloadParameters.EndUtc, dataType,
                symbol: symbol,
                resolution: downloadParameters.Resolution,
                exchangeHours: exchangeHours,
                dataTimeZone: dataTimeZone,
                fillForwardResolution: downloadParameters.Resolution,
                includeExtendedMarketHours: true,
                isCustomData: false,
                dataNormalizationMode: DataNormalizationMode.Raw,
                tickType: downloadParameters.TickType);

            return EmptyIfSupportedChain(symbol, _historyProvider.GetHistory(historyRequest));
        }

        /// <summary>
        /// Returns an empty result instead of null for an equity or index option chain.
        /// </summary>
        /// <remarks>
        /// LEAN's CanonicalDataDownloaderDecorator reads null for a canonical symbol as "canonical symbols are not supported".
        /// It then asks LiveOptionChainProvider, which calls QuantConnect's servers, for the chain and downloads contract by contract.
        /// On the Free plan the open interest request of each universe day is denied, and that fallback stopped a backtest for minutes.
        /// </remarks>
        private static IEnumerable<BaseData>? EmptyIfSupportedChain(Symbol symbol, IEnumerable<BaseData>? data)
        {
            return data ?? (ThetaDataProvider.IsSupportedCanonicalOption(symbol) ? Enumerable.Empty<BaseData>() : null);
        }

        /// <summary>
        /// Gets the data of each contract of the chain, a bounded number of contracts at a time.
        /// </summary>
        /// <remarks>
        /// The earlier version called <c>Any()</c> on the consuming enumerable, which took the first item out of the
        /// collection, so the result always lost its first data point. A failure in a worker was also lost.
        /// </remarks>
        private IEnumerable<BaseData>? GetCanonicalOptionHistoryByContract(Symbol symbol, DateTime startUtc, DateTime endUtc, Type dataType,
            Resolution resolution, SecurityExchangeHours exchangeHours, DateTimeZone dataTimeZone, TickType tickType)
        {
            var contracts = GetOptions(symbol, startUtc, endUtc).ToList();
            if (contracts.Count == 0)
            {
                return null;
            }
            return GetContractsHistory(contracts, startUtc, endUtc, dataType, resolution, exchangeHours, dataTimeZone, tickType);
        }

        private IEnumerable<BaseData> GetContractsHistory(List<Symbol> contracts, DateTime startUtc, DateTime endUtc, Type dataType,
            Resolution resolution, SecurityExchangeHours exchangeHours, DateTimeZone dataTimeZone, TickType tickType)
        {
            // Bounded, so a slow consumer stops the workers instead of filling memory.
            var collection = new BlockingCollection<BaseData>(boundedCapacity: 100_000);
            // Stops the workers when the consumer stops early, so they do not wait forever on a full collection.
            var cancellation = new CancellationTokenSource();
            var producer = Task.Run(() =>
            {
                try
                {
                    var options = new ParallelOptions { MaxDegreeOfParallelism = _maxParallelContracts, CancellationToken = cancellation.Token };
                    Parallel.ForEach(contracts, options, contract =>
                    {
                        var historyRequest = new HistoryRequest(startUtc, endUtc, dataType, contract, resolution, exchangeHours, dataTimeZone,
                            resolution, true, false, DataNormalizationMode.Raw, tickType);
                        var history = _historyProvider.GetHistory(historyRequest);
                        // Null means an unsupported request or no data the plan allows, so skip the contract.
                        if (history == null)
                        {
                            return;
                        }
                        foreach (var data in history)
                        {
                            collection.Add(data, cancellation.Token);
                        }
                    });
                }
                finally
                {
                    collection.CompleteAdding();
                }
            });

            var completed = false;
            try
            {
                foreach (var data in collection.GetConsumingEnumerable())
                {
                    yield return data;
                }
                completed = true;
            }
            finally
            {
                if (!completed)
                {
                    cancellation.Cancel();
                }
            }
            // Rethrows a worker failure, so a failed download does not look complete.
            producer.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Gets the contracts of the chain on each trading day of the range.
        /// </summary>
        protected virtual IEnumerable<Symbol> GetOptions(Symbol symbol, DateTime startUtc, DateTime endUtc)
        {
            var exchangeHours = _marketHoursDatabase.GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType);

            return Time.EachTradeableDay(exchangeHours, startUtc.Date, endUtc.Date)
                .Select(date => _historyProvider.GetOptionChain(symbol, date))
                .SelectMany(x => x)
                .Distinct();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            _historyProvider.DisposeSafely();
        }
    }
}
