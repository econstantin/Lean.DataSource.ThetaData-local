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
using QuantConnect.Data.Market;
using QuantConnect.Lean.DataSource.ThetaData.Models.Rest;

namespace QuantConnect.Lean.DataSource.ThetaData
{
    /// <summary>
    /// Converts v3 rows to LEAN data. Each method returns null for a row that holds no market observation.
    /// It never invents a price: a missing side of a quote stays missing.
    /// </summary>
    /// <remarks>
    /// ThetaData sends times as ET wall-clock times, see https://thetadata.net/docs/Articles/Data-And-Requests/OHLC-EOD.html.
    /// </remarks>
    public static class ThetaDataBarConverter
    {
        /// <summary>
        /// The time zone of every ThetaData timestamp.
        /// </summary>
        public static readonly DateTimeZone ThetaDataTimeZone = TimeZones.NewYork;

        /// <summary>
        /// Creates a daily trade bar from an end-of-day row.
        /// </summary>
        /// <returns>
        /// Null when the row has no trades. ThetaData then sends 0 for open, high, low, and close,
        /// "there were no qualifying trades during that interval". An index row has a count and volume
        /// of 0 but real prices, so the check uses the prices, not the count.
        /// </returns>
        public static TradeBar? ToDailyTradeBar(EndOfDayRow row, Symbol symbol, DateTimeZone exchangeTimeZone)
        {
            if (!HasValidPrices(row.Open, row.High, row.Low, row.Close))
            {
                return null;
            }
            return new TradeBar(ToTradingDay(row, exchangeTimeZone), symbol, row.Open, row.High, row.Low, row.Close, row.Volume, Time.OneDay);
        }

        /// <summary>
        /// Creates a daily quote bar from the NBBO in an end-of-day row.
        /// </summary>
        /// <remarks>
        /// The NBBO is the one at report time, about 17:15 ET, not at the 16:00 close.
        /// A side with a price or size of 0 has no quote, so the bar leaves that side empty (issue #11).
        /// </remarks>
        /// <returns>Null when neither side has a quote.</returns>
        public static QuoteBar? ToDailyQuoteBar(EndOfDayRow row, Symbol symbol, DateTimeZone exchangeTimeZone)
        {
            return CreateQuoteBar(ToTradingDay(row, exchangeTimeZone), symbol, row.Bid, row.BidSize, row.Ask, row.AskSize, Time.OneDay);
        }

        /// <summary>
        /// Creates an intraday trade bar. The row timestamp is the bar start.
        /// </summary>
        /// <returns>Null when the interval has no trades.</returns>
        public static TradeBar? ToTradeBar(OhlcRow row, Symbol symbol, TimeSpan period, DateTimeZone exchangeTimeZone)
        {
            if (!HasValidPrices(row.Open, row.High, row.Low, row.Close))
            {
                return null;
            }
            return new TradeBar(ToExchangeTime(row.Timestamp, exchangeTimeZone), symbol, row.Open, row.High, row.Low, row.Close, row.Volume, period);
        }

        /// <summary>
        /// Creates an intraday quote bar from the last NBBO of the interval.
        /// </summary>
        /// <returns>Null when neither side has a quote.</returns>
        public static QuoteBar? ToQuoteBar(QuoteRow row, Symbol symbol, TimeSpan period, DateTimeZone exchangeTimeZone)
        {
            return CreateQuoteBar(ToExchangeTime(row.Timestamp, exchangeTimeZone), symbol, row.Bid, row.BidSize, row.Ask, row.AskSize, period);
        }

        /// <summary>
        /// Creates a quote tick. A side with no quote keeps a price and size of 0, as a LEAN quote tick does.
        /// </summary>
        /// <returns>Null when neither side has a quote.</returns>
        public static Tick? ToQuoteTick(QuoteRow row, Symbol symbol, DateTimeZone exchangeTimeZone)
        {
            var hasBid = row.Bid > 0 && row.BidSize > 0;
            var hasAsk = row.Ask > 0 && row.AskSize > 0;
            if (!hasBid && !hasAsk)
            {
                return null;
            }
            var exchange = (hasAsk ? row.AskExchange : row.BidExchange).TryGetExchangeOrDefault();
            return new Tick(ToExchangeTime(row.Timestamp, exchangeTimeZone), symbol, string.Empty, exchange,
                hasBid ? row.BidSize : 0m, hasBid ? row.Bid : 0m, hasAsk ? row.AskSize : 0m, hasAsk ? row.Ask : 0m);
        }

        /// <summary>
        /// Creates a trade tick.
        /// </summary>
        /// <returns>Null when the price is not positive.</returns>
        public static Tick? ToTradeTick(TradeRow row, Symbol symbol, DateTimeZone exchangeTimeZone)
        {
            if (row.Price <= 0)
            {
                return null;
            }
            return new Tick(ToExchangeTime(row.Timestamp, exchangeTimeZone), symbol, row.Condition.ToStringInvariant(), row.Exchange.TryGetExchangeOrDefault(), row.Size, row.Price);
        }

        /// <summary>
        /// Creates an index price tick.
        /// </summary>
        /// <returns>Null when the price is not positive.</returns>
        public static Tick? ToIndexTick(PriceRow row, Symbol symbol, DateTimeZone exchangeTimeZone)
        {
            if (row.Price <= 0)
            {
                return null;
            }
            return new Tick(ToExchangeTime(row.Timestamp, exchangeTimeZone), symbol, string.Empty, string.Empty, 0m, row.Price);
        }

        /// <summary>
        /// Creates an open interest point at the time ThetaData reports it.
        /// </summary>
        public static OpenInterest ToOpenInterest(OpenInterestRow row, Symbol symbol, DateTimeZone exchangeTimeZone)
        {
            return new OpenInterest(ToExchangeTime(row.Timestamp, exchangeTimeZone), symbol, row.OpenInterest);
        }

        /// <summary>
        /// Orders data by time and removes repeated bars for the same symbol and start time.
        /// Ticks keep their duplicates, because 2 trades can share a timestamp.
        /// </summary>
        public static IEnumerable<BaseData> OrderAndRemoveDuplicates(IEnumerable<BaseData> data, Resolution resolution)
        {
            var ordered = data.OrderBy(x => x.Time);
            if (resolution == Resolution.Tick)
            {
                return ordered;
            }
            return ordered.DistinctBy(x => (x.Symbol, x.Time, x.DataType));
        }

        /// <summary>
        /// Builds hour bars from minute bars. Each minute bar goes into the clock hour in which it starts.
        /// </summary>
        /// <remarks>
        /// LEAN hour bars start on the clock hour, so the first regular-session equity bar is 09:00 to 10:00 and holds the minutes from 09:30.
        /// A quote side stays empty when no minute of the hour has it.
        /// </remarks>
        public static List<BaseData> ToHourBars(IEnumerable<BaseData> minuteBars)
        {
            var hourBars = new List<BaseData>();
            foreach (var hour in minuteBars.OrderBy(x => x.Time).GroupBy(x => (x.Symbol, Start: x.Time.RoundDown(Time.OneHour), Type: x.GetType())))
            {
                if (hour.Key.Type == typeof(TradeBar))
                {
                    var trades = hour.Cast<TradeBar>().ToList();
                    hourBars.Add(new TradeBar(hour.Key.Start, hour.Key.Symbol, trades[0].Open, trades.Max(x => x.High), trades.Min(x => x.Low),
                        trades[^1].Close, trades.Sum(x => x.Volume), Time.OneHour));
                }
                else if (hour.Key.Type == typeof(QuoteBar))
                {
                    var quotes = hour.Cast<QuoteBar>().ToList();
                    var bids = quotes.Where(x => x.Bid != null).ToList();
                    var asks = quotes.Where(x => x.Ask != null).ToList();
                    hourBars.Add(new QuoteBar(hour.Key.Start, hour.Key.Symbol,
                        bids.Count > 0 ? new Bar(bids[0].Bid.Open, bids.Max(x => x.Bid.High), bids.Min(x => x.Bid.Low), bids[^1].Bid.Close) : null,
                        bids.Count > 0 ? bids[^1].LastBidSize : 0m,
                        asks.Count > 0 ? new Bar(asks[0].Ask.Open, asks.Max(x => x.Ask.High), asks.Min(x => x.Ask.Low), asks[^1].Ask.Close) : null,
                        asks.Count > 0 ? asks[^1].LastAskSize : 0m,
                        Time.OneHour));
                }
                else
                {
                    throw new ArgumentException($"{nameof(ThetaDataBarConverter)}.{nameof(ToHourBars)}: Unsupported data type {hour.Key.Type.Name}.");
                }
            }
            return hourBars;
        }

        /// <summary>
        /// The trading day of an end-of-day row, as midnight in the exchange time zone.
        /// ThetaData builds the report after the close of that day, so the date of <see cref="EndOfDayRow.Created"/> is the trading day.
        /// </summary>
        public static DateTime ToTradingDay(EndOfDayRow row, DateTimeZone exchangeTimeZone)
        {
            return ToExchangeTime(row.Created.Date, exchangeTimeZone);
        }

        private static DateTime ToExchangeTime(DateTime thetaDataTime, DateTimeZone exchangeTimeZone)
            => thetaDataTime.ConvertTo(ThetaDataTimeZone, exchangeTimeZone);

        private static bool HasValidPrices(decimal open, decimal high, decimal low, decimal close)
            => open > 0 && high > 0 && low > 0 && close > 0;

        private static QuoteBar? CreateQuoteBar(DateTime time, Symbol symbol, decimal bid, decimal bidSize, decimal ask, decimal askSize, TimeSpan period)
        {
            var hasBid = bid > 0 && bidSize > 0;
            var hasAsk = ask > 0 && askSize > 0;
            if (!hasBid && !hasAsk)
            {
                return null;
            }
            return new QuoteBar(time, symbol,
                hasBid ? new Bar(bid, bid, bid, bid) : null, hasBid ? bidSize : 0m,
                hasAsk ? new Bar(ask, ask, ask, ask) : null, hasAsk ? askSize : 0m,
                period);
        }
    }
}
