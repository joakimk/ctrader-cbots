using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

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

        protected override void OnBar()
        {       
            var position =  CurrentPosition();

        }

        // Shared code for risk management and error handling below ----------------------------------------------------------

        protected override void OnStart()
        {
            ReportToHealthchecksIfMarketIsClosed();
        }
        
        protected override void OnTick()
        {
            // Only run at certain intervals so that backtests can use realistic tick data
            // that works exactly like live without being super slow.
            var lastBarMinute = Bars.Last(1).OpenTime.Minute;
            if(lastRunOnMinute == lastBarMinute) {
                return;
            }
            
            ReportToHealthchecks();

            lastRunOnMinute = lastBarMinute;
        }

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

       private double ProfitToday() {
            // This explicitly uses History without filtering on this bot instance
            // to keep losses in check when running multiple bots.
            var allTradesToday = History.Where(ht => ht.EntryTime.Date == Server.Time.Date).ToArray();
            var profitToday = allTradesToday.Sum(trade => trade.NetProfit);
            
            Print($"There are {Positions.Count} open positions.");
            foreach(var position in Positions) {
                // Ignore positions without stop loss (like the cTrader strategy provider hold positions).
                if(position.StopLoss == null) {
                    continue;
                }
                
                if(position.TradeType == TradeType.Buy) {
                    var positionProfit = (double) ((position.StopLoss - position.EntryPrice) / position.Symbol.PipSize) *  position.VolumeInUnits * position.Symbol.TickValue;

                    Print($"Position {position.SymbolName}:{position.EntryPrice}:{position.TradeType} has profit ${positionProfit}");
                    profitToday += positionProfit;
                } else {
                    var pips = (position.EntryPrice - position.StopLoss) / position.Symbol.PipSize;
                    var positionProfit = (double) (pips * position.VolumeInUnits * position.Symbol.TickValue);
                    Print($"Position {position.SymbolName}:{position.EntryPrice}:{position.TradeType} has profit {positionProfit}");
                    profitToday += positionProfit;
                }
            }
            
            return profitToday;
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
                "GBP" => volumeInSymbolCurrency / Symbols.GetSymbol("GBPSEK").Ask,

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