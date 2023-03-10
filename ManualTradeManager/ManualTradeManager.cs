using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using cAlgo.API;
using cAlgo.API.Collections;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.None)]
    public class CustomPRODManualTradeManager : Robot
    {
        [Parameter(DefaultValue = 150)]
        public int InitialStopPips { get; set; }

        [Parameter(DefaultValue = 300)]
        public int IncrementOnPips { get; set; }
        
        protected override void OnStart()
        {
        }

        protected override void OnTick()
        {
            // TODO:
            // - [ ] healthcheck
            // - [ ] trailing stop, perhaps one increment behind
            // - [ ] add test cases before trying to do addons
            MakeVerificationTradesInBacktests();
            
            foreach(var position in Positions) {
                if(position.Label != null) { continue; }
                
                if(!position.StopLoss.HasValue) {
                    position.ModifyStopLossPips(InitialStopPips);
                }
                
                //var incrementPriceDifference = Symbol.PipSize * IncrementOnPips;
                
                if(position.Pips > IncrementOnPips) {
                    if(position.TradeType == TradeType.Buy) {
                        if(position.StopLoss < position.EntryPrice) {
                            position.ModifyStopLossPrice(position.EntryPrice + Symbol.Spread);
                        }
                    }
                    
                    if(position.TradeType == TradeType.Sell) {
                        if(position.StopLoss > position.EntryPrice) {
                            position.ModifyStopLossPrice(position.EntryPrice - Symbol.Spread);
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
            
        }
    }
}