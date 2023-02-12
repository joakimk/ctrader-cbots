import:
	@cd ../cAlgo && git pull
	@cp "../cAlgo/Custom - PROD - Trend Capture/Custom - PROD - Trend Capture/Custom - PROD - Trend Capture.cs" TrendCapture/TrendCapture.cs
	@cp ../cAlgo/TC-US100-LONG.cbotset TrendCapture/
	@cp ../cAlgo/TC-US100-SHORT.cbotset TrendCapture/
	@cp ../cAlgo/TC-US100-US-OPEN.cbotset TrendCapture/
