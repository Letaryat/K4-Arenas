namespace K4Arenas
{
	using CounterStrikeSharp.API;
	using CounterStrikeSharp.API.Core;
	using CounterStrikeSharp.API.Core.Translations;
	using CounterStrikeSharp.API.Modules.Utils;
	using K4Arenas.Models;
    using Microsoft.Extensions.Logging;

    public sealed partial class Plugin : BasePlugin
	{
		private int lastRealPlayers = 0;
		public void Initialize_Events()
		{
			RegisterListener<Listeners.OnMapStart>((mapName) =>
			{
				Task.Run(PurgeDatabaseAsync);

				AddTimer(0.1f, () =>
				{
					Arenas ??= new Arenas(this);
					lastRealPlayers = 0;

					GameConfig?.Apply();
					CheckCommonProblems();

					gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules;

					foreach (CCSPlayerController player in Utilities.GetPlayers().Where(x => x?.IsValid == true && !x.IsHLTV && x.Connected == PlayerConnectedState.PlayerConnected && !x.IsBot))
					{
						if (Arenas.FindPlayer(player) == null)
							SetupPlayer(player);
					}
				});
			});

			RegisterListener<Listeners.OnMapEnd>(() =>
			{
				Arenas?.Clear();
				Arenas = null;

				WaitingArenaPlayers.Clear();
				IsBetweenRounds = false;


				ArenaFinderTest = null;
				gameRules = null;
				if (WarmupTimer != null)
				{
					WarmupTimer.Kill();
				}


			});

			RegisterEventHandler((EventRoundFreezeEnd @event, GameEventInfo info) =>
			{
				var players = Utilities.GetPlayers().Where(x => x?.IsValid == true && x.PlayerPawn?.IsValid == true && !x.IsBot && !x.IsHLTV);

				if (players.Any())
				{
					foreach (var player in players)
					{
						ArenaPlayer? arenaPlayer = Arenas?.FindPlayer(player);
						if (arenaPlayer != null)
						{
							AddTimer(3.0f, () =>
							{
								arenaPlayer.CenterMessage = string.Empty;
							});
						}
					}
				}
				return HookResult.Continue;
			});

			RegisterEventHandler((EventPlayerActivate @event, GameEventInfo info) =>
			{
				CCSPlayerController? playerController = @event.Userid;

				if (playerController is null || !playerController.IsValid)
					return HookResult.Continue;

				if (playerController.IsHLTV)
					return HookResult.Continue;

				if (Arenas?.FindPlayer(playerController) != null)
					return HookResult.Continue;

				SetupPlayer(playerController);

				if (gameRules?.WarmupPeriod == false)
				{
					if (playerController.IsBot)
						return HookResult.Continue;

					TerminateRoundIfPossible();
				}

				return HookResult.Continue;
			});

			RegisterEventHandler((EventPlayerDisconnect @event, GameEventInfo info) =>
			{
				CCSPlayerController? playerController = @event.Userid;
				if (playerController is null || !playerController.IsValid)
					return HookResult.Continue;

				WaitingArenaPlayers = new Queue<ArenaPlayer>(WaitingArenaPlayers.Where(p => p.Controller != playerController));
				Arenas?.ArenaList.ForEach(arena => arena.RemovePlayer(playerController));
				return HookResult.Continue;
			});

			RegisterEventHandler<EventPlayerBlind>((@event, info) =>
			{
				if (!Config.CompatibilitySettings.BlockFlashOfNotOpponent)
					return HookResult.Continue;

				ArenaPlayer? attacker = Arenas?.FindPlayer(@event.Attacker);
				ArenaPlayer? target = Arenas?.FindPlayer(@event.Userid);

				if (Arenas is null || attacker is null || target is null)
					return HookResult.Continue;

				if (!attacker.IsValid || !target.IsValid)
					return HookResult.Continue;

				int attackerArenaNumber = Arenas.ArenaList.FindIndex(a => a.Team1?.Any(p => p == attacker) == true || a.Team2?.Any(p => p == attacker) == true);
				int targetArenaNumber = Arenas.ArenaList.FindIndex(a => a.Team1?.Any(p => p == target) == true || a.Team2?.Any(p => p == target) == true);

				if (attackerArenaNumber != targetArenaNumber)
				{
					if (target.Controller.PlayerPawn.Value != null)
						target.Controller.PlayerPawn.Value.BlindUntilTime = Server.CurrentTime;
				}

				return HookResult.Continue;
			});

			RegisterEventHandler((EventPlayerHurt @event, GameEventInfo info) =>
			{
				if (!Config.CompatibilitySettings.BlockDamageOfNotOpponent)
					return HookResult.Continue;

				ArenaPlayer? attacker = Arenas?.FindPlayer(@event.Attacker);
				ArenaPlayer? target = Arenas?.FindPlayer(@event.Userid);

				if (Arenas is null || attacker is null || target is null)
					return HookResult.Continue;

				if (!attacker.IsValid || !target.IsValid)
					return HookResult.Continue;

				int attackerArenaNumber = Arenas.ArenaList.FindIndex(a => a.Team1?.Any(p => p == attacker) == true || a.Team2?.Any(p => p == attacker) == true);
				int targetArenaNumber = Arenas.ArenaList.FindIndex(a => a.Team1?.Any(p => p == target) == true || a.Team2?.Any(p => p == target) == true);

				if (attackerArenaNumber != targetArenaNumber)
				{
					if (target.Controller.PlayerPawn.Value != null)
					{
						target.Controller.PlayerPawn.Value.Health += @event.DmgHealth;
						target.Controller.PlayerPawn.Value.ArmorValue += @event.DmgArmor;
					}
				}

				return HookResult.Continue;
			}, HookMode.Pre);

			RegisterEventHandler((EventRoundPrestart @event, GameEventInfo info) =>
			{
				if (gameRules == null || gameRules.WarmupPeriod || Arenas == null)
					return HookResult.Continue;

				// === OPTYMALIZACJA 1: Użyj List zamiast Queue dla lepszej wydajności ===
				var arenaWinners = new List<ArenaPlayer>();
				var arenaLosers = new List<ArenaPlayer>();
				var challengesToRemove = new List<ChallengeModel>();

				// === OPTYMALIZACJA 2: Posortuj raz, użyj ToList() aby uniknąć re-sortowania ===
				var sortedArenas = Arenas.ArenaList
					.OrderBy(a => a.ArenaID < 0)
					.ThenBy(a => Math.Abs(a.ArenaID))
					.ToList();

				foreach (Arena arena in sortedArenas)
				{
					if (arena.ArenaID == -2)
					{
						// === OPTYMALIZACJA 3: Przechowuj arenaPlayers w jednej kolekcji ===
						var arenaPlayers = new List<ArenaPlayer>();
						if (arena.Team1 != null) arenaPlayers.AddRange(arena.Team1);
						if (arena.Team2 != null) arenaPlayers.AddRange(arena.Team2);

						if (arenaPlayers.Count == 0)
							continue;

						foreach (var player in arenaPlayers)
						{
							ChallengeModel? challenge = FindChallengeForPlayer(player.Controller);
							if (challenge is null)
							{
								arenaLosers.Add(player);
								continue;
							}

							ArenaResult result = arena.Result;

							// === OPTYMALIZACJA 4: Użyj HashSet dla O(1) lookup ===
							var winnersSet = result.Winners != null ? new HashSet<ArenaPlayer>(result.Winners) : null;

							if (winnersSet?.Contains(challenge.Player1) == true)
								MoveBackChallengePlayer(challenge.Player1, challenge.Player1Placement, arenaWinners);
							else
								MoveBackChallengePlayer(challenge.Player1, challenge.Player1Placement, arenaLosers);

							if (winnersSet?.Contains(challenge.Player2) == true)
								MoveBackChallengePlayer(challenge.Player2, challenge.Player2Placement, arenaWinners);
							else
								MoveBackChallengePlayer(challenge.Player2, challenge.Player2Placement, arenaLosers);

							challengesToRemove.Add(challenge);
						}
					}
					else
					{
						ArenaResult arenaResult = arena.Result;

						switch (arenaResult.ResultType)
						{
							case ArenaResultType.Win:
								if (arenaResult.Winners != null) arenaWinners.AddRange(arenaResult.Winners);
								if (arenaResult.Losers != null) arenaLosers.AddRange(arenaResult.Losers);
								break;
							case ArenaResultType.NoOpponent:
								if (arenaResult.Winners != null) arenaWinners.AddRange(arenaResult.Winners);
								break;
							case ArenaResultType.Tie:
								if (arena.Team1?.All(p => p.Controller.IsBot) == true &&
									arena.Team2?.All(p => p.Controller.IsBot) == true)
								{
									var (winners, losers) = Random.Shared.Next(2) == 0
										? (arena.Team1, arena.Team2)
										: (arena.Team2, arena.Team1);

									arenaWinners.AddRange(winners);
									arenaLosers.AddRange(losers);
								}
								else
								{
									if (arena.Team1 != null) arenaLosers.AddRange(arena.Team1);
									if (arena.Team2 != null) arenaLosers.AddRange(arena.Team2);
								}
								break;
						}
					}
				}

				// === OPTYMALIZACJA 5: Usuń po iteracji, nie podczas ===
				foreach (var challenge in challengesToRemove)
					Challenges.Remove(challenge);

				Challenges.RemoveAll(c => c.IsEnded || !c.IsAccepted);

				// === OPTYMALIZACJA 6: Zbuduj rankedPlayers bez nadmiernych kopii ===
				var rankedPlayers = new List<ArenaPlayer>();

				if (arenaWinners.Count > 1)
				{
					rankedPlayers.Add(arenaWinners[0]);
					rankedPlayers.Add(arenaWinners[1]);
					arenaWinners.RemoveRange(0, 2);
				}

				// Przeplataj zwycięzców i przegranych
				int winnerIdx = 0, loserIdx = 0;
				while (winnerIdx < arenaWinners.Count || loserIdx < arenaLosers.Count)
				{
					if (winnerIdx < arenaWinners.Count)
						rankedPlayers.Add(arenaWinners[winnerIdx++]);

					if (loserIdx < arenaLosers.Count)
						rankedPlayers.Add(arenaLosers[loserIdx++]);
				}

				// Dodaj oczekujących graczy
				rankedPlayers.AddRange(WaitingArenaPlayers);

				Arenas.Shuffle();

				// === OPTYMALIZACJA 7: Filtruj tylko raz ===
				var notAFKrankedPlayers = new List<ArenaPlayer>();
				var newWaitingPlayers = new List<ArenaPlayer>();

				foreach (ArenaPlayer player in rankedPlayers.Where(p => p.IsValid))
				{
					if (player.AFK)
					{
						player.Controller.PrintToChat($"{Localizer.ForPlayer(player.Controller, "k4.general.prefix")} {Localizer.ForPlayer(player.Controller, "k4.chat.afk_reminder", Config.CommandSettings.AFKCommands.FirstOrDefault("Missing"))}");

						player.ArenaTag = $"{Localizer["k4.general.afk"]} |";

						if (!Config.CompatibilitySettings.DisableClantags)
						{
							SetScoreTag(player.Controller, player.ArenaTag);
						}

						newWaitingPlayers.Add(player);
					}
					else
					{
						notAFKrankedPlayers.Add(player);
					}
				}

				bool anyTeamRoundTypes = RoundType.RoundTypes.Any(roundType => roundType.TeamSize > 1);

				// === OPTYMALIZACJA 8: Sortuj raz, nie twórz nowej Queue ===
				notAFKrankedPlayers.Sort((a, b) => a.Controller.IsBot.CompareTo(b.Controller.IsBot));

				// === OPTYMALIZACJA 9: Usuń invalid challenges przed pętlą ===
				Challenges.RemoveAll(c => !c.Player1.IsValid || !c.Player2.IsValid);

				int displayIndex = 1;
				int handledChallenges = 0;

				// === OPTYMALIZACJA 10: Użyj HashSet dla szybszego Except ===
				var usedPlayers = new HashSet<ArenaPlayer>();

				for (int arenaID = 0; arenaID < Arenas.Count; arenaID++)
				{
					if (Challenges.Count > handledChallenges)
					{
						ChallengeModel challenge = Challenges[handledChallenges];

						List<ArenaPlayer> team1 = [challenge.Player1];
						List<ArenaPlayer> team2 = [challenge.Player2];

						usedPlayers.Add(challenge.Player1);
						usedPlayers.Add(challenge.Player2);

						handledChallenges++;
						Arenas.ArenaList[arenaID].AddChallengePlayers(team1, team2);
						continue;
					}

					// Usuń użytych graczy
					notAFKrankedPlayers.RemoveAll(p => usedPlayers.Contains(p));
					usedPlayers.Clear();

					if (anyTeamRoundTypes && RoundType.RoundTypes.Where(roundType => roundType.TeamSize > 1).Any(roundType => Arenas.AddTeamsToArena(arenaID, displayIndex, roundType.TeamSize, new Queue<ArenaPlayer>(notAFKrankedPlayers), roundType)))
					{
						displayIndex++;
						continue;
					}

					if (notAFKrankedPlayers.Count >= 1)
					{
						ArenaPlayer player1 = notAFKrankedPlayers[0];
						notAFKrankedPlayers.RemoveAt(0);

						ArenaPlayer? player2 = null;
						if (notAFKrankedPlayers.Count > 0)
						{
							player2 = notAFKrankedPlayers[0];
							notAFKrankedPlayers.RemoveAt(0);
						}

						RoundType roundType = GetCommonRoundType(player1.RoundPreferences, player2?.RoundPreferences, false);

						Arenas.ArenaList[arenaID].AddPlayers([player1], player2 != null ? [player2] : null, roundType, displayIndex, (Arenas.Count - displayIndex) * 50);
						displayIndex++;
					}
					else
					{
						Arenas.ArenaList[arenaID].AddPlayers(null, null, null, displayIndex, (Arenas.Count - displayIndex) * 50);
						displayIndex++;
					}
				}

				// === OPTYMALIZACJA 11: Przepisz WaitingArenaPlayers raz ===
				WaitingArenaPlayers.Clear();

				foreach (var player in notAFKrankedPlayers)
				{
					player.ArenaTag = $"{Localizer["k4.general.waiting"]} |";

					if (!Config.CompatibilitySettings.DisableClantags)
					{
						SetScoreTag(player.Controller, player.ArenaTag);
					}

					if (player.PlayerIsSafe)
						player.Controller.ChangeTeam(CsTeam.Spectator);

					WaitingArenaPlayers.Enqueue(player);
				}

				foreach (var player in newWaitingPlayers)
				{
					WaitingArenaPlayers.Enqueue(player);
				}

				return HookResult.Continue;
			});

			RegisterEventHandler((EventRoundStart @event, GameEventInfo info) =>
			{
				Logger.LogInformation("=== RoundStart ===");
				if(Arenas != null)
                {
                    foreach(var arena in Arenas.ArenaList)
                    {
						Logger.LogInformation($"Arena {arena.ArenaID} | Team1: {arena.Team1!.Count()} Team2 {arena.Team2!.Count()}");
                    }
                }
				IsBetweenRounds = false;
				return HookResult.Continue;
			});

			RegisterEventHandler((EventRoundEnd @event, GameEventInfo info) =>
			{
				IsBetweenRounds = true;

				if (Arenas is null)
					return HookResult.Continue;

				foreach (Arena arena in Arenas.ArenaList)
					arena.OnRoundEnd();

				return HookResult.Continue;
			});

			RegisterEventHandler((EventPlayerSpawn @event, GameEventInfo info) =>
			{
				if (Arenas is null)
					return HookResult.Continue;

				CCSPlayerController? player = @event.Userid;
				if (player == null || !player.IsValid)
					return HookResult.Continue;

				// ✅ Znajdź TYLKO arenę tego gracza
				Arena? playerArena = null;
				foreach (Arena arena in Arenas.ArenaList)
				{
					bool inTeam1 = arena.Team1?.Any(p => p.Controller == player) == true;
					bool inTeam2 = arena.Team2?.Any(p => p.Controller == player) == true;

					if (inTeam1 || inTeam2)
					{
						playerArena = arena;
						break;
					}
				}

				// ✅ Setup tylko jeśli gracz jest w arenie
				if (playerArena != null)
					playerArena.SetupArenaPlayer(player);

				return HookResult.Continue;
			});

			RegisterEventHandler((EventRoundMvp @event, GameEventInfo info) =>
			{
				return HookResult.Handled;
			}, HookMode.Pre);

			RegisterEventHandler((EventPlayerDeath @event, GameEventInfo info) =>
			{
				AddTimer(1.0f, TerminateRoundIfPossible);
				return HookResult.Continue;
			});

			RegisterEventHandler((EventPlayerTeam @event, GameEventInfo info) =>
			{
				info.DontBroadcast = true;
				var player = @event.Userid;

				if (player is null || !player.IsValid)
					return HookResult.Continue;

				var oldTeam = (CsTeam)@event.Oldteam;
				var newTeam = (CsTeam)@event.Team;

				if (oldTeam == CsTeam.None || (oldTeam > CsTeam.Spectator && newTeam > CsTeam.Spectator))
					return HookResult.Continue;

				if (!player.IsBot)
					TerminateRoundIfPossible();

				ArenaPlayer? arenaPlayer = Arenas?.FindPlayer(player);

				if (arenaPlayer?.AFK == false && player.Team != CsTeam.Spectator && newTeam == CsTeam.Spectator)
				{
					arenaPlayer!.AFK = true;

					arenaPlayer.ArenaTag = $"{Localizer["k4.general.afk"]} |";

					if (!Config.CompatibilitySettings.DisableClantags)
					{
						SetScoreTag(player, arenaPlayer.ArenaTag);
						/*
						player.Clan = arenaPlayer.ArenaTag;
						Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
						*/
					}

					player!.ChangeTeam(CsTeam.Spectator);

					player.PrintToChat($" {Localizer.ForPlayer(player, "k4.general.prefix")} {string.Format(Localizer.ForPlayer(player, "k4.chat.afk_enabled"), Config.CommandSettings.AFKCommands.FirstOrDefault("Missing"))}");
					return HookResult.Stop;
				}
				else if (arenaPlayer?.AFK == true && player.Team == CsTeam.Spectator && newTeam > CsTeam.Spectator)
				{
					arenaPlayer!.AFK = false;

					arenaPlayer.ArenaTag = $"{Localizer["k4.general.waiting"]} |";

					if (!Config.CompatibilitySettings.DisableClantags)
					{
						SetScoreTag(arenaPlayer.Controller, arenaPlayer.ArenaTag);
						/*
						arenaPlayer.Controller.Clan = arenaPlayer.ArenaTag;
						Utilities.SetStateChanged(arenaPlayer.Controller, "CCSPlayerController", "m_szClan");
						*/
					}

					player.PrintToChat($" {Localizer.ForPlayer(player, "k4.general.prefix")} {Localizer.ForPlayer(player, "k4.chat.afk_disabled")}");
					return HookResult.Continue;
				}

				return HookResult.Changed;
			}, HookMode.Pre);

			RegisterEventHandler((EventSwitchTeam @event, GameEventInfo info) =>
			{
				info.DontBroadcast = true;
				return HookResult.Changed;
			}, HookMode.Pre);
		}
	}
}