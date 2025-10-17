					AddTimer(3, () => // ! Fixes issues with the 3 sec warmup countdown when warmuptime is 0
					{
						if (gameRules?.WarmupPeriod == true && ConVar.Find("mp_warmuptime")?.GetPrimitiveValue<float>() > 0.0f)
						{
							WarmupTimer = AddTimer(2.0f, () => // ! Populate warmup slots every 2 seconds
							{
								if (lastRealPlayers == 0)
								{
									lastRealPlayers = Utilities.GetPlayers().Count(x => x?.IsValid == true && !x.IsHLTV && x.Connected == PlayerConnectedState.PlayerConnected && !x.IsBot);
									return;
								}

								if (gameRules?.WarmupPeriod == true)
								{
									foreach (Arena arena in Arenas.ArenaList)
										arena.WarmupPopulate();
								}
								else
								{
									GameConfig?.Apply();
									WarmupTimer?.Kill();
								}
							}, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
						}
					});