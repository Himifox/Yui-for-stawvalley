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

    private static int Facing(int facing, int up, int right, int down, int left) => facing switch
    {
        0 => up,
        1 => right,
        2 => down,
        _ => left,
    };
}
