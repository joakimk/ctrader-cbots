`I’m going to ask you to write a cTrader automate bot for me.

Base it on this code:

using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo
{
    [Robot(TimeZone = TimeZones.CentralEuropeanStandardTime, AccessRights = AccessRights.None)]
    public class BotName : Robot
    {
        [Parameter(“Bot Identifier”, DefaultValue = “BotName”)]
        public string BotIdentifier { get; set; }
        
        // Code specific to this bot ————————————————————————————————————————————
        
        protected override void OnStart() { }
        protected override void OnTick() { }
        protected override void OnBar() { }
        
        // Shared code for risk management and error handling below —————————————————————————————

        private void PlaceMarketOrder(TradeType type, double stopLossPips, double? takeProfitPips, double? overrideVolume) // Ignore implementation.
        private Position CurrentPosition() => Positions.FirstOrDefault(position => position.Label == BotIdentifier);
}

Here are some instructions before we start.

- MarketSeries.GetBars(1) is now Bars.Last(1)
- ModifyStopLoss is now positon.ModifyStopLossPrice
- Bars.Close.Count is now Bars.Count
- Bars.Close.Last(1) is now Bars.Last(1).Close
- CloseTimeUtc is now ClosingTime
- Early returns should be one liners.
- If the entire body of an if statement is “return;”, or “return false;” put it on the same line as the if statement like “if(foo) { return; }”.
- Add any improvement you think will make the bot perform better.
- Only use the API, no third party indicators.
- Only show me example code and only the new changes.
- When adding parameters add appropriate min max and stepping for the US100 market.
- For parameters bigger than 50 make stepping 10.
- Bars[i].ClosingTime does not exist, use Bars[i].OpenTime when possible.
- Update the bot name to match what it does.
- Do not show any of the pre existing code.
- Do not show the [Robot.. line
- Put OnBar, OnTick or OnStart on top and new helper methods below that.
- When I specify specific numbers for things, assume it needs to be configurable.
- Only keep one position at a time.
- When using a single moving average always call it _movingAverage regardless of type.

Here are the specific instructions for this bot.

- If the market goes higher than the last 5 days maximum high on the previous day.
    - Wait for the market to go back to the low of the previous day.
    - Enter long 100 pips below that low.
    - Sell 5 minutes before the close of the day.