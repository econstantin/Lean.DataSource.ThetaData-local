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

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Securities;
using QuantConnect.Data.Market;
using QuantConnect.Configuration;
using System.Collections.Generic;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Lean.DataSource.ThetaData.Models.Rest;

namespace QuantConnect.Lean.DataSource.ThetaData.Tests
{
    /// <summary>
    /// The history provider, downloader, and chain provider on recorded v3 responses of the Free plan.
    /// No terminal, account, or network is needed.
    /// </summary>
    [TestFixture]
    public class ThetaDataV3HistoryTests
    {
        private static readonly Symbol Spy = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
        private static readonly Symbol CanonicalSpy = Symbol.CreateCanonicalOption(Spy);
        private const string FreePlanDenial = "Requesting an option endpoint requiring a value subscription, but you only have a FREE subscription. Please consider upgrading! ( https://www.thetadata.net/subscribe )";

        private bool _noAuthNeeded;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _noAuthNeeded = Config.GetBool("no-auth-needed");
            Config.Set("no-auth-needed", "true");
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            Config.Set("no-auth-needed", _noAuthNeeded ? "true" : "false");
        }

        /// <summary>
        /// Answers like a Free plan terminal that has data for 2026-10-19 and 2026-10-20 only.
        /// </summary>
        private static HttpResponseMessage FreePlanTerminal(FakeThetaDataHandler.FakeRequest request)
        {
            var start = request.Query["start_date"] ?? request.Query["date"];
            switch (request.Path)
            {
                case "option/list/contracts/quote":
                case "option/history/open_interest":
                    return FakeThetaDataHandler.Respond(403, FreePlanDenial);
                case "stock/history/eod" when string.CompareOrdinal(start, "20261020") <= 0:
                    return FakeThetaDataHandler.Fixture("stock_eod_spy_20261019_20261020.json");
                case "index/history/eod" when string.CompareOrdinal(start, "20261020") <= 0:
                    return FakeThetaDataHandler.Fixture("index_eod_spx_20261019_20261020.json");
                case "option/history/eod" when request.Query["expiration"] == "*" && start == "20261020":
                    return FakeThetaDataHandler.Fixture("option_eod_spy_bulk_20261020_sample.json");
                case "option/history/eod" when request.Query["expiration"] == "20240119":
                    return FakeThetaDataHandler.Fixture("option_eod_spy_20240119_320c_20240102_20240103.json");
                default:
                    return FakeThetaDataHandler.Respond(472, "No data found for your request");
            }
        }

        private static (ThetaDataProvider Provider, FakeThetaDataHandler Handler) CreateProvider(Func<FakeThetaDataHandler.FakeRequest, HttpResponseMessage>? respond = null, string plan = "Free")
        {
            var handler = new FakeThetaDataHandler(respond ?? FreePlanTerminal);
            var client = new ThetaDataRestApiClient("http://127.0.0.1:25503", null, 1, handler, _ => TimeSpan.Zero, 1);
            return (new ThetaDataProvider(plan, client), handler);
        }

        private static HistoryRequest CreateRequest(Symbol symbol, Type dataType, TickType tickType, DateTime startLocal, DateTime endLocal, Resolution resolution = Resolution.Daily)
        {
            var exchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType);
            return new HistoryRequest(startLocal.ConvertToUtc(exchangeHours.TimeZone), endLocal.ConvertToUtc(exchangeHours.TimeZone), dataType, symbol, resolution,
                exchangeHours, exchangeHours.TimeZone, resolution, true, false, DataNormalizationMode.Raw, tickType);
        }

        [Test]
        public void GetsEquityDailyTradeBars()
        {
            var (provider, handler) = CreateProvider();
            using var _ = provider;

            var bars = provider.GetHistory(CreateRequest(Spy, typeof(TradeBar), TickType.Trade, new DateTime(2026, 10, 19), new DateTime(2026, 10, 21)))!.Cast<TradeBar>().ToList();

            Assert.AreEqual(2, bars.Count);
            Assert.AreEqual(new DateTime(2026, 10, 19), bars[0].Time);
            Assert.AreEqual(new DateTime(2026, 10, 20), bars[1].Time);
            var expected = ThetaDataRestApiClient.ParseRows<EndOfDayRow>(FakeThetaDataHandler.ReadFixture("stock_eod_spy_20261019_20261020.json"));
            Assert.AreEqual(expected[1].Close, bars[1].Close);
            Assert.AreEqual(expected[1].Volume, bars[1].Volume);

            var request = handler.Requests.Single();
            Assert.AreEqual("stock/history/eod", request.Path);
            Assert.AreEqual("SPY", request.Query["symbol"]);
            // The request ends at 2026-10-21 00:00 ET, so it holds no data of 2026-10-21.
            Assert.AreEqual("20261019", request.Query["start_date"]);
            Assert.AreEqual("20261020", request.Query["end_date"]);
        }

        [Test]
        public void ReturnsTheDailyBarOfTheDayForAUtcMidnightWindow()
        {
            // lean data download --start 20261020 --end 20261021 asks for 2026-10-20 00:00 UTC to 2026-10-21 00:00 UTC.
            // That is 2026-10-19 20:00 to 2026-10-20 20:00 ET, and it must return the bar of 2026-10-20.
            var (provider, _) = CreateProvider();
            using var __ = provider;
            foreach (var (symbol, dataType, tickType, expected) in new[]
            {
                (Spy, typeof(TradeBar), TickType.Trade, 1),
                (CanonicalSpy, typeof(QuoteBar), TickType.Quote, 6)
            })
            {
                var exchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, symbol, symbol.SecurityType);
                var request = new HistoryRequest(new DateTime(2026, 10, 20), new DateTime(2026, 10, 21), dataType, symbol, Resolution.Daily,
                    exchangeHours, exchangeHours.TimeZone, Resolution.Daily, true, false, DataNormalizationMode.Raw, tickType);

                var history = provider.GetHistory(request)!.ToList();

                Assert.AreEqual(expected, history.Count, symbol.ToString());
                Assert.That(history.All(x => x.Time == new DateTime(2026, 10, 20)), symbol.ToString());
            }
        }

        [Test]
        public void ClampsTheStartToTheFirstEasternDateOfThePlan()
        {
            // LEAN asks for SPY daily data from its first date, 1998-01-02. The Free plan starts on 2023-06-01 (ET):
            // a request for 2023-05-31 is denied with HTTP 403 for the whole range.
            var (provider, handler) = CreateProvider();
            using var _ = provider;
            var exchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, Spy, SecurityType.Equity);
            var request = new HistoryRequest(new DateTime(1998, 1, 2), new DateTime(2026, 10, 27), typeof(TradeBar), Spy, Resolution.Daily,
                exchangeHours, exchangeHours.TimeZone, Resolution.Daily, true, false, DataNormalizationMode.Raw, TickType.Trade);

            Assert.AreEqual(2, provider.GetHistory(request)!.Count());
            // 1998 to 2026 is clamped to 2023-06-01, then split into requests of at most 365 days.
            Assert.AreEqual("20230601", handler.Requests.First().Query["start_date"]);
            Assert.AreEqual(4, handler.Requests.Count);
        }

        [Test]
        public void GetsOptionContractDailyDataWithV3Parameters()
        {
            var (provider, handler) = CreateProvider();
            using var _ = provider;
            var contract = Symbol.CreateOption(Spy, Market.USA, OptionStyle.American, OptionRight.Call, 320m, new DateTime(2024, 1, 19));

            var trades = provider.GetHistory(CreateRequest(contract, typeof(TradeBar), TickType.Trade, new DateTime(2024, 1, 2), new DateTime(2024, 1, 4)))!.Cast<TradeBar>().ToList();
            var quotes = provider.GetHistory(CreateRequest(contract, typeof(QuoteBar), TickType.Quote, new DateTime(2024, 1, 2), new DateTime(2024, 1, 4)))!.Cast<QuoteBar>().ToList();

            // 2024-01-02 had no trade: no trade bar, but a quote bar.
            Assert.AreEqual(1, trades.Count);
            Assert.AreEqual(new DateTime(2024, 1, 3), trades[0].Time);
            Assert.AreEqual(151.08m, trades[0].Close);
            Assert.AreEqual(2, quotes.Count);
            Assert.AreEqual(153.38m, quotes[0].Bid.Close);
            Assert.AreEqual(153.93m, quotes[0].Ask.Close);

            var request = handler.Requests.First();
            Assert.AreEqual("option/history/eod", request.Path);
            Assert.AreEqual("SPY", request.Query["symbol"]);
            Assert.AreEqual("20240119", request.Query["expiration"]);
            Assert.AreEqual("320.000", request.Query["strike"]);
            Assert.AreEqual("call", request.Query["right"]);
            Assert.IsNull(request.Query["root"]);
            Assert.IsNull(request.Query["exp"]);
        }

        [Test]
        public void GetsTheWholeChainForACanonicalSymbolInOneRequest()
        {
            // Issue #12: ?SPY used to return null with "Unsupported SecurityType 'Option' for symbol '?SPY'".
            var (provider, handler) = CreateProvider();
            using var _ = provider;

            var quotes = provider.GetHistory(CreateRequest(CanonicalSpy, typeof(QuoteBar), TickType.Quote, new DateTime(2026, 10, 20), new DateTime(2026, 10, 21)))!.ToList();
            var trades = provider.GetHistory(CreateRequest(CanonicalSpy, typeof(TradeBar), TickType.Trade, new DateTime(2026, 10, 20), new DateTime(2026, 10, 21)))!.ToList();

            Assert.AreEqual(6, quotes.Count, "every contract with a bid or an ask");
            Assert.AreEqual(4, trades.Count, "every contract that traded");
            Assert.That(quotes.Concat(trades).All(x => x.Symbol.Underlying == Spy && !x.Symbol.IsCanonical()));
            var zeroDtePut = quotes.Cast<QuoteBar>().Single(x => x.Symbol.ID.Date == new DateTime(2026, 10, 20) && x.Symbol.ID.OptionRight == OptionRight.Put);
            Assert.IsNull(zeroDtePut.Bid);
            Assert.AreEqual(0.01m, zeroDtePut.Ask.Close);

            // The quote and trade data of the day share 1 bulk request.
            var bulk = handler.Requests.Where(x => x.Path == "option/history/eod").ToList();
            Assert.AreEqual(1, bulk.Count);
            Assert.AreEqual("*", bulk[0].Query["expiration"]);
            Assert.AreEqual("20261020", bulk[0].Query["start_date"]);
            Assert.AreEqual("20261020", bulk[0].Query["end_date"]);
        }

        [Test]
        public void BuildsTheOptionUniverseForACanonicalHistoryRequest()
        {
            // Issue #12: QCAlgorithm.OptionChain calls History(?SPY, 1) with the OptionUniverse type.
            var (provider, _) = CreateProvider();
            using var __ = provider;

            var history = provider.GetHistory(CreateRequest(CanonicalSpy, typeof(OptionUniverse), TickType.Quote, new DateTime(2026, 10, 20), new DateTime(2026, 10, 21)))!.ToList();

            var universe = (OptionUniverse)history.Single();
            Assert.AreEqual(CanonicalSpy, universe.Symbol);
            Assert.AreEqual(new DateTime(2026, 10, 20), universe.Time);
            Assert.AreEqual(new DateTime(2026, 10, 21), universe.EndTime);
            Assert.AreEqual(6, universe.Data.Count);
            Assert.AreEqual(Spy, universe.Underlying.Symbol);
            var expectedUnderlying = ThetaDataRestApiClient.ParseRows<EndOfDayRow>(FakeThetaDataHandler.ReadFixture("stock_eod_spy_20261019_20261020.json"))[1];
            Assert.AreEqual(expectedUnderlying.Close, universe.Underlying.Price);

            var contract = universe.Data.Cast<OptionUniverse>().Single(x => x.Symbol.ID.StrikePrice == 380m && x.Symbol.ID.OptionRight == OptionRight.Put);
            Assert.AreEqual(new DateTime(2028, 1, 21), contract.Symbol.ID.Date);
            Assert.That(contract.Close, Is.GreaterThan(0));
        }

        [Test]
        public void BuildsRegularSessionEquityHourBarsOnTheClockHour()
        {
            // The request starts at 09:30. The first hour bar must be 09:00 to 10:00, as LEAN's own hour bars are.
            static string Minute(string time, decimal open, decimal high, decimal low, decimal close, int volume) =>
                $"{{\"timestamp\":\"2026-10-20T{time}:00.000\",\"open\":{open},\"high\":{high},\"low\":{low},\"close\":{close},\"volume\":{volume},\"count\":1}}";
            var (provider, handler) = CreateProvider(request => request.Path == "stock/history/ohlc"
                ? FakeThetaDataHandler.Respond(200, "{\"response\":[" + string.Join(",",
                    Minute("09:30", 10.0m, 10.5m, 9.9m, 10.2m, 100), Minute("09:59", 10.2m, 11.0m, 10.1m, 10.8m, 50), Minute("10:00", 10.8m, 10.9m, 10.6m, 10.7m, 30)) + "]}")
                : FakeThetaDataHandler.Respond(472, "No data found for your request"), plan: "Value");
            using var _ = provider;
            var exchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, Spy, SecurityType.Equity);
            var request = new HistoryRequest(new DateTime(2026, 10, 20).ConvertToUtc(exchangeHours.TimeZone), new DateTime(2026, 10, 21).ConvertToUtc(exchangeHours.TimeZone),
                typeof(TradeBar), Spy, Resolution.Hour, exchangeHours, exchangeHours.TimeZone, Resolution.Hour, false, false, DataNormalizationMode.Raw, TickType.Trade);

            var bars = provider.GetHistory(request)!.Cast<TradeBar>().ToList();

            Assert.AreEqual("1m", handler.Requests.Single(x => x.Path == "stock/history/ohlc").Query["interval"]);
            Assert.AreEqual(2, bars.Count);
            Assert.AreEqual((new DateTime(2026, 10, 20, 9, 0, 0), new DateTime(2026, 10, 20, 10, 0, 0)), (bars[0].Time, bars[0].EndTime));
            Assert.AreEqual((10.0m, 11.0m, 9.9m, 10.8m, 150m), (bars[0].Open, bars[0].High, bars[0].Low, bars[0].Close, bars[0].Volume));
            Assert.AreEqual((new DateTime(2026, 10, 20, 10, 0, 0), 30m), (bars[1].Time, bars[1].Volume));
        }

        [Test]
        public void BuildsTheOptionUniverseWithOpenInterestWhenThePlanIncludesIt()
        {
            // The row shape comes from the v3 spec. The Free plan denies open interest, so no recorded response exists.
            var (provider, handler) = CreateProvider(request => request.Path == "option/history/open_interest"
                ? FakeThetaDataHandler.Respond(200,
                    "{\"response\":[{\"contract\":{\"symbol\":\"SPY\",\"expiration\":\"2026-10-20\",\"right\":\"CALL\",\"strike\":760.0},\"data\":[{\"timestamp\":\"2026-10-20T06:30:00.000\",\"open_interest\":1234}]}]}")
                : FreePlanTerminal(request), plan: "Standard");
            using var _ = provider;

            var universe = (OptionUniverse)provider.GetHistory(CreateRequest(CanonicalSpy, typeof(OptionUniverse), TickType.Quote, new DateTime(2026, 10, 20), new DateTime(2026, 10, 21)))!.Single();

            var call = universe.Data.Cast<OptionUniverse>().Single(x => x.Symbol.ID.StrikePrice == 760m && x.Symbol.ID.OptionRight == OptionRight.Call);
            Assert.AreEqual(1234m, call.OpenInterest);
            Assert.That(call.Close, Is.GreaterThan(0), "open interest adds to the row, it does not replace the prices");
            Assert.AreEqual(1, handler.Requests.Count(x => x.Path == "option/history/open_interest" && x.Query["expiration"] == "*"));
        }

        [Test]
        public void SendsTheDeniedUniverseOpenInterestRequestOnce()
        {
            var (provider, handler) = CreateProvider();
            using var _ = provider;

            for (var i = 0; i < 2; i++)
            {
                Assert.IsNotNull(provider.GetHistory(CreateRequest(CanonicalSpy, typeof(OptionUniverse), TickType.Quote, new DateTime(2026, 10, 20), new DateTime(2026, 10, 21)))!.Single());
            }

            Assert.AreEqual(1, handler.Requests.Count(x => x.Path == "option/history/open_interest"));
        }

        [Test]
        public void BuildsTheOptionUniverseWhenThePlanDeniesTheUnderlying()
        {
            // The v3 docs say the Free plan has no index data. A denied underlying must not drop the contracts of the day.
            var (provider, _) = CreateProvider(request => request.Path == "stock/history/eod"
                ? FakeThetaDataHandler.Respond(403, FreePlanDenial)
                : FreePlanTerminal(request));
            using var __ = provider;

            var history = provider.GetHistory(CreateRequest(CanonicalSpy, typeof(OptionUniverse), TickType.Quote, new DateTime(2026, 10, 20), new DateTime(2026, 10, 21)))!.ToList();

            var universe = (OptionUniverse)history.Single();
            Assert.AreEqual(6, universe.Data.Count);
            Assert.IsNull(universe.Underlying);
        }

        [Test]
        public void DoesNotReturnTheUniverseOfADayBeforeThatDayEnds()
        {
            // The universe of 2026-10-20 holds the end-of-day report of that day, so it is not available at 18:00 the same day.
            var (provider, handler) = CreateProvider();
            using var _ = provider;

            var history = provider.GetHistory(CreateRequest(CanonicalSpy, typeof(OptionUniverse), TickType.Quote, new DateTime(2026, 10, 20), new DateTime(2026, 10, 20, 18, 0, 0)));

            Assert.That(history, Is.Null.Or.Empty);
            Assert.IsEmpty(handler.Requests.Where(x => x.Path == "option/history/eod"));
        }

        [Test]
        public void ListsTheChainFromTheEndOfDayReportOnTheFreePlan()
        {
            var (provider, handler) = CreateProvider();
            using var _ = provider;

            var first = provider.GetOptionChain(Spy, new DateTime(2026, 10, 20)).ToList();
            var second = provider.GetOptionChain(CanonicalSpy, new DateTime(2026, 10, 20)).ToList();

            Assert.AreEqual(6, first.Count);
            CollectionAssert.AreEquivalent(first, second);
            Assert.That(first.All(x => x.SecurityType == SecurityType.Option && x.Underlying == Spy && x.ID.OptionStyle == OptionStyle.American));
            // The first denial and 1 request for a recent date show a plan denial. The request is then not sent again.
            Assert.AreEqual(2, handler.Requests.Count(x => x.Path == "option/list/contracts/quote"), "the denied request is not sent again");
        }

        [Test]
        public void KeepsTheContractListWhenThePlanDeniesOnlyAnOldDate()
        {
            // A paid plan can include the contract list but not an old date. That denial must not turn off the list for every date.
            var (provider, handler) = CreateProvider(request => request.Path == "option/list/contracts/quote"
                ? string.CompareOrdinal(request.Query["date"], "20250101") < 0
                    ? FakeThetaDataHandler.Respond(403, "Your subscription does not include this date.")
                    : FakeThetaDataHandler.Respond(200,
                        "{\"response\":[{\"symbol\":\"SPY\",\"expiration\":\"2026-11-20\",\"strike\":700.000,\"right\":\"CALL\"},{\"symbol\":\"SPY\",\"expiration\":\"2026-11-20\",\"strike\":700.000,\"right\":\"PUT\"}]}")
                : FreePlanTerminal(request), plan: "Value");
            using var _ = provider;

            var old = provider.GetOptionChain(Spy, new DateTime(2024, 1, 2)).ToList();
            var recent = provider.GetOptionChain(Spy, new DateTime(2026, 10, 20)).ToList();

            Assert.IsEmpty(old, "the end-of-day report of the fake terminal has no data for 2024-01-02");
            Assert.AreEqual(2, recent.Count, "the recent chain comes from the contract list, not from the 6-contract report");
            Assert.AreEqual(3, handler.Requests.Count(x => x.Path == "option/list/contracts/quote"), "the old date, the check of a recent date, and 2026-10-20");
        }

        [Test]
        public void ReturnsNoDataWhenThePlanDeniesIt()
        {
            var (provider, _) = CreateProvider();
            using var __ = provider;
            var contract = Symbol.CreateOption(Spy, Market.USA, OptionStyle.American, OptionRight.Call, 320m, new DateTime(2024, 1, 19));

            var history = provider.GetHistory(CreateRequest(contract, typeof(OpenInterest), TickType.OpenInterest, new DateTime(2024, 1, 2), new DateTime(2024, 1, 4)));

            Assert.IsNull(history);
        }

        [Test]
        public void DownloaderReturnsEmptyNotNullForADeniedChainRequest()
        {
            // LEAN's CanonicalDataDownloaderDecorator treats null as "canonical not supported" and falls back to
            // LiveOptionChainProvider, which calls QuantConnect's servers. A backtest stopped there for minutes.
            var (provider, handler) = CreateProvider();
            using var downloader = new ThetaDataDownloader(provider);
            var exchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, CanonicalSpy, SecurityType.Option);

            var data = downloader.Get(new DataDownloaderGetParameters(CanonicalSpy, Resolution.Daily,
                new DateTime(2026, 10, 20).ConvertToUtc(exchangeHours.TimeZone), new DateTime(2026, 10, 21).ConvertToUtc(exchangeHours.TimeZone), TickType.OpenInterest));

            Assert.IsNotNull(data);
            Assert.IsEmpty(data!);
            Assert.AreEqual(1, handler.Requests.Count(x => x.Path == "option/history/open_interest"));
        }

        [Test]
        public void ThrowsWhenTheTerminalFails()
        {
            // A failed request must not look like an empty result.
            var (provider, _) = CreateProvider(_ => throw new HttpRequestException("Connection refused"));
            using var __ = provider;

            Assert.Throws<ThetaDataRequestException>(() => provider.GetHistory(CreateRequest(Spy, typeof(TradeBar), TickType.Trade, new DateTime(2026, 10, 19), new DateTime(2026, 10, 21))));
        }

        [Test]
        public void IntradayCanonicalDownloadKeepsTheFirstDataPoint()
        {
            // The old downloader called Any() on the consuming enumerable, which removed the first item.
            var bar = "{\"timestamp\":\"2026-10-20T09:30:00.000\",\"open\":1.00,\"high\":1.10,\"low\":0.90,\"close\":1.05,\"volume\":10,\"count\":2}";
            var (provider, _) = CreateProvider(request => request.Path switch
            {
                "option/list/contracts/quote" => FakeThetaDataHandler.Respond(200,
                    "{\"response\":[{\"symbol\":\"SPY\",\"expiration\":\"2026-11-20\",\"strike\":700.000,\"right\":\"CALL\"},{\"symbol\":\"SPY\",\"expiration\":\"2026-11-20\",\"strike\":700.000,\"right\":\"PUT\"}]}"),
                "option/history/ohlc" => FakeThetaDataHandler.Respond(200,
                    $"{{\"response\":[{{\"contract\":{{\"symbol\":\"SPY\",\"expiration\":\"2026-11-20\",\"right\":\"{request.Query["right"]!.ToUpperInvariant()}\",\"strike\":700.000}},\"data\":[{bar}]}}]}}"),
                _ => FakeThetaDataHandler.Respond(472, "No data found for your request")
            }, plan: "Pro");
            using var downloader = new ThetaDataDownloader(provider);
            var exchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, CanonicalSpy, SecurityType.Option);

            var data = downloader.Get(new DataDownloaderGetParameters(CanonicalSpy, Resolution.Minute,
                new DateTime(2026, 10, 20).ConvertToUtc(exchangeHours.TimeZone), new DateTime(2026, 10, 21).ConvertToUtc(exchangeHours.TimeZone), TickType.Trade))!.ToList();

            Assert.AreEqual(2, data.Count);
            CollectionAssert.AreEquivalent(new[] { OptionRight.Call, OptionRight.Put }, data.Select(x => x.Symbol.ID.OptionRight));
        }

        [Test]
        public void WritesDailyOptionFilesThatLeanReadsBack()
        {
            var handler = new FakeThetaDataHandler(FreePlanTerminal);
            using var client = new ThetaDataRestApiClient("http://127.0.0.1:25503", null, 1, handler, _ => TimeSpan.Zero, 1);
            var dataFolder = Path.Combine(Path.GetTempPath(), "thetadata-writer-" + Guid.NewGuid().ToString("N"));
            var exchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, CanonicalSpy, SecurityType.Option);
            try
            {
                var writer = new ThetaDataOptionDataWriter(client, dataFolder);
                var summary = writer.Write(CanonicalSpy, new DateTime(2026, 10, 20), new DateTime(2026, 10, 20), exchangeHours);
                // Writing the same day again replaces its bars instead of adding a second line.
                writer.Write(CanonicalSpy, new DateTime(2026, 10, 20), new DateTime(2026, 10, 20), exchangeHours);

                Assert.AreEqual((1, 1, 6, 4, 6), (summary.Days, summary.UniverseFiles, summary.Contracts, summary.TradeBars, summary.QuoteBars));
                var daily = Path.Combine(dataFolder, "option", "usa", "daily");
                var trades = new QuantConnect.ToolBox.LeanDataReader(Path.Combine(daily, "spy_2026_trade_american.zip")).Parse().Cast<TradeBar>().ToList();
                var quotes = new QuantConnect.ToolBox.LeanDataReader(Path.Combine(daily, "spy_2026_quote_american.zip")).Parse().Cast<QuoteBar>().ToList();

                Assert.AreEqual(4, trades.Count);
                Assert.AreEqual(6, quotes.Count);
                Assert.That(trades.Concat<BaseData>(quotes).All(x => x.Time == new DateTime(2026, 10, 20)));
                var expected = ThetaDataRestApiClient.ParseRows<EndOfDayRow>(FakeThetaDataHandler.ReadFixture("option_eod_spy_bulk_20261020_sample.json"))
                    .Single(x => x.Contract!.Strike == 760m && x.Contract.Right == "CALL");
                var call = trades.Single(x => x.Symbol.ID.StrikePrice == 760m && x.Symbol.ID.OptionRight == OptionRight.Call);
                Assert.AreEqual((expected.Open, expected.High, expected.Low, expected.Close, expected.Volume), (call.Open, call.High, call.Low, call.Close, call.Volume));
                var put = quotes.Single(x => x.Symbol.ID.StrikePrice == 760m && x.Symbol.ID.OptionRight == OptionRight.Put);
                Assert.AreEqual(0.01m, put.Ask.Close);
                Assert.That(put.Bid == null || put.Bid.Close == 0m, "the missing bid stays missing");

                var universe = File.ReadAllLines(Path.Combine(dataFolder, "option", "usa", "universes", "spy", "20261020.csv"));
                Assert.AreEqual(1 + 1 + 6, universe.Length);
            }
            finally
            {
                if (Directory.Exists(dataFolder))
                {
                    Directory.Delete(dataFolder, true);
                }
            }
        }

        [Test]
        public void LeanWritesAUniverseFileFromTheDownloader()
        {
            // Issue #9: a backtest gets its option chain from data/option/usa/universes/<root>/<date>.csv, which LEAN's
            // RunUniverseDownloader writes from the downloader. Before this change the folder had to exist already.
            var exchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, CanonicalSpy, SecurityType.Option);
            var (provider, _) = CreateProvider();
            using var downloader = new ThetaDataDownloader(provider);
            var dataFolder = Path.Combine(Path.GetTempPath(), "thetadata-universe-" + Guid.NewGuid().ToString("N"));
            // LEAN reloads the market hours from the data folder, so the temp folder needs them. It has no universes folder.
            foreach (var folder in new[] { "market-hours", "symbol-properties" })
            {
                var target = Directory.CreateDirectory(Path.Combine(dataFolder, folder));
                foreach (var file in Directory.GetFiles(Path.Combine(Globals.DataFolder, folder)))
                {
                    File.Copy(file, Path.Combine(target.FullName, Path.GetFileName(file)));
                }
            }
            var previousDataFolder = Config.Get("data-folder");
            Config.Set("data-folder", dataFolder);
            Globals.Reset();
            try
            {
                UniverseExtensions.RunUniverseDownloader(downloader,
                    new DataUniverseDownloaderGetParameters(CanonicalSpy, new DateTime(2026, 10, 20), new DateTime(2026, 10, 21), exchangeHours));

                var file = Path.Combine(dataFolder, "option", "usa", "universes", "spy", "20261020.csv");
                Assert.IsTrue(File.Exists(file), file);
                var lines = File.ReadAllLines(file);
                Assert.That(lines[0], Does.StartWith("#expiry,strike,right,open,high,low,close,volume,open_interest"));
                Assert.That(lines[1], Does.StartWith(","), "the underlying line comes first");
                Assert.AreEqual(1 + 1 + 6, lines.Length, "header, SPY, and 6 contracts");
                Assert.That(lines.Skip(2).All(x => char.IsDigit(x[0])));
            }
            finally
            {
                Config.Set("data-folder", previousDataFolder);
                Globals.Reset();
                if (Directory.Exists(dataFolder))
                {
                    Directory.Delete(dataFolder, true);
                }
            }
        }
    }
}
