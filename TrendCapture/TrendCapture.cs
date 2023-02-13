using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Timers;
using System.IO;
using System.Text.Json;

using cAlgo.API;
using cAlgo.API.Collections;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

// This strategy is built for use with US100, but can likely work with other markets too
// since priceaction is mostly the same regardless of financial instrument.

// The idea with this strategy is to try and join trending markets at a good place and then
// hold that position as long as possible by applying a moving stop once it has moved far enough.
//
// Loosing positions are always based on "Risk %", but winner size does not have any limit. In
// other words even if most positions loose you could still win overall.
//
// Before using this, backtest and optimize. I would suggest optimizing last 4 weeks minus one, then
// running backtest on the last week to confirm that setup still works.
//
// Optimize and backtest two instances of the script, one to trade upswings and one to trade downswings.
//
// Redo the optimization and backtesting about once a week.
//
// Do not modify ongoing trades in any way. Just as it can increase wins it can also increase losses.
// The backtested script is far more likely to make the correct decisions (over time) than you are.

// Some possible future improvements:
//
// - Better entries.
// - Adding on to winning positions to make them bigger.
// - Selling part of positions when the market pushes quickly and rebuying when it pulls back.
// - Possibly limit max daily reversal, e.g. if you win 1000, only allow max 300 loss after that.

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.FullAccess, TimeZone = TimeZones.CentralEuropeanStandardTime)]
    public class CustomPRODTrendCapture : Robot
    {
        // Identifies which bot made a trade. Has to be unique for each bot instance.
        [Parameter("Bot identifier")]
        public String BotIdentifier { get; set; }
        
        [Parameter("Healthchecks URL")]
        public string HealthchecksUrl { get; set; }
        
        // Risk -------------------------------------------------------------------------------
        
        [Parameter("Risk %", Group = "Risk", DefaultValue = 3.5)]
        public double MaxRiskPerTradePercent { get; set; }
        
        [Parameter("Max DL %", Group = "Risk", DefaultValue = 8)]
        public double MaxDailyLossPercent { get; set; }
        
        // Strategy ---------------------------------------------------------------------------

        [Parameter("Trade upswings?", Group = "Strategy", DefaultValue = true)]
        public bool TradeDirectionIsBuy { get; set; }
        
        [Parameter("Trade both?", Group = "Strategy", DefaultValue = false)]
        public bool TradeBothWays { get; set; }
        
        [Parameter("Trade only US open?", Group = "Strategy", DefaultValue = false)]
        public bool OnlyTradeUsMarketOpen { get; set; }
        
        [Parameter("Trade only EU open?", Group = "Strategy", DefaultValue = false)]
        public bool OnlyTradeEuMarketOpen { get; set; }
      
        [Parameter("Trend MA", Group = "Strategy", DefaultValue = 50, MinValue = 3, Step = 1)]
        public int TrendMA { get; set; }
        
        [Parameter("TS Scale", Group = "Strategy", DefaultValue = 4, MinValue = 2, Step = 0.1)]
        public double TrailingStopScale { get; set; }
        
        [Parameter("Stop Scale", Group = "Strategy", DefaultValue = 1.5, MinValue = 0.5, Step = 0.1)]
        public double StopScale { get; set; }
        
        [Parameter("Min pullback pips", Group = "Strategy", DefaultValue = 50, MinValue = 0, Step = 25)]
        public double MinDistanceToHighestPriceInPips { get; set; }
        
        [Parameter("Min high lookback", Group = "Strategy", DefaultValue = 10, MinValue = 0, Step = 1)]
        public int HighLookbackBars { get; set; }
        
        [Parameter("Trend lookback bars", Group = "Strategy", DefaultValue = 120, MinValue = 10, Step = 10)]
        public int TrendLookbackDistance { get; set; }
        
        [Parameter("Trend confirm bars", Group = "Strategy", DefaultValue = 60, MinValue = 10, Step = 10)]
        public int TrendConfirmBarCount { get; set; }
        
        [Parameter("Early P %", Group = "Strategy", DefaultValue = 5, MinValue = 1, Step = 1)]
        public double TakeEarlyProfitAtAccountIncreasePercent { get; set; }
        
        [Parameter("Early C %", Group = "Strategy", DefaultValue = 25, MinValue = 0, Step = 25, MaxValue = 100)]
        public double PercentToCaptureEarly { get; set; }
      
        // The hours of the day between which it can enter a trade.
        [Parameter("Start hour", Group = "Strategy", DefaultValue = 0, MinValue = 0, Step = 1)]
        public int StartHour { get; set; }
        [Parameter("Stop hour", Group = "Strategy", DefaultValue = 23, MinValue = 0, Step = 1)]
        public int StopHour { get; set; }
        
        [Parameter("Source", Group = "Data")]
        public DataSeries Source { get; set; }
        
        private MovingAverage trendMa;
        
        private int lastRunOnMinute = -1;
        private PersistedPositionState persistedPositionState;
       
        protected override void OnStart()
        {
            trendMa = Indicators.MovingAverage(Source, TrendMA, MovingAverageType.Simple);
            
            if(MaxDailyLossHasBeenReached()) { Print("Daily loss reached."); }
            
            ReportToHealthchecksIfMarketIsClosed();
            
            var position = CurrentPosition();
            if(position != null) {
                persistedPositionState = PersistedPositionState.Load(position, IsBacktesting);
            }

            Positions.Opened += OnPositionOpened;
            Positions.Closed += OnPositionClosed;
        }
        
        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            var position = args.Position;
            if(position.Label != BotIdentifier) { return; }

            persistedPositionState = PersistedPositionState.Load(position, IsBacktesting);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            if(position.Label != BotIdentifier) { return; }
            
            persistedPositionState.Reset();
            persistedPositionState = null;
        }
        
        // Backup code
        // robocopy C:\Users\jocke\Documents\cAlgo Z:\shared_to_vm\Documents\cAlgo /MIR
        
        protected override void OnTick()
        {
            // Only run at certain intervals so that backtests can use realistic tick data
            // that works exactly like live without being super slow.
            var lastBarMinute = Bars.Last(1).OpenTime.Minute;
            if(lastRunOnMinute == lastBarMinute) {
                return;
            } else {
                lastRunOnMinute = lastBarMinute;
            }
            
            ReportToHealthchecks();
            
            if(ManageExistingPosition())         { return; }
            if(IsOutsideTradingHours())          { return; }
            
            EnterNewPosition();
        }

        protected override void OnStop()
        {
        }
        
        private bool ManageExistingPosition()
        {
            var position = CurrentPosition();
        
            if (position == null)
            {
                return false;
            }
        
            HandleMovingStop(position);
            HandleEarlyProfit(position);
        
            return true;
        }
        
        private void HandleMovingStop(Position position)
        {
            if (persistedPositionState.HasChangedToMovingStop || 
                position.NetProfit <= 0 || 
                position.HasTrailingStop || 
                position.TakeProfit.HasValue)
            {
                return;
            }
        
            // Calculate stop loss values
            var initialStopPips = Math.Abs(position.EntryPrice - position.StopLoss.Value) / Symbol.PipSize;
            var pipsAwayFromStop = Math.Abs(Bars.Last(0).Close - position.StopLoss.Value) / Symbol.PipSize;
        
            if (pipsAwayFromStop <= initialStopPips * TrailingStopScale)
            {
                return;
            }
        
            // Modify trailing stop
            if (!position.ModifyTrailingStop(true).IsSuccessful)
            {
                // If failed to set up trailing stop, close position and shut down
                Print("Failed to set up trailing stop. Closing position and shutting down.");
                position.Close();
                Stop();
            }
        
            // Modify stop loss price to entry price
            position.ModifyStopLossPrice(position.EntryPrice);
        
            // Update persisted position state
            persistedPositionState.HasChangedToMovingStop = true;
            persistedPositionState.Save();
        }
        
        private void HandleEarlyProfit(Position position)
        {
            if (persistedPositionState.HasTakenEarlyProfit || 
                PercentToCaptureEarly == 0.0 ||
                position.NetProfit <= Account.Balance * (TakeEarlyProfitAtAccountIncreasePercent / 100.0))
            {
                return;
            }
        
            // Reduce position volume and update stop loss price to entry price
            if(PercentToCaptureEarly == 100.0) {
                position.Close();
            } else {
                position.ModifyVolume(position.VolumeInUnits * (1 - (PercentToCaptureEarly / 100.0)));
                position.ModifyStopLossPrice(position.EntryPrice);
            }
        
            // Update persisted position state
            persistedPositionState.HasTakenEarlyProfit = true;
            persistedPositionState.Save();
        }

                
        private bool IsOutsideTradingHours() {
            if(OnlyTradeEuMarketOpen) {
                return !EuMarketRecentlyOpened();
            }
            else if(OnlyTradeUsMarketOpen) {
                return !UsMarketRecentlyOpened();
            } else {
                return (Server.Time.Hour < StartHour || Server.Time.Hour > StopHour) ||
                    UsMarketRecentlyOpened();
            }
        }
        
        // Don't enter when the US market has recently opened. It
        // often generates false signals since it's too volatile then.
        private bool UsMarketRecentlyOpened() {
            return Server.Time.Hour == 15 && Server.Time.Minute >= 30;
        }
        
        private bool EuMarketRecentlyOpened() {
            return Server.Time.Hour == 9;
        }
        
        private void EnterNewPosition() {
            if(TradeBothWays) {
                EnterBullMarket();
                EnterBearMarket();
            }
            else if(TradeDirectionIsBuy) {
                EnterBullMarket();
            } else {
                EnterBearMarket();
            }
        }
        
        private void EnterBullMarket() {
            var maEntries = trendMa.Result.TakeLast(TrendLookbackDistance).ToArray();
            var bars = Bars.TakeLast(TrendLookbackDistance).ToArray();
            var barsAboveMa = 0;
            for(var i = 0; i < maEntries.Count(); i += 1) {
                if(bars[i].Close > maEntries[i]) { barsAboveMa += 1; }
            }

            var isInUprend = barsAboveMa > TrendConfirmBarCount;
            if(!isInUprend) {
                return;
            }
            
            var lastMa = trendMa.Result.Last(0);
            var previousMa = trendMa.Result.Last(1);
            var previousMa2 = trendMa.Result.Last(2);
 
            var previousLow = Bars.Last(1).Low;
            var previousLow2 = Bars.Last(2).Low;
            var lastOpen = Bars.Last(0).Open;
            
            var minLow = Bars.TakeLast(10).Min(bar => bar.Low);

            if(previousLow < previousMa || previousLow2 < previousMa2) {
                if(lastOpen > lastMa) {
                    double? highestPrice = FindLastHigh(HighLookbackBars);
                    if(!highestPrice.HasValue) { return; }
                
                
                    var distanceToHighestPriceInPips = (highestPrice.Value - lastOpen - Symbol.Spread) / Symbol.PipSize;
                    var distanceToStopInPips = (lastOpen - minLow + Symbol.Spread) * StopScale / Symbol.PipSize;

                    // Skip if the risk to reward is too high
                    if(distanceToHighestPriceInPips < MinDistanceToHighestPriceInPips) {
                        return;
                    }

                    PlaceMarketOrder(TradeType.Buy, distanceToStopInPips, null);
                }
            }
        }
        
        private void EnterBearMarket() {
            var maEntries = trendMa.Result.TakeLast(TrendLookbackDistance).ToArray();
            var bars = Bars.TakeLast(TrendLookbackDistance).ToArray();
            var barsBelowMa = 0;
            for(var i = 0; i < maEntries.Count(); i += 1) {
                if(bars[i].Close < maEntries[i]) { barsBelowMa += 1; }
            }

            var isInDowntrend = barsBelowMa > TrendConfirmBarCount;
            if(!isInDowntrend) {
                return;
            }
            
            var lastMa = trendMa.Result.Last(0);
            var previousMa = trendMa.Result.Last(1);
            var previousMa2 = trendMa.Result.Last(2);
 
            var previousHigh = Bars.Last(1).High;
            var previousHigh2 = Bars.Last(2).High;
            var lastOpen = Bars.Last(0).Open;
            
            var maxHigh = Bars.TakeLast(10).Max(bar => bar.High);

            if(previousHigh > previousMa || previousHigh2 > previousMa2) {
                if(lastOpen < lastMa) {    
                    double? lowestPrice = FindLastLow(HighLookbackBars);
                    if(!lowestPrice.HasValue) { return; }
                      
                    var distanceToLowestPriceInPips = (lastOpen - lowestPrice.Value - Symbol.Spread) / Symbol.PipSize;
                    var distanceToStopInPips = (maxHigh - lastOpen + Symbol.Spread) * StopScale / Symbol.PipSize;

                    if(distanceToLowestPriceInPips < MinDistanceToHighestPriceInPips) {
                        Print("bad risk to reward");
                        return;
                    }
                    
                    PlaceMarketOrder(TradeType.Sell, distanceToStopInPips, null);
                    
                }
            }
        }
        
        // Only returns a high if it can find the price was lower than the current bar
        // before that high (e.g. we're in an uptrend).
        private double? FindLastHigh(int offset = 0) {
            var reverseBarHistory = Bars.SkipLast(offset).TakeLast(48*60).Reverse();
            var lastBar = Bars.Last(1);
            
            double highestPrice = 0;
 
            foreach(var bar in reverseBarHistory) {
                if(bar.High > highestPrice) {
                    highestPrice = bar.High;
                } else if(highestPrice > lastBar.High && bar.Close < lastBar.Close) {
                    return highestPrice;
                }
            }
            
            return null;
        }
        
        private double? FindLastLow(int offset = 0) {
            var reverseBarHistory = Bars.SkipLast(offset).TakeLast(48*60).Reverse();
            var lastBar = Bars.Last(1);
            
            double lowestPrice = 999_999_999;
 
            foreach(var bar in reverseBarHistory) {
                if(bar.Low < lowestPrice) {
                    lowestPrice = bar.Low;
                } else if(lowestPrice < lastBar.Low && bar.Close > lastBar.Close) {
                    return lowestPrice;
                }
            }
            
            return null;
        }
        
        private void PlaceMarketOrder(TradeType type, double stopLossPips, double? takeProfitPips) {
            double? volumeInUnits = VolumeToTrade(stopLossPips);
            if(!volumeInUnits.HasValue || volumeInUnits.Value == 0) {
                Print("No volume to trade.");
                return;
            }
            
            var result = ExecuteMarketOrder(type, Symbol.Name, volumeInUnits.Value, BotIdentifier, stopLossPips, takeProfitPips);
            var position = result.Position;
            
            if(!position.StopLoss.HasValue) {
                Print("Failed to set up stop loss. The distance is probably too close. Closing position and shutting down.");
                position.Close();
                Stop();
            }
            
            if(takeProfitPips.HasValue && !position.TakeProfit.HasValue) {
                Print("Failed to set up take profit. The distances are probably too close. Closing position and shutting down.");
                position.Close();
                Stop();
            }
        }
        
        private Position CurrentPosition() {
            return Positions.Find(BotIdentifier);
        }
        
        // Returns the volume to trade in order to not risk more than MaxRiskPerTradePercent
        // and also to make the most of smaller distances by using larger volumes.
        //
        // It can return null if the requested trade would use more of the available margin
        // than MaxMarginUsagePercent allows or max daily loss as been reached.
        private double? VolumeToTrade(double stopPips) {
            if(MaxDailyLossHasBeenReached()) { Print("Daily loss reached."); return null; }

            var dailyLossBalanceLeft = StartingBalanceToday() + ProfitToday();

            var maxRiskAmountPerTrade = Account.Balance * (MaxRiskPerTradePercent / 100.0);

            var stopLoss = Symbol.PipSize * stopPips;
            var riskAmount = Account.Balance * MaxRiskPerTradePercent / 100;
            var volume = (maxRiskAmountPerTrade / stopLoss) / Symbols.GetSymbol("USDSEK").Ask;
              
            if(MarginAvailable() > riskAmount) {
                if(volume < Symbol.VolumeInUnitsMin) {
                    return null;
                } else {
                    return volume;
                }
            } else {
                Print("There is not enough margin available in the account to make the planned trade. Skipping.");
                return null;
            }
        }
        
        private double MarginAvailable() {
            return Account.Balance - Account.Margin;
        }
        
        private bool MaxDailyLossHasBeenReached() { 
            var allTradesToday = BotHistory().Where(ht => ht.ClosingTime.Date == Server.Time.Date);
            var profitToday = allTradesToday.Sum(trade => trade.NetProfit);
            var startingBalance = Account.Balance - profitToday;
            var maxRiskAmountPerDay = StartingBalanceToday() * (MaxDailyLossPercent / 100.0);
            Print($"MaxDL? Profit {Math.Round(ProfitToday())} < maxRiskAmountPerDay {-Math.Round(maxRiskAmountPerDay)}: {ProfitToday() < -maxRiskAmountPerDay}");
            return ProfitToday() < -maxRiskAmountPerDay;
       }
       
       private double StartingBalanceToday() {
            return Account.Balance - ProfitToday();
       }
       
       private double ProfitToday() {
            var allTradesToday = BotHistory().Where(ht => ht.EntryTime.Date == Server.Time.Date).ToArray();
            var profitToday = allTradesToday.Sum(trade => trade.NetProfit);
            return profitToday;
       }
       
       private IEnumerable<HistoricalTrade> BotHistory() {
            return History.Where(trade => trade.Label == BotIdentifier);
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
    }
}

[Serializable]
public class PersistedPositionState
{
    public bool HasChangedToMovingStop { get; set; }
    public bool HasTakenEarlyProfit { get; set; }
    
    public bool IsBacktesting { get; set; }
    public string FilePath { get; set; }
    
    public PersistedPositionState() {}
    
    private PersistedPositionState(bool hasChangedToMovingStop, bool hasTakenEarlyProfit, string filepath, bool isBacktesting)
    {
        HasChangedToMovingStop = hasChangedToMovingStop;
        HasTakenEarlyProfit = hasTakenEarlyProfit;
        FilePath = filepath;
        IsBacktesting = isBacktesting;
    }

    public void Save()
    {
        if(IsBacktesting) { return; }
        
        string json = JsonSerializer.Serialize(this);
        File.WriteAllText(FilePath, json);
    }

    public static PersistedPositionState Load(Position position, bool isBacktesting)
    {
        if(isBacktesting) { return new PersistedPositionState(false, false, "", true); }
        
        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string filePath = Path.Combine(desktopPath, $"{position.Label}-{position.Id}-positionstate.json");
        if (!File.Exists(filePath))
        {
            return new PersistedPositionState(false, false, filePath, false);
        }

        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<PersistedPositionState>(json);
    }

    public void Reset()
    {
        HasChangedToMovingStop = false;
        HasTakenEarlyProfit = false;
        
        if(IsBacktesting) { return; }
        
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
