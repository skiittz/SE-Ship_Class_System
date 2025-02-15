﻿using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace ShipClassSystem
{
    public class CubeGridLogic
    {
        public readonly Dictionary<BlockLimit, List<KeyValuePair<IMyCubeBlock, double>>> BlocksPerLimit = new Dictionary<BlockLimit, List<KeyValuePair<IMyCubeBlock, double>>>();
        public readonly HashSet<MyCubeBlock> Blocks = new HashSet<MyCubeBlock>();

        private static Dictionary<long, CubeGridLogic> CubeGridLogics => ModSessionManager.Instance.CubeGridLogics;

        private long _gridClassId;
        public IMyCubeGrid Grid;

        public IMyFaction OwningFaction => Grid.GetOwningFaction();

        public long MajorityOwningPlayerId => GetMajorityOwner();

        public long GridClassId
        {
            get { return _gridClassId; }
            set
            {
                var withinFactionLimit = GridsPerFactionClassManager.WillGridBeWithinFactionLimits(this, value);
                var withinPlayerLimit = GridsPerPlayerClassManager.WillGridBeWithinPlayerLimits(this, value);
                Utils.Log($"Within Faction limit: { withinFactionLimit } | Within Player limit: { withinPlayerLimit }");
                if (!withinFactionLimit)
                {
                    Utils.ShowNotification("Grid is not within allowed limit assigned to the faction!", Grid);
                    return;
                }
                if (!withinPlayerLimit)
                {
                    Utils.ShowNotification("Grid is not within allowed limit assigned to the player!", Grid);
                    return;
                }

                var gridClass = ModSessionManager.Config.GetGridClassById(value);
                var maxBlocks = gridClass.MaxBlocks;
                var maxPCU = gridClass.MaxPCU;
                var maxMass = gridClass.MaxMass;

                List<IMyCubeGrid> subgrids;
                var main = Utils.GetMainCubeGrid(Grid, out subgrids);
                var concreteGrid = main as MyCubeGrid;
                if (concreteGrid == null) return;
                var actualBlocks = concreteGrid.BlocksCount;
                var actualPCU = concreteGrid.BlocksPCU;
                var actualMass = concreteGrid.Mass;

                if (gridClass.LargeGridStatic && gridClass.LargeGridMobile == false && main.IsStatic == false)
                {
                    Utils.ShowNotification($"Can not set grid to class {gridClass.Name}, grid is supposed to be static!", Grid);
                    return;
                }

                foreach (var concreteSubgrid in subgrids.OfType<MyCubeGrid>())
                {
                    actualBlocks += concreteSubgrid.BlocksCount;
                    actualPCU += concreteSubgrid.BlocksPCU;
                    actualMass += concreteSubgrid.Mass;
                }

                if (maxBlocks > 1 && actualBlocks > maxBlocks)
                {
                    Utils.ShowNotification($"Can not set grid to class {gridClass.Name}, grid is {actualBlocks - maxBlocks} blocks over the limit!", Grid);
                    return;
                }
                if (maxPCU > 1 && actualPCU > maxPCU)
                {
                    Utils.ShowNotification($"Can not set grid to class {gridClass.Name}, grid is {actualPCU - maxPCU} PCU over the limit!", Grid);
                    return;
                }
                if (maxMass > 1 && actualMass > maxMass)
                {
                    Utils.ShowNotification($"Can not set grid to class {gridClass.Name}, grid is {actualMass - maxMass} KG over the limit!", Grid);
                    return;
                }

                if (!ModSessionManager.Config.IsValidGridClassId(value))
                    throw new Exception($"CubeGridLogic:: set GridClassId: invalid grid class id {value}");

                Utils.Log($"CubeGridLogic::GridClassId setting grid class to {value}", 1);
                _gridClassId = value;
                GridClassHasChanged();
            }
        }

        public GridClass GridClass => ModSessionManager.Config.GetGridClassById(GridClassId);
        public GridModifiers Modifiers => GridClass.Modifiers;
        public GridDamageModifiers DamageModifiers = new GridDamageModifiers();

        public void Initialize(IMyEntity entity)
        {
            Grid = entity as IMyCubeGrid;
            if (Grid == null) return;
            if (ModSessionManager.Instance.CubeGridLogics.ContainsKey(Grid.EntityId) && 
                _gridClassId == DefaultGridClassConfig.DefaultGridClassDefinition.Id) return;
            
            List<IMyCubeGrid> subs;
            var main = Utils.GetMainCubeGrid(Grid, out subs);
            if (main.EntityId != Grid.EntityId)
            {
                var logic = new CubeGridLogic();
                logic.Initialize(main);
                return;
            }

            //Init event handlers
            Grid.OnBlockOwnershipChanged += OnBlockOwnershipChanged;
            Grid.OnIsStaticChanged += OnIsStaticChanged;
            Grid.OnBlockAdded += OnBlockAdded;
            Grid.OnBlockRemoved += OnBlockRemoved;
            Grid.OnGridMerge += OnGridMerge;

            var config = ModSessionManager.Config;
            if (OwningFaction != null)
            {
                if (!config.IncludeAiFactions && OwningFaction.IsEveryoneNpc()) return;
                if (config.IgnoreFactionTags.Contains(OwningFaction.Tag)) return;
            }
            Blocks.UnionWith(Grid.GetFatBlocks<MyCubeBlock>().Where(b => b.IsPreview == false));

            if (Grid.Storage == null) Grid.Storage = new MyModStorageComponent();
            string value;
            if (Grid.Storage.TryGetValue(Constants.GridClassStorageGUID, out value))
            {
                long id;
                var gridClassId = long.TryParse(value, out id) ? id : 0;
                Utils.Log($"[CubeGridLogic] Assigning GridClassId = {gridClassId}");
                _gridClassId = gridClassId;
            }
            else
            {
                _gridClassId = DefaultGridClassConfig.DefaultGridClassDefinition.Id;
                Grid.Storage[Constants.GridClassStorageGUID] = DefaultGridClassConfig.DefaultGridClassDefinition.Id.ToString();
            }

            // If subgrid then blacklist and add blocks to main grid
            if (!AddGridLogic()) return;

            List<IMyCubeGrid> subgrids;
            Utils.GetMainCubeGrid(Grid, out subgrids);
            foreach (var subgrid in subgrids)
            {
                subgrid.OnBlockOwnershipChanged += OnBlockOwnershipChanged;
                subgrid.OnIsStaticChanged += OnIsStaticChanged;
                subgrid.OnBlockAdded += OnBlockAdded;
                subgrid.OnBlockRemoved += OnBlockRemoved;

                Blocks.UnionWith(subgrid.GetFatBlocks<MyCubeBlock>().Where(b => b.IsPreview == false));
            }

            foreach (var blockLimit in GridClass.BlockLimits)
            {
                var blockVals = new List<KeyValuePair<IMyCubeBlock, double>>();
                foreach (var blockType in blockLimit.BlockTypes)
                {
                    var countingBlocks = Blocks
                        .Where(b => Utils.GetBlockTypeId(b) == blockType.TypeId &&
                                    Utils.GetBlockSubtypeId(b) == blockType.SubtypeId);
                    blockVals.AddRange(countingBlocks.Select(bl => new KeyValuePair<IMyCubeBlock, double>(bl, blockType.CountWeight)));
                }
                BlocksPerLimit[blockLimit] = blockVals;
            }

            foreach (var funcBlock in Blocks.OfType<IMyFunctionalBlock>())
            {
                funcBlock.EnabledChanged += FuncBlockOnEnabledChanged;
                funcBlock.OnUpgradeValuesChanged += OnUpgradeValuesChanged;
            }

            EnforceBlockPunishment();
            ApplyModifiers();
        }

        private bool AddGridLogic()
        {
            try
            {
                if (this == null) throw new Exception("gridLogic cannot be null");
                if (Grid == null) throw new Exception("gridLogic.Grid cannot be null");
                if (CubeGridLogics == null) throw new Exception("CubeGridLogics cannot be null");

                Utils.Log($"Adding Logic: {Grid.EntityId} | {Grid.CustomName}");

                GridsPerFactionClassManager.AddCubeGrid(this);
                GridsPerPlayerClassManager.AddCubeGrid(this);
                CubeGridLogics[Grid.EntityId] = this;

                var concreteGrid = Grid as MyCubeGrid;
                return concreteGrid != null;
            }
            catch (Exception e)
            {
                Utils.Log("CubeGridLogic::AddGridLogic: caught error", 3);
                Utils.LogException(e);
                return false;
            }
        }

        private void FuncBlockOnEnabledChanged(IMyTerminalBlock obj)
        {
            var func = obj as IMyFunctionalBlock;
            if (func == null) return;
            if (HasFunctioningBeaconIfNeeded() == false)
            {
                DamageModifiers = DefaultGridClassConfig.DefaultGridDamageModifiers2X;
                foreach (var block in Blocks)
                    CubeGridModifiers.ApplyModifiers(block, DefaultGridClassConfig.DefaultGridModifiers);
            }
            if (func.Enabled)
                EnforceBlockPunishment(func);
        }

        private void OnUpgradeValuesChanged()
        {
            ApplyModifiers();
        }

        public void RemoveGridLogic()
        {
            try
            {
                // called when entity is about to be removed for whatever reason (block destroyed, entity deleted, grid despawn because of sync range, etc)
                MyAPIGateway.Session.Factions.FactionStateChanged -= FactionsOnFactionStateChanged;
                foreach (var funcBlock in Blocks.OfType<IMyFunctionalBlock>())
                {
                    funcBlock.EnabledChanged -= FuncBlockOnEnabledChanged;
                    funcBlock.OnUpgradeValuesChanged -= OnUpgradeValuesChanged;
                }
                Grid.OnBlockOwnershipChanged -= OnBlockOwnershipChanged;
                Grid.OnIsStaticChanged -= OnIsStaticChanged;
                Grid.OnBlockAdded -= OnBlockAdded;
                Grid.OnBlockRemoved -= OnBlockRemoved;
                Grid.OnGridMerge -= OnGridMerge;

                CubeGridLogics.Remove(Grid.EntityId);
                GridsPerFactionClassManager.RemoveCubeGrid(this);
                GridsPerPlayerClassManager.RemoveCubeGrid(this);
            }
            catch (Exception e)
            {
                Utils.Log(e.ToString());
            }
        }

        private void ApplyModifiers(GridModifiers modifiers = null)
        {
            DamageModifiers = GridClass.DamageModifiers;
            foreach (var block in from block in Blocks let terminalBlock = block as IMyTerminalBlock where terminalBlock != null select block)
            {
                CubeGridModifiers.ApplyModifiers(block, modifiers ?? Modifiers);
            }
        }

        private static void OnGridMerge(IMyCubeGrid main, IMyCubeGrid sub)
        {
            if (!CubeGridLogics.ContainsKey(sub.EntityId) ||
                !CubeGridLogics.ContainsKey(main.EntityId)) return;

            CubeGridLogics[sub.EntityId].RemoveGridLogic();
            var mainLogic = CubeGridLogics[main.EntityId];

            sub.OnBlockOwnershipChanged += mainLogic.OnBlockOwnershipChanged;
            sub.OnIsStaticChanged += mainLogic.OnIsStaticChanged;
            sub.OnBlockAdded += mainLogic.OnBlockAdded;
            sub.OnBlockRemoved += mainLogic.OnBlockRemoved;

            mainLogic.Blocks.Clear();
            mainLogic.Blocks.UnionWith(mainLogic.Grid.GetFatBlocks<MyCubeBlock>().Where(b => b.IsPreview == false));
        }

        //Event handlers
        private void OnIsStaticChanged(IMyCubeGrid grid, bool isStatic)
        {
            if (GridClass.LargeGridStatic && !GridClass.LargeGridMobile && !isStatic) grid.IsStatic = true;
            if (!GridClass.LargeGridStatic && isStatic) grid.IsStatic = false;
        }

        private void GridClassHasChanged()
        {
            Utils.Log($"CubeGridLogic::OnGridClassChanged: new grid class id = {GridClassId}", 2);

            GridsPerFactionClassManager.Reset();
            foreach (var gridLogic in CubeGridLogics) GridsPerFactionClassManager.AddCubeGrid(gridLogic.Value);
            GridsPerPlayerClassManager.Reset();
            foreach (var gridLogic in CubeGridLogics) GridsPerPlayerClassManager.AddCubeGrid(gridLogic.Value);
            Grid.Storage[Constants.GridClassStorageGUID] = GridClassId.ToString();

            BlocksPerLimit.Clear();
            foreach (var blockLimit in GridClass.BlockLimits)
            {
                var blockVals = new List<KeyValuePair<IMyCubeBlock, double>>();
                foreach (var blockType in blockLimit.BlockTypes)
                {
                    var countingBlocks = Blocks
                        .Where(b => Utils.GetBlockTypeId(b) == blockType.TypeId &&
                                    Utils.GetBlockSubtypeId(b) == blockType.SubtypeId);
                    blockVals.AddRange(countingBlocks.Select(bl => new KeyValuePair<IMyCubeBlock, double>(bl, blockType.CountWeight)));
                }
                BlocksPerLimit[blockLimit] = blockVals;
            }
            EnforceBlockPunishment();
            ApplyModifiers();
        }

        private void OnBlockAdded(IMySlimBlock obj)
        {
            Utils.Log($"{Utils.GetBlockTypeId(obj)} | {Utils.GetBlockSubtypeId(obj)}");
            var concreteGrid = Grid as MyCubeGrid;
            if (concreteGrid?.BlocksCount > GridClass.MaxBlocks)
            {
                Grid.RemoveBlock(obj);
                return;
            }

            var relevantLimits = GetRelevantLimits(obj);
            foreach (var limit in relevantLimits)
            {
                var limitBlocks = BlocksPerLimit[limit];
                var countWeight = limitBlocks.Sum(b => b.Value);
                var countForSpecificBlock = limit.BlockTypes.First(l =>
                    l.TypeId == Utils.GetBlockTypeId(obj) && l.SubtypeId == Utils.GetBlockSubtypeId(obj)).CountWeight;

                Utils.Log($"{countWeight} | {countForSpecificBlock} | {limit.MaxCount}");

                if (countWeight + countForSpecificBlock > limit.MaxCount)
                {
                    Grid.RemoveBlock(obj);
                    List<IMyCubeGrid> subs;
                    Utils.GetMainCubeGrid(Grid, out subs);
                    foreach (var subgrid in subs)
                    {
                        subgrid.RemoveBlock(obj);
                    }
                    return;
                }
                BlocksPerLimit[limit].Add(new KeyValuePair<IMyCubeBlock, double>(obj.FatBlock, countForSpecificBlock));
            }

            Blocks.Add(obj.FatBlock as MyCubeBlock);

            var funcBlock = obj.FatBlock as IMyFunctionalBlock;
            if (funcBlock != null)
                funcBlock.EnabledChanged += FuncBlockOnEnabledChanged;
            ApplyModifiers();
        }

        private void OnBlockRemoved(IMySlimBlock obj)
        {
            if (obj.FatBlock != null && HasFunctioningBeaconIfNeeded() == false)
            {
                DamageModifiers = DefaultGridClassConfig.DefaultGridDamageModifiers2X;
                foreach (var block in Blocks)
                {
                    CubeGridModifiers.ApplyModifiers(block, DefaultGridClassConfig.DefaultGridModifiers);
                }
            }
            else DamageModifiers = GridClass.DamageModifiers;

            var relevantLimits = GetRelevantLimits(obj);
            foreach (var limit in relevantLimits)
            {
                if (!BlocksPerLimit.ContainsKey(limit)) return;
                var index = BlocksPerLimit[limit].FindIndex(b => b.Key == obj.FatBlock);
                if (index >= 0)
                    BlocksPerLimit[limit].RemoveAt(index);
            }

            var concreteGrid = Grid as MyCubeGrid;
            if (concreteGrid?.BlocksCount < GridClass.MinBlocks)
            {
                GridClassId = 0;
            }
            Blocks.Remove(obj.FatBlock as MyCubeBlock);
            ApplyModifiers();
        }

        private void OnBlockOwnershipChanged(IMyCubeGrid obj)
        {
            EnforceBlockPunishment();
        }

        private void FactionsOnFactionStateChanged(MyFactionStateChange action, long fromFactionId, long toFactionId, long playerId, long senderId)
        {
            if ((action != MyFactionStateChange.FactionMemberAcceptJoin &&
                 action != MyFactionStateChange.FactionMemberLeave &&
                 action != MyFactionStateChange.FactionMemberKick) || Grid.BigOwners[0] != playerId) return;
            if (!GridsPerFactionClassManager.WillGridBeWithinFactionLimits(this, GridClassId)) GridClassId = 0;
        }

        private void EnforceBlockPunishment(IMyCubeBlock block = null)
        {
            if (block != null)
            {
                var relevantLimits = GetRelevantLimits(block.SlimBlock);
                foreach (var limit in relevantLimits)
                {
                    var limitBlocks = BlocksPerLimit[limit];
                    var countWeight = limitBlocks.Sum(l => l.Value);
                    Utils.Log($"Block check: {limit.Name} | {countWeight} | {limit.MaxCount}");
                    if (countWeight <= limit.MaxCount) continue;
                    var func = block as IMyFunctionalBlock;
                    if (func != null) func.Enabled = false;
                    else
                    {
                        var slim = block.SlimBlock;
                        var targetIntegrity = slim.MaxIntegrity * 0.2;
                        var damageRequired = slim.Integrity - targetIntegrity;

                        if (damageRequired < 0)
                        {
                            damageRequired = 0;
                        }
                        slim.DoDamage((float)damageRequired, MyDamageType.Bullet, true);
                    }
                }
            }
            else
            {
                foreach (var limit in GridClass.BlockLimits)
                {
                    if (!BlocksPerLimit.ContainsKey(limit)) return;
                    var limitBlocks = BlocksPerLimit[limit];
                    double countWeight = 0;
                    foreach (var limitBlock in limitBlocks)
                    {
                        countWeight += limitBlock.Value;
                        if (countWeight <= limit.MaxCount) continue;
                        var func = limitBlock.Key as IMyFunctionalBlock;
                        if (func != null) func.Enabled = false;
                        else
                        {
                            var slim = limitBlock.Key.SlimBlock;
                            var targetIntegrity = slim.MaxIntegrity * 0.2;
                            var damageRequired = slim.Integrity - targetIntegrity;

                            if (damageRequired < 0)
                            {
                                damageRequired = 0;
                            }
                            slim.DoDamage((float)damageRequired, MyDamageType.Bullet, true);
                        }
                    }
                }
            }
            
        }

        private bool HasFunctioningBeaconIfNeeded()
        {
            return GridClass.ForceBroadCast == false || Blocks.OfType<IMyFunctionalBlock>().Any(block => block is IMyBeacon && block.Enabled);
        }

        private IEnumerable<BlockLimit> GetRelevantLimits(IMySlimBlock block)
        {
            return GridClass.BlockLimits.Where(limit => limit.BlockTypes
                .Any(type => type.TypeId == Utils.GetBlockTypeId(block) && type.SubtypeId == Utils.GetBlockSubtypeId(block)));
        }

        private long GetMajorityOwner()
        {
            return Grid.BigOwners.FirstOrDefault();
        }
    }
}