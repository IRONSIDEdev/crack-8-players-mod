using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using TMPro;
using UnityEngine;

namespace Crack8P
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "crack.8players";
        public const string Name = "CRACK 8 Players";
        public const string Version = "1.1.0";

        /// <summary>Quantidade de quartos (camas, portas, guarda-roupas) que existem no mapa.</summary>
        public const int VanillaRooms = 4;

        internal static ManualLogSource Log;
        internal static ConfigEntry<int> MaxPlayersCfg;
        internal static ConfigEntry<string> CreditCfg;

        public static int MaxPlayers => Mathf.Clamp(MaxPlayersCfg?.Value ?? 8, 1, 16);

        private void Awake()
        {
            Log = Logger;
            MaxPlayersCfg = Config.Bind("General", "MaxPlayers", 8,
                new ConfigDescription("Número máximo de jogadores no lobby (o jogo original usa 4). " +
                                      "Todos no lobby precisam ter o mod instalado.",
                    new AcceptableValueRange<int>(1, 16)));

            CreditCfg = Config.Bind("General", "CreditText", "Mod Desenvolvido por Maçonicos",
                "Texto mostrado na tela inicial, abaixo da versão do jogo. Deixe vazio para esconder.");

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{Name} {Version} carregado: limite de {MaxPlayers} jogadores.");
        }

        /// <summary>Índice do quarto usado por um jogador: 4→0, 5→1, 6→2, 7→3.</summary>
        public static int RoomSlot(int playerIndex) => playerIndex < 0 ? playerIndex : playerIndex % VanillaRooms;
    }

    // ------------------------------------------------------------------
    // 1) Limite do lobby: NetworkManager.maxConnections (Mirror) = 8.
    //    Tudo no jogo (CreateLobby, SetLobbyMemberLimit, lista de amigos,
    //    spawn, evento de pizza) lê este campo.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(MyNetworkManager), "Awake")]
    internal static class MaxConnectionsPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MyNetworkManager __instance)
        {
            int before = __instance.maxConnections;
            __instance.maxConnections = Plugin.MaxPlayers;
            Plugin.Log.LogInfo($"maxConnections {before} -> {__instance.maxConnections}");
        }
    }

    [HarmonyPatch(typeof(MyNetworkManager), "Start")]
    internal static class MaxConnectionsStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MyNetworkManager __instance) => __instance.maxConnections = Plugin.MaxPlayers;
    }

    // ------------------------------------------------------------------
    // 2) Índices de jogador: o original procura um índice livre só de 0 a 3.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(GameManager), "GetAvailablePlayerIndex")]
    internal static class AvailableIndexPatch
    {
        private static readonly FieldInfo PlayersField = AccessTools.Field(typeof(GameManager), "players");

        [HarmonyPrefix]
        private static bool Prefix(GameManager __instance, ref int __result)
        {
            var used = new HashSet<int>();
            if (PlayersField?.GetValue(__instance) is IEnumerable list)
            {
                foreach (var p in list)
                {
                    if (p is PlayerController pc && pc != null)
                        used.Add(pc.index);
                }
            }

            int max = Mathf.Max(Plugin.MaxPlayers, NetworkManager.singleton != null ? NetworkManager.singleton.maxConnections : 0);
            for (int i = 0; i < max; i++)
            {
                if (!used.Contains(i)) { __result = i; return false; }
            }
            __result = -1;
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 3) Quartos: o mapa tem 4. Jogadores 5-8 dividem com 1-4.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(QuartersRoom), nameof(QuartersRoom.Get))]
    internal static class QuartersRoomGetPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref int playerIndex) => playerIndex = Plugin.RoomSlot(playerIndex);
    }

    [HarmonyPatch(typeof(Bed), nameof(Bed.GetForPlayer))]
    internal static class BedGetForPlayerPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref int playerIndex) => playerIndex = Plugin.RoomSlot(playerIndex);
    }

    /// <summary>
    /// Nas checagens "este jogador é o dono deste quarto/cama/guarda-roupa/porta?",
    /// troca a leitura de PlayerController.index por RoomSlot(index).
    /// </summary>
    [HarmonyPatch]
    internal static class OwnershipChecksPatch
    {
        private static readonly FieldInfo IndexField = AccessTools.Field(typeof(PlayerController), "index");
        private static readonly MethodInfo RoomSlotMethod = AccessTools.Method(typeof(Plugin), nameof(Plugin.RoomSlot));

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = new (Type type, string method)[]
            {
                (typeof(Bed), "CanInteract"),
                (typeof(Bed), "Interact"),
                (typeof(Wardrobe), "IsAssignedPlayer"),
                (typeof(Wardrobe), "ServerCycle"),
                (typeof(Wardrobe), "UserCode_CmdEquip__NetworkConnectionToClient"),
                (typeof(QuartersDoor), "ReaderHoldsAssignedPlayer"),
                (typeof(QuartersDoor), "HasUnauthorizedBadge"),
                (typeof(QuartersDoor), "ShowWaitForCrewTip"),
            };
            foreach (var (type, name) in targets)
            {
                var m = AccessTools.Method(type, name);
                if (m != null) yield return m;
                else Plugin.Log.LogWarning($"Método não encontrado: {type.Name}.{name} (o jogo atualizou?)");
            }
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int count = 0;
            foreach (var ins in instructions)
            {
                yield return ins;
                if (ins.LoadsField(IndexField))
                {
                    count++;
                    yield return new CodeInstruction(OpCodes.Call, RoomSlotMethod);
                }
            }
            Plugin.Log.LogDebug($"{original.DeclaringType?.Name}.{original.Name}: {count} checagem(ns) de dono ajustada(s)");
        }
    }

    // ------------------------------------------------------------------
    // 4) Spawn: quem divide o quarto nasce um pouco ao lado, não em cima.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(GameManager), "GetSpawnPoint")]
    internal static class SpawnOffsetPatch
    {
        [HarmonyPostfix]
        private static void Postfix(int playerIndex, ref ValueTuple<Vector3, Quaternion> __result)
        {
            if (playerIndex < Plugin.VanillaRooms) return;
            int layer = playerIndex / Plugin.VanillaRooms; // 1 para jogadores 5-8
            Vector3 side = __result.Item2 * Vector3.right;
            __result.Item1 += side * (0.9f * layer);
        }
    }

    // ------------------------------------------------------------------
    // 5) Placa com o nome do quarto mostra os dois moradores.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(QuartersRoom), "Update")]
    internal static class QuartersLabelPatch
    {
        private static readonly AccessTools.FieldRef<QuartersRoom, int> RoomIndex =
            AccessTools.FieldRefAccess<QuartersRoom, int>("_playerIndex");
        private static readonly AccessTools.FieldRef<QuartersRoom, TMP_Text> Label =
            AccessTools.FieldRefAccess<QuartersRoom, TMP_Text>("_nameLabel");

        [HarmonyPostfix]
        private static void Postfix(QuartersRoom __instance)
        {
            var label = Label(__instance);
            var gm = GameManager.instance;
            if (label == null || gm == null) return;

            var names = new List<string>();
            for (int i = RoomIndex(__instance); i < Plugin.MaxPlayers; i += Plugin.VanillaRooms)
            {
                var pc = gm.GetPlayerControllerFromIndex(i);
                if (pc != null && !string.IsNullOrEmpty(pc.steamName))
                    names.Add(pc.steamName.ToUpper());
            }
            if (names.Count > 1)
            {
                string text = string.Join(" / ", names);
                if (label.text != text) label.text = text;
            }
        }
    }

    // ------------------------------------------------------------------
    // 6) Cores por jogador: o jogo tem 4 e repete em ciclo. Adiciona mais 4.
    // ------------------------------------------------------------------
    internal static class ExtraColors
    {
        private static readonly Color[] Extra =
        {
            new Color(1.00f, 0.55f, 0.10f), // laranja
            new Color(0.15f, 0.85f, 0.95f), // ciano
            new Color(1.00f, 0.40f, 0.75f), // rosa
            new Color(0.60f, 0.95f, 0.20f), // verde-limão
        };

        public static void Extend(ref Color[] colors)
        {
            if (colors == null || colors.Length == 0 || colors.Length >= Plugin.MaxPlayers) return;
            var list = new List<Color>(colors);
            int k = 0;
            while (list.Count < Plugin.MaxPlayers)
                list.Add(Extra[k++ % Extra.Length]);
            colors = list.ToArray();
        }
    }

    [HarmonyPatch(typeof(PlayerBlips), "Rebuild")]
    internal static class BlipColorsPatch
    {
        private static readonly AccessTools.FieldRef<PlayerBlips, Color[]> Colors =
            AccessTools.FieldRefAccess<PlayerBlips, Color[]>("playerColors");

        [HarmonyPrefix]
        private static void Prefix(PlayerBlips __instance) => ExtraColors.Extend(ref Colors(__instance));
    }

    [HarmonyPatch]
    internal static class ShiftColorsPatch
    {
        private static readonly AccessTools.FieldRef<ShiftCompleteUI, Color[]> Colors =
            AccessTools.FieldRefAccess<ShiftCompleteUI, Color[]>("_playerColors");

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "BuildRows", "SetGraphLines" })
            {
                var m = AccessTools.Method(typeof(ShiftCompleteUI), name);
                if (m != null) yield return m;
            }
        }

        [HarmonyPrefix]
        private static void Prefix(ShiftCompleteUI __instance) => ExtraColors.Extend(ref Colors(__instance));
    }

    // ------------------------------------------------------------------
    // 7) Crédito do mod na tela inicial, junto da versão do jogo.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(VersionText), "Awake")]
    internal static class CreditPatch
    {
        [HarmonyPostfix]
        private static void Postfix(VersionText __instance)
        {
            var credit = Plugin.CreditCfg?.Value;
            var label = __instance.GetComponent<TMP_Text>();
            if (label == null) return;

            string extra = $"Mod {Plugin.MaxPlayers} jogadores v{Plugin.Version}";
            if (!string.IsNullOrWhiteSpace(credit)) extra += " - " + credit.Trim();

            label.text = label.text + "\n" + extra;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
        }
    }
}
