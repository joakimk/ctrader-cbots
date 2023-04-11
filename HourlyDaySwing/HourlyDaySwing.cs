using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

// Trading bot for capturing full intra day swings on US100 and adding on to working trades.

// NOTE: This bot ignores MaxRiskPerTradePercent in favour of InitialPositionSize and InitialStopPips.

// Usually set this for optimization:
// - 1m data, 0 fees, copy live spread
// - optimization critera = maximize netprofit, minimize balance drawdown %, maximize total trades.
//
// Optimize 1 month, verify 3 or more weeks in backtests after that period.

namespace cAlgo
{
    [Robot(TimeZone = TimeZones.EasternStandardTime, AccessRights = AccessRights.FullAccess)]
    public class HourlyDaySwing : Robot
    {
        [Parameter("Bot Identifier", DefaultValue = "HDS")]
        public string BotIdentifier { get; set; }

        [Parameter("Max Risk Per Trade Percent", DefaultValue = 1.0)]
        public double MaxRiskPerTradePercent { get; set; }

        [Parameter("Max Daily Loss Percent", DefaultValue = 2.0)]
        public double MaxDailyLossPercent { get; set; }
        
        [Parameter("Max Usable Balance Percent", DefaultValue = 100.0)]
        public double MaxUsableBalancePercent { get; set; }
        
        [Parameter("Healthchecks URL")]
        public string HealthchecksUrl { get; set; }
        
        // Code specific to this bot -----------------------------------------------------------------------------------------


        [Parameter("Add at pips from entry", DefaultValue = 500, MinValue = 100, MaxValue = 1000, Step = 100)]
        public double AddAtPipsFromEntry { get; set; }
        
        [Parameter("Initial position size", DefaultValue = 0.01, MinValue = 0.01, MaxValue = 0.2, Step = 0.01)]
        public double InitialPositionSize { get; set; }
        
        [Parameter("Initial stop pips", DefaultValue = 500, MinValue = 100, MaxValue = 2000, Step = 100)]
        public double InitialStopPips { get; set; }
        
        [Parameter("Add ratio", DefaultValue = 2, MinValue = 0.25, MaxValue = 20, Step = 0.25)]
        public double AddRatio { get; set; }
        
        [Parameter("Moving Average", DefaultValue = 20, MinValue = 5, MaxValue = 200, Step = 10)]
        public int MovingAverage { get; set; }

        private SimpleMovingAverage _ma;

        protected override void OnStart()
        {
            _ma = Indicators.SimpleMovingAverage(Bars.ClosePrices, MovingAverage);
            
            ReportToHealthchecksIfMarketIsClosed();
        }

        protected override void OnBar()
        {
            var lastBar = Bars.Last(1);
            
            var currentPostion = CurrentPosition();
            
            if(lastBar.OpenTime.Hour == 9 && lastBar.OpenTime.Date == Server.Time.Date && currentPostion == null) {
                if(lastBar.Close > lastBar.Open && lastBar.Close > _ma.Result.Last(1)) {
                    PlaceMarketOrder(TradeType.Buy, InitialStopPips, null, InitialPositionSize); 
                }
            }

            if(currentPostion == null) { return; }
            
            var distanceToDouble = AddAtPipsFromEntry * Symbol.PipSize;
            if(currentPostion.EntryPrice + distanceToDouble < Symbol.Ask) {
                if(currentPostion.ModifyVolume(currentPostion.VolumeInUnits * AddRatio).IsSuccessful) {
                    if(!currentPostion.ModifyStopLossPrice(currentPostion.EntryPrice + Symbol.Spread).IsSuccessful &&
                        RunningMode == RunningMode.Optimization) {
                        Stop();
                    }
                }
            }
            
            if(lastBar.OpenTime.Hour == 15) { currentPostion.Close(); }
        }

        private int lastReportedToHealthchecksOnMinute = -1;
        protected override void OnTick()
        {
            var lastMinute = Server.Time.Minute;
            if(lastReportedToHealthchecksOnMinute != lastMinute) {
                ReportToHealthchecks();
                lastReportedToHealthchecksOnMinute = lastMinute;
            }
        }

        // Shared code for risk management and error handling below ----------------------------------------------------------

        private void PlaceMarketOrder(TradeType type, double stopLossPips, double? takeProfitPips, double? overrideVolume)
        {
            double? volumeInUnits = overrideVolume.HasValue ? overrideVolume : VolumeToTrade(stopLossPips);
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
        
        private void ReportToHealthchecksIfMarketIsClosed() {
            var timer = new System.Timers.Timer(60 * 1000);
            
            timer.Elapsed += (_, _) => {
                BeginInvokeOnMainThread(() => {
                    if(!Symbol.MarketHours.IsOpened()) {
                        Print("[ReportToHealthchecksIfMarketIsClosed] Market is closed. Reporting to healthchecks from timer thread.");
                        ReportToHealthchecks();
                    } else {
                        Print("[ReportToHealthchecksIfMarketIsClosed] Market is open. Reporting to healthchecks via OnTick.");
                    }
                });
            };
            
            timer.Start();   
       }
        
       private void ReportToHealthchecks()
       {
            if(IsBacktesting) { return; }
            
            if (string.IsNullOrWhiteSpace(HealthchecksUrl))
                return;

            try {
                using (var client = new WebClient())
                {
                    client.DownloadString(HealthchecksUrl);
                }
            } catch (Exception ex) {}
       }
    }
}