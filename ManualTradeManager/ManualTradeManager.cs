using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using cAlgo.API;
using cAlgo.API.Collections;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.FullAccess)]
    public class CustomPRODManualTradeManager : Robot
    {
        [Parameter("Healthchecks URL")]
        public string HealthchecksUrl { get; set; }

        [Parameter(DefaultValue = 350, MinValue = 50, MaxValue = 500)]
        public int InitialStopPips { get; set; }

        [Parameter(DefaultValue = 350, MinValue = 50, MaxValue = 1000)]
        public int IncrementOnPips { get; set; }
        
        [Parameter(DefaultValue = 2)]
        public double RiskPercentage { get; set; }
        
        [Parameter(DefaultValue = 0.05)]
        public double InitialVolume { get; set; }
        
        private List<PendingOrder> pendingOrders = new List<PendingOrder>();
        private int lastReportedToHealthchecksOnMinute = -1;
        
        protected override void OnStart()
        {
            ReportToHealthchecksIfMarketIsClosed();
            
            if(IsBacktesting) {
                pendingOrders.Add(new PendingOrder(DateTime.Parse("2023-03-09 07:24"), TradeType.Sell));
                pendingOrders.Add(new PendingOrder(DateTime.Parse("2023-03-09 16:43"), TradeType.Sell));
            }
        }

        protected override void OnTick()
        {
            var lastBarMinute = Bars.Last(1).OpenTime.Minute;
            if(lastReportedToHealthchecksOnMinute != lastBarMinute) {
                ReportToHealthchecks();
                lastReportedToHealthchecksOnMinute = lastBarMinute;
                return;
            }
            
            // TODO:
            // - [ ] trailing stop, perhaps one increment behind
            // - [ ] add test cases before trying to do addons
            MakeVerificationTradesInBacktests();
            
            foreach(var position in Positions) {
                if(position.Label != null) { continue; }
                
                AdjustVolumeBasedOnCurrentStopPips(position);
            
                if(!position.StopLoss.HasValue) {
                    position.ModifyStopLossPips(InitialStopPips);
                }
                
                //var incrementPriceDifference = Symbol.PipSize * IncrementOnPips;
                
                if(position.Pips > IncrementOnPips) {
                    if(position.TradeType == TradeType.Buy) {
                        if(position.StopLoss < position.EntryPrice) {
                            position.ModifyStopLossPrice(position.EntryPrice + Symbol.Spread);
                            
                            // Auto management of the trade is not implemented yet, so trail in backtests.
                            if(IsBacktesting) {
                                position.ModifyTrailingStop(true);
                            }
                        }
                    }
                    
                    if(position.TradeType == TradeType.Sell) {
                        if(position.StopLoss > position.EntryPrice) {
                            position.ModifyStopLossPrice(position.EntryPrice - Symbol.Spread);
                            
                            // Auto management of the trade is not implemented yet, so trail in backtests.
                            if(IsBacktesting) {
                                position.ModifyTrailingStop(true);
                            }
                        }
                    }
                }                
            }
        }

        protected override void OnStop()
        {
        }
        
        private void MakeVerificationTradesInBacktests()
        {
            if(!IsBacktesting) { return; }
            
            // 2023-03-09 07:24 - small reversal
            // 2023-03-09 16:43 - huge reversal
            pendingOrders
                .Where(pendingOrder => !pendingOrder.Placed && pendingOrder.Time < Server.Time)
                .ToList()
                .ForEach(pendingOrder =>
                {
                    ExecuteMarketOrder(pendingOrder.TradeType, Symbol.Name, Symbol.VolumeInUnitsMin, null, InitialStopPips, null);
                    
                    //var volume = Symbol.VolumeForProportionalRisk(ProportionalAmountType.Balance, RiskPercentage, InitialStopPips);
                    //var volume = InitialVolume;
                    
                    //ExecuteMarketOrder(pendingOrder.TradeType, Symbol.Name, volume, null, InitialStopPips, null);
                    pendingOrder.Placed = true;
                });
        }
        
        private void AdjustVolumeBasedOnCurrentStopPips(Position position)
        {
            if(position.VolumeInUnits != Symbol.VolumeInUnitsMin) { return; }
            if(position.TradeType == TradeType.Buy && position.StopLoss > position.EntryPrice) { return; }
            if(position.TradeType == TradeType.Sell && position.StopLoss < position.EntryPrice) { return; }
            //if(position.NetProfit < 0) { return; }
            
            position.ModifyVolume(InitialVolume);
            
            // This does not return proper values, yet
            /*var volume =
                Symbol.VolumeForProportionalRisk(
                    ProportionalAmountType.Balance,
                    RiskPercentage,
                    InitialStopPips
                );
            
            Print(volume);
            if(position.VolumeInUnits != volume) {
                Print("Adjusting volume to match risk percentage.");
                //position.ModifyVolume(volume);
            }*/
        }
        
               
       private void ReportToHealthchecksIfMarketIsClosed() {
            var timer = new System.Timers.Timer(60 * 1000);
            
            timer.Elapsed += (_, _) => {
                BeginInvokeOnMainThread(() => {
                    if(!Symbol.MarketHours.IsOpened()) {
                        Print("Market is closed. Reporting to healthchecks from timer thread.");
                        ReportToHealthchecks();
                    } else {
                        Print("Market is open. Reporting to healthchecks via OnTick.");
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

        private class PendingOrder
        {
            public DateTime Time { get; }
            public TradeType TradeType { get; }
            public bool Placed { get; set; }

            public PendingOrder(DateTime time, TradeType tradeType)
            {
                Time = time;
                TradeType = tradeType;
                Placed = false;
            }
        }
    }
}