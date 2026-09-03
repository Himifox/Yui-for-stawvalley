using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace YuiToIssho;

internal sealed class CompanionCraftingMenuCoordinator
{
    private readonly IModHelper helper;
    private readonly CompanionRegistry registry;
    private readonly CompanionCommands commands;
    private readonly ModConfig config;
    private readonly Func<LifecycleState> getLifecycle;
    private readonly Func<bool> canMutate;
    private readonly CraftingRecipePolicy policy = new();

    public CompanionCraftingMenuCoordinator(IModHelper helper, CompanionRegistry registry, CompanionCommands commands, ModConfig config, Func<LifecycleState> getLifecycle, Func<bool> canMutate)
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
        if (e.Button != this.config.CraftingMenuButton || !Context.IsWorldReady || this.getLifecycle() != LifecycleState.SaveReady)
            return;
        if (Game1.activeClickableMenu is CompanionCraftingMenu)
        {
            Game1.exitActiveMenu();
            this.helper.Input.Suppress(e.Button);
            return;
        }
        if (Game1.activeClickableMenu is not null || !Context.IsPlayerFree || (Context.IsMainPlayer && !this.canMutate()))
            return;
        CompanionIdentity identity = CompanionIdentity.ForOwner(Game1.player.UniqueMultiplayerID);
        if (Context.IsMainPlayer && !this.registry.TryGet(identity, out _))
            return;
        Game1.activeClickableMenu = new CompanionCraftingMenu(identity, Game1.player, this.policy.ListAvailable(Game1.player), this.policy, this.commands);
        this.helper.Input.Suppress(e.Button);
    }
}

internal sealed class CompanionCraftingMenu : IClickableMenu
{
    private const int RowsPerPage = 8;
    private readonly CompanionIdentity identity;
    private readonly Farmer owner;
    private readonly IReadOnlyList<string> recipes;
    private readonly CraftingRecipePolicy policy;
    private readonly CompanionCommands commands;
    private readonly List<(Rectangle Bounds, int Index)> recipeButtons = new();
    private int selectedIndex;
    private int page;
    private int craftCount = 1;
    private string lastReceipt = "选择配方 / Select a recipe";
    private Rectangle previousPage;
    private Rectangle nextPage;
    private Rectangle minus;
    private Rectangle plus;
    private Rectangle start;
    private Rectangle cancel;

    public CompanionCraftingMenu(CompanionIdentity identity, Farmer owner, IReadOnlyList<string> recipes, CraftingRecipePolicy policy, CompanionCommands commands)
        : base((Game1.uiViewport.Width - 900) / 2, (Game1.uiViewport.Height - 640) / 2, 900, 640, showUpperRightCloseButton: true)
    {
        this.identity = identity;
        this.owner = owner;
        this.recipes = recipes;
        this.policy = policy;
        this.commands = commands;
        this.selectedIndex = recipes.Count == 0 ? -1 : 0;
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        foreach ((Rectangle bounds, int index) in this.recipeButtons)
        {
            if (!bounds.Contains(x, y)) continue;
            this.selectedIndex = index;
            Game1.playSound("smallSelect");
            return;
        }
        if (this.previousPage.Contains(x, y) && this.page > 0) { this.page--; this.selectedIndex = this.page * RowsPerPage; Game1.playSound("shwip"); return; }
        if (this.nextPage.Contains(x, y) && (this.page + 1) * RowsPerPage < this.recipes.Count) { this.page++; this.selectedIndex = this.page * RowsPerPage; Game1.playSound("shwip"); return; }
        if (this.minus.Contains(x, y) && this.craftCount > 1) { this.craftCount--; Game1.playSound("smallSelect"); return; }
        if (this.plus.Contains(x, y) && this.craftCount < 25) { this.craftCount++; Game1.playSound("smallSelect"); return; }
        if (this.start.Contains(x, y) && this.selectedIndex >= 0)
        {
            NetworkCommandResult result = this.commands.SubmitCraftStart(this.identity, this.recipes[this.selectedIndex], this.craftCount);
            this.lastReceipt = $"{result.Code}: {result.Message}";
            Game1.playSound(result.IsSuccess ? "coin" : "cancel");
            return;
        }
        if (this.cancel.Contains(x, y))
        {
            NetworkCommandResult result = this.commands.SubmitCraftCancel(this.identity);
            this.lastReceipt = $"{result.Code}: {result.Message}";
            Game1.playSound(result.IsSuccess ? "smallSelect" : "cancel");
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape || key == Keys.F7)
            this.exitThisMenu();
        else
            base.receiveKeyPress(key);
    }

    public override void draw(SpriteBatch b)
    {
        Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, false, true);
        this.recipeButtons.Clear();
        int left = this.xPositionOnScreen + 40;
        int top = this.yPositionOnScreen + 58;
        Utility.drawTextWithShadow(b, "Yui 制作 / Crafting   [F7]", Game1.dialogueFont, new Vector2(left, this.yPositionOnScreen + 20), Color.DarkSlateBlue);
        for (int row = 0; row < RowsPerPage; row++)
        {
            int index = this.page * RowsPerPage + row;
            if (index >= this.recipes.Count) break;
            Rectangle bounds = new(left, top + row * 55, 360, 46);
            b.Draw(Game1.staminaRect, bounds, index == this.selectedIndex ? Color.SteelBlue * 0.85f : Color.DarkSlateGray * 0.75f);
            Utility.drawTextWithShadow(b, this.recipes[index], Game1.smallFont, new Vector2(bounds.X + 10, bounds.Y + 10), Color.White, 0.78f);
            this.recipeButtons.Add((bounds, index));
        }

        int right = this.xPositionOnScreen + 440;
        DrawWrapped(b, this.selectedIndex < 0 ? "没有已学会且允许的配方。\nNo learned recipe is allowed." : this.DescribeRecipe(this.recipes[this.selectedIndex]), new Vector2(right, top), 400, Color.DarkSlateGray);
        this.minus = new Rectangle(right, this.yPositionOnScreen + 370, 48, 42);
        this.plus = new Rectangle(right + 150, this.yPositionOnScreen + 370, 48, 42);
        DrawButton(b, this.minus, "−", this.craftCount > 1);
        DrawButton(b, this.plus, "+", this.craftCount < 25);
        Utility.drawTextWithShadow(b, $"数量 / Count: {this.craftCount}", Game1.smallFont, new Vector2(right + 54, this.yPositionOnScreen + 381), Color.DarkSlateGray, 0.75f);
        this.start = new Rectangle(right, this.yPositionOnScreen + 430, 185, 48);
        this.cancel = new Rectangle(right + 205, this.yPositionOnScreen + 430, 185, 48);
        DrawButton(b, this.start, "开始 / Start", this.selectedIndex >= 0);
        DrawButton(b, this.cancel, "取消事务 / Cancel", true);
        DrawWrapped(b, this.lastReceipt, new Vector2(right, this.yPositionOnScreen + 495), 400, Color.SaddleBrown);

        this.previousPage = new Rectangle(left, this.yPositionOnScreen + 520, 130, 42);
        this.nextPage = new Rectangle(left + 230, this.yPositionOnScreen + 520, 130, 42);
        DrawButton(b, this.previousPage, "上一页 / Prev", this.page > 0);
        DrawButton(b, this.nextPage, "下一页 / Next", (this.page + 1) * RowsPerPage < this.recipes.Count);
        base.draw(b);
        this.drawMouse(b);
    }

    private string DescribeRecipe(string key)
    {
        CraftRecipeResolution resolved = this.policy.TryResolve(this.owner, key);
        if (!resolved.IsSuccess || resolved.Recipe is null)
            return $"{resolved.Code}\n{resolved.Message}";
        CraftRecipeDescriptor recipe = resolved.Recipe;
        string ingredients = string.Join("\n", recipe.Ingredients.Select(item => $"• {item.IngredientId} × {item.RequiredPerCraft * this.craftCount}"));
        return $"{key}\n\n材料 / Materials\n{ingredients}\n\n产出 / Output\n{recipe.OutputQualifiedItemId} × {recipe.OutputPerCraft * this.craftCount}\n\n材料与容量由 Host 开始时重新验证。\nHost revalidates materials and capacity.";
    }

    private static void DrawButton(SpriteBatch batch, Rectangle bounds, string label, bool enabled)
    {
        batch.Draw(Game1.staminaRect, bounds, (enabled ? Color.DarkSlateGray : Color.Gray) * 0.8f);
        Vector2 size = Game1.smallFont.MeasureString(label) * 0.75f;
        Utility.drawTextWithShadow(batch, label, Game1.smallFont, new Vector2(bounds.Center.X - size.X / 2f, bounds.Center.Y - size.Y / 2f), enabled ? Color.White : Color.LightGray, 0.75f);
    }

    private static void DrawWrapped(SpriteBatch batch, string text, Vector2 position, int width, Color color)
    {
        Utility.drawTextWithShadow(batch, Game1.parseText(text, Game1.smallFont, width), Game1.smallFont, position, color, 0.72f);
    }
}
