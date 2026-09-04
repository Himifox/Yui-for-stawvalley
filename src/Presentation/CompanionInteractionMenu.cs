using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace YuiToIssho;

internal sealed class CompanionInteractionMenu : IClickableMenu
{
    private const int MenuWidth = 520;
    private const int MenuHeight = 430;
    private readonly string displayName;
    private readonly string giftSummary;
    private readonly Action<string> execute;
    private readonly List<InteractionOption> options;
    private int selectedIndex;

    public CompanionInteractionMenu(string displayName, bool canGift, string giftSummary, Action<string> execute)
        : base((Game1.uiViewport.Width - MenuWidth) / 2, (Game1.uiViewport.Height - MenuHeight) / 2, MenuWidth, MenuHeight, showUpperRightCloseButton: true)
    {
        this.displayName = displayName;
        this.giftSummary = giftSummary;
        this.execute = execute;
        this.options =
        [
            new("talk", "交谈 / Talk", true),
            new("hug", "拥抱 / Hug", true),
            new("gift", "送礼 / Gift", canGift),
            new("sit", "坐下 / Sit together", true),
            new("stand", "起身 / Stand", true),
        ];
        this.selectedIndex = 0;
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        for (int index = 0; index < this.options.Count; index++)
        {
            if (this.GetOptionBounds(index).Contains(x, y))
            {
                this.selectedIndex = index;
                this.ActivateSelected();
                return;
            }
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        switch (key)
        {
            case Keys.Up:
            case Keys.W:
                this.MoveSelection(-1);
                return;
            case Keys.Down:
            case Keys.S:
                this.MoveSelection(1);
                return;
            case Keys.Enter:
            case Keys.Space:
                this.ActivateSelected();
                return;
            case Keys.Escape:
                this.exitThisMenu();
                return;
            default:
                base.receiveKeyPress(key);
                return;
        }
    }

    public override void receiveGamePadButton(Buttons button)
    {
        switch (button)
        {
            case Buttons.DPadUp:
            case Buttons.LeftThumbstickUp:
                this.MoveSelection(-1);
                return;
            case Buttons.DPadDown:
            case Buttons.LeftThumbstickDown:
                this.MoveSelection(1);
                return;
            case Buttons.A:
                this.ActivateSelected();
                return;
            case Buttons.B:
                this.exitThisMenu();
                return;
            default:
                base.receiveGamePadButton(button);
                return;
        }
    }

    public override void draw(SpriteBatch batch)
    {
        this.drawBackground(batch);
        Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, false, true);
        Utility.drawTextWithShadow(batch, $"和 {this.displayName} 一起 / With {this.displayName}", Game1.dialogueFont, new Vector2(this.xPositionOnScreen + 42, this.yPositionOnScreen + 24), Color.DarkSlateBlue);
        for (int index = 0; index < this.options.Count; index++)
        {
            InteractionOption option = this.options[index];
            Rectangle bounds = this.GetOptionBounds(index);
            Color fill = !option.Enabled ? Color.Gray * 0.55f : index == this.selectedIndex ? Color.SteelBlue * 0.9f : Color.DarkSlateGray * 0.76f;
            batch.Draw(Game1.staminaRect, bounds, fill);
            Utility.drawTextWithShadow(batch, option.Label, Game1.smallFont, new Vector2(bounds.X + 18, bounds.Y + 10), option.Enabled ? Color.White : Color.LightGray, 0.78f);
        }
        string hint = this.options[2].Enabled
            ? $"当前礼物 / Held gift: {this.giftSummary}"
            : "手持可赠送物品后再选择送礼 / Hold a giftable item first";
        Utility.drawTextWithShadow(batch, Game1.parseText(hint, Game1.smallFont, this.width - 84), Game1.smallFont, new Vector2(this.xPositionOnScreen + 42, this.yPositionOnScreen + 352), Color.SaddleBrown, 0.68f);
        base.draw(batch);
        this.drawMouse(batch);
    }

    private Rectangle GetOptionBounds(int index) => new(this.xPositionOnScreen + 42, this.yPositionOnScreen + 82 + index * 52, this.width - 84, 43);

    private void MoveSelection(int direction)
    {
        int next = this.selectedIndex;
        do
            next = (next + direction + this.options.Count) % this.options.Count;
        while (!this.options[next].Enabled && next != this.selectedIndex);
        this.selectedIndex = next;
        Game1.playSound("shiny4");
    }

    private void ActivateSelected()
    {
        InteractionOption selected = this.options[this.selectedIndex];
        if (!selected.Enabled)
        {
            Game1.playSound("cancel");
            return;
        }
        Game1.playSound("smallSelect");
        this.execute(selected.Command);
    }

    private sealed record InteractionOption(string Command, string Label, bool Enabled);
}
