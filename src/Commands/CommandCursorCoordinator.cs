using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace YuiToIssho;

internal sealed class CommandCursorCoordinator
{
    private const int TerminalMarkerTicks = 240;
    private const int StatusPollTicks = 60;
    private static readonly string[] Kinds = { WorkKinds.Mow, WorkKinds.Water, WorkKinds.Harvest, WorkKinds.Till, WorkKinds.Chop, WorkKinds.Mine, WorkKinds.Forage, WorkKinds.Pet, WorkKinds.Milk, WorkKinds.Shear };
    private readonly IModHelper helper;
    private readonly CompanionCommands commands;
    private readonly CompanionRegistry registry;
    private readonly TaskExecutionService taskExecution;
    private readonly CompanionWorkCoordinator work;
    private readonly CompanionProjectionCoordinator projection;
    private readonly CompanionMultiplayerCoordinator multiplayer;
    private readonly ModConfig config;
    private readonly Func<LifecycleState> getLifecycle;
    private readonly Func<bool> canMutate;
    private readonly PerScreen<CursorState> state = new(() => new CursorState());
    private readonly Dictionary<string, CursorMarker> markersByRequestId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CursorMarker> markersByOperationId = new(StringComparer.Ordinal);

    public CommandCursorCoordinator(
        IModHelper helper,
        CompanionCommands commands,
        CompanionRegistry registry,
        TaskExecutionService taskExecution,
        CompanionWorkCoordinator work,
        CompanionProjectionCoordinator projection,
        CompanionMultiplayerCoordinator multiplayer,
        ModConfig config,
        Func<LifecycleState> getLifecycle,
        Func<bool> canMutate)
    {
        this.helper = helper;
        this.commands = commands;
        this.registry = registry;
        this.taskExecution = taskExecution;
        this.work = work;
        this.projection = projection;
        this.multiplayer = multiplayer;
        this.config = config;
        this.getLifecycle = getLifecycle;
        this.canMutate = canMutate;
    }

    public void Attach()
    {
        this.helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        this.helper.Events.Input.ButtonReleased += this.OnButtonReleased;
        this.helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        this.helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        this.helper.Events.Display.RenderedHud += this.OnRenderedHud;
        this.helper.Events.GameLoop.ReturnedToTitle += (_, _) => this.Clear();
        this.helper.Events.GameLoop.Saving += (_, _) => this.Clear();
        this.helper.Events.Player.Warped += (_, _) => this.Clear();
        this.multiplayer.AttachReceiptObserver(this.OnReceipt);
    }

    private bool IsActive => Context.IsWorldReady
        && this.getLifecycle() == LifecycleState.SaveReady
        && (!Context.IsMainPlayer || this.canMutate())
        && Game1.activeClickableMenu is null
        && Context.IsPlayerFree
        && (this.helper.Input.IsDown(this.config.CommandModeButton) || this.state.Value.ControllerChordDown);

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        CursorState state = this.state.Value;
        if (e.Button == this.config.ControllerCommandModePrimaryButton)
            state.ControllerPrimaryDown = true;
        if (e.Button == this.config.ControllerCommandModeSecondaryButton)
            state.ControllerSecondaryDown = true;

        if (!this.IsActive)
            return;
        if (state.ControllerChordDown
            && (e.Button == this.config.ControllerCommandModePrimaryButton || e.Button == this.config.ControllerCommandModeSecondaryButton))
        {
            this.helper.Input.Suppress(this.config.ControllerCommandModePrimaryButton);
            this.helper.Input.Suppress(this.config.ControllerCommandModeSecondaryButton);
            state.UseControllerCursor = true;
            return;
        }
        if (e.Button == this.config.ControllerSecondCornerButton)
        {
            state.ControllerSecondCornerDown = true;
            this.helper.Input.Suppress(e.Button);
            return;
        }
        if (e.Button == SButton.Q || e.Button == this.config.ControllerSwitchKindButton)
        {
            state.KindIndex = (state.KindIndex + 1) % Kinds.Length;
            this.helper.Input.Suppress(e.Button);
            return;
        }

        if (e.Button == this.config.ControllerCancelButton)
        {
            this.ClearSelection(state);
            state.UseControllerCursor = true;
            this.helper.Input.Suppress(e.Button);
            return;
        }

        Point direction = e.Button switch
        {
            SButton.DPadUp => new Point(0, -1),
            SButton.DPadRight => new Point(1, 0),
            SButton.DPadDown => new Point(0, 1),
            SButton.DPadLeft => new Point(-1, 0),
            _ => Point.Zero,
        };
        if (direction != Point.Zero)
        {
            Point current = state.ControllerTile ?? Game1.player.TilePoint;
            Point next = new(current.X + direction.X, current.Y + direction.Y);
            if (Game1.currentLocation.isTileOnMap(new Vector2(next.X, next.Y)))
                state.ControllerTile = next;
            state.UseControllerCursor = true;
            this.helper.Input.Suppress(e.Button);
            return;
        }

        Point tile;
        bool controllerConfirm = e.Button == this.config.ControllerConfirmButton;
        if (controllerConfirm)
        {
            tile = state.ControllerTile ?? Game1.player.TilePoint;
            state.ControllerTile = tile;
            state.UseControllerCursor = true;
        }
        else if (e.Button == SButton.MouseLeft && TryGetWorldTile(e.Cursor.ScreenPixels, out tile))
            state.UseControllerCursor = false;
        else
            return;

        bool selectingArea = this.helper.Input.IsDown(this.config.SecondCornerModifierButton)
            || state.ControllerSecondCornerDown;
        this.SelectTile(state, tile, selectingArea);
        this.helper.Input.Suppress(e.Button);
    }

    private void OnButtonReleased(object? sender, ButtonReleasedEventArgs e)
    {
        CursorState state = this.state.Value;
        if (e.Button == this.config.ControllerCommandModePrimaryButton)
            state.ControllerPrimaryDown = false;
        if (e.Button == this.config.ControllerCommandModeSecondaryButton)
            state.ControllerSecondaryDown = false;
        if (e.Button == this.config.ControllerSecondCornerButton)
            state.ControllerSecondCornerDown = false;
    }

    private void OnReceipt(CommandReceiptObservation receipt)
    {
        if (receipt.Command is "cursor-single" or "work-start")
        {
            if (this.markersByRequestId.TryGetValue(receipt.RequestId, out CursorMarker? marker)
                && marker.Identity == receipt.Identity)
                this.ApplySubmissionResult(marker, receipt.Result, receipt.SnapshotVersion);
            return;
        }
        if (receipt.Command == "operation-status"
            && receipt.Fields.TryGetValue("operationId", out string? operationId)
            && this.markersByOperationId.TryGetValue(operationId, out CursorMarker? operationMarker)
            && operationMarker.Identity == receipt.Identity)
        {
            operationMarker.StatusQueryPending = false;
            operationMarker.NextStatusQueryTick = Game1.ticks + StatusPollTicks;
            this.ApplyOperationStatus(operationMarker, receipt.Result.IsSuccess, receipt.Result.Code);
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        CursorMarker? marker = this.state.Value.Marker;
        if (marker is null || marker.IsTerminal || !marker.ReceiptAccepted)
            return;
        if (Context.IsMainPlayer)
            this.RefreshHostMarker(marker);
        else
            this.RefreshClientMarker(marker);
    }

    private void RefreshHostMarker(CursorMarker marker)
    {
        if (marker.IsSingle)
        {
            TaskSessionSnapshot? session = this.taskExecution.GetSnapshot(marker.Identity);
            if (session is TaskSessionSnapshot active && active.OperationId == marker.OperationId)
            {
                ApplyTaskPhase(marker, active.Phase);
                return;
            }
            TaskExecutionResult result = this.taskExecution.GetOperationStatus(marker.Identity, marker.OperationId);
            this.ApplyOperationStatus(marker, result.IsSuccess, result.Code);
            return;
        }

        if (!this.registry.TryGet(marker.Identity, out CompanionRecord record)
            || record.WorkDirective is not WorkDirectiveRecord directive
            || !Matches(marker, directive))
        {
            this.SetTerminal(marker, CursorMarkerPhases.Completed, "WORK-ENDED");
            return;
        }
        WorkRuntimeSnapshot? snapshot = this.work.GetSnapshot(marker.Identity);
        if (snapshot is not WorkRuntimeSnapshot workSnapshot)
            return;
        string phase = workSnapshot.Phase;
        if (workSnapshot.CurrentOperationId is string operationId
            && this.taskExecution.GetSnapshot(marker.Identity) is TaskSessionSnapshot task
            && task.OperationId == operationId)
            phase = task.Phase == TaskSessionPhase.Settling.ToString() ? CursorMarkerPhases.Executing : CursorMarkerPhases.Navigating;
        this.ApplyWorkPhase(marker, phase, workSnapshot.LastReason);
    }

    private void RefreshClientMarker(CursorMarker marker)
    {
        if (!this.projection.TryGetProjectedState(marker.Identity, out CompanionSnapshotDto projected))
            return;
        if (!marker.IsSingle)
        {
            if (Matches(marker, projected))
            {
                string phase = projected.WorkPhase;
                if (!string.IsNullOrEmpty(projected.WorkOperationId))
                    phase = projected.Presentation?.OperationId == projected.WorkOperationId
                        ? CursorMarkerPhases.Executing
                        : CursorMarkerPhases.Navigating;
                this.ApplyWorkPhase(marker, phase, projected.WorkLastReason);
            }
            else if (this.projection.SnapshotVersion > marker.ReceiptSnapshotVersion)
            {
                this.SetTerminal(marker, CursorMarkerPhases.Completed, "WORK-ENDED");
            }
            return;
        }

        if (projected.ActiveTransactionId == marker.OperationId)
            marker.Phase = projected.Presentation?.OperationId == marker.OperationId
                ? CursorMarkerPhases.Executing
                : CursorMarkerPhases.Navigating;
        if (Game1.ticks >= marker.NextStatusQueryTick)
        {
            NetworkCommandResult query = this.commands.SubmitOperationStatus(marker.Identity, marker.OperationId);
            marker.NextStatusQueryTick = Game1.ticks + StatusPollTicks;
            marker.StatusQueryPending = query.Code == "REQUEST-SENT";
            if (!query.IsSuccess && query.Code != "REQUEST-QUEUE-FULL")
            {
                marker.Phase = CursorMarkerPhases.Paused;
                marker.Summary = query.Code;
            }
        }
    }

    private void ApplySubmissionResult(CursorMarker marker, NetworkCommandResult result, ulong snapshotVersion)
    {
        marker.Summary = result.Code;
        if (result.Code == "REQUEST-SENT")
        {
            marker.Phase = CursorMarkerPhases.Pending;
            return;
        }
        if (!result.IsSuccess)
        {
            this.SetTerminal(marker, CursorMarkerPhases.Failed, result.Code);
            return;
        }
        marker.ReceiptAccepted = true;
        marker.ReceiptSnapshotVersion = snapshotVersion;
        marker.Phase = CursorMarkerPhases.Accepted;
        marker.ExpiresAt = int.MaxValue;
        marker.NextStatusQueryTick = Game1.ticks + StatusPollTicks;
    }

    private void ApplyOperationStatus(CursorMarker marker, bool success, string code)
    {
        if (code is "OPERATION-ACTIVE" or "OPERATION-RECONCILING")
        {
            if (marker.Phase is CursorMarkerPhases.Accepted or CursorMarkerPhases.Pending or CursorMarkerPhases.Paused)
                marker.Phase = CursorMarkerPhases.Navigating;
            marker.Summary = code;
            return;
        }
        this.SetTerminal(marker, success ? CursorMarkerPhases.Completed : CursorMarkerPhases.Failed, code);
    }

    private static void ApplyTaskPhase(CursorMarker marker, string phase)
    {
        marker.Phase = phase switch
        {
            nameof(TaskSessionPhase.Settling) => CursorMarkerPhases.Executing,
            nameof(TaskSessionPhase.Traveling) => CursorMarkerPhases.Navigating,
            _ => CursorMarkerPhases.Accepted,
        };
        marker.Summary = phase;
    }

    private void ApplyWorkPhase(CursorMarker marker, string phase, string? reason)
    {
        marker.Phase = phase switch
        {
            CursorMarkerPhases.Navigating => CursorMarkerPhases.Navigating,
            CursorMarkerPhases.Executing => CursorMarkerPhases.Executing,
            WorkRuntimePhases.Paused or WorkRuntimePhases.Blocked => CursorMarkerPhases.Paused,
            WorkRuntimePhases.Faulted => CursorMarkerPhases.Failed,
            _ => CursorMarkerPhases.Accepted,
        };
        marker.Summary = string.IsNullOrWhiteSpace(reason) ? phase : reason;
        if (marker.Phase == CursorMarkerPhases.Failed)
            marker.ExpiresAt = Game1.ticks + TerminalMarkerTicks;
    }

    private void SetTerminal(CursorMarker marker, string phase, string summary)
    {
        marker.Phase = phase;
        marker.Summary = summary;
        marker.StatusQueryPending = false;
        marker.ExpiresAt = Game1.ticks + TerminalMarkerTicks;
    }

    private static bool Matches(CursorMarker marker, WorkDirectiveRecord directive) =>
        directive.LocationKey == marker.LocationKey
        && directive.Kind == marker.Kind
        && directive.Shape == marker.Shape
        && directive.AnchorX == marker.First.X
        && directive.AnchorY == marker.First.Y
        && directive.EndX == marker.Second.X
        && directive.EndY == marker.Second.Y;

    private static bool Matches(CursorMarker marker, CompanionSnapshotDto state) =>
        state.WorkLocationKey == marker.LocationKey
        && state.WorkKind == marker.Kind
        && state.WorkShape == marker.Shape
        && state.WorkAnchorX == marker.First.X
        && state.WorkAnchorY == marker.First.Y
        && state.WorkEndX == marker.Second.X
        && state.WorkEndY == marker.Second.Y;

    private void SelectTile(CursorState state, Point tile, bool selectingArea)
    {
        string locationKey = Game1.currentLocation.NameOrUniqueName;
        CompanionIdentity identity = CompanionIdentity.ForOwner(Game1.player.UniqueMultiplayerID);
        if (!selectingArea)
        {
            state.FirstCorner = null;
            state.FirstLocationKey = null;
            var single = new WorkScopeRequest(locationKey, tile.X, tile.Y, WorkScopeShapes.SingleTarget, 0, Kinds[state.KindIndex], WorkCompletionPolicies.Single)
            {
                EndX = tile.X,
                EndY = tile.Y,
            };
            NetworkCommandResult singleResult = this.commands.SubmitCursorScope(identity, single);
            var marker = new CursorMarker(identity, locationKey, tile, tile, Kinds[state.KindIndex], WorkScopeShapes.SingleTarget, singleResult.RequestId)
            {
                OperationId = string.IsNullOrEmpty(singleResult.RequestId) ? string.Empty : $"cursor-{singleResult.RequestId}",
            };
            this.ApplySubmissionResult(marker, singleResult, 0);
            this.SetMarker(state, marker);
            return;
        }
        if (state.FirstCorner is not Point start || state.FirstLocationKey != locationKey)
        {
            state.FirstCorner = tile;
            state.FirstLocationKey = locationKey;
            return;
        }
        if (!WorkScopeContracts.IsRectangleWithinLimit(start.X, start.Y, tile.X, tile.Y))
        {
            this.SetMarker(state, CursorMarker.LocalFailure(identity, locationKey, start, tile, Kinds[state.KindIndex], $"MAX-{WorkScopeContracts.MaximumRectangleWidth}x{WorkScopeContracts.MaximumRectangleHeight}", Game1.ticks + TerminalMarkerTicks));
            return;
        }

        var scope = new WorkScopeRequest(locationKey, start.X, start.Y, WorkScopeShapes.Rectangle, 0, Kinds[state.KindIndex], WorkCompletionPolicies.UntilClear)
        {
            EndX = tile.X,
            EndY = tile.Y,
        };
        NetworkCommandResult result = this.commands.SubmitCursorScope(identity, scope);
        var areaMarker = new CursorMarker(identity, locationKey, start, tile, Kinds[state.KindIndex], WorkScopeShapes.Rectangle, result.RequestId)
        {
            OperationId = result.RequestId,
        };
        this.ApplySubmissionResult(areaMarker, result, 0);
        this.SetMarker(state, areaMarker);
        state.FirstCorner = null;
        state.FirstLocationKey = null;
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        CursorState state = this.state.Value;
        if (state.Marker is not null && Game1.ticks >= state.Marker.ExpiresAt)
            this.SetMarker(state, null);
        if (state.Marker is not null && state.Marker.LocationKey == Game1.currentLocation.NameOrUniqueName)
        {
            Color markerColor = MarkerColor(state.Marker.Phase);
            DrawRectangle(e.SpriteBatch, state.Marker.First, state.Marker.Second, markerColor);
            DrawLabel(e.SpriteBatch, state.Marker.First, $"{state.Marker.Kind} {state.Marker.Phase} · {state.Marker.Summary}", markerColor);
        }
        if (!this.IsActive)
            return;

        Point hover;
        if (state.UseControllerCursor)
        {
            hover = state.ControllerTile ?? Game1.player.TilePoint;
            state.ControllerTile = hover;
        }
        else if (!TryGetWorldTile(this.helper.Input.GetCursorPosition().ScreenPixels, out hover))
            return;
        Point start = state.FirstCorner ?? hover;
        bool withinLimit = state.FirstCorner is null || WorkScopeContracts.IsRectangleWithinLimit(start.X, start.Y, hover.X, hover.Y);
        Color previewColor = withinLimit ? state.FirstCorner is null ? Color.White : Color.Cyan : Color.IndianRed;
        DrawRectangle(e.SpriteBatch, start, hover, previewColor);
        string label = state.FirstCorner is null
            ? state.UseControllerCursor ? "A 单点 · Y+A 开始框选 / A: single · Y+A: area" : "点击单点 · Shift+点击开始框选 / Click: single · Shift+click: area"
            : withinLimit
                ? state.UseControllerCursor ? "Y+A 确定第二角 / Y+A: second corner" : "Shift+点击确定第二角 / Shift+click second corner"
                : $"最多 {WorkScopeContracts.MaximumRectangleWidth}x{WorkScopeContracts.MaximumRectangleHeight} 格 / Max {WorkScopeContracts.MaximumRectangleWidth}x{WorkScopeContracts.MaximumRectangleHeight}";
        DrawLabel(e.SpriteBatch, hover, label, previewColor);
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        if (this.IsActive)
            this.DrawControls(e.SpriteBatch);
    }

    private void DrawControls(SpriteBatch batch)
    {
        int x = 24;
        int y = 24;
        Rectangle kindBounds = new(x, y, 360, 28);
        batch.Draw(Game1.staminaRect, kindBounds, Color.Black * 0.78f);
        CursorState state = this.state.Value;
        string controls = state.UseControllerCursor ? "[LB+RB 模式 · X 切换 · A 单点 · Y+A 框选 · B 取消]" : "[Q 切换 · 点击单点 · Shift+点击框选]";
        batch.DrawString(Game1.smallFont, $"Yui · {KindLabel(Kinds[state.KindIndex])}  {controls}", new Vector2(kindBounds.X + 6, kindBounds.Y + 4), Color.White, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 1f);
        if (Kinds[state.KindIndex] == WorkKinds.Mow && state.FirstCorner is not null)
        {
            var noteBounds = new Rectangle(x, y + 31, 620, 24);
            batch.Draw(Game1.staminaRect, noteBounds, Color.Black * 0.72f);
            batch.DrawString(Game1.smallFont, "选区决定主动目标；原版镰刀可能触及边缘外侧 / Scope selects targets; vanilla swing may cross its edge", new Vector2(noteBounds.X + 6, noteBounds.Y + 3), Color.Wheat, 0f, Vector2.Zero, 0.52f, SpriteEffects.None, 1f);
        }
    }

    private static string KindLabel(string kind) => kind switch
    {
        WorkKinds.Mow => "割草 / Mow",
        WorkKinds.Water => "浇水 / Water",
        WorkKinds.Harvest => "收获 / Harvest",
        WorkKinds.Till => "锄地 / Till",
        WorkKinds.Chop => "砍树 / Chop",
        WorkKinds.Mine => "采矿 / Mine",
        WorkKinds.Forage => "采集 / Forage",
        WorkKinds.Pet => "抚摸 / Pet",
        WorkKinds.Milk => "挤奶 / Milk",
        WorkKinds.Shear => "剪毛 / Shear",
        _ => kind,
    };

    private void Clear()
    {
        CursorState state = this.state.Value;
        this.UnregisterMarker(state.Marker);
        state.Reset();
    }

    private void ClearSelection(CursorState state)
    {
        this.UnregisterMarker(state.Marker);
        state.ClearSelection();
    }

    private void SetMarker(CursorState state, CursorMarker? marker)
    {
        this.UnregisterMarker(state.Marker);
        state.Marker = marker;
        if (marker is null)
            return;
        if (!string.IsNullOrEmpty(marker.RequestId))
            this.markersByRequestId[marker.RequestId] = marker;
        if (!string.IsNullOrEmpty(marker.OperationId))
            this.markersByOperationId[marker.OperationId] = marker;
    }

    private void UnregisterMarker(CursorMarker? marker)
    {
        if (marker is null)
            return;
        if (!string.IsNullOrEmpty(marker.RequestId)
            && this.markersByRequestId.GetValueOrDefault(marker.RequestId) == marker)
            this.markersByRequestId.Remove(marker.RequestId);
        if (!string.IsNullOrEmpty(marker.OperationId)
            && this.markersByOperationId.GetValueOrDefault(marker.OperationId) == marker)
            this.markersByOperationId.Remove(marker.OperationId);
    }

    private static Color MarkerColor(string phase) => phase switch
    {
        CursorMarkerPhases.Pending => Color.Gold,
        CursorMarkerPhases.Accepted => Color.DodgerBlue,
        CursorMarkerPhases.Navigating => Color.Gold,
        CursorMarkerPhases.Executing => Color.LimeGreen,
        CursorMarkerPhases.Paused => Color.Orange,
        CursorMarkerPhases.Completed => Color.Gray,
        _ => Color.IndianRed,
    };

    private static bool TryGetWorldTile(Vector2 screen, out Point tile)
    {
        if (screen.X < 0 || screen.Y < 0 || screen.X >= Game1.viewport.Width || screen.Y >= Game1.viewport.Height)
        {
            tile = default;
            return false;
        }
        tile = new Point((Game1.viewport.X + (int)screen.X) / Game1.tileSize, (Game1.viewport.Y + (int)screen.Y) / Game1.tileSize);
        return true;
    }

    private static void DrawRectangle(SpriteBatch batch, Point first, Point second, Color color)
    {
        int left = Math.Min(first.X, second.X);
        int right = Math.Max(first.X, second.X);
        int top = Math.Min(first.Y, second.Y);
        int bottom = Math.Max(first.Y, second.Y);
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            if (x != left && x != right && y != top && y != bottom) continue;
            Vector2 local = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * Game1.tileSize, y * Game1.tileSize));
            Rectangle bounds = new((int)local.X, (int)local.Y, Game1.tileSize, Game1.tileSize);
            batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, 2), color * 0.8f);
            batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - 2, bounds.Width, 2), color * 0.8f);
            batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, 2, bounds.Height), color * 0.8f);
            batch.Draw(Game1.staminaRect, new Rectangle(bounds.Right - 2, bounds.Y, 2, bounds.Height), color * 0.8f);
        }
    }

    private static void DrawLabel(SpriteBatch batch, Point tile, string text, Color color)
    {
        Vector2 local = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * Game1.tileSize, tile.Y * Game1.tileSize - 28));
        batch.DrawString(Game1.smallFont, text, local, color, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 1f);
    }

    private static class CursorMarkerPhases
    {
        public const string Pending = "Pending";
        public const string Accepted = "Accepted";
        public const string Navigating = "Navigating";
        public const string Executing = "Executing";
        public const string Paused = "Paused";
        public const string Failed = "Failed";
        public const string Completed = "Completed";
    }

    private sealed class CursorMarker
    {
        public CursorMarker(CompanionIdentity identity, string locationKey, Point first, Point second, string kind, string shape, string requestId)
        {
            this.Identity = identity;
            this.LocationKey = locationKey;
            this.First = first;
            this.Second = second;
            this.Kind = kind;
            this.Shape = shape;
            this.RequestId = requestId;
        }

        public CompanionIdentity Identity { get; }
        public string LocationKey { get; }
        public Point First { get; }
        public Point Second { get; }
        public string Kind { get; }
        public string Shape { get; }
        public string RequestId { get; }
        public string OperationId { get; init; } = string.Empty;
        public string Phase { get; set; } = CursorMarkerPhases.Pending;
        public string Summary { get; set; } = "REQUEST-PENDING";
        public bool ReceiptAccepted { get; set; }
        public ulong ReceiptSnapshotVersion { get; set; }
        public bool StatusQueryPending { get; set; }
        public int NextStatusQueryTick { get; set; }
        public int ExpiresAt { get; set; } = int.MaxValue;

        public bool IsSingle => this.Shape == WorkScopeShapes.SingleTarget;
        public bool IsTerminal => this.Phase is CursorMarkerPhases.Failed or CursorMarkerPhases.Completed;

        public static CursorMarker LocalFailure(CompanionIdentity identity, string locationKey, Point first, Point second, string kind, string summary, int expiresAt) => new(identity, locationKey, first, second, kind, WorkScopeShapes.SingleTarget, string.Empty)
        {
            Phase = CursorMarkerPhases.Failed,
            Summary = summary,
            ExpiresAt = expiresAt,
        };
    }

    private sealed class CursorState
    {
        public int KindIndex { get; set; }
        public Point? FirstCorner { get; set; }
        public string? FirstLocationKey { get; set; }
        public Point? ControllerTile { get; set; }
        public bool UseControllerCursor { get; set; }
        public CursorMarker? Marker { get; set; }
        public bool ControllerPrimaryDown { get; set; }
        public bool ControllerSecondaryDown { get; set; }
        public bool ControllerSecondCornerDown { get; set; }
        public bool ControllerChordDown => this.ControllerPrimaryDown && this.ControllerSecondaryDown;

        public void ClearSelection()
        {
            this.FirstCorner = null;
            this.FirstLocationKey = null;
            this.Marker = null;
        }

        public void Reset()
        {
            this.ClearSelection();
            this.ControllerTile = null;
            this.UseControllerCursor = false;
            this.ControllerPrimaryDown = false;
            this.ControllerSecondaryDown = false;
            this.ControllerSecondCornerDown = false;
        }
    }
}
