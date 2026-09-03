using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace YuiToIssho;

internal sealed class CompanionDiagnosticsPanel
{
    private const int PanelWidth = 860;
    private const int PanelHeight = 680;
    private const int Padding = 24;
    private readonly IModHelper helper;
    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly TaskExecutionService execution;
    private readonly CombatCoordinator combat;
    private readonly CompanionWorkCoordinator work;
    private readonly AgentRuntimeCoordinator agents;
    private readonly CompanionMultiplayerCoordinator multiplayer;
    private readonly Func<CompanionIdentity, string, NetworkCommandResult> runNearestTest;
    private readonly List<TestButton> buttons = new();
    private readonly List<LanguageButton> languageButtons = new();
    private readonly List<SectionButton> sectionButtons = new();
    private PanelLanguage language;
    private PanelSection section = PanelSection.Overview;
    private string? lastTestResult;
    private Rectangle closeButton;
    private Rectangle panelBounds;
    private int scrollOffset;
    private int maxScrollOffset;
    private int contentClipTop;
    private int contentClipBottom;
    private bool clipContent;

    public CompanionDiagnosticsPanel(IModHelper helper, CompanionRegistry registry, CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, CombatCoordinator combat, CompanionWorkCoordinator work, AgentRuntimeCoordinator agents, CompanionMultiplayerCoordinator multiplayer, Func<CompanionIdentity, string, NetworkCommandResult> runNearestTest)
    {
        this.helper = helper;
        this.registry = registry;
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.combat = combat;
        this.work = work;
        this.agents = agents;
        this.multiplayer = multiplayer;
        this.runNearestTest = runNearestTest;
    }

    public void Attach()
    {
        this.helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        this.helper.Events.Input.MouseWheelScrolled += this.OnMouseWheelScrolled;
        this.helper.Events.Display.RenderedHud += this.OnRenderedHud;
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button == SButton.MouseLeft && this.language != PanelLanguage.Closed)
        {
            Point cursor = e.Cursor.ScreenPixels.ToPoint();
            if (this.closeButton.Contains(cursor))
            {
                this.language = PanelLanguage.Closed;
                this.helper.Input.Suppress(e.Button);
                return;
            }
            LanguageButton? languageButton = this.languageButtons.FirstOrDefault(candidate => candidate.Bounds.Contains(cursor));
            if (languageButton is not null)
            {
                this.language = languageButton.Language;
                this.helper.Input.Suppress(e.Button);
                return;
            }
            SectionButton? sectionButton = this.sectionButtons.FirstOrDefault(candidate => candidate.Bounds.Contains(cursor));
            if (sectionButton is not null)
            {
                this.section = sectionButton.Section;
                this.scrollOffset = 0;
                this.helper.Input.Suppress(e.Button);
                return;
            }
            TestButton? button = this.buttons.FirstOrDefault(candidate => candidate.Bounds.Contains(cursor));
            if (button is not null)
            {
                NetworkCommandResult result = this.runNearestTest(button.Identity, button.Action);
                this.lastTestResult = $"{result.Code}: {result.Message}";
                this.helper.Input.Suppress(e.Button);
            }
            else if (this.panelBounds.Contains(cursor))
                this.helper.Input.Suppress(e.Button);
            return;
        }
        if (e.Button != SButton.F8)
            return;
        this.language = this.language == PanelLanguage.Closed ? PanelLanguage.Chinese : PanelLanguage.Closed;
        this.scrollOffset = 0;
        this.helper.Input.Suppress(e.Button);
    }

    private void OnMouseWheelScrolled(object? sender, MouseWheelScrolledEventArgs e)
    {
        if (this.language == PanelLanguage.Closed)
            return;
        this.scrollOffset = Math.Clamp(this.scrollOffset - Math.Sign(e.Delta) * 72, 0, this.maxScrollOffset);
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        if (this.language == PanelLanguage.Closed || !Context.IsWorldReady)
            return;

        int width = Math.Min(PanelWidth, Game1.uiViewport.Width - 32);
        int height = Math.Min(PanelHeight, Game1.uiViewport.Height - 32);
        int x = (Game1.uiViewport.Width - width) / 2;
        int y = (Game1.uiViewport.Height - height) / 2;
        this.panelBounds = new Rectangle(x, y, width, height);
        Game1.drawDialogueBox(x, y, width, height, speaker: false, drawOnlyBox: true);
        Vector2 cursor = new(x + Padding, y + Padding);
        this.buttons.Clear();
        this.languageButtons.Clear();
        this.sectionButtons.Clear();
        this.clipContent = false;
        DrawLine(e.SpriteBatch, this.language == PanelLanguage.Chinese ? "Yui to Issho! 诊断与测试  [F8: 关闭]" : "Yui to Issho! Diagnostics & Tests  [F8: Close]", ref cursor, Color.Gold, 1.05f);
        this.closeButton = new Rectangle(x + width - 58, y + 22, 30, 30);
        DrawButton(e.SpriteBatch, this.closeButton, "×", Color.DarkRed, 0.9f);
        DrawLanguageButtons(e.SpriteBatch, ref cursor);
        DrawSectionButtons(e.SpriteBatch, ref cursor);
        this.contentClipTop = (int)cursor.Y + 3;
        this.contentClipBottom = y + height - 42;
        cursor.Y = this.contentClipTop - this.scrollOffset;
        this.clipContent = true;

        if (this.section == PanelSection.Tests && !string.IsNullOrWhiteSpace(this.lastTestResult))
            DrawLine(e.SpriteBatch, $"{T("最近结果", "Last result")}: {Bounded(this.lastTestResult, 106)}", ref cursor, Color.Khaki, 0.72f);

        if (!Context.IsMainPlayer)
        {
            DrawLine(e.SpriteBatch, T("客户端只持有只读身体与表现；完整责任诊断请在主机查看。", "Farmhands only hold read-only bodies and presentation; inspect full responsibility state on the host."), ref cursor, Color.OrangeRed);
            DrawLine(e.SpriteBatch, $"  Network: {this.multiplayer.DescribeSession()}", ref cursor, Color.LightSkyBlue);
            this.FinishScrollableContent(cursor.Y);
            this.DrawFooter(e.SpriteBatch);
            return;
        }

        CompanionRecord[] records = this.registry.Active.OrderBy(record => record.OwnerId).ToArray();
        if (records.Length == 0)
        {
            DrawLine(e.SpriteBatch, T("当前没有 Yui 实例。", "No Yui instances exist."), ref cursor, Color.White);
            this.FinishScrollableContent(cursor.Y);
            this.DrawFooter(e.SpriteBatch);
            return;
        }

        foreach (CompanionRecord record in records)
        {
            DrawCompanion(e.SpriteBatch, record, ref cursor);
            cursor.Y += 8;
        }
        this.FinishScrollableContent(cursor.Y);
        this.DrawFooter(e.SpriteBatch);
    }

    private void DrawCompanion(SpriteBatch batch, CompanionRecord record, ref Vector2 cursor)
    {
        CompanionIdentity identity = record.Identity;
        string bodyText;
        if (this.bodies.TryGetBody(identity, out NPC body) && body.currentLocation is not null)
            bodyText = $"{body.currentLocation.NameOrUniqueName} ({body.TilePoint.X},{body.TilePoint.Y}) dir={body.FacingDirection} speed={body.Speed}";
        else
            bodyText = T("未召唤", "not summoned");
        TaskSessionSnapshot? task = this.execution.GetSnapshot(identity);
        AppearanceActionSnapshot? action = this.appearance.GetActionSnapshot(identity);
        DeliveryRecord? delivery = record.Deliveries.FirstOrDefault(item => DeliveryPhases.OwnsEscrow(item.Phase));
        AgentRuntimeSnapshot? agent = this.agents.GetSnapshot(identity);

        DrawLine(batch, $"{record.DisplayName}  [{identity}]  mode={record.Mode}", ref cursor, Color.CornflowerBlue);
        if (this.section == PanelSection.Tests)
        {
            DrawTestButtons(batch, identity, ref cursor);
            DrawLine(batch, $"  {T("身体/站位", "Body/stand")}: {bodyText}", ref cursor, Color.White);
            DrawLine(batch, task is null
                ? $"  {T("任务目标", "Task target")}: none  transaction={record.ActiveTransactionId ?? "none"}"
                : $"  {T("任务目标", "Task target")}: {task.Value.TaskKind}/{task.Value.Phase} targets={task.Value.TargetCount} op={Bounded(task.Value.OperationId, 48)}", ref cursor, Color.White);
            CombatCommandResult combat = this.combat.Status(identity);
            DrawLine(batch, $"  {T("战斗", "Combat")}: {combat.Code} {Bounded(combat.Message, 92)}", ref cursor, Color.LightSalmon, 0.68f);
            DrawLine(batch, action is null
                ? $"  {T("动作/朝向", "Action/facing")}: none  last={this.appearance.GetLastFailure(identity) ?? "none"}"
                : $"  {T("动作/朝向", "Action/facing")}: {action.Value.Kind}/{action.Value.Phase} facing={action.Value.Facing} frame={action.Value.Frame} ticks={action.Value.RemainingTicks}", ref cursor, Color.White);
            return;
        }

        if (this.section == PanelSection.Overview)
        {
            DrawLine(batch, $"  {T("身体", "Body")}: {bodyText}", ref cursor, Color.White);
            DrawLine(batch, $"  Network: {Bounded(this.multiplayer.DescribeCompanion(identity), 112)}", ref cursor, Color.LightSkyBlue);
            DrawLine(batch, $"  {T("生命/体力", "Vitals")}: {record.Vitals.Health}/{record.Vitals.MaxHealth}  {record.Vitals.Stamina:0.#}/{record.Vitals.MaxStamina:0.#}  state={record.Vitals.State}", ref cursor, Color.White);
            DrawLine(batch, $"  {T("物品", "Items")}: bag={this.inventories.Count(identity)}/{CompanionInventoryStore.Capacity} pending={this.inventories.PendingOutputCount(identity)} escrow={this.inventories.EscrowCount(identity)} vault={this.inventories.RecoveryVaultCount(identity)} lock={this.inventories.IsBagLocked(identity)}", ref cursor, Color.White);
            DrawLine(batch, task is null
                ? $"  {T("任务", "Task")}: none  transaction={record.ActiveTransactionId ?? "none"}"
                : $"  {T("任务", "Task")}: {task.Value.TaskKind}/{task.Value.Phase} targets={task.Value.TargetCount} op={Bounded(task.Value.OperationId, 48)}", ref cursor, Color.White);
            DrawLine(batch, action is null
                ? $"  {T("表现", "Presentation")}: none  last={this.appearance.GetLastFailure(identity) ?? "none"}"
                : $"  {T("表现", "Presentation")}: {action.Value.Kind}/{action.Value.Phase} facing={action.Value.Facing} frame={action.Value.Frame} ticks={action.Value.RemainingTicks}", ref cursor, Color.White);
            return;
        }

        DrawLine(batch, $"  {T("持续工作", "Continuous work")}: {Bounded(this.work.DescribeRuntime(identity), 112)}", ref cursor, record.WorkDirective is null ? Color.White : Color.LightGreen);
        DrawLine(batch, agent is null
            ? $"  {T("代理运行时", "Agent runtime")}: none"
            : $"  {T("代理运行时", "Agent runtime")}: {agent.Value.BehaviorState}/{agent.Value.BrainPhase} snapshot={agent.Value.SnapshotVersion} plan={agent.Value.PlanGeneration} intent={agent.Value.IntentId ?? "none"} step={agent.Value.StepKind ?? "none"} cancel={Bounded(agent.Value.LastCancellationReason ?? "none", 32)}", ref cursor, agent is null ? Color.White : Color.LightSkyBlue);
        DrawLine(batch, delivery is null
            ? $"  {T("递送", "Delivery")}: none"
            : $"  {T("递送", "Delivery")}: {Bounded(delivery.DeliveryId, 32)} phase={delivery.Phase} recipient={delivery.RecipientPlayerId} last={Bounded(delivery.LastFailure ?? "none", 44)}", ref cursor, delivery is null ? Color.White : Color.Khaki);
        CraftTransactionRecord? craft = record.CraftTransaction;
        DrawLine(batch, craft is null
            ? $"  {T("制作", "Craft")}: none escrow={this.inventories.CraftEscrowCount(identity)}"
            : $"  {T("制作", "Craft")}: {Bounded(craft.RecipeKey, 32)} {craft.CompletedCount}/{craft.CraftCount} phase={craft.Phase} escrow={this.inventories.CraftEscrowCount(identity)} output={craft.OutputLocation ?? "none"} last={Bounded(craft.LastFailure ?? "none", 36)}", ref cursor, craft is null ? Color.White : Color.LightGoldenrodYellow);
        PlantingTransactionRecord? planting = record.PlantingTransaction;
        DrawLine(batch, planting is null
            ? $"  {T("播种", "Planting")}: none escrow={this.inventories.PlantEscrowCount(identity)}"
            : $"  {T("播种", "Planting")}: {planting.PlantedCount}/{planting.RequestedCount} phase={planting.Phase} escrow={this.inventories.PlantEscrowCount(identity)} step={planting.CurrentStep?.Phase ?? "none"} last={Bounded(planting.LastFailure ?? "none", 36)}", ref cursor, planting is null ? Color.White : Color.LightGreen);
        OperationReceiptRecord? receipt = record.RecentOperations.LastOrDefault();
        DrawLine(batch, $"  {T("责任", "Responsibility")}: storage={record.StorageLiabilities.Count} pending={record.PendingResponsibilities.Count} blocked={record.StorageWriteBlocked}  {T("最近回执", "Last receipt")}={receipt?.Code ?? "none"}", ref cursor, record.StorageWriteBlocked ? Color.OrangeRed : Color.White);
    }

    private void DrawTestButtons(SpriteBatch batch, CompanionIdentity identity, ref Vector2 cursor)
    {
        (string Action, string Zh, string En)[] definitions =
        {
            ("mow", "最近割草", "Nearest grass"),
            ("dig", "最近锄地", "Nearest till"),
            ("chop", "最近砍树", "Nearest tree"),
            ("mine", "最近挖石", "Nearest stone"),
            ("water", "最近浇水", "Nearest water"),
            ("harvest", "最近收获", "Nearest harvest"),
            ("forage", "最近采集", "Nearest forage"),
            ("fish", "最近钓鱼", "Nearest fishing"),
            ("pet", "最近抚摸", "Nearest pet"),
            ("milk", "最近挤奶", "Nearest milk"),
            ("shear", "最近剪毛", "Nearest shear"),
            ("fight", "最近战斗", "Nearest fight"),
            ("plant-preview", "最近播种位预览", "Nearest plant slot"),
        };
        int buttonWidth = 160;
        int buttonHeight = 30;
        int columns = 4;
        for (int index = 0; index < definitions.Length; index++)
        {
            int column = index % columns;
            int row = index / columns;
            Rectangle bounds = new((int)cursor.X + column * (buttonWidth + 8), (int)cursor.Y + row * (buttonHeight + 5), buttonWidth, buttonHeight);
            if (bounds.Bottom >= this.contentClipTop && bounds.Top <= this.contentClipBottom)
            {
                string label = this.language == PanelLanguage.Chinese ? definitions[index].Zh : definitions[index].En;
                DrawButton(batch, bounds, label, Color.DarkSlateGray, 0.72f);
                this.buttons.Add(new TestButton(bounds, identity, definitions[index].Action));
            }
        }
        cursor.Y += ((definitions.Length + columns - 1) / columns) * (buttonHeight + 5);
    }

    private void DrawLanguageButtons(SpriteBatch batch, ref Vector2 cursor)
    {
        (PanelLanguage Language, string Label)[] definitions =
        {
            (PanelLanguage.Chinese, "中文"),
            (PanelLanguage.English, "English"),
        };
        const int buttonWidth = 96;
        const int buttonHeight = 26;
        for (int index = 0; index < definitions.Length; index++)
        {
            Rectangle bounds = new((int)cursor.X + index * (buttonWidth + 8), (int)cursor.Y, buttonWidth, buttonHeight);
            Color color = this.language == definitions[index].Language ? Color.SteelBlue : Color.DarkSlateGray;
            batch.Draw(Game1.staminaRect, bounds, color * 0.92f);
            Vector2 size = Game1.smallFont.MeasureString(definitions[index].Label) * 0.68f;
            Utility.drawTextWithShadow(batch, definitions[index].Label, Game1.smallFont, new Vector2(bounds.Center.X - size.X / 2f, bounds.Center.Y - size.Y / 2f), Color.White, 0.68f);
            this.languageButtons.Add(new LanguageButton(bounds, definitions[index].Language));
        }
        cursor.Y += buttonHeight + 7;
    }

    private void DrawSectionButtons(SpriteBatch batch, ref Vector2 cursor)
    {
        (PanelSection Section, string Zh, string En)[] definitions =
        {
            (PanelSection.Overview, "概览", "Overview"),
            (PanelSection.Tests, "工作测试", "Work tests"),
            (PanelSection.Responsibility, "运行与责任", "Runtime & custody"),
        };
        const int width = 180;
        const int height = 30;
        for (int index = 0; index < definitions.Length; index++)
        {
            Rectangle bounds = new((int)cursor.X + index * (width + 8), (int)cursor.Y, width, height);
            string label = this.language == PanelLanguage.Chinese ? definitions[index].Zh : definitions[index].En;
            DrawButton(batch, bounds, label, this.section == definitions[index].Section ? Color.SteelBlue : Color.DarkSlateGray, 0.72f);
            this.sectionButtons.Add(new SectionButton(bounds, definitions[index].Section));
        }
        cursor.Y += height + 9;
    }

    private void FinishScrollableContent(float contentBottom)
    {
        int visibleHeight = Math.Max(1, this.contentClipBottom - this.contentClipTop);
        int totalHeight = Math.Max(0, (int)contentBottom + this.scrollOffset - this.contentClipTop);
        this.maxScrollOffset = Math.Max(0, totalHeight - visibleHeight);
        this.scrollOffset = Math.Clamp(this.scrollOffset, 0, this.maxScrollOffset);
        this.clipContent = false;
    }

    private void DrawFooter(SpriteBatch batch)
    {
        string text = this.maxScrollOffset > 0
            ? $"{T("滚轮滚动", "Mouse wheel to scroll")}  {this.scrollOffset}/{this.maxScrollOffset}"
            : T("状态只读；测试按钮通过权威任务入口执行", "Status is read-only; test buttons use authoritative task entry points");
        Vector2 position = new(this.panelBounds.X + Padding, this.panelBounds.Bottom - 38);
        Utility.drawTextWithShadow(batch, text, Game1.smallFont, position, Color.SlateGray, 0.64f);
    }

    private static void DrawButton(SpriteBatch batch, Rectangle bounds, string label, Color color, float scale)
    {
        batch.Draw(Game1.staminaRect, bounds, color * 0.92f);
        Vector2 size = Game1.smallFont.MeasureString(label) * scale;
        Utility.drawTextWithShadow(batch, label, Game1.smallFont, new Vector2(bounds.Center.X - size.X / 2f, bounds.Center.Y - size.Y / 2f), Color.White, scale);
    }

    private string T(string chinese, string english) => this.language == PanelLanguage.Chinese ? chinese : english;

    private static string Bounded(string value, int maximum) => value.Length <= maximum ? value : value[..Math.Max(1, maximum - 1)] + "…";

    private void DrawLine(SpriteBatch batch, string text, ref Vector2 cursor, Color color, float scale = 0.82f)
    {
        int lineHeight = (int)(Game1.smallFont.LineSpacing * scale) + 3;
        if (!this.clipContent || cursor.Y + lineHeight >= this.contentClipTop && cursor.Y <= this.contentClipBottom)
            Utility.drawTextWithShadow(batch, text, Game1.smallFont, cursor, color, scale);
        cursor.Y += lineHeight;
    }

    private enum PanelLanguage
    {
        Closed,
        Chinese,
        English,
    }

    private enum PanelSection
    {
        Overview,
        Tests,
        Responsibility,
    }

    private sealed record TestButton(Rectangle Bounds, CompanionIdentity Identity, string Action);

    private sealed record LanguageButton(Rectangle Bounds, PanelLanguage Language);

    private sealed record SectionButton(Rectangle Bounds, PanelSection Section);
}
