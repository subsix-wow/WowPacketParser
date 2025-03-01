﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WowPacketParser.Enums;
using WowPacketParser.Misc;
using WowPacketParser.Store;
using WowPacketParser.Store.Objects;

namespace WowPacketParser.SQL.Builders
{
    [BuilderClass]
    public static class Spawns
    {
        private static bool GetTransportMap(WoWObject @object, out int mapId)
        {
            mapId = -1;

            if (@object.Movement.Transport == null)
                return false;

            WoWObject transport;
            if (!Storage.Objects.TryGetValue(@object.Movement.Transport.Guid, out transport))
                return false;

            if (transport.Type == ObjectType.Player || transport.Type == ObjectType.ActivePlayer)
                return false;

            if (SQLConnector.Enabled)
            {
                var transportTemplates = SQLDatabase.Get(new RowList<GameObjectTemplate> { new GameObjectTemplate { Entry = (uint)transport.ObjectData.EntryID } });
                if (transportTemplates.Count == 0)
                    return false;

                mapId = transportTemplates.First().Data.Data[6].GetValueOrDefault();
            }

            return true;
        }

        [BuilderMethod(Units = true)]
        public static string Creature(Dictionary<WowGuid, Unit> units)
        {
            if (units.Count == 0)
                return string.Empty;

            if (!Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.creature))
                return string.Empty;

            uint count = 0;
            CreatureAddon addonDefault = null;
            if (Settings.DBEnabled && Settings.SkipRowsWithFallbackValues)
                addonDefault = SQLUtil.GetDefaultObject<CreatureAddon>();
            var dbFields = SQLUtil.GetDBFields<CreatureAddon>(false);
            var rows = new RowList<Creature>();
            var addonRows = new RowList<CreatureAddon>();

            foreach (var unit in units)
            {
                Row<Creature> row = new Row<Creature>();
                bool badTransport = false;

                Unit creature = unit.Value;

                if (Settings.AreaFilters.Length > 0)
                    if (!(creature.Area.ToString(CultureInfo.InvariantCulture).MatchesFilters(Settings.AreaFilters)))
                        continue;

                if (Settings.MapFilters.Length > 0)
                    if (!(creature.Map.ToString(CultureInfo.InvariantCulture).MatchesFilters(Settings.MapFilters)))
                        continue;

                uint entry = (uint)creature.ObjectData.EntryID;
                if (entry == 0)
                    continue;   // broken entry, nothing to spawn

                uint movementType = 0;
                int wanderDistance = 0;
                row.Data.AreaID = 0;
                row.Data.ZoneID = 0;

                if (creature.Movement.HasWpsOrRandMov)
                {
                    movementType = 1;
                    wanderDistance = 10;
                }

                row.Data.GUID = "@CGUID+" + count;

                row.Data.ID = entry;
                if (!creature.IsOnTransport())
                    row.Data.Map = creature.Map;
                else
                {
                    int mapId;
                    badTransport = !GetTransportMap(creature, out mapId);
                    if (mapId != -1)
                        row.Data.Map = (uint)mapId;
                }

                if (creature.Area != -1)
                    row.Data.AreaID = (uint)creature.Area;

                if (creature.Zone != -1)
                    row.Data.ZoneID = (uint)creature.Zone;

                row.Data.SpawnMask = (uint)creature.GetDefaultSpawnMask();

                if (ClientVersion.AddedInVersion(ClientVersionBuild.V7_0_3_22248))
                {
                    string data = string.Join(",", creature.GetDefaultSpawnDifficulties());
                    if (string.IsNullOrEmpty(data))
                        data = "0";

                    row.Data.spawnDifficulties = data;
                }

                row.Data.PhaseMask = creature.PhaseMask;

                if (ClientVersion.AddedInVersion(ClientVersionBuild.V4_3_4_15595) && creature.Phases != null)
                {
                    string data = string.Join(" - ", creature.Phases);
                    if (string.IsNullOrEmpty(data) || Settings.ForcePhaseZero)
                        data = "0";

                    row.Data.PhaseID = data;
                }

                if (SQLDatabase.CreatureEquipments.TryGetValue(entry, out var equipList))
                {
                    var equip = equipList.FirstOrDefault(x => x.EquipEqual(creature.UnitData.VirtualItems));
                    if (equip != null) // in case creature_equip_template parsing is disabled this is null for new equips
                        row.Data.EquipmentID = (int)equip.ID;
                }

                if (!creature.IsOnTransport())
                {
                    row.Data.PositionX = creature.Movement.Position.X;
                    row.Data.PositionY = creature.Movement.Position.Y;
                    row.Data.PositionZ = creature.Movement.Position.Z;
                    row.Data.Orientation = creature.Movement.Orientation;
                }
                else
                {
                    row.Data.PositionX = creature.Movement.Transport.Offset.X;
                    row.Data.PositionY = creature.Movement.Transport.Offset.Y;
                    row.Data.PositionZ = creature.Movement.Transport.Offset.Z;
                    row.Data.Orientation = creature.Movement.Transport.Offset.O;
                }

                row.Data.SpawnTimeSecs = creature.GetDefaultSpawnTime(creature.DifficultyID);
                row.Data.WanderDistance = wanderDistance;
                row.Data.MovementType = movementType;

                // set some defaults
                row.Data.PhaseGroup = 0;
                row.Data.ModelID = 0;
                row.Data.CurrentWaypoint = 0;
                row.Data.CurHealth = (uint)creature.UnitData.MaxHealth;
                row.Data.CurMana = (uint)creature.UnitData.MaxPower[0];
                row.Data.NpcFlag = 0;
                row.Data.UnitFlag = 0;
                row.Data.DynamicFlag = 0;

                row.Comment = StoreGetters.GetName(StoreNameType.Unit, (int)unit.Key.GetEntry(), false);
                row.Comment += " (Area: " + StoreGetters.GetName(StoreNameType.Area, creature.Area, false) + " - ";
                row.Comment += "Difficulty: " + StoreGetters.GetName(StoreNameType.Difficulty, (int)creature.DifficultyID, false) + ")";

                string auras = string.Empty;
                string commentAuras = string.Empty;
                if (creature.Auras != null && creature.Auras.Count != 0)
                {
                    foreach (Aura aura in creature.Auras)
                    {
                        if (aura == null)
                            continue;

                        // usually "template auras" do not have caster
                        if (ClientVersion.AddedInVersion(ClientType.MistsOfPandaria) ? !aura.AuraFlags.HasAnyFlag(AuraFlagMoP.NoCaster) : !aura.AuraFlags.HasAnyFlag(AuraFlag.NotCaster))
                            continue;

                        auras += aura.SpellId + " ";
                        commentAuras += aura.SpellId + " - " + StoreGetters.GetName(StoreNameType.Spell, (int)aura.SpellId, false) + ", ";
                    }

                    auras = auras.TrimEnd(' ');
                    commentAuras = commentAuras.TrimEnd(',', ' ');

                    row.Comment += " (Auras: " + commentAuras + ")";
                }

                var addonRow = new Row<CreatureAddon>();
                if (Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.creature_addon))
                {
                    addonRow.Data.PathID = 0;
                    addonRow.Data.Mount = (uint)creature.UnitData.MountDisplayID;
                    addonRow.Data.Bytes1 = creature.Bytes1;
                    addonRow.Data.Bytes2 = creature.Bytes2;
                    addonRow.Data.Emote = 0;
                    addonRow.Data.Auras = auras;
                    addonRow.Data.AIAnimKit = creature.AIAnimKit.GetValueOrDefault(0);
                    addonRow.Data.MovementAnimKit = creature.MovementAnimKit.GetValueOrDefault(0);
                    addonRow.Data.MeleeAnimKit = creature.MeleeAnimKit.GetValueOrDefault(0);
                    addonRow.Data.VisibilityDistanceType = creature.VisibilityDistanceType;

                    if (addonDefault == null || !SQLUtil.AreDBFieldsEqual(addonDefault, addonRow.Data, dbFields))
                    {
                        addonRow.Data.GUID = $"@CGUID+{count}";
                        addonRow.Comment += StoreGetters.GetName(StoreNameType.Unit, (int)unit.Key.GetEntry(), false);
                        if (!string.IsNullOrWhiteSpace(auras))
                            addonRow.Comment += $" - {commentAuras}";
                        addonRows.Add(addonRow);
                    }
                }

                if (creature.IsTemporarySpawn() && !Settings.SaveTempSpawns)
                {
                    row.CommentOut = true;
                    row.Comment += " - !!! might be temporary spawn !!!";
                    if (Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.creature_addon))
                    {
                        addonRow.CommentOut = true;
                        addonRow.Comment += " - !!! might be temporary spawn !!!";
                    }
                }
                else if (creature.IsOnTransport() && badTransport)
                {
                    row.CommentOut = true;
                    row.Comment += " - !!! on transport - transport template not found !!!";
                    if (Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.creature_addon))
                    {
                        addonRow.CommentOut = true;
                        addonRow.Comment += " - !!! on transport - transport template not found !!!";
                    }
                }
                ++count;

                if (creature.Movement.HasWpsOrRandMov)
                    row.Comment += " (possible waypoints or random movement)";

                rows.Add(row);
            }

            if (count == 0)
                return string.Empty;

            StringBuilder result = new StringBuilder();
            // delete query for GUIDs
            var delete = new SQLDelete<Creature>(Tuple.Create("@CGUID+0", "@CGUID+" + --count));
            result.Append(delete.Build());
            var sql = new SQLInsert<Creature>(rows, false);
            result.Append(sql.Build());

            if (Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.creature_addon))
            {
                var addonDelete = new SQLDelete<CreatureAddon>(Tuple.Create("@CGUID+0", "@CGUID+" + count));
                result.Append(addonDelete.Build());
                var addonSql = new SQLInsert<CreatureAddon>(addonRows, false);
                result.Append(addonSql.Build());
            }

            return result.ToString();
        }

        [BuilderMethod(Gameobjects = true)]
        public static string GameObject(Dictionary<WowGuid, GameObject> gameObjects)
        {
            if (gameObjects.Count == 0)
                return string.Empty;

            if (!Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.gameobject))
                return string.Empty;

            uint count = 0;
            var rows = new RowList<GameObjectModel>();
            var addonRows = new RowList<GameObjectAddon>();
            foreach (var gameobject in gameObjects)
            {
                Row<GameObjectModel> row = new Row<GameObjectModel>();

                GameObject go = gameobject.Value;

                if (Settings.AreaFilters.Length > 0)
                    if (!(go.Area.ToString(CultureInfo.InvariantCulture).MatchesFilters(Settings.AreaFilters)))
                        continue;

                if (Settings.MapFilters.Length > 0)
                    if (!(go.Map.ToString(CultureInfo.InvariantCulture).MatchesFilters(Settings.MapFilters)))
                        continue;

                uint entry = (uint)go.ObjectData.EntryID;
                if (entry == 0)
                    continue;   // broken entry, nothing to spawn

                bool badTransport = false;

                row.Data.GUID = "@OGUID+" + count;

                row.Data.ID = entry;
                if (!go.IsOnTransport())
                    row.Data.Map = go.Map;
                else
                {
                    int mapId;
                    badTransport = !GetTransportMap(go, out mapId);
                    if (mapId != -1)
                        row.Data.Map = (uint)mapId;
                }

                row.Data.ZoneID = 0;
                row.Data.AreaID = 0;

                if (go.Area != -1)
                    row.Data.AreaID = (uint)go.Area;

                if (go.Zone != -1)
                    row.Data.ZoneID = (uint)go.Zone;

                row.Data.SpawnMask = (uint)go.GetDefaultSpawnMask();


                if (ClientVersion.AddedInVersion(ClientVersionBuild.V7_0_3_22248))
                {
                    string data = string.Join(",", go.GetDefaultSpawnDifficulties());
                    if (string.IsNullOrEmpty(data))
                        data = "0";

                    row.Data.spawnDifficulties = data;
                }

                row.Data.PhaseMask = go.PhaseMask;

                if (ClientVersion.AddedInVersion(ClientVersionBuild.V4_3_4_15595) && go.Phases != null)
                {
                    string data = string.Join(" - ", go.Phases);
                    if (string.IsNullOrEmpty(data) || Settings.ForcePhaseZero)
                        data = "0";

                    row.Data.PhaseID = data;
                }

                if (!go.IsOnTransport())
                {
                    row.Data.PositionX = go.Movement.Position.X;
                    row.Data.PositionY = go.Movement.Position.Y;
                    row.Data.PositionZ = go.Movement.Position.Z;
                    row.Data.Orientation = go.Movement.Orientation;
                }
                else
                {
                    row.Data.PositionX = go.Movement.Transport.Offset.X;
                    row.Data.PositionY = go.Movement.Transport.Offset.Y;
                    row.Data.PositionZ = go.Movement.Transport.Offset.Z;
                    row.Data.Orientation = go.Movement.Transport.Offset.O;
                }

                var rotation = go.GetStaticRotation();
                row.Data.Rotation = new float?[] { rotation.X, rotation.Y, rotation.Z, rotation.W };

                bool add = false;
                var addonRow = new Row<GameObjectAddon>();
                if (Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.gameobject_addon))
                {
                    addonRow.Data.GUID = "@OGUID+" + count;

                    var parentRotation = go.GetParentRotation();

                    if (parentRotation != null)
                    {
                        addonRow.Data.parentRot0 = parentRotation.Value.X;
                        addonRow.Data.parentRot1 = parentRotation.Value.Y;
                        addonRow.Data.parentRot2 = parentRotation.Value.Z;
                        addonRow.Data.parentRot3 = parentRotation.Value.W;

                        if (addonRow.Data.parentRot0 != 0.0f ||
                            addonRow.Data.parentRot1 != 0.0f ||
                            addonRow.Data.parentRot2 != 0.0f ||
                            addonRow.Data.parentRot3 != 1.0f)
                            add = true;
                    }

                    addonRow.Data.WorldEffectID = go.WorldEffectID.GetValueOrDefault(0);
                    addonRow.Data.AIAnimKitID = go.AIAnimKitID.GetValueOrDefault(0);

                    if (go.WorldEffectID != null || go.AIAnimKitID != null)
                        add = true;

                    addonRow.Comment += StoreGetters.GetName(StoreNameType.GameObject, (int)gameobject.Key.GetEntry(), false);

                    if (add)
                        addonRows.Add(addonRow);
                }

                row.Data.SpawnTimeSecs = go.GetDefaultSpawnTime(go.DifficultyID);
                row.Data.AnimProgress = go.GameObjectData.PercentHealth;
                row.Data.State = (uint)go.GameObjectData.State;

                // set some defaults
                row.Data.PhaseGroup = 0;

                row.Comment = StoreGetters.GetName(StoreNameType.GameObject, (int)gameobject.Key.GetEntry(), false);
                row.Comment += " (Area: " + StoreGetters.GetName(StoreNameType.Area, go.Area, false) + " - ";
                row.Comment += "Difficulty: " + StoreGetters.GetName(StoreNameType.Difficulty, (int)go.DifficultyID, false) + ")";

                if (go.IsTemporarySpawn() && !Settings.SaveTempSpawns)
                {
                    row.CommentOut = true;
                    row.Comment += " - !!! might be temporary spawn !!!";
                    if (Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.gameobject_addon))
                    {
                        addonRow.CommentOut = true;
                        addonRow.Comment += " - !!! might be temporary spawn !!!";
                    }
                }
                else if (go.IsTransport())
                {
                    row.CommentOut = true;
                    row.Comment += " - !!! transport !!!";
                    if (Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.gameobject_addon))
                    {
                        addonRow.CommentOut = true;
                        addonRow.Comment += " - !!! transport !!!";
                    }
                }
                else if (go.IsOnTransport() && badTransport)
                {
                    row.CommentOut = true;
                    row.Comment += " - !!! on transport - transport template not found !!!";
                    if (Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.gameobject_addon))
                    {
                        addonRow.CommentOut = true;
                        addonRow.Comment += " - !!! on transport - transport template not found !!!";
                    }
                }
                else
                    ++count;

                rows.Add(row);
            }

            if (count == 0)
                return String.Empty;

            StringBuilder result = new StringBuilder();
            // delete query for GUIDs
            var delete = new SQLDelete<GameObjectModel>(Tuple.Create("@OGUID+0", "@OGUID+" + --count));
            result.Append(delete.Build());

            var sql = new SQLInsert<GameObjectModel>(rows, false);
            result.Append(sql.Build());

            if (Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.gameobject_addon))
            {
                var addonDelete = new SQLDelete<GameObjectAddon>(Tuple.Create("@OGUID+0", "@OGUID+" + count));
                result.Append(addonDelete.Build());
                var addonSql = new SQLInsert<GameObjectAddon>(addonRows, false);
                result.Append(addonSql.Build());
            }

            return result.ToString();
        }
    }
}
