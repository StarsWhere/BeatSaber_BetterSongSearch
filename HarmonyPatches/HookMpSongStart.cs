using BetterSongSearch.UI;
using HarmonyLib;

namespace BetterSongSearch.HarmonyPatches {
	[HarmonyPatch(typeof(MultiplayerLevelScenesTransitionSetupData), nameof(MultiplayerLevelScenesTransitionSetupData.Init))]
	static class HookMpSongStart {
		static void Prefix() => BSSFlowCoordinator.Close(true);
	}
}
