using MinerPlugin;
using MinerPluginToolkitV1;
using MinerPluginToolkitV1.Configs;
using MinerPluginToolkitV1.ExtraLaunchParameters;
using MinerPluginToolkitV1.Interfaces;
using Newtonsoft.Json;
using NiceHashMinerLegacy.Common;
using NiceHashMinerLegacy.Common.Algorithm;
using NiceHashMinerLegacy.Common.Device;
using NiceHashMinerLegacy.Common.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XmrStak.Configs;

namespace XmrStak
{
    public class XmrStakPlugin : IMinerPlugin, IInitInternals, IXmrStakConfigHandler, IBinaryPackageMissingFilesChecker, IReBenchmarkChecker, IGetApiMaxTimeout
    {
        public XmrStakPlugin()
        {
            _pluginUUID = "3d4e56b0-7238-11e9-b20c-f9f12eb6d835";
        }
        public XmrStakPlugin(string pluginUUID = "3d4e56b0-7238-11e9-b20c-f9f12eb6d835")
        {
            _pluginUUID = pluginUUID;
        }
        private readonly string _pluginUUID;
        public string PluginUUID => _pluginUUID;

        public Version Version => new Version(1, 1);
        public string Name => "XmrStak";

        public string Author => "stanko@nicehash.com";

        protected Dictionary<string, DeviceType> _registeredDeviceUUIDTypes = new Dictionary<string, DeviceType>();
        protected HashSet<AlgorithmType> _registeredAlgorithmTypes = new HashSet<AlgorithmType>();

        public Dictionary<BaseDevice, IReadOnlyList<Algorithm>> GetSupportedAlgorithms(IEnumerable<BaseDevice> devices)
        {
            var supported = new Dictionary<BaseDevice, IReadOnlyList<Algorithm>>();

            var devicesToAdd = new List<BaseDevice>();
            // AMD case check if we should check Gcn4
            var amdGpus = devices.Where(dev => dev is AMDDevice /* amd && Checkers.IsGcn4(amd)*/).Cast<AMDDevice>(); 
            var cudaGpus = devices.Where(dev => dev is CUDADevice cuda && cuda.SM_major >= 3).Cast<CUDADevice>();
            var cpus = devices.Where(dev => dev is CPUDevice).Cast<CPUDevice>();

            // CUDA 9.2+ driver 397.44
            var mininumRequiredDriver = new Version(397, 44);
            if (CUDADevice.INSTALLED_NVIDIA_DRIVERS >= mininumRequiredDriver)
            {
                devicesToAdd.AddRange(cudaGpus);
            }
            devicesToAdd.AddRange(amdGpus);
            devicesToAdd.AddRange(cpus);

            // CPU 
            foreach (var dev in devicesToAdd)
            {
                var algorithms = GetSupportedAlgorithms(dev);
                if (algorithms.Count > 0)
                {
                    supported.Add(dev, algorithms);
                    _registeredDeviceUUIDTypes[dev.UUID] = dev.DeviceType;
                    foreach (var algorithm in algorithms)
                    {
                        _registeredAlgorithmTypes.Add(algorithm.FirstAlgorithmType);
                    }
                }
            }


            return supported;
        }

        private List<Algorithm> GetSupportedAlgorithms(BaseDevice dev)
        {
            // multiple OpenCL GPUs seem to freeze the whole system
            var AMD_DisabledByDefault = dev.DeviceType != DeviceType.AMD;
            var algos = new List<Algorithm>
            {
                new Algorithm(PluginUUID, AlgorithmType.CryptoNightHeavy) { Enabled = AMD_DisabledByDefault },
                new Algorithm(PluginUUID, AlgorithmType.CryptoNightV8) { Enabled = AMD_DisabledByDefault },
                new Algorithm(PluginUUID, AlgorithmType.CryptoNightR) { Enabled = AMD_DisabledByDefault },
            };
            return algos;
        }

        public IMiner CreateMiner()
        {
            return new XmrStak(PluginUUID, AMDDevice.GlobalOpenCLPlatformID, this)
            {
                MinerOptionsPackage = _minerOptionsPackage,
                MinerSystemEnvironmentVariables = _minerSystemEnvironmentVariables,
                MinerReservedApiPorts = _minerReservedApiPorts
            };
        }

        public bool CanGroup(MiningPair a, MiningPair b)
        {
            return a.Algorithm.FirstAlgorithmType == b.Algorithm.FirstAlgorithmType;
        }

        private string GetMinerConfigsRoot()
        {
            return Path.Combine(Paths.MinerPluginsPath(), PluginUUID, "configs");
        }

        // these here are slightly different
        #region Internal settings
        public void InitInternals()
        {
            var pluginRoot = Path.Combine(Paths.MinerPluginsPath(), PluginUUID);
            var fileMinerOptionsPackage = InternalConfigs.InitInternalsHelper(pluginRoot, _minerOptionsPackage);
            if (fileMinerOptionsPackage != null) _minerOptionsPackage = fileMinerOptionsPackage;

            var readFromFileEnvSysVars = InternalConfigs.InitMinerSystemEnvironmentVariablesSettings(pluginRoot, _minerSystemEnvironmentVariables);
            if (readFromFileEnvSysVars != null) _minerSystemEnvironmentVariables = readFromFileEnvSysVars;

            var fileMinerReservedPorts = InternalConfigs.InitMinerReservedPorts(pluginRoot, _minerReservedApiPorts);
            if (fileMinerReservedPorts != null) _minerReservedApiPorts = fileMinerReservedPorts;


            var minerConfigPath = GetMinerConfigsRoot();
            if (!Directory.Exists(minerConfigPath)) return; // no settings

            var configFiles = Directory.GetFiles(minerConfigPath, "cached_*.json");
            var registeredDeviceTypes = _registeredDeviceUUIDTypes.Select(kvp => kvp.Value).Distinct();

            foreach (var deviceType in registeredDeviceTypes)
            {
                var uuids = _registeredDeviceUUIDTypes.Where(kvp => kvp.Value == deviceType).Select(kvp => kvp.Key);
                foreach (var algorithm in _registeredAlgorithmTypes)
                {
                    var cachedConfig = $"{algorithm.ToString()}_{deviceType.ToString()}";
                    var cachedConfigPath = configFiles.Where(path => path.Contains(cachedConfig)).FirstOrDefault();
                    if (string.IsNullOrEmpty(cachedConfigPath)) continue;

                    var cachedConfigContent = File.ReadAllText(cachedConfigPath);
                    try
                    {
                        switch (deviceType)
                        {
                            case DeviceType.CPU:
                                var cpuConfig = JsonConvert.DeserializeObject<CachedCpuSettings>(cachedConfigContent);
                                var isCpuSame = uuids.Except(cpuConfig.DeviceUUIDs).Count() == 0;
                                if (isCpuSame) SetCpuConfig(algorithm, cpuConfig.CachedConfig);
                                break;
                            case DeviceType.AMD:
                                var amdConfig = JsonConvert.DeserializeObject<CachedAmdSettings>(cachedConfigContent);
                                var isAmdSame = uuids.Except(amdConfig.DeviceUUIDs).Count() == 0;
                                if (isAmdSame) SetAmdConfig(algorithm, amdConfig.CachedConfig);
                                break;
                            case DeviceType.NVIDIA:
                                var nvidiaConfig = JsonConvert.DeserializeObject<CachedNvidiaSettings>(cachedConfigContent);
                                var isNvidiaSame = uuids.Except(nvidiaConfig.DeviceUUIDs).Count() == 0;
                                if (isNvidiaSame) SetNvidiaConfig(algorithm, nvidiaConfig.CachedConfig);
                                break;
                        }
                    }
                    catch (Exception)
                    { }
                }
            }
        }

        protected static MinerReservedPorts _minerReservedApiPorts = new MinerReservedPorts { };

        protected static MinerSystemEnvironmentVariables _minerSystemEnvironmentVariables = new MinerSystemEnvironmentVariables
        {
            DefaultSystemEnvironmentVariables = new Dictionary<string, string>
            {
                { "XMRSTAK_NOWAIT", "1" },
                // https://github.com/fireice-uk/xmr-stak/blob/master/doc/tuning.md#increase-memory-pool
                // for AMD backend
                {"GPU_MAX_ALLOC_PERCENT", "100"},
                {"GPU_SINGLE_ALLOC_PERCENT", "100"},
                {"GPU_MAX_HEAP_SIZE", "100"},
                {"GPU_FORCE_64BIT_PTR", "1"}
            }
        };

        protected static MinerOptionsPackage _minerOptionsPackage = new MinerOptionsPackage
        {
            GeneralOptions = new List<MinerOption>
            {
                /// <summary>
                /// Directory to store AMD binary files
                /// </summary>
                new MinerOption
                {
                    Type = MinerOptionType.OptionWithSingleParameter,
                    ID = "xmrstak_amdCacheDir",
                    ShortName = "--amdCacheDir",
                },
            }
        };
        #endregion Internal settings



        #region Cached configs
        protected ConcurrentDictionary<AlgorithmType, CpuConfig> _cpuConfigs = new ConcurrentDictionary<AlgorithmType, CpuConfig>();
        protected ConcurrentDictionary<AlgorithmType, AmdConfig> _amdConfigs = new ConcurrentDictionary<AlgorithmType, AmdConfig>();
        protected ConcurrentDictionary<AlgorithmType, NvidiaConfig> _nvidiaConfigs = new ConcurrentDictionary<AlgorithmType, NvidiaConfig>();


        public bool HasConfig(DeviceType deviceType, AlgorithmType algorithmType)
        {
            switch (deviceType)
            {
                case DeviceType.CPU:
                    return GetCpuConfig(algorithmType) != null;
                case DeviceType.AMD:
                    return GetAmdConfig(algorithmType) != null;
                case DeviceType.NVIDIA:
                    return GetNvidiaConfig(algorithmType) != null;
            }
            return false;
        }

        public void SaveMoveConfig(DeviceType deviceType, AlgorithmType algorithmType, string sourcePath)
        {
            try
            {
                string destinationPath = Path.Combine(GetMinerConfigsRoot(), $"{algorithmType.ToString()}_{deviceType.ToString()}.txt");
                var dirPath = Path.GetDirectoryName(destinationPath);
                if (Directory.Exists(dirPath) == false)
                {
                    Directory.CreateDirectory(dirPath);
                }

                var readConfigContent = File.ReadAllText(sourcePath);
                // make it JSON 
                readConfigContent = "{" + readConfigContent + "}";
                // remove old if any
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                // move to path
                File.Move(sourcePath, destinationPath);

                var cachedFileSettings = $"cached_{algorithmType.ToString()}_{deviceType.ToString()}.json";
                var cachedFileSettingsPath = Path.Combine(GetMinerConfigsRoot(), cachedFileSettings);
                var uuids = _registeredDeviceUUIDTypes.Where(kvp => kvp.Value == deviceType).Select(kvp => kvp.Key).ToList();
                object cachedSettings = null;
                //TODO load and save 
                switch (deviceType)
                {
                    case DeviceType.CPU:
                        var cpuConfig = JsonConvert.DeserializeObject<CpuConfig>(readConfigContent);
                        SetCpuConfig(algorithmType, cpuConfig);
                        cachedSettings = new CachedCpuSettings {
                            CachedConfig = cpuConfig,
                            DeviceUUIDs = uuids
                        };
                        break;
                    case DeviceType.AMD:
                        var amdConfig = JsonConvert.DeserializeObject<AmdConfig>(readConfigContent);
                        SetAmdConfig(algorithmType, amdConfig);
                        cachedSettings = new CachedAmdSettings
                        {
                            CachedConfig = amdConfig,
                            DeviceUUIDs = uuids
                        };
                        break;
                    case DeviceType.NVIDIA:
                        var nvidiaConfig = JsonConvert.DeserializeObject<NvidiaConfig>(readConfigContent);
                        SetNvidiaConfig(algorithmType, nvidiaConfig);
                        cachedSettings = new CachedNvidiaSettings
                        {
                            CachedConfig = nvidiaConfig,
                            DeviceUUIDs = uuids
                        };
                        break;
                }
                if (cachedSettings != null)
                {
                    var header = "// This config file was autogenerated by NHML.";
                    header += "\n// \"DeviceUUIDs\" is used to check if we have same devices and should not be edited.";
                    header += "\n// \"CachedConfig\" can be edited as it is used as config template (edit this only if you know what you are doing)";
                    header += "\n// If \"DeviceUUIDs\" is different (new devices added or old ones removed) this file will be overwritten and \"CachedConfig\" will be set to defaults.";
                    header += "\n";
                    if (deviceType == DeviceType.CPU)
                    {
                        header += "\n// Thread configuration for each thread.";
                        header += "\n// low_power_mode - This can either be a boolean (true or false), or a number between 1 to 5. When set to true";
                        header += "\n// \t\t\t\t\tthis mode will double the cache usage, and double the single thread performance. It will";
                        header += "\n// \t\t\t\t\tconsume much less power (as less cores are working), but will max out at around 80-85% of";
                        header += "\n// \t\t\t\t\tthe maximum performance. When set to a number N greater than 1, this mode will increase the";
                        header += "\n// \t\t\t\t\tcache usage and single thread performance by N times.";
                        header += "\n";
                        header += "\n// no_prefetch    - Some systems can gain up to extra 5% here, but sometimes it will have no difference or make";
                        header += "\n// \t\t\t\t\tthings slower.";
                        header += "\n";
                        header += "\n// asm            - Allow to switch to a assembler version of cryptonight_v8; allowed value [auto, off, intel_avx, amd_avx]";
                        header += "\n// \t\t\t\t\t  - auto: xmr-stak will automatically detect the asm type (default)";
                        header += "\n// \t\t\t\t\t  - off: disable the usage of optimized assembler";
                        header += "\n// \t\t\t\t\t  - intel_avx: supports Intel cpus with avx instructions e.g. Xeon v2, Core i7/i5/i3 3xxx, Pentium G2xxx, Celeron G1xxx";
                        header += "\n// \t\t\t\t\t  - amd_avx: supports AMD cpus with avx instructions e.g. AMD Ryzen 1xxx and 2xxx series";
                        header += "\n";
                        header += "\n// affine_to_cpu  - This can be either false (no affinity), or the CPU core number. Note that on hyperthreading";
                        header += "\n// \t\t\t\t\tsystems it is better to assign threads to physical cores. On Windows this usually means selecting";
                        header += "\n// \t\t\t\t\teven or odd numbered cpu numbers. For Linux it will be usually the lower CPU numbers, so for a 4";
                        header += "\n// \t\t\t\t\tphysical core CPU you should select cpu numbers 0-3.";
                    }
                    else if (deviceType == DeviceType.NVIDIA)
                    {
                        header += "\n// GPU configuration. You should play around with threads and blocks as the fastest settings will vary.";
                        header += "\n// index         - GPU index number usually starts from 0.";
                        header += "\n// threads       - Number of GPU threads (nothing to do with CPU threads).";
                        header += "\n// blocks        - Number of GPU blocks (nothing to do with CPU threads).";
                        header += "\n// bfactor       - Enables running the Cryptonight kernel in smaller pieces.";
                        header += "\n// \t\t\t\t\tIncrease if you want to reduce GPU lag. Recommended setting on GUI systems - 8";
                        header += "\n// bsleep        - Insert a delay of X microseconds between kernel launches.";
                        header += "\n// \t\t\t\t\tIncrease if you want to reduce GPU lag. Recommended setting on GUI systems - 100";
                        header += "\n// affine_to_cpu - This will affine the thread to a CPU. This can make a GPU miner play along nicer with a CPU miner.";
                        header += "\n// sync_mode     - method used to synchronize the device";
                        header += "\n// \t\t\t\t\tdocumentation: http://docs.nvidia.com/cuda/cuda-runtime-api/group__CUDART__DEVICE.html#group__CUDART__DEVICE_1g69e73c7dda3fc05306ae7c811a690fac";
                        header += "\n// \t\t\t\t\t0 = cudaDeviceScheduleAuto";
                        header += "\n// \t\t\t\t\t1 = cudaDeviceScheduleSpin - create a high load on one cpu thread per gpu";
                        header += "\n// \t\t\t\t\t2 = cudaDeviceScheduleYield";
                        header += "\n// \t\t\t\t\t3 = cudaDeviceScheduleBlockingSync (default)";
                        header += "\n// mem_mode      - select the memory access pattern (this option has only a meaning for cryptonight_v8 and monero)";
                        header += "\n// \t\t\t\t\t0 = 64bit memory loads";
                        header += "\n// \t\t\t\t\t1 = 256bit memory loads";
                    }
                    else if (deviceType == DeviceType.AMD)
                    {
                        header += "\n// GPU configuration. You should play around with threads and blocks as the fastest settings will vary.";
                        header += "\n// index         - GPU index number usually starts from 0.";
                        header += "\n// intensity     - Number of parallel GPU threads (nothing to do with CPU threads)";
                        header += "\n// worksize      - Number of local GPU threads (nothing to do with CPU threads)";
                        header += "\n// affine_to_cpu - This will affine the thread to a CPU. This can make a GPU miner play along nicer with a CPU miner.";
                        header += "\n// strided_index - switch memory pattern used for the scratchpad memory";
                        header += "\n// \t\t\t\t\t3 = chunked memory, chunk size based on the 'worksize'";
                        header += "\n// \t\t\t\t\t    required: intensity must be a multiple of worksize";
                        header += "\n// \t\t\t\t\t2 = chunked memory, chunk size is controlled by 'mem_chunk'";
                        header += "\n// \t\t\t\t\t    required: intensity must be a multiple of worksize";
                        header += "\n// \t\t\t\t\t1 or true  = use 16 byte contiguous memory per thread, the next memory block has offset of intensity blocks";
                        header += "\n// \t\t\t\t\t            (for cryptonight_v8 and monero it is equal to strided_index = 0)";
                        header += "\n// \t\t\t\t\t0 or false = use a contiguous block of memory per thread";
                        header += "\n// mem_chunk     - range 0 to 18: set the number of elements (16byte) per chunk";
                        header += "\n// \t\t\t\t\tthis value is only used if 'strided_index' == 2";
                        header += "\n// \t\t\t\t\telement count is computed with the equation: 2 to the power of 'mem_chunk' e.g. 4 means a chunk of 16 elements(256 byte)";
                        header += "\n// unroll        - allow to control how often the POW main loop is unrolled; valid range [1;128) - for most OpenCL implementations it must be a power of two.";
                        header += "\n// comp_mode     - Compatibility enable/disable the automatic guard around compute kernel which allows";
                        header += "\n// \t\t\t\t\tto use an intensity which is not the multiple of the worksize.";
                        header += "\n// \t\t\t\t\tIf you set false and the intensity is not multiple of the worksize the miner can crash:";
                        header += "\n// \t\t\t\t\tin this case set the intensity to a multiple of the worksize or activate comp_mode.";
                        header += "\n// interleave    - Controls the starting point in time between two threads on the same GPU device relative to the last started thread.";
                        header += "\n// \t\t\t\t\tThis option has only an effect if two compute threads using the same GPU device: valid range [0;100]";
                        header += "\n// \t\t\t\t\t0  = disable thread interleaving";
                        header += "\n// \t\t\t\t\t40 = each working thread waits until 40% of the hash calculation of the previously started thread is finished";
                    }
                    header += "\n\n";
                    var jsonText = JsonConvert.SerializeObject(cachedSettings, Formatting.Indented);
                    var headerWithConfigs = header + jsonText;
                    InternalConfigs.WriteFileSettings(cachedFileSettingsPath, headerWithConfigs);
                }
            }
            catch (Exception)
            { }
        }

        public CpuConfig GetCpuConfig(AlgorithmType algorithmType)
        {
            CpuConfig config = null;
            _cpuConfigs.TryGetValue(algorithmType, out config);
            return config;
        }

        public AmdConfig GetAmdConfig(AlgorithmType algorithmType)
        {
            AmdConfig config = null;
            _amdConfigs.TryGetValue(algorithmType, out config);
            return config;
        }

        public NvidiaConfig GetNvidiaConfig(AlgorithmType algorithmType)
        {
            NvidiaConfig config = null;
            _nvidiaConfigs.TryGetValue(algorithmType, out config);
            return config;
        }

        public void SetCpuConfig(AlgorithmType algorithmType, CpuConfig conf)
        {
            if (HasConfig(DeviceType.CPU, algorithmType)) return;
            _cpuConfigs.TryAdd(algorithmType, conf);
        }

        public void SetAmdConfig(AlgorithmType algorithmType, AmdConfig conf)
        {
            if (HasConfig(DeviceType.AMD, algorithmType)) return;
            _amdConfigs.TryAdd(algorithmType, conf);
        }

        public void SetNvidiaConfig(AlgorithmType algorithmType, NvidiaConfig conf)
        {
            if (HasConfig(DeviceType.NVIDIA, algorithmType)) return;
            _nvidiaConfigs.TryAdd(algorithmType, conf);
        }

        #endregion Cached configs

        public IEnumerable<string> CheckBinaryPackageMissingFiles()
        {
            var miner = CreateMiner() as IBinAndCwdPathsGettter;
            if (miner == null) return Enumerable.Empty<string>();
            var pluginRootBinsPath = miner.GetBinAndCwdPaths().Item2;
            return BinaryPackageMissingFilesCheckerHelpers.ReturnMissingFiles(pluginRootBinsPath, new List<string> { "concrt140.dll", "libeay32.dll", "msvcp140.dll", "msvcp140_1.dll",
                "msvcp140_2.dll", "nvrtc-builtins64_100.dll", "nvrtc-builtins64_90.dll", "nvrtc64_100_0.dll", "nvrtc64_90.dll", "ssleay32.dll", "vccorlib140.dll", "vcruntime140.dll",
                "xmr-stak.exe", "xmrstak_cuda_backend.dll", "xmrstak_cuda_backend_cuda10_0.dll", "xmrstak_opencl_backend.dll"
            });
        }

        public bool ShouldReBenchmarkAlgorithmOnDevice(BaseDevice device, Version benchmarkedPluginVersion, params AlgorithmType[] ids)
        {
            //no new version available
            return false;
        }

        public TimeSpan GetApiMaxTimeout()
        {
            return new TimeSpan(0, 5, 0);
        }
    }
}
