namespace K4Arenas
{
	using System;
	using CounterStrikeSharp.API;
	using CounterStrikeSharp.API.Core;
	using CounterStrikeSharp.API.Modules.Commands;
	using CounterStrikeSharp.API.Modules.Utils;
	using K4Arenas.Models;
	using Microsoft.Extensions.Logging;

	public sealed partial class Plugin : BasePlugin
	{
		public void Initialize_Listeners()
		{
			RegisterListener<Listeners.OnTick>(OnTick);

			AddCommandListener("jointeam", ListenerJoinTeam);

			AddCommandListener("changelevel", ListenerChangeLevel, HookMode.Pre);
			AddCommandListener("map", ListenerChangeLevel, HookMode.Pre);
			AddCommandListener("host_workshop_map", ListenerChangeLevel, HookMode.Pre);
			AddCommandListener("ds_workshop_changelevel", ListenerChangeLevel, HookMode.Pre);
		}

		private void OnTick()
		{
			var players = Utilities.GetPlayers().Where(x => x?.IsValid == true && x.PlayerPawn?.IsValid == true && !x.IsBot && !x.IsHLTV);

			if (players.Any())
			{
				foreach (var player in players)
				{
					ArenaPlayer? arenaPlayer = Arenas?.FindPlayer(player);
					if (arenaPlayer != null && !arenaPlayer.AFK && !string.IsNullOrEmpty(arenaPlayer.CenterMessage))
					{
						player.PrintToCenterHtml(arenaPlayer.CenterMessage);
					}
				}
			}
		}

		public HookResult ListenerJoinTeam(CCSPlayerController? player, CommandInfo info)
		{
			if (player?.IsValid == true && player.PlayerPawn?.IsValid == true)
			{
				ArenaPlayer? arenaPlayer = Arenas?.FindPlayer(player);
				if (arenaPlayer != null)
					arenaPlayer.PlayerIsSafe = true;

				if (player.Team != CsTeam.None)
				{
					if (arenaPlayer?.AFK == false && player.Team != CsTeam.Spectator && info.ArgByIndex(1) == "1")
					{
						return HookResult.Continue;
					}
					else if (arenaPlayer?.AFK == true && player.Team == CsTeam.Spectator && (info.ArgByIndex(1) == "2" || info.ArgByIndex(1) == "3"))
					{
						return HookResult.Continue;
					}

					player.ExecuteClientCommand("play sounds/ui/weapon_cant_buy.vsnd_c");
					return HookResult.Stop;
				}
			}

			return HookResult.Continue;
		}

		private HookResult ListenerChangeLevel(CCSPlayerController? player, CommandInfo commandInfo)
		{
			Logger.LogInformation("ListenerChangeLevel - Clearing cache: spawning Lists");
			ArenaFinderTest?.ctSpawns.Clear();
			ArenaFinderTest?.tSpawns.Clear();
			ArenaFinderTest?.teleportDestinations.Clear();
			ArenaFinderTest = null;


			Arenas?.ArenaList.Clear();

			gameRules = null;

			Arenas?.Clear();
			Arenas = null;

			WaitingArenaPlayers.Clear();
			IsBetweenRounds = false;

			return HookResult.Continue;
		}

	}
}