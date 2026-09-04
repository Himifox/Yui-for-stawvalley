using StardewValley;
using StardewValley.Tools;

namespace YuiToIssho;

internal readonly record struct VisualToolAnimation(int AnimationIndex, int FrameCount);

internal static class CompanionVisualToolAnimation
{
    public static bool TryResolve(string kind, int facing, out VisualToolAnimation animation)
    {
        int index = kind switch
        {
            AppearanceActionKinds.Watering => Facing(facing, 180, 172, 164, 188),
            AppearanceActionKinds.Chopping or AppearanceActionKinds.Mining or AppearanceActionKinds.Digging => Facing(facing, 176, 168, 160, 184),
            AppearanceActionKinds.HarvestScythe or AppearanceActionKinds.Mowing or AppearanceActionKinds.CombatSword or AppearanceActionKinds.CombatClub => Facing(facing, 248, 240, 232, 256),
            AppearanceActionKinds.CombatDagger => Facing(facing, 276, 274, 272, 278),
            _ => -1,
        };
        if (index < 0)
        {
            animation = default;
            return false;
        }
        int frameCount = kind switch
        {
            AppearanceActionKinds.CombatDagger => 2,
            AppearanceActionKinds.Watering => 4,
            AppearanceActionKinds.Chopping or AppearanceActionKinds.Mining or AppearanceActionKinds.Digging => 5,
            _ => 6,
        };
        animation = new VisualToolAnimation(index, frameCount);
        return true;
    }

    public static bool UsesSecondaryArm(string kind, int facing, int frame) => kind switch
    {
        AppearanceActionKinds.HarvestScythe or AppearanceActionKinds.Mowing
            or AppearanceActionKinds.CombatSword or AppearanceActionKinds.CombatClub => true,
        AppearanceActionKinds.CombatDagger => facing is 0 or 2,
        AppearanceActionKinds.Watering => frame is 45 or 46,
        _ => false,
    };

    public static void Draw(Farmer farmer, Tool tool, VisualToolAnimation animation, int elapsedTicks, int totalTicks)
    {
        int motionFrame = Math.Min(
            animation.FrameCount - 1,
            Math.Max(0, elapsedTicks) * animation.FrameCount / Math.Max(1, totalTicks));
        int originalInitialIndex = tool.InitialParentTileIndex;
        int originalCurrentIndex = tool.CurrentParentTileIndex;
        try
        {
            farmer.FarmerSprite.currentSingleAnimation = animation.AnimationIndex;
            farmer.FarmerSprite.currentAnimationIndex = motionFrame;
            if (tool is not MeleeWeapon)
            {
                tool.Update(farmer.FacingDirection, motionFrame, farmer);
                if (tool is not WateringCan)
                {
                    int intendedIndex = tool.CurrentParentTileIndex;
                    tool.Update(farmer.FacingDirection, 0, farmer);
                    tool.InitialParentTileIndex += intendedIndex - tool.CurrentParentTileIndex;
                }
            }
            Game1.drawTool(farmer);
        }
        finally
        {
            tool.InitialParentTileIndex = originalInitialIndex;
            tool.CurrentParentTileIndex = originalCurrentIndex;
        }
    }

    private static int Facing(int facing, int up, int right, int down, int left) => facing switch
    {
        0 => up,
        1 => right,
        2 => down,
        _ => left,
    };
}
