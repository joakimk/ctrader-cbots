using System;
using System.Linq;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

// Trading bot mostly coded by Chat GPT (except for the generic code).

namespace cAlgo
{
    [Robot(TimeZone = TimeZones.EasternStandardTime, AccessRights = AccessRights.None)]
    public class RangeTrader : Robot
    {
        [Parameter("Bot Identifier", DefaultValue = "MyBotIdentifier")]
        public string BotIdentifier { get; set; }

        [Parameter("Max Risk Per Trade Percent", DefaultValue = 1.0)]
        public double MaxRiskPerTradePercent { get; set; }

        [Parameter("Max Daily Loss Percent", DefaultValue = 2.0)]
        public double MaxDailyLossPercent { get; set; }
        
        [Parameter("Max Usable Balance Percent", DefaultValue = 100.0)]
        public double MaxUsableBalancePercent { get; set; }
        
        // Code specific to this bot -----------------------------------------------------------------------------------------
 
        [Parameter("Stop Loss to Take Profit Ratio", DefaultValue = 0.5, MinValue = 0.5, MaxValue = 8, Step = 1)]
        public double StopLossToTakeProfitRatio { get; set; }
        
        [Parameter("Number of Bars", DefaultValue = 20, MinValue = 5, MaxValue = 80, Step = 5)]
        public int NumberOfBars { get; set; }
        
        [Parameter("Minimum Distance Between High and Low", DefaultValue = 10, MinValue = 0.01, MaxValue = 150, Step = 30)]
        public double MinDistanceBetweenHighLow { get; set; }
        
        [Parameter("Maximum Distance Between High and Low", DefaultValue = 400, MinValue = 0.01, MaxValue = 400, Step = 10)]
        public double MaxDistanceBetweenHighLow { get; set; }
     
        [Parameter("SMA Period", DefaultValue = 20, MinValue = 5, MaxValue = 20, Step = 2)]
        public int SMAPeriod { get; set; }
        
        [Parameter("Price Offset Percent", DefaultValue = 0, MinValue = 0, MaxValue = 20, Step = 2)]
        public double PriceOffsetPercent { get; set; }
     
        private ExponentialMovingAverage _ema;

        protected override void OnStart()
        {
            _ema = Indicators.ExponentialMovingAverage(Bars.ClosePrices, SMAPeriod);
        }


        private readonly Dictionary<long, double> _positionMaxPriceMoved = new Dictionary<long, double>();

        protected override void OnBar()
        {      
            var currentPosition = CurrentPosition();

            // Check for take profit condition
            if (currentPosition != null && RunningMode != RunningMode.Optimization)
            {
                var positionOpenTime = currentPosition.EntryTime;
                var positionAge = Server.Time - positionOpenTime;
                var currentPrice = currentPosition.TradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
                var priceMovedFraction = (currentPrice - currentPosition.EntryPrice) / (currentPosition.TakeProfit - currentPosition.EntryPrice);
        
                // Update maximum price moved for the current position
                if (!_positionMaxPriceMoved.ContainsKey(currentPosition.Id))
                {
                    _positionMaxPriceMoved[currentPosition.Id] = priceMovedFraction.Value;
                }
                else
                {
                    _positionMaxPriceMoved[currentPosition.Id] = Math.Max(_positionMaxPriceMoved[currentPosition.Id], priceMovedFraction.Value);
                }
        
        
                if (positionAge >= TimeSpan.FromMinutes(15) && _positionMaxPriceMoved[currentPosition.Id] <= 0)
                {
                    ClosePosition(currentPosition);
                    return;
                }
                
                if (positionAge < TimeSpan.FromMinutes(15) && _positionMaxPriceMoved[currentPosition.Id] <= -0.5)
                {
                    ClosePosition(currentPosition);
                    return;
                }
                
                
                if (positionAge > TimeSpan.FromMinutes(15) && positionAge < TimeSpan.FromMinutes(45))
                {
                    if (priceMovedFraction >= 0.75)
                    {
                        ClosePosition(currentPosition);
                        _positionMaxPriceMoved.Remove(currentPosition.Id);
                        return;
                    } 
                } 
            }
                
            var (avgHigh, avgLow) = GetAverageHighLow(NumberOfBars);
            var priceOffset = (avgHigh - avgLow) * PriceOffsetPercent / 100;
            avgHigh += priceOffset;
            avgLow -= priceOffset;
  
            var lastBar = Bars.Last(1);
            var takeProfitPips = Math.Abs(avgHigh - avgLow) / Symbol.PipSize;
            var stopLossPips = takeProfitPips * StopLossToTakeProfitRatio;
        
            if (takeProfitPips < MinDistanceBetweenHighLow || takeProfitPips > MaxDistanceBetweenHighLow || currentPosition != null)
            {
                return;
            }
        
            if (lastBar.Close > avgHigh || lastBar.Close < avgLow)
            {
                var smaValue = _ema.Result.LastValue;
                
                // SMA might look inverted but this is because this is
                // looking for non-trending markets where range trading works.
        
                if (lastBar.Close > avgHigh && lastBar.Close < smaValue)
                {
                   PlaceMarketOrder(TradeType.Sell, stopLossPips, takeProfitPips);
                }
                else if (lastBar.Close < avgLow && lastBar.Close > smaValue)
                {
                   PlaceMarketOrder(TradeType.Buy, stopLossPips, takeProfitPips);
                }
            }
        }

        private (double high, double low) GetAverageHighLow(int numberOfBars)
        {
            var bars = Bars.TakeLast(numberOfBars).ToList();
            double avgHigh = bars.Average(bar => bar.High);
            double avgLow = bars.Average(bar => bar.Low);
        
            return (avgHigh, avgLow);
        }
               
        // Shared code for risk management and error handling below ----------------------------------------------------------

        private void PlaceMarketOrder(TradeType type, double stopLossPips, double? takeProfitPips)
        {
            double? volumeInUnits = VolumeToTrade(stopLossPips);
            if (!volumeInUnits.HasValue || volumeInUnits.Value == 0)
            {
                Print("[PlaceMarketOrder] No volume to trade.");
                return;
            }

            var result = ExecuteMarketOrder(type, Symbol.Name, volumeInUnits.Value, BotIdentifier, stopLossPips, takeProfitPips);
            var position = result.Position;

            if (!position.StopLoss.HasValue)
            {
                Print("[PlaceMarketOrder] Failed to set up stop loss. The distance is probably too close. Closing position and shutting down.");
                position.Close();
                Stop();
            }

            if (takeProfitPips.HasValue && !position.TakeProfit.HasValue)
            {
                Print("[PlaceMarketOrder] Failed to set up take profit. The distances are probably too close. Closing position and shutting down.");
                position.Close();
                Stop();
            }
        }

        private Position CurrentPosition() => Positions.FirstOrDefault(position => position.Label == BotIdentifier);

        private double UsableBalance() => Account.Balance * (MaxUsableBalancePercent / 100.0);

        private double MarginAvailable() => UsableBalance() - Account.Margin;

        private double ProfitToday()
        {
            var allTradesToday = History.Where(ht => ht.EntryTime.Date == Server.Time.Date).ToArray();
            return allTradesToday.Sum(trade => trade.NetProfit);
        }

        private double StartingBalanceToday() => UsableBalance() - ProfitToday();

        private bool MaxDailyLossHasBeenReached()
        {
            var profitToday = ProfitToday();
            var maxRiskAmountPerDay = StartingBalanceToday() * (MaxDailyLossPercent / 100.0);
            Print($"[MaxDailyLossHasBeenReached] Profit {Math.Round(profitToday)} < maxRiskAmountPerDay {-Math.Round(maxRiskAmountPerDay)}: {profitToday < -maxRiskAmountPerDay}");
            return profitToday < -maxRiskAmountPerDay;
        }
        
        private double? VolumeToTrade(double stopPips)
        {
            if (MaxDailyLossHasBeenReached()) { Print("[VolumeToTrade] Daily loss reached."); return null; }
        
            var maxRiskAmountPerTrade = UsableBalance() * (MaxRiskPerTradePercent / 100.0);
            var stopLoss = Symbol.PipSize * stopPips;
            var riskAmount = UsableBalance() * MaxRiskPerTradePercent / 100;
            var volumeInSymbolCurrency = (maxRiskAmountPerTrade / stopLoss);
        
            double volume = Symbol.QuoteAsset.Name switch
            {
                "SEK" => Math.Floor(volumeInSymbolCurrency),
                "USD" => volumeInSymbolCurrency / Symbols.GetSymbol("USDSEK").Ask,
                _ => throw new NotSupportedException($"[VolumeToTrade] I don't know how to trade in currency: {Symbol.QuoteAsset.Name}")
            };
        
            var marginPerVolumeUnit = Symbol.GetEstimatedMargin(TradeType.Buy, Symbol.VolumeInUnitsMin);
            var maxVolume = Math.Floor(MarginAvailable() / marginPerVolumeUnit) * Symbol.VolumeInUnitsMin;
            volume = Math.Min(volume, maxVolume);
        
            if (MarginAvailable() > riskAmount)
            {
                if (volume < Symbol.VolumeInUnitsMin)
                {
                    Print("[VolumeToTrade] Volume is below minimum volume. E.g. stop pips are too large to be able to trade within risk limits.");
                    return null;
                }
                else { return volume; }
            }
            else
            {
                Print("[VolumeToTrade] There is not enough margin available in the account to make the planned trade. Skipping.");
                return null;
            }
        }
    }
}