using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Pathfinding;
using YuiToIssho;

internal static class CompanionRegressionTests
{
    internal static void Run()
    {
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
    }
}
