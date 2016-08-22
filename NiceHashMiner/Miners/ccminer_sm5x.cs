﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using NiceHashMiner.Configs;
using NiceHashMiner.Devices;
using NiceHashMiner.Enums;

namespace NiceHashMiner.Miners
{
    class ccminer_sm5x : ccminer
    {
        public ccminer_sm5x() :
            base()
        {
            MinerDeviceName = "NVIDIA5.x";
            Path = MinerPaths.ccminer_sp;
        }

        protected override MinerType GetMinerType() {
            return MinerType.ccminer_sm5x;
        }

        protected override void InitSupportedMinerAlgorithms() {
            var allGroupSupportedList = GroupAlgorithms.GetAlgorithmKeysForGroup(DeviceGroupType.NVIDIA_5_x);
            allGroupSupportedList.Remove(AlgorithmType.DaggerHashimoto);
            _supportedMinerAlgorithms = allGroupSupportedList.ToArray();
        }

        public override string GetOptimizedMinerPath(AlgorithmType algorithmType, string devCodename, bool isOptimized) {
            if (AlgorithmType.Decred == algorithmType) {
                return MinerPaths.ccminer_decred;
            }
            if (AlgorithmType.NeoScrypt == algorithmType) {
                return MinerPaths.ccminer_neoscrypt;
            }
            if (AlgorithmType.Lyra2RE == algorithmType || AlgorithmType.Lyra2REv2 == algorithmType) {
                return MinerPaths.ccminer_nanashi;
            }

            return MinerPaths.ccminer_sp;
        }
    }
}