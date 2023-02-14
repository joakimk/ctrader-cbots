# ctrader-cbots

These are some of the algorithmic trading bots I've written for use with [cTrader](https://ctrader.com/).

There is no guarantee of any kind that they will work for you. Do your own backtesting in cTrader and only risk what you are comfortable loosing. Also it's a good idea to run them on demo mode before risking any real money.

The strategies rely on config being up to date with the current market conditions. I'm aiming for code that works when config is updated about once a week. Updating config takes a long time since optimization runs can take hours to find a good setup for each instance (even with many CPU cores).

# Backtesting and optimizing in cTrader

More detailed instructions might come later, however always run with tick-data since it produces realistic results. The scripts still run fast since they most often only run the code on the first tick of every minute.

WIP

# Running

- I always backtest last week and ensure it produces good results before I turn a bot on.
- WIP
