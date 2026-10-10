using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Xna.Framework;
using Netcode;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.Network;
using YuiToIssho;

internal static class CompanionRegressionTests
{
    internal static void Run()
    {
        // NetPosition.Get reads Game1.HostPaused through the network world root.
        // A default world supplies that flag without loading game content.
        Game1.netWorldState ??= new NetRoot<NetWorldState>(new NetWorldState());

        // Run the real pause path without a game session. Controller objects are
        // identity tokens only; none of their content-dependent methods execute.
        var identity = CompanionIdentity.ForOwner(123);
        var record = new CompanionRecord { OwnerId = identity.OwnerId, Slot = identity.Slot };
        var bodies = new CompanionBodyBinder(null!);
        var follow = new FollowCoordinator(bodies, new TaskNavigationService(bodies), _ => false, null!);
        // Character.Halt stops its sprite animation; an unnamed default sprite
        // supplies that state without loading a texture or graphics device.
        var body = new NPC { Sprite = new AnimatedSprite() };
        var bound = (IDictionary)typeof(CompanionBodyBinder).GetField("boundBodies", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bodies)!;
        bound.Add(identity, body);
        var runtime = (IDictionary)typeof(FollowCoordinator).GetField("runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(follow)!;
        Type stateType = typeof(FollowCoordinator).GetNestedType("FollowRuntime", BindingFlags.NonPublic)!;
        PathFindController NewController() => (PathFindController)FormatterServices.GetUninitializedObject(typeof(PathFindController));
        void Track(PathFindController controller)
        {
            object state = Activator.CreateInstance(stateType, Vector2.Zero, 0UL)!;
            stateType.GetProperty("Controller")!.SetValue(state, controller);
            runtime[identity] = state;
        }
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        var owned = NewController();
        var external = NewController();
        Track(owned);
        body.controller = external;
        follow.PauseAll(new[] { record });
        Check(ReferenceEquals(body.controller, external), "Follow pause must preserve another coordinator's replacement path.");
        Check(runtime.Count == 0, "Yielding must discard stale follow ownership.");

        body.controller = external;
        follow.PauseAll(new[] { record });
        Check(ReferenceEquals(body.controller, external), "Follow pause without a runtime must preserve external movement.");

        Track(owned);
        body.controller = owned;
        follow.PauseAll(new[] { record });
        Check(body.controller is null, "Follow pause must stop its own active path.");
        Check(runtime.Count == 0, "Stopping the owned path must release follow state.");

        Track(owned);
        body.controller = null;
        follow.PauseAll(new[] { record });
        Check(runtime.Count == 0, "A completed follow path must release ownership safely.");

        follow = new FollowCoordinator(bodies, new TaskNavigationService(bodies), _ => true, null!);
        runtime = (IDictionary)typeof(FollowCoordinator).GetField("runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(follow)!;
        Track(owned);
        body.controller = external;
        typeof(FollowCoordinator).GetMethod("UpdateOne", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(follow, new object[] { record, 6UL });
        Check(ReferenceEquals(body.controller, external), "Presentation/seat gating must preserve the path issued earlier in the update.");

        Track(owned);
        record.ActiveTransactionId = "active-task";
        body.controller = external;
        typeof(FollowCoordinator).GetMethod("UpdateOne", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(follow, new object[] { record, 12UL });
        Check(ReferenceEquals(body.controller, external), "An active task must retain its path when follow yields.");

        var navigation = new TaskNavigationService(bodies);
        var taskState = new TaskNavigationState(body.Position, 0) { FailedAttempts = 2 };
        // Repeated busy/free transitions must stop movement without turning the
        // deliberately cancelled controller into a failed path on resume.
        for (ulong tick = 100; tick < 200; tick++)
        {
            body.controller = NewController();
            navigation.MarkPathIssued(taskState, body.Position, tick - 1, 30);
            navigation.Pause(identity, taskState, tick);
            Check(body.controller is null, "A task lifecycle pause must stop NPC movement.");
            TaskNavigationResult resumed = navigation.Observe(identity, body, taskState, tick, 300, 5, 30);
            Check(!resumed.BudgetExhausted && resumed.CanIssuePath, "A lifecycle pause must allow navigation to resume without a retry failure.");
            Check(taskState.FailedAttempts == 2, "Repeated pauses must preserve the existing failure budget.");
        }
        navigation.MarkPathIssued(taskState, body.Position, 200, 30);
        body.controller = null;
        TaskNavigationResult failedPath = navigation.Observe(identity, body, taskState, 201, 300, 5, 30);
        Check(taskState.FailedAttempts == 3 && !failedPath.CanIssuePath, "A genuinely lost path must still count as a failure and apply retry backoff.");

        int issuedAttempts = 2;
        for (int pause = 0; pause < 100; pause++)
        {
            // Storage/crafting/planting charge each newly issued approach path.
            issuedAttempts++;
            body.controller = NewController();
            issuedAttempts = TaskNavigationService.PauseIssuedPath(body, issuedAttempts);
            Check(body.controller is null && issuedAttempts == 2, "Interrupting an issued path must refund exactly that attempt and stop movement.");
            issuedAttempts = TaskNavigationService.PauseIssuedPath(body, issuedAttempts);
            Check(issuedAttempts == 2, "Additional busy ticks without a live controller must not refund earlier failures.");
        }
        Check(TaskNavigationService.PauseIssuedPath(body, 0) == 0, "Pausing without a live path cannot make an issue budget negative.");


    }
}
