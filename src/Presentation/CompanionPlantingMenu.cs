using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace YuiToIssho;

internal sealed class CompanionPlantingMenuCoordinator
{
    private readonly IModHelper helper;
    private readonly CompanionRegistry registry;
    private readonly CompanionCommands commands;
    private readonly ModConfig config;
    private readonly Func<LifecycleState> getLifecycle;
    private readonly Func<bool> canMutate;

    public CompanionPlantingMenuCoordinator(IModHelper helper, CompanionRegistry registry, CompanionCommands commands, ModConfig config, Func<LifecycleState> getLifecycle, Func<bool> canMutate)
    {
        this.helper = helper;
        this.registry = registry;
        this.commands = commands;
        this.config = config;
        this.getLifecycle = getLifecycle;
        this.canMutate = canMutate;
    }

    public void Attach() => this.helper.Events.Input.ButtonPressed += this.OnButtonPressed;

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button != this.config.PlantingMenuButton || !Context.IsWorldReady || this.getLifecycle() != LifecycleState.SaveReady)
            return;
        if (Game1.activeClickableMenu is CompanionPlantingMenu)
        {
            Game1.exitActiveMenu();
            this.helper.Input.Suppress(e.Button);
            return;
        }
        if (Game1.activeClickableMenu is not null || !Context.IsPlayerFree)
            return;
        if (!Context.IsMainPlayer)
        {
            Game1.addHUDMessage(new HUDMessage("F9 播种菜单当前由主机打开；农场成员可使用 yui plant 多人命令。", HUDMessage.error_type));
            this.helper.Input.Suppress(e.Button);
            return;
        }
        CompanionIdentity identity = CompanionIdentity.ForOwner(Game1.player.UniqueMultiplayerID);
        if (!this.canMutate() || !this.registry.TryGet(identity, out _))
            return;
        Game1.activeClickableMenu = new CompanionPlantingMenu(identity, this.commands, this.config.PlantingMenuButton);
        this.helper.Input.Suppress(e.Button);
    }
}

internal sealed class CompanionPlantingMenu : IClickableMenu
{
    private const int RowsPerPage = 7;
    private readonly CompanionIdentity identity;
    private readonly CompanionCommands commands;
    private readonly SButton menuButton;
    private readonly List<(Rectangle Bounds, int Index)> optionButtons = new();
    private IReadOnlyList<PlantSeedOptionDto> options = Array.Empty<PlantSeedOptionDto>();
    private int selectedIndex = -1;
    private int page;
    private int count = 1;
    private int radius = WorkScopeContracts.DefaultRadius;
    private string receipt = "正在读取真实种子 / Reading real seeds";
    private Rectangle previousPage;
    private Rectangle nextPage;
    private Rectangle countMinus;
    private Rectangle countPlus;
    private Rectangle radiusMinus;
    private Rectangle radiusPlus;
    private Rectangle refresh;
    private Rectangle preview;
    private Rectangle start;
    private Rectangle resume;
    private Rectangle cancel;

    public CompanionPlantingMenu(CompanionIdentity identity, CompanionCommands commands, SButton menuButton)
        : base((Game1.uiViewport.Width - 980) / 2, (Game1.uiViewport.Height - 690) / 2, 980, 690, showUpperRightCloseButton: true)
    {
        this.identity = identity;
        this.commands = commands;
        this.menuButton = menuButton;
        this.RefreshOptions();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        foreach ((Rectangle bounds, int index) in this.optionButtons)
        {
            if (!bounds.Contains(x, y))
                continue;
            this.selectedIndex = index;
            this.count = Math.Min(this.count, Math.Max(1, this.options[index].AvailableCount));
            Game1.playSound("smallSelect");
            return;
        }
        if (this.previousPage.Contains(x, y) && this.page > 0) { this.page--; this.selectedIndex = this.page * RowsPerPage; Game1.playSound("shwip"); return; }
        if (this.nextPage.Contains(x, y) && (this.page + 1) * RowsPerPage < this.options.Count) { this.page++; this.selectedIndex = this.page * RowsPerPage; Game1.playSound("shwip"); return; }
        if (this.countMinus.Contains(x, y) && this.count > 1) { this.count--; Game1.playSound("smallSelect"); return; }
        if (this.countPlus.Contains(x, y) && this.count < this.MaximumSelectableCount()) { this.count++; Game1.playSound("smallSelect"); return; }
        if (this.radiusMinus.Contains(x, y) && this.radius > WorkScopeContracts.MinimumRadius) { this.radius--; Game1.playSound("smallSelect"); return; }
        if (this.radiusPlus.Contains(x, y) && this.radius < WorkScopeContracts.MaximumRadius) { this.radius++; Game1.playSound("smallSelect"); return; }
        if (this.refresh.Contains(x, y)) { this.RefreshOptions(); return; }
        if (this.preview.Contains(x, y) && this.TrySelected(out PlantSeedOptionDto? selected))
        {
            this.Apply(this.commands.SubmitPlantPreview(this.identity, selected.SeedOptionId, this.count, this.radius), updateOptions: false);
            return;
        }
        if (this.start.Contains(x, y) && this.TrySelected(out selected))
        {
            this.Apply(this.commands.SubmitPlantStart(this.identity, selected.SeedOptionId, this.count, this.radius), updateOptions: false);
            return;
        }
        if (this.resume.Contains(x, y)) { this.Apply(this.commands.SubmitPlantControl(this.identity, "resume"), false); return; }
        if (this.cancel.Contains(x, y)) { this.Apply(this.commands.SubmitPlantControl(this.identity, "cancel"), false); return; }
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape || key.ToString().Equals(this.menuButton.ToString(), StringComparison.OrdinalIgnoreCase))
            this.exitThisMenu();
        else
            base.receiveKeyPress(key);
    }

    public override void draw(SpriteBatch b)
    {
        Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, false, true);
        this.optionButtons.Clear();
        int left = this.xPositionOnScreen + 40;
        int top = this.yPositionOnScreen + 60;
        Utility.drawTextWithShadow(b, $"Yui 播种 / Planting   [{this.menuButton}]", Game1.dialogueFont, new Vector2(left, this.yPositionOnScreen + 20), Color.DarkGreen);
        for (int row = 0; row < RowsPerPage; row++)
        {
            int index = this.page * RowsPerPage + row;
            if (index >= this.options.Count)
                break;
            PlantSeedOptionDto option = this.options[index];
            Rectangle bounds = new(left, top + row * 59, 440, 50);
            b.Draw(Game1.staminaRect, bounds, index == this.selectedIndex ? Color.ForestGreen * 0.86f : Color.DarkSlateGray * 0.76f);
            string text = $"{option.CropDisplayName}  ← {option.SeedDisplayName}  ×{option.AvailableCount}\n{option.ReasonCode}  {option.ExpiresInSeconds}s";
            Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(bounds.X + 9, bounds.Y + 5), option.PlantableHere ? Color.White : Color.LightSalmon, 0.67f);
            this.optionButtons.Add((bounds, index));
        }

        int right = this.xPositionOnScreen + 520;
        this.countMinus = new Rectangle(right, top + 50, 46, 40);
        this.countPlus = new Rectangle(right + 190, top + 50, 46, 40);
        this.radiusMinus = new Rectangle(right, top + 115, 46, 40);
        this.radiusPlus = new Rectangle(right + 190, top + 115, 46, 40);
        DrawButton(b, this.countMinus, "−", this.count > 1);
        DrawButton(b, this.countPlus, "+", this.count < this.MaximumSelectableCount());
        DrawButton(b, this.radiusMinus, "−", this.radius > WorkScopeContracts.MinimumRadius);
        DrawButton(b, this.radiusPlus, "+", this.radius < WorkScopeContracts.MaximumRadius);
        Utility.drawTextWithShadow(b, $"数量 / Count: {this.count}", Game1.smallFont, new Vector2(right + 52, top + 60), Color.DarkSlateGray, 0.74f);
        Utility.drawTextWithShadow(b, $"半径 / Radius: {this.radius}", Game1.smallFont, new Vector2(right + 52, top + 125), Color.DarkSlateGray, 0.74f);

        this.refresh = new Rectangle(right + 255, top + 50, 155, 40);
        this.preview = new Rectangle(right + 255, top + 115, 155, 40);
        this.start = new Rectangle(right, top + 190, 195, 46);
        this.resume = new Rectangle(right + 215, top + 190, 195, 46);
        this.cancel = new Rectangle(right, top + 252, 410, 46);
        DrawButton(b, this.refresh, "刷新 / Refresh", true);
        DrawButton(b, this.preview, "预览 / Preview", this.selectedIndex >= 0);
        DrawButton(b, this.start, "开始 / Start", this.selectedIndex >= 0);
        DrawButton(b, this.resume, "恢复 / Resume", true);
        DrawButton(b, this.cancel, "取消并返还 / Cancel", true);
        DrawWrapped(b, this.DescribeSelection(), new Vector2(right, top + 320), 410, Color.DarkSlateGray);
        DrawWrapped(b, this.receipt, new Vector2(right, top + 420), 410, Color.SaddleBrown);

        this.previousPage = new Rectangle(left, this.yPositionOnScreen + 540, 150, 42);
        this.nextPage = new Rectangle(left + 290, this.yPositionOnScreen + 540, 150, 42);
        DrawButton(b, this.previousPage, "上一页 / Prev", this.page > 0);
        DrawButton(b, this.nextPage, "下一页 / Next", (this.page + 1) * RowsPerPage < this.options.Count);
        base.draw(b);
        this.drawMouse(b);
    }

    private void RefreshOptions() => this.Apply(this.commands.SubmitPlantOptions(this.identity), updateOptions: true);

    private void Apply(NetworkCommandResult result, bool updateOptions)
    {
        this.receipt = $"{result.Code}: {result.Message}";
        if (updateOptions && result.Planting?.Options is { } returned)
        {
            this.options = returned;
            this.page = 0;
            this.selectedIndex = returned.Count == 0 ? -1 : 0;
            this.count = Math.Min(this.count, this.MaximumSelectableCount());
        }
        if (result.Planting?.Preview is PlantingPreviewDto previewResult)
            this.receipt = $"{result.Code}: {previewResult.CropDisplayName} {previewResult.RequestedCount}，种子 {previewResult.AvailableSeedCount}，可播地块 {previewResult.MatchingSlotCount}";
        Game1.playSound(result.IsSuccess ? "smallSelect" : "cancel");
    }

    private bool TrySelected(out PlantSeedOptionDto selected)
    {
        if (this.selectedIndex >= 0 && this.selectedIndex < this.options.Count)
        {
            selected = this.options[this.selectedIndex];
            return true;
        }
        selected = null!;
        return false;
    }

    private int MaximumSelectableCount() => this.selectedIndex >= 0 && this.selectedIndex < this.options.Count
        ? Math.Clamp(this.options[this.selectedIndex].AvailableCount, 1, PlantingConstants.MaximumCount)
        : 1;

    private string DescribeSelection() => this.TrySelected(out PlantSeedOptionDto? selected)
        ? $"{selected.SeedDisplayName} → {selected.CropDisplayName}\n{selected.AvailableCount} 粒真实库存；选项剩余 {selected.ExpiresInSeconds} 秒。\n开始时主机重新验证种子、范围和空地。"
        : "没有符合策略的真实种子。\nNo real seed passes the planting policy.";

    private static void DrawButton(SpriteBatch batch, Rectangle bounds, string label, bool enabled)
    {
        batch.Draw(Game1.staminaRect, bounds, (enabled ? Color.DarkSlateGray : Color.Gray) * 0.82f);
        Vector2 size = Game1.smallFont.MeasureString(label) * 0.72f;
        Utility.drawTextWithShadow(batch, label, Game1.smallFont, new Vector2(bounds.Center.X - size.X / 2f, bounds.Center.Y - size.Y / 2f), enabled ? Color.White : Color.LightGray, 0.72f);
    }

    private static void DrawWrapped(SpriteBatch batch, string text, Vector2 position, int width, Color color) =>
        Utility.drawTextWithShadow(batch, Game1.parseText(text, Game1.smallFont, width), Game1.smallFont, position, color, 0.68f);
}
