using System;

namespace KitLugia.Core
{
    public class OptimizationSettings
    {
        public bool ApplyRegistryTweaks { get; set; }
        public bool ApplyPowerPlan { get; set; }
        public bool ApplyGamingOptimizations { get; set; }
        public bool ApplyVerboseBoot { get; set; }
        public bool ApplyVramTweak { get; set; }
        public bool UseExtremeProfile { get; set; }
        public string TargetGpuRegPath { get; set; }
        public int VramSizeMb { get; set; }
        
        public OptimizationSettings()
        {
            ApplyRegistryTweaks = false;
            ApplyPowerPlan = false;
            ApplyGamingOptimizations = false;
            ApplyVerboseBoot = false;
            ApplyVramTweak = false;
            UseExtremeProfile = false;
            TargetGpuRegPath = "";
            VramSizeMb = 0;
        }
    }
}
