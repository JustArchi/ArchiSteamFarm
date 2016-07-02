﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2016 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using HtmlAgilityPack;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ArchiSteamFarm {
	internal sealed class CardsFarmer {
		internal const byte MaxGamesPlayedConcurrently = 32; // This is limit introduced by Steam Network

		[JsonProperty]
		internal readonly ConcurrentDictionary<uint, float> GamesToFarm = new ConcurrentDictionary<uint, float>();

		[JsonProperty]
		internal readonly ConcurrentHashSet<uint> CurrentGamesFarming = new ConcurrentHashSet<uint>();

		private readonly ManualResetEventSlim FarmResetEvent = new ManualResetEventSlim(false);
		private readonly SemaphoreSlim FarmingSemaphore = new SemaphoreSlim(1);
		private readonly Bot Bot;
		private readonly Timer Timer;

		[JsonProperty]
		internal bool ManualMode { get; private set; }

		private bool KeepFarming, NowFarming;

		internal CardsFarmer(Bot bot) {
			if (bot == null) {
				throw new ArgumentNullException(nameof(bot));
			}

			Bot = bot;

			if ((Timer == null) && (Program.GlobalConfig.IdleFarmingPeriod > 0)) {
				Timer = new Timer(
					e => CheckGamesForFarming(),
					null,
					TimeSpan.FromHours(Program.GlobalConfig.IdleFarmingPeriod), // Delay
					TimeSpan.FromHours(Program.GlobalConfig.IdleFarmingPeriod) // Period
				);
			}
		}

		internal async Task SwitchToManualMode(bool manualMode) {
			if (ManualMode == manualMode) {
				return;
			}

			ManualMode = manualMode;

			if (ManualMode) {
				Logging.Log("Now running in Manual Farming mode", LogSeverity.Info, Bot.BotName);
				await StopFarming().ConfigureAwait(false);
			} else {
				Logging.Log("Now running in Automatic Farming mode", LogSeverity.Info, Bot.BotName);
				StartFarming().Forget();
			}
		}

		internal async Task StartFarming() {
			if (NowFarming || ManualMode || Bot.PlayingBlocked) {
				return;
			}

			await FarmingSemaphore.WaitAsync().ConfigureAwait(false);

			if (NowFarming || ManualMode || Bot.PlayingBlocked) {
				FarmingSemaphore.Release(); // We have nothing to do, don't forget to release semaphore
				return;
			}

			if (!await IsAnythingToFarm().ConfigureAwait(false)) {
				FarmingSemaphore.Release(); // We have nothing to do, don't forget to release semaphore
				Logging.Log("We don't have anything to farm on this account!", LogSeverity.Info, Bot.BotName);
				await Bot.OnFarmingFinished(false).ConfigureAwait(false);
				return;
			}

			Logging.Log("We have a total of " + GamesToFarm.Count + " games to farm on this account...", LogSeverity.Info, Bot.BotName);

			// This is the last moment for final check if we can farm
			if (Bot.PlayingBlocked) {
				Logging.Log("But account is currently occupied, so farming is stopped!", LogSeverity.Info, Bot.BotName);
				FarmingSemaphore.Release(); // We have nothing to do, don't forget to release semaphore
				return;
			}

			KeepFarming = NowFarming = true;
			FarmingSemaphore.Release(); // From this point we allow other calls to shut us down

			do {
				// Now the algorithm used for farming depends on whether account is restricted or not
				if (Bot.BotConfig.CardDropsRestricted) { // If we have restricted card drops, we use complex algorithm
					Logging.Log("Chosen farming algorithm: Complex", LogSeverity.Info, Bot.BotName);
					while (GamesToFarm.Count > 0) {
						HashSet<uint> gamesToFarmSolo = GetGamesToFarmSolo(GamesToFarm);
						if (gamesToFarmSolo.Count > 0) {
							while (gamesToFarmSolo.Count > 0) {
								uint appID = gamesToFarmSolo.First();
								if (await FarmSolo(appID).ConfigureAwait(false)) {
									gamesToFarmSolo.Remove(appID);
									gamesToFarmSolo.TrimExcess();
								} else {
									NowFarming = false;
									return;
								}
							}
						} else {
							if (FarmMultiple()) {
								Logging.Log("Done farming: " + string.Join(", ", GamesToFarm.Keys), LogSeverity.Info, Bot.BotName);
							} else {
								NowFarming = false;
								return;
							}
						}
					}
				} else { // If we have unrestricted card drops, we use simple algorithm
					Logging.Log("Chosen farming algorithm: Simple", LogSeverity.Info, Bot.BotName);
					while (GamesToFarm.Count > 0) {
						uint appID = GamesToFarm.Keys.FirstOrDefault();
						if (await FarmSolo(appID).ConfigureAwait(false)) {
							continue;
						}

						NowFarming = false;
						return;
					}
				}
			} while (await IsAnythingToFarm().ConfigureAwait(false));

			CurrentGamesFarming.ClearAndTrim();
			NowFarming = false;

			Logging.Log("Farming finished!", LogSeverity.Info, Bot.BotName);
			await Bot.OnFarmingFinished(true).ConfigureAwait(false);
		}

		internal async Task StopFarming() {
			if (!NowFarming) {
				return;
			}

			await FarmingSemaphore.WaitAsync().ConfigureAwait(false);

			if (!NowFarming) {
				FarmingSemaphore.Release();
				return;
			}

			Logging.Log("Sending signal to stop farming", LogSeverity.Info, Bot.BotName);
			KeepFarming = false;
			FarmResetEvent.Set();

			Logging.Log("Waiting for reaction...", LogSeverity.Info, Bot.BotName);
			for (byte i = 0; (i < 5) && NowFarming; i++) {
				await Task.Delay(1000).ConfigureAwait(false);
			}

			if (NowFarming) {
				Logging.Log("Timed out!", LogSeverity.Warning, Bot.BotName);
			}

			Logging.Log("Farming stopped!", LogSeverity.Info, Bot.BotName);
			Bot.OnFarmingStopped();
			FarmingSemaphore.Release();
		}

		internal void OnDisconnected() => StopFarming().Forget();

		internal void OnNewItemsNotification() {
			if (!NowFarming) {
				return;
			}

			FarmResetEvent.Set();
		}

		internal async Task OnNewGameAdded() {
			if (!NowFarming) {
				// If we're not farming yet, obviously it's worth it to make a check
				StartFarming().Forget();
				return;
			}

			if (Bot.BotConfig.CardDropsRestricted && (GamesToFarm.Count > 0) && (GamesToFarm.Values.Min() < 2)) {
				// If we have Complex algorithm and some games to boost, it's also worth to make a check
				// That's because we would check for new games after our current round anyway
				await StopFarming().ConfigureAwait(false);
				StartFarming().Forget();
			}
		}

		private static HashSet<uint> GetGamesToFarmSolo(ConcurrentDictionary<uint, float> gamesToFarm) {
			if (gamesToFarm == null) {
				Logging.LogNullError(nameof(gamesToFarm));
				return null;
			}

			HashSet<uint> result = new HashSet<uint>();
			foreach (KeyValuePair<uint, float> keyValue in gamesToFarm.Where(keyValue => keyValue.Value >= 2)) {
				result.Add(keyValue.Key);
			}

			return result;
		}

		private async Task<bool> IsAnythingToFarm() {
			Logging.Log("Checking badges...", LogSeverity.Info, Bot.BotName);

			// Find the number of badge pages
			Logging.Log("Checking first page...", LogSeverity.Info, Bot.BotName);
			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetBadgePage(1).ConfigureAwait(false);
			if (htmlDocument == null) {
				Logging.Log("Could not get badges information, will try again later!", LogSeverity.Warning, Bot.BotName);
				return false;
			}

			byte maxPages = 1;

			HtmlNode htmlNode = htmlDocument.DocumentNode.SelectSingleNode("(//a[@class='pagelink'])[last()]");
			if (htmlNode != null) {
				string lastPage = htmlNode.InnerText;
				if (string.IsNullOrEmpty(lastPage)) {
					Logging.LogNullError(nameof(lastPage), Bot.BotName);
					return false;
				}

				if (!byte.TryParse(lastPage, out maxPages) || (maxPages == 0)) {
					Logging.LogNullError(nameof(maxPages), Bot.BotName);
					return false;
				}
			}

			GamesToFarm.Clear();
			CheckPage(htmlDocument);

			if (maxPages == 1) {
				return GamesToFarm.Count > 0;
			}

			Logging.Log("Checking other pages...", LogSeverity.Info, Bot.BotName);

			List<Task> tasks = new List<Task>(maxPages - 1);
			for (byte page = 2; page <= maxPages; page++) {
				byte currentPage = page; // We need a copy of variable being passed when in for loops, as loop will proceed before task is launched
				tasks.Add(CheckPage(currentPage));
			}

			await Task.WhenAll(tasks).ConfigureAwait(false);
			return GamesToFarm.Count > 0;
		}

		private void CheckPage(HtmlDocument htmlDocument) {
			if (htmlDocument == null) {
				Logging.LogNullError(nameof(htmlDocument), Bot.BotName);
				return;
			}

			HtmlNodeCollection htmlNodes = htmlDocument.DocumentNode.SelectNodes("//div[@class='badge_title_stats']");
			if (htmlNodes == null) { // For example a page full of non-games badges
				return;
			}

			foreach (HtmlNode htmlNode in htmlNodes) {
				HtmlNode farmingNode = htmlNode.SelectSingleNode(".//a[@class='btn_green_white_innerfade btn_small_thin']");
				if (farmingNode == null) {
					continue; // This game is not needed for farming
				}

				string steamLink = farmingNode.GetAttributeValue("href", null);
				if (string.IsNullOrEmpty(steamLink)) {
					Logging.LogNullError(nameof(steamLink), Bot.BotName);
					return;
				}

				int index = steamLink.LastIndexOf('/');
				if (index < 0) {
					Logging.LogNullError(nameof(index), Bot.BotName);
					return;
				}

				index++;
				if (steamLink.Length <= index) {
					Logging.LogNullError(nameof(steamLink.Length), Bot.BotName);
					return;
				}

				steamLink = steamLink.Substring(index);

				uint appID;
				if (!uint.TryParse(steamLink, out appID) || (appID == 0)) {
					Logging.LogNullError(nameof(appID), Bot.BotName);
					return;
				}

				if (GlobalConfig.GlobalBlacklist.Contains(appID) || Program.GlobalConfig.Blacklist.Contains(appID)) {
					continue;
				}

				HtmlNode timeNode = htmlNode.SelectSingleNode(".//div[@class='badge_title_stats_playtime']");
				if (timeNode == null) {
					Logging.LogNullError(nameof(timeNode), Bot.BotName);
					return;
				}

				string hoursString = timeNode.InnerText;
				if (string.IsNullOrEmpty(hoursString)) {
					Logging.LogNullError(nameof(hoursString), Bot.BotName);
					return;
				}

				float hours = 0;

				Match match = Regex.Match(hoursString, @"[0-9\.,]+");
				if (match.Success) {
					if (!float.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out hours)) {
						Logging.LogNullError(nameof(hours), Bot.BotName);
						return;
					}
				}

				GamesToFarm[appID] = hours;
			}
		}

		private async Task CheckPage(byte page) {
			if (page == 0) {
				Logging.LogNullError(nameof(page), Bot.BotName);
				return;
			}

			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetBadgePage(page).ConfigureAwait(false);
			if (htmlDocument == null) {
				return;
			}

			CheckPage(htmlDocument);
		}

		private void CheckGamesForFarming() {
			if (NowFarming || ManualMode || !Bot.SteamClient.IsConnected) {
				return;
			}

			StartFarming().Forget();
		}

		private async Task<bool?> ShouldFarm(uint appID) {
			if (appID == 0) {
				Logging.LogNullError(nameof(appID), Bot.BotName);
				return false;
			}

			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetGameCardsPage(appID).ConfigureAwait(false);
			if (htmlDocument == null) {
				return null;
			}

			HtmlNode htmlNode = htmlDocument.DocumentNode.SelectSingleNode("//span[@class='progress_info_bold']");
			if (htmlNode == null) {
				Logging.LogNullError(nameof(htmlNode), Bot.BotName);
				return null;
			}

			string progress = htmlNode.InnerText;
			if (string.IsNullOrEmpty(progress)) {
				Logging.LogNullError(nameof(progress), Bot.BotName);
				return null;
			}

			byte cardsRemaining = 0;

			Match match = Regex.Match(progress, @"\d+");
			if (match.Success) {
				if (!byte.TryParse(match.Value, out cardsRemaining)) {
					Logging.LogNullError(nameof(cardsRemaining), Bot.BotName);
					return null;
				}
			}

			Logging.Log("Status for " + appID + ": " + cardsRemaining + " cards remaining", LogSeverity.Info, Bot.BotName);
			return cardsRemaining > 0;
		}

		private bool FarmMultiple() {
			if (GamesToFarm.Count == 0) {
				return true;
			}

			float maxHour = 0;
			foreach (KeyValuePair<uint, float> game in GamesToFarm) {
				CurrentGamesFarming.Add(game.Key);
				if (game.Value > maxHour) {
					maxHour = game.Value;
				}

				if (CurrentGamesFarming.Count >= MaxGamesPlayedConcurrently) {
					break;
				}
			}

			if (maxHour >= 2) {
				CurrentGamesFarming.ClearAndTrim();
				return true;
			}

			Logging.Log("Now farming: " + string.Join(", ", CurrentGamesFarming), LogSeverity.Info, Bot.BotName);

			bool result = FarmHours(maxHour, CurrentGamesFarming);
			CurrentGamesFarming.ClearAndTrim();
			return result;
		}

		private async Task<bool> FarmSolo(uint appID) {
			if (appID == 0) {
				Logging.LogNullError(nameof(appID), Bot.BotName);
				return true;
			}

			CurrentGamesFarming.Add(appID);

			Logging.Log("Now farming: " + appID, LogSeverity.Info, Bot.BotName);

			bool result = await Farm(appID).ConfigureAwait(false);
			CurrentGamesFarming.ClearAndTrim();

			if (!result) {
				return false;
			}

			float hours;
			if (!GamesToFarm.TryRemove(appID, out hours)) {
				return false;
			}

			TimeSpan timeSpan = TimeSpan.FromHours(hours);
			Logging.Log("Done farming: " + appID + " after " + timeSpan.ToString(@"hh\:mm") + " hours of playtime!", LogSeverity.Info, Bot.BotName);
			return true;
		}

		private async Task<bool> Farm(uint appID) {
			if (appID == 0) {
				Logging.LogNullError(nameof(appID), Bot.BotName);
				return false;
			}

			Bot.ArchiHandler.PlayGames(appID);
			DateTime endFarmingDate = DateTime.Now.AddHours(Program.GlobalConfig.MaxFarmingTime);

			bool success = true;
			bool? keepFarming = await ShouldFarm(appID).ConfigureAwait(false);

			while (keepFarming.GetValueOrDefault(true) && (DateTime.Now < endFarmingDate)) {
				Logging.Log("Still farming: " + appID, LogSeverity.Info, Bot.BotName);

				DateTime startFarmingPeriod = DateTime.Now;
				if (FarmResetEvent.Wait(60 * 1000 * Program.GlobalConfig.FarmingDelay)) {
					FarmResetEvent.Reset();
					success = KeepFarming;
				}

				// Don't forget to update our GamesToFarm hours
				GamesToFarm[appID] += (float) DateTime.Now.Subtract(startFarmingPeriod).TotalHours;

				if (!success) {
					break;
				}

				keepFarming = await ShouldFarm(appID).ConfigureAwait(false);
			}

			Logging.Log("Stopped farming: " + appID, LogSeverity.Info, Bot.BotName);
			return success;
		}

		private bool FarmHours(float maxHour, ConcurrentHashSet<uint> appIDs) {
			if ((maxHour < 0) || (appIDs == null) || (appIDs.Count == 0)) {
				Logging.LogNullError(nameof(maxHour) + " || " + nameof(appIDs) + " || " + nameof(appIDs.Count), Bot.BotName);
				return false;
			}

			Bot.ArchiHandler.PlayGames(appIDs);

			bool success = true;
			while (maxHour < 2) {
				Logging.Log("Still farming: " + string.Join(", ", appIDs), LogSeverity.Info, Bot.BotName);

				DateTime startFarmingPeriod = DateTime.Now;
				if (FarmResetEvent.Wait(60 * 1000 * Program.GlobalConfig.FarmingDelay)) {
					FarmResetEvent.Reset();
					success = KeepFarming;
				}

				// Don't forget to update our GamesToFarm hours
				float timePlayed = (float) DateTime.Now.Subtract(startFarmingPeriod).TotalHours;
				foreach (uint appID in appIDs) {
					GamesToFarm[appID] += timePlayed;
				}

				if (!success) {
					break;
				}

				maxHour += timePlayed;
			}

			Logging.Log("Stopped farming: " + string.Join(", ", appIDs), LogSeverity.Info, Bot.BotName);
			return success;
		}
	}
}
