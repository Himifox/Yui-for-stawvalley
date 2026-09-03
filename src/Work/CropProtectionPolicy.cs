using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.TerrainFeatures;

namespace YuiToIssho;

internal static class CropProtectionPolicy
{
    public static string? GetReason(Crop crop)
    {
        string harvestItemId = crop.indexOfHarvest.Value;
        if (!string.IsNullOrWhiteSpace(harvestItemId)
            && GiantCrop.GetGiantCropsFor(harvestItemId).Count > 0)
            return WorldTargetReasons.GiantCropPotential;

        CropData? data = crop.GetData();
        return data is not null && !data.CountForMonoculture && !data.CountForPolyculture
            ? WorldTargetReasons.SpecialCropProtected
            : null;
    }
}
