using StardewModdingAPI;

namespace YuiToIssho;

internal sealed class ModConfig
{
    public bool AutoSummonOnFirstLoad { get; set; } = true;

    public bool EnableNaturalWorkAssist { get; set; } = true;

    public bool EnableExperimentalFeatures { get; set; } = false;

    public bool EnableDiagnostics { get; set; } = false;

    public bool EnableNekoBridge { get; set; } = false;

    public string NekoBridgeToken { get; set; } = string.Empty;

    public SButton CommandModeButton { get; set; } = SButton.LeftAlt;

    public SButton SecondCornerModifierButton { get; set; } = SButton.LeftShift;

    public SButton ControllerCommandModePrimaryButton { get; set; } = SButton.LeftShoulder;

    public SButton ControllerCommandModeSecondaryButton { get; set; } = SButton.RightShoulder;

    public SButton ControllerConfirmButton { get; set; } = SButton.ControllerA;

    public SButton ControllerCancelButton { get; set; } = SButton.ControllerB;

    public SButton ControllerSwitchKindButton { get; set; } = SButton.ControllerX;

    public SButton ControllerSecondCornerButton { get; set; } = SButton.ControllerY;

    public SButton CraftingMenuButton { get; set; } = SButton.F7;

    public SButton PlantingMenuButton { get; set; } = SButton.F9;

}
