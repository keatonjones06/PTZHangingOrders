#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public class PTZDailyPlanStrategy_Simplified : Strategy
	{
		private double lastCheckPrice;
		private DateTime lastCheckTime;
		private Dictionary<double, string> priceLevels;
		private DateTime lastLevelUpdate;
		private Dictionary<double, LevelCrossInfo> levelCrossTracker;

		private double dailyPnL;
		private DateTime currentTradingDate;
		private bool dailyLimitReached;
		private Dictionary<double, DateTime> levelLastTradeTime;

		private double contract1ExitPrice;
		private double contract2StopPrice;
		private double initialStopPrice;
		private bool contract1Exited;
		private bool contract2BreakevenSet;
		private bool initialStopHit;

		private string c1TargetTag = "C1_Target";
		private string c2TargetTag = "C2_Target";
		private string c2StopTag = "C2_Stop";
		private string initialStopTag = "Initial_Stop";

		private class LevelCrossInfo
		{
			public bool CrossedAbove { get; set; }
			public bool CrossedBelow { get; set; }
			public DateTime CrossTime { get; set; }
			public string Description { get; set; }
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"Simplified trailing system: 1 or 2 contracts with 22-tick initial stop, visual targets/stops";
				Name = "PTZ Daily Plan Strategy (Simplified)";
				Calculate = Calculate.OnPriceChange;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 30;
				IsFillLimitOnTouch = false;
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution = OrderFillResolution.Standard;
				Slippage = 0;
				StartBehavior = StartBehavior.WaitUntilFlat;
				TimeInForce = TimeInForce.Gtc;
				TraceOrders = false;
				RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 20;
				IsInstantiatedOnEachOptimizationIteration = true;

				UseSupport = true;
				UseResistance = true;
				UsePivotBull = true;
				UsePivotBear = true;
				UseStrengthConfirmed = false;
				UseWeaknessConfirmed = false;
				UseGLLevels = true;

				PriceProximityTicks = 2;
				TradeOnCrossover = true;
				TradeOnTouch = true;

				UseLBLFilter = false;
				RequireLBLInDescription = false;

				NumberOfContracts = 2;
				InitialStopTicks = 22;

				Contract1ScalpTicks = 7;
				Contract1BreakevenTicks = 4;

				Contract2TargetTicks = 80;
				Contract2BreakevenTicks = 4;
				Contract2TrailTicks = 7;

				KeywordSupport = "Support";
				KeywordResistance = "Resistance";
				KeywordPivotBull = "Pivot Bull";
				KeywordPivotBear = "Pivot Bear";
				KeywordStrengthConfirmed = "Strength Confirmed";
				KeywordWeaknessConfirmed = "Weakness Confirmed";
				KeywordGL = "GL";

				EnableTimeFilter = true;
				TradingStartHour = 9;
				TradingStartMinute = 45;
				TradingEndHour = 15;
				TradingEndMinute = 45;

				EnableDailyLossLimit = true;
				DailyLossLimit = 500;
				EnableDailyTargetLimit = true;
				DailyTargetLimit = 500;

				EnableLevelCooldown = true;
				LevelCooldownMinutes = 5;
			}
			else if (State == State.Configure)
			{
			}
			else if (State == State.DataLoaded)
			{
				lastCheckPrice = 0;
				lastCheckTime = DateTime.MinValue;
				priceLevels = new Dictionary<double, string>();
				lastLevelUpdate = DateTime.MinValue;
				levelCrossTracker = new Dictionary<double, LevelCrossInfo>();

				dailyPnL = 0;
				currentTradingDate = DateTime.MinValue;
				dailyLimitReached = false;
				levelLastTradeTime = new Dictionary<double, DateTime>();

				contract1ExitPrice = 0;
				contract2StopPrice = 0;
				initialStopPrice = 0;
				contract1Exited = false;
				contract2BreakevenSet = false;
				initialStopHit = false;
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToTrade)
				return;

			try
			{
				if (Time[0].Date != currentTradingDate.Date)
				{
					ResetDailyTracking();
				}

				UpdateDailyPnL();

				if (CheckDailyLimits())
				{
					if (!dailyLimitReached)
					{
						Print(string.Format("{0}: Daily limit reached. Daily P&L: {1:C}", Time[0], dailyPnL));
						dailyLimitReached = true;
						CloseAllPositions("Daily limit reached");
					}
					return;
				}

				if (EnableTimeFilter && !IsWithinTradingHours())
				{
					return;
				}

				if (Time[0].Date != lastLevelUpdate.Date || priceLevels.Count == 0)
				{
					UpdatePriceLevelsFromChart();
					lastLevelUpdate = Time[0];
				}

				double currentPrice = Close[0];
				double previousPrice = lastCheckPrice > 0 ? lastCheckPrice : Close[Math.Max(0, CurrentBar - 1)];

				UpdateLevelCrossTracking(currentPrice, previousPrice);

				if (Position.MarketPosition != MarketPosition.Flat)
				{
					ManageTrailingExits();
				}

				if (Position.MarketPosition == MarketPosition.Flat)
				{
					CheckForBuySignals(currentPrice, previousPrice);
					CheckForSellSignals(currentPrice, previousPrice);
				}

				lastCheckPrice = currentPrice;
				lastCheckTime = Time[0];
			}
			catch (Exception ex)
			{
				Print(string.Format("{0}: ERROR in OnBarUpdate: {1}", Time[0], ex.Message));
				CloseAllPositions("OnBarUpdate exception");
			}
		}

		private void ManageTrailingExits()
		{
			try
			{
				double currentPrice = Close[0];
				double entryPrice = Position.AveragePrice;
				double profitTicks = 0;

				UpdateChartDrawings();

				if (Position.MarketPosition == MarketPosition.Long)
				{
					profitTicks = (currentPrice - entryPrice) / TickSize;

					if (!initialStopHit && currentPrice <= initialStopPrice)
					{
						ExitLong("Initial_Stop");
						initialStopHit = true;
						Print(string.Format("{0}: LONG initial stop hit at {1:F2}", Time[0], currentPrice));
						return;
					}

					if (NumberOfContracts == 2 && Position.Quantity >= 2 && !contract1Exited)
					{
						if (profitTicks >= Contract1ScalpTicks)
						{
							ExitLong(1, "C1_Scalp", "");
							contract1Exited = true;
							Print(string.Format("{0}: LONG C1 exited at {1:F2} ({2:F1} tick profit)",
								Time[0], currentPrice, profitTicks));
						}
					}

					if (Position.Quantity >= 1)
					{
						if (!contract2BreakevenSet && profitTicks >= Contract2BreakevenTicks)
						{
							contract2StopPrice = entryPrice;
							contract2BreakevenSet = true;
							Print(string.Format("{0}: LONG C2 breakeven set at {1:F2}", Time[0], contract2StopPrice));
						}

						if (contract2BreakevenSet)
						{
							double newStopPrice = currentPrice - (Contract2TrailTicks * TickSize);

							if (newStopPrice > contract2StopPrice)
							{
								contract2StopPrice = newStopPrice;
								Print(string.Format("{0}: LONG C2 trail updated to {1:F2} (Price: {2:F2}, Profit: {3:F1} ticks)",
									Time[0], contract2StopPrice, currentPrice, profitTicks));
							}

							if (currentPrice <= contract2StopPrice)
							{
								ExitLong("C2_Trail", "");
								Print(string.Format("{0}: LONG C2 trail stop hit at {1:F2}", Time[0], currentPrice));
							}
						}

						if (profitTicks >= Contract2TargetTicks)
						{
							ExitLong("C2_Target", "");
							Print(string.Format("{0}: LONG C2 target hit at {1:F2} ({2:F1} tick profit)",
								Time[0], currentPrice, profitTicks));
						}
					}
				}
				else if (Position.MarketPosition == MarketPosition.Short)
				{
					profitTicks = (entryPrice - currentPrice) / TickSize;

					if (!initialStopHit && currentPrice >= initialStopPrice)
					{
						ExitShort("Initial_Stop");
						initialStopHit = true;
						Print(string.Format("{0}: SHORT initial stop hit at {1:F2}", Time[0], currentPrice));
						return;
					}

					if (NumberOfContracts == 2 && Position.Quantity >= 2 && !contract1Exited)
					{
						if (profitTicks >= Contract1ScalpTicks)
						{
							ExitShort(1, "C1_Scalp", "");
							contract1Exited = true;
							Print(string.Format("{0}: SHORT C1 exited at {1:F2} ({2:F1} tick profit)",
								Time[0], currentPrice, profitTicks));
						}
					}

					if (Position.Quantity >= 1)
					{
						if (!contract2BreakevenSet && profitTicks >= Contract2BreakevenTicks)
						{
							contract2StopPrice = entryPrice;
							contract2BreakevenSet = true;
							Print(string.Format("{0}: SHORT C2 breakeven set at {1:F2}", Time[0], contract2StopPrice));
						}

						if (contract2BreakevenSet)
						{
							double newStopPrice = currentPrice + (Contract2TrailTicks * TickSize);

							if (contract2StopPrice == 0 || newStopPrice < contract2StopPrice)
							{
								contract2StopPrice = newStopPrice;
								Print(string.Format("{0}: SHORT C2 trail updated to {1:F2} (Price: {2:F2}, Profit: {3:F1} ticks)",
									Time[0], contract2StopPrice, currentPrice, profitTicks));
							}

							if (currentPrice >= contract2StopPrice)
							{
								ExitShort("C2_Trail", "");
								Print(string.Format("{0}: SHORT C2 trail stop hit at {1:F2}", Time[0], currentPrice));
							}
						}

						if (profitTicks >= Contract2TargetTicks)
						{
							ExitShort("C2_Target", "");
							Print(string.Format("{0}: SHORT C2 target hit at {1:F2} ({2:F1} tick profit)",
								Time[0], currentPrice, profitTicks));
						}
					}
				}
			}
			catch (Exception ex)
			{
				Print(string.Format("{0}: ERROR in ManageTrailingExits: {1}", Time[0], ex.Message));
			}
		}

		private void CheckForBuySignals(double currentPrice, double previousPrice)
		{
			if (Position.MarketPosition == MarketPosition.Long)
				return;

			double buyLevelPrice = 0;
			if (ShouldBuyAtLevel(currentPrice, previousPrice, out buyLevelPrice))
			{
				try
				{
					EnterLong(NumberOfContracts, "Buy_Support");

					initialStopPrice = currentPrice - (InitialStopTicks * TickSize);
					contract1Exited = false;
					contract2BreakevenSet = false;
					contract2StopPrice = 0;
					initialStopHit = false;

					if (EnableLevelCooldown && buyLevelPrice > 0)
					{
						levelLastTradeTime[buyLevelPrice] = Time[0];
					}

					Print(string.Format("{0}: LONG entry at {1:F2}", Time[0], currentPrice));
				}
				catch (Exception ex)
				{
					Print(string.Format("{0}: ERROR entering long: {1}", Time[0], ex.Message));
				}
			}
		}

		private void CheckForSellSignals(double currentPrice, double previousPrice)
		{
			if (Position.MarketPosition == MarketPosition.Short)
				return;

			double sellLevelPrice = 0;
			if (ShouldSellAtLevel(currentPrice, previousPrice, out sellLevelPrice))
			{
				try
				{
					EnterShort(NumberOfContracts, "Sell_Resistance");

					initialStopPrice = currentPrice + (InitialStopTicks * TickSize);
					contract1Exited = false;
					contract2BreakevenSet = false;
					contract2StopPrice = 0;
					initialStopHit = false;

					if (EnableLevelCooldown && sellLevelPrice > 0)
					{
						levelLastTradeTime[sellLevelPrice] = Time[0];
					}

					Print(string.Format("{0}: SHORT entry at {1:F2}", Time[0], currentPrice));
				}
				catch (Exception ex)
				{
					Print(string.Format("{0}: ERROR entering short: {1}", Time[0], ex.Message));
				}
			}
		}

		private void UpdatePriceLevelsFromChart()
		{
			priceLevels.Clear();

			if (ChartControl == null || ChartPanel == null)
			{
				Print(string.Format("{0}: ChartControl is null - strategy must be run on a chart", Time[0]));
				return;
			}

			try
			{
				if (DrawObjects != null && DrawObjects.Count > 0)
				{
					foreach (var drawObject in DrawObjects)
					{
						if (drawObject == null)
							continue;

						string typeName = drawObject.GetType().Name;
						if (typeName == "HorizontalLine")
						{
							try
							{
								var objType = drawObject.GetType();

								double priceLevel = 0;
								var startAnchorProp = objType.GetProperty("StartAnchor");
								if (startAnchorProp != null)
								{
									var startAnchor = startAnchorProp.GetValue(drawObject);
									if (startAnchor != null)
									{
										var priceProp = startAnchor.GetType().GetProperty("Price");
										if (priceProp != null)
										{
											priceLevel = (double)priceProp.GetValue(startAnchor);
										}
									}
								}

								string tag = string.Empty;
								var tagProp = objType.GetProperty("Tag");
								if (tagProp != null)
								{
									tag = tagProp.GetValue(drawObject)?.ToString() ?? string.Empty;
								}

								if (priceLevel > 0 && !string.IsNullOrEmpty(tag))
								{
									string description = tag;

									if (tag.Contains("|PTZDPHLine") || tag.Contains("|GOLDPTZDPHLine"))
									{
										description = tag.Split('|')[0].Trim();
									}

									if (description.StartsWith("LBL="))
									{
										description = description.Substring(4).Trim();
									}

									lock (priceLevels)
									{
										if (!priceLevels.ContainsKey(priceLevel))
										{
											priceLevels[priceLevel] = description;
										}
									}
								}
							}
							catch (Exception ex)
							{
								Print(string.Format("  Error extracting level: {0}", ex.Message));
							}
						}
					}
				}

				int levelCount = 0;
				lock (priceLevels)
				{
					levelCount = priceLevels.Count;
				}

				if (levelCount > 0)
				{
					Print(string.Format("{0}: Loaded {1} price levels", Time[0], levelCount));
				}
			}
			catch (Exception ex)
			{
				Print(string.Format("Error updating price levels: {0}", ex.Message));
			}
		}

		private void UpdateLevelCrossTracking(double currentPrice, double previousPrice)
		{
			lock (priceLevels)
			{
				foreach (var level in priceLevels)
				{
					double levelPrice = level.Key;
					string description = level.Value;

					if (!levelCrossTracker.ContainsKey(levelPrice))
					{
						levelCrossTracker[levelPrice] = new LevelCrossInfo
						{
							CrossedAbove = false,
							CrossedBelow = false,
							CrossTime = DateTime.MinValue,
							Description = description
						};
					}

					var crossInfo = levelCrossTracker[levelPrice];

					if (previousPrice <= levelPrice && currentPrice > levelPrice)
					{
						crossInfo.CrossedAbove = true;
						crossInfo.CrossedBelow = false;
						crossInfo.CrossTime = Time[0];
					}
					else if (previousPrice >= levelPrice && currentPrice < levelPrice)
					{
						crossInfo.CrossedBelow = true;
						crossInfo.CrossedAbove = false;
						crossInfo.CrossTime = Time[0];
					}
				}
			}
		}

		private bool ShouldBuyAtLevel(double currentPrice, double previousPrice, out double levelPrice)
		{
			levelPrice = 0;

			lock (priceLevels)
			{
				if (priceLevels.Count == 0)
					return false;

				double proximity = PriceProximityTicks * TickSize;

				foreach (var level in priceLevels)
				{
					double currentLevelPrice = level.Key;
					string description = level.Value.ToLower();

					if (IsLevelOnCooldown(currentLevelPrice))
					{
						continue;
					}

					if (UseLBLFilter)
					{
						bool hasLBL = description.Contains("lbl");
						if (RequireLBLInDescription && !hasLBL)
							continue;
						if (!RequireLBLInDescription && hasLBL)
							continue;
					}

					bool isBuyLevel = false;

					if (UseSupport && description.Contains(KeywordSupport.ToLower()))
						isBuyLevel = true;
					if (UsePivotBull && description.Contains(KeywordPivotBull.ToLower()))
						isBuyLevel = true;
					if (UseStrengthConfirmed && description.Contains(KeywordStrengthConfirmed.ToLower()))
						isBuyLevel = true;
					if (UseGLLevels && description.Contains(KeywordGL.ToLower()) && currentLevelPrice < currentPrice)
						isBuyLevel = true;

					if (isBuyLevel)
					{
						if (levelCrossTracker.ContainsKey(currentLevelPrice))
						{
							var crossInfo = levelCrossTracker[currentLevelPrice];
							if (crossInfo.CrossedBelow && previousPrice < currentLevelPrice && currentPrice >= currentLevelPrice)
							{
								crossInfo.CrossedBelow = false;
								levelPrice = currentLevelPrice;
								return true;
							}
						}

						if (TradeOnCrossover)
						{
							if (previousPrice < currentLevelPrice - proximity && currentPrice >= currentLevelPrice)
							{
								levelPrice = currentLevelPrice;
								return true;
							}
						}

						if (TradeOnTouch)
						{
							if (currentPrice >= currentLevelPrice - proximity && currentPrice <= currentLevelPrice + proximity)
							{
								levelPrice = currentLevelPrice;
								return true;
							}
						}
					}
				}

				return false;
			}
		}

		private bool ShouldSellAtLevel(double currentPrice, double previousPrice, out double levelPrice)
		{
			levelPrice = 0;

			lock (priceLevels)
			{
				if (priceLevels.Count == 0)
					return false;

				double proximity = PriceProximityTicks * TickSize;

				foreach (var level in priceLevels)
				{
					double currentLevelPrice = level.Key;
					string description = level.Value.ToLower();

					if (IsLevelOnCooldown(currentLevelPrice))
					{
						continue;
					}

					if (UseLBLFilter)
					{
						bool hasLBL = description.Contains("lbl");
						if (RequireLBLInDescription && !hasLBL)
							continue;
						if (!RequireLBLInDescription && hasLBL)
							continue;
					}

					bool isSellLevel = false;

					if (UseResistance && description.Contains(KeywordResistance.ToLower()))
						isSellLevel = true;
					if (UsePivotBear && description.Contains(KeywordPivotBear.ToLower()))
						isSellLevel = true;
					if (UseWeaknessConfirmed && description.Contains(KeywordWeaknessConfirmed.ToLower()))
						isSellLevel = true;
					if (UseGLLevels && description.Contains(KeywordGL.ToLower()) && currentLevelPrice > currentPrice)
						isSellLevel = true;

					if (isSellLevel)
					{
						if (levelCrossTracker.ContainsKey(currentLevelPrice))
						{
							var crossInfo = levelCrossTracker[currentLevelPrice];
							if (crossInfo.CrossedAbove && previousPrice > currentLevelPrice && currentPrice <= currentLevelPrice)
							{
								crossInfo.CrossedAbove = false;
								levelPrice = currentLevelPrice;
								return true;
							}
						}

						if (TradeOnCrossover)
						{
							if (previousPrice > currentLevelPrice + proximity && currentPrice <= currentLevelPrice)
							{
								levelPrice = currentLevelPrice;
								return true;
							}
						}

						if (TradeOnTouch)
						{
							if (currentPrice >= currentLevelPrice - proximity && currentPrice <= currentLevelPrice + proximity)
							{
								levelPrice = currentLevelPrice;
								return true;
							}
						}
					}
				}

				return false;
			}
		}

		private void ResetDailyTracking()
		{
			currentTradingDate = Time[0].Date;
			dailyPnL = 0;
			dailyLimitReached = false;
			Print(string.Format("{0}: New trading day started", Time[0]));
		}

		private void UpdateDailyPnL()
		{
			try
			{
				double unrealizedPnL = Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);
				double realizedPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
				dailyPnL = realizedPnL + unrealizedPnL;
			}
			catch (Exception ex)
			{
				Print(string.Format("{0}: ERROR updating daily P&L: {1}", Time[0], ex.Message));
			}
		}

		private bool CheckDailyLimits()
		{
			if (EnableDailyLossLimit && dailyPnL <= -DailyLossLimit)
				return true;
			if (EnableDailyTargetLimit && dailyPnL >= DailyTargetLimit)
				return true;
			return false;
		}

		private bool IsWithinTradingHours()
		{
			try
			{
				TimeZoneInfo estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
				DateTime estTime = TimeZoneInfo.ConvertTime(Time[0], estZone);

				int currentHour = estTime.Hour;
				int currentMinute = estTime.Minute;

				if (currentHour < TradingStartHour || (currentHour == TradingStartHour && currentMinute < TradingStartMinute))
					return false;
				if (currentHour > TradingEndHour || (currentHour == TradingEndHour && currentMinute >= TradingEndMinute))
					return false;

				return true;
			}
			catch (Exception ex)
			{
				Print(string.Format("{0}: ERROR checking trading hours: {1}", Time[0], ex.Message));
				return true;
			}
		}

		private bool IsLevelOnCooldown(double levelPrice)
		{
			if (!EnableLevelCooldown)
				return false;

			if (levelLastTradeTime.ContainsKey(levelPrice))
			{
				DateTime lastTradeTime = levelLastTradeTime[levelPrice];
				TimeSpan timeSinceLastTrade = Time[0] - lastTradeTime;

				if (timeSinceLastTrade.TotalMinutes < LevelCooldownMinutes)
					return true;
			}

			return false;
		}

		private void CloseAllPositions(string reason)
		{
			try
			{
				if (Position.MarketPosition != MarketPosition.Flat)
				{
					Print(string.Format("{0}: Closing all positions - {1}", Time[0], reason));

					if (Position.MarketPosition == MarketPosition.Long)
						ExitLong("Emergency");
					else if (Position.MarketPosition == MarketPosition.Short)
						ExitShort("Emergency");

					contract1Exited = false;
					contract2BreakevenSet = false;
					contract2StopPrice = 0;
					initialStopPrice = 0;
					initialStopHit = false;

					RemoveChartDrawings();
				}
			}
			catch (Exception ex)
			{
				Print(string.Format("{0}: ERROR closing positions: {1}", Time[0], ex.Message));
			}
		}

		private void UpdateChartDrawings()
		{
			if (ChartControl == null || Position.MarketPosition == MarketPosition.Flat)
				return;

			try
			{
				double entryPrice = Position.AveragePrice;
				int currentBar = CurrentBar;

				if (Position.MarketPosition == MarketPosition.Long)
				{
					if (initialStopPrice > 0 && !initialStopHit)
					{
						Draw.Line(this, initialStopTag, false, 10, initialStopPrice, 0, initialStopPrice, Brushes.Red, DashStyleHelper.Solid, 2);
					}

					if (NumberOfContracts == 2 && !contract1Exited)
					{
						double c1Target = entryPrice + (Contract1ScalpTicks * TickSize);
						Draw.Line(this, c1TargetTag, false, 10, c1Target, 0, c1Target, Brushes.LimeGreen, DashStyleHelper.Dash, 2);
					}

					if (Position.Quantity >= 1)
					{
						double c2Target = entryPrice + (Contract2TargetTicks * TickSize);
						Draw.Line(this, c2TargetTag, false, 10, c2Target, 0, c2Target, Brushes.Cyan, DashStyleHelper.Dash, 2);

						if (contract2StopPrice > 0)
						{
							Draw.Line(this, c2StopTag, false, 10, contract2StopPrice, 0, contract2StopPrice, Brushes.Orange, DashStyleHelper.Solid, 2);
						}
					}
				}
				else if (Position.MarketPosition == MarketPosition.Short)
				{
					if (initialStopPrice > 0 && !initialStopHit)
					{
						Draw.Line(this, initialStopTag, false, 10, initialStopPrice, 0, initialStopPrice, Brushes.Red, DashStyleHelper.Solid, 2);
					}

					if (NumberOfContracts == 2 && !contract1Exited)
					{
						double c1Target = entryPrice - (Contract1ScalpTicks * TickSize);
						Draw.Line(this, c1TargetTag, false, 10, c1Target, 0, c1Target, Brushes.LimeGreen, DashStyleHelper.Dash, 2);
					}

					if (Position.Quantity >= 1)
					{
						double c2Target = entryPrice - (Contract2TargetTicks * TickSize);
						Draw.Line(this, c2TargetTag, false, 10, c2Target, 0, c2Target, Brushes.Cyan, DashStyleHelper.Dash, 2);

						if (contract2StopPrice > 0)
						{
							Draw.Line(this, c2StopTag, false, 10, contract2StopPrice, 0, contract2StopPrice, Brushes.Orange, DashStyleHelper.Solid, 2);
						}
					}
				}
			}
			catch (Exception ex)
			{
				Print(string.Format("{0}: ERROR updating chart drawings: {1}", Time[0], ex.Message));
			}
		}

		private void RemoveChartDrawings()
		{
			try
			{
				RemoveDrawObject(initialStopTag);
				RemoveDrawObject(c1TargetTag);
				RemoveDrawObject(c2TargetTag);
				RemoveDrawObject(c2StopTag);
			}
			catch
			{
			}
		}

		#region Properties

		[NinjaScriptProperty]
		[Display(Name="Use Support Levels", Order=1, GroupName="1) Level Types")]
		public bool UseSupport { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Use Resistance Levels", Order=2, GroupName="1) Level Types")]
		public bool UseResistance { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Use Pivot Bull Levels", Order=3, GroupName="1) Level Types")]
		public bool UsePivotBull { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Use Pivot Bear Levels", Order=4, GroupName="1) Level Types")]
		public bool UsePivotBear { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Use Strength Confirmed", Order=5, GroupName="1) Level Types")]
		public bool UseStrengthConfirmed { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Use Weakness Confirmed", Order=6, GroupName="1) Level Types")]
		public bool UseWeaknessConfirmed { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Use GL Levels", Order=7, GroupName="1) Level Types")]
		public bool UseGLLevels { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name="Price Proximity (Ticks)", Order=1, GroupName="2) Entry Rules")]
		public int PriceProximityTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Trade on Crossover", Order=2, GroupName="2) Entry Rules")]
		public bool TradeOnCrossover { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Trade on Touch", Order=3, GroupName="2) Entry Rules")]
		public bool TradeOnTouch { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Use LBL Filter", Order=4, GroupName="2) Entry Rules")]
		public bool UseLBLFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Require LBL in Description", Order=5, GroupName="2) Entry Rules")]
		public bool RequireLBLInDescription { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2)]
		[Display(Name="Number of Contracts", Description="Trade 1 or 2 contracts", Order=1, GroupName="3) Exit Settings")]
		public int NumberOfContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Initial Stop Loss (Ticks)", Description="Initial stop loss for both contracts", Order=2, GroupName="3) Exit Settings")]
		public int InitialStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Contract 1: Scalp Target (Ticks)", Description="Quick exit profit target for first contract", Order=3, GroupName="3) Exit Settings")]
		public int Contract1ScalpTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Contract 1: Breakeven (Ticks)", Description="Move to breakeven after this profit", Order=4, GroupName="3) Exit Settings")]
		public int Contract1BreakevenTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Contract 2: Profit Target (Ticks)", Description="Final profit target for runner", Order=5, GroupName="3) Exit Settings")]
		public int Contract2TargetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Contract 2: Breakeven (Ticks)", Description="Move to breakeven after this profit", Order=6, GroupName="3) Exit Settings")]
		public int Contract2BreakevenTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Contract 2: Trail Distance (Ticks)", Description="Trail stop distance behind price", Order=7, GroupName="3) Exit Settings")]
		public int Contract2TrailTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Support Keyword", Order=1, GroupName="4) Keywords")]
		public string KeywordSupport { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Resistance Keyword", Order=2, GroupName="4) Keywords")]
		public string KeywordResistance { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Pivot Bull Keyword", Order=3, GroupName="4) Keywords")]
		public string KeywordPivotBull { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Pivot Bear Keyword", Order=4, GroupName="4) Keywords")]
		public string KeywordPivotBear { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Strength Confirmed Keyword", Order=5, GroupName="4) Keywords")]
		public string KeywordStrengthConfirmed { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Weakness Confirmed Keyword", Order=6, GroupName="4) Keywords")]
		public string KeywordWeaknessConfirmed { get; set; }

		[NinjaScriptProperty]
		[Display(Name="GL Keyword", Order=7, GroupName="4) Keywords")]
		public string KeywordGL { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Time Filter", Order=1, GroupName="5) Time Filter")]
		public bool EnableTimeFilter { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name="Trading Start Hour", Order=2, GroupName="5) Time Filter")]
		public int TradingStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name="Trading Start Minute", Order=3, GroupName="5) Time Filter")]
		public int TradingStartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name="Trading End Hour", Order=4, GroupName="5) Time Filter")]
		public int TradingEndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name="Trading End Minute", Order=5, GroupName="5) Time Filter")]
		public int TradingEndMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Daily Loss Limit", Order=1, GroupName="6) Daily Limits")]
		public bool EnableDailyLossLimit { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name="Daily Loss Limit ($)", Order=2, GroupName="6) Daily Limits")]
		public double DailyLossLimit { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Daily Target Limit", Order=3, GroupName="6) Daily Limits")]
		public bool EnableDailyTargetLimit { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name="Daily Target Limit ($)", Order=4, GroupName="6) Daily Limits")]
		public double DailyTargetLimit { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Enable Level Cooldown", Order=1, GroupName="7) Level Cooldown")]
		public bool EnableLevelCooldown { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1440)]
		[Display(Name="Cooldown Minutes", Order=2, GroupName="7) Level Cooldown")]
		public int LevelCooldownMinutes { get; set; }

		#endregion
	}
}
