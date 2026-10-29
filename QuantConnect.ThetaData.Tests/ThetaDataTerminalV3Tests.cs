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
using System.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Securities;
using QuantConnect.Data.Market;
using QuantConnect.Configuration;
using QuantConnect.Data.UniverseSelection;

namespace QuantConnect.Lean.DataSource.ThetaData.Tests
{
    /// <summary>
    /// Runs against a real Theta Terminal v3 on any plan, including Free. Set the address in the 'thetadata-rest-url'
    /// config key or the QC_THETADATA_REST_URL environment variable. The default is http://127.0.0.1:25503.
    /// The terminal holds the ThetaData login, so these tests need no credential.
    /// </summary>
    [TestFixture]
    [Explicit("Needs Theta Terminal v3 running. Uses only end-of-day data, which the Free plan allows.")]
    public class ThetaDataTerminalV3Tests
    {
        private static readonly Symbol Spy = Symbol.Create("SPY", SecurityType.Equity, Market.USA);
        private static readonly DateTime TradingDay = new(2026, 10, 20);
        private ThetaDataProvider _provider = null!;
        private bool _noAuthNeeded;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _noAuthNeeded = Config.GetBool("no-auth-needed");
            Config.Set("no-auth-needed", "true");
            _provider = new ThetaDataProvider("Free");
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _provider.Dispose();
            Config.Set("no-auth-needed", _noAuthNeeded ? "true" : "false");
        }

        private static HistoryRequest Request(Symbol symbol, Type dataType, TickType tickType, DateTime startLocal, DateTime endLocal)
        {
            var exchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType);
            return new HistoryRequest(startLocal.ConvertToUtc(exchangeHours.TimeZone), endLocal.ConvertToUtc(exchangeHours.TimeZone), dataType, symbol, Resolution.Daily,
                exchangeHours, exchangeHours.TimeZone, Resolution.Daily, true, false, DataNormalizationMode.Raw, tickType);
        }

        [Test]
        public void GetsSpyDailyBars()
        {
            var bars = _provider.GetHistory(Request(Spy, typeof(TradeBar), TickType.Trade, TradingDay.AddDays(-1), TradingDay.AddDays(1)))!.Cast<TradeBar>().ToList();

            Assert.AreEqual(2, bars.Count);
            Assert.That(bars.All(x => x.Low > 0 && x.Low <= x.Open && x.Low <= x.Close && x.High >= x.Open && x.High >= x.Close && x.Volume > 0));
            Assert.AreEqual(TradingDay, bars.Last().Time);
        }

        [Test]
        public void GetsOneDayOfTheSpyOptionChain()
        {
            // Test A of the smoke test: 1 day of SPY option end-of-day data.
            var canonical = Symbol.CreateCanonicalOption(Spy);
            var quotes = _provider.GetHistory(Request(canonical, typeof(QuoteBar), TickType.Quote, TradingDay, TradingDay.AddDays(1)))!.Cast<QuoteBar>().ToList();
            var trades = _provider.GetHistory(Request(canonical, typeof(TradeBar), TickType.Trade, TradingDay, TradingDay.AddDays(1)))!.Cast<TradeBar>().ToList();

            TestContext.Out.WriteLine($"SPY {TradingDay:yyyy-MM-dd}: {quotes.Count} quote bars, {trades.Count} trade bars, " +
                $"{quotes.Select(x => x.Symbol.ID.Date).Distinct().Count()} expirations, first {quotes.First().Symbol.Value}");
            Assert.That(quotes.Count, Is.GreaterThan(1000));
            Assert.That(trades.Count, Is.GreaterThan(100).And.LessThanOrEqualTo(quotes.Count + 1000));
            Assert.That(quotes.All(x => x.Time == TradingDay && x.Period == Time.OneDay));
            Assert.That(quotes.All(x => x.Symbol.SecurityType == SecurityType.Option && x.Symbol.Underlying == Spy && x.Symbol.ID.OptionStyle == OptionStyle.American));
            Assert.That(quotes.All(x => x.Symbol.ID.StrikePrice > 0 && x.Symbol.ID.Date >= TradingDay));
            Assert.That(quotes.All(x => (x.Bid == null || x.Bid.Close > 0) && (x.Ask == null || x.Ask.Close > 0) && (x.Bid != null || x.Ask != null)));
            Assert.That(quotes.Where(x => x.Bid != null && x.Ask != null).All(x => x.Bid.Close <= x.Ask.Close), "crossed quote");
            Assert.That(trades.All(x => x.Low > 0 && x.High >= x.Low && x.Volume > 0));
            Assert.AreEqual(quotes.Count, quotes.Select(x => x.Symbol).Distinct().Count(), "duplicate contract");
            Assert.That(quotes.Select(x => x.Symbol.ID.OptionRight).Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void ListsTheSpyChainAndBuildsTheUniverse()
        {
            var chain = _provider.GetOptionChain(Spy, TradingDay).ToList();
            var universe = (OptionUniverse)_provider.GetHistory(Request(Symbol.CreateCanonicalOption(Spy), typeof(OptionUniverse), TickType.Quote, TradingDay, TradingDay.AddDays(1)))!.Single();

            TestContext.Out.WriteLine($"chain {chain.Count} contracts, universe {universe.Data.Count} contracts, underlying close {universe.Underlying?.Price}");
            Assert.That(chain.Count, Is.GreaterThan(1000));
            Assert.That(universe.Data.Count, Is.GreaterThan(1000).And.LessThanOrEqualTo(chain.Count));
            Assert.That(universe.Underlying?.Price, Is.GreaterThan(0));
        }

        [Test]
        public void ReturnsNoDataForADeniedEndpoint()
        {
            // The Free plan denies option open interest with HTTP 403. That is a plan limit, not a connector failure.
            var contract = Symbol.CreateOption(Spy, Market.USA, OptionStyle.American, OptionRight.Call, 700m, new DateTime(2026, 11, 20));
            Assert.IsNull(_provider.GetHistory(Request(contract, typeof(OpenInterest), TickType.OpenInterest, TradingDay, TradingDay.AddDays(1))));
        }
    }
}
