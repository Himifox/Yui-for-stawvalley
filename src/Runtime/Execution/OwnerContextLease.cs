using Microsoft.Xna.Framework;
using StardewValley;

namespace YuiToIssho;

/// <summary>Temporarily projects a real Farmer into the context required by a vanilla action.</summary>
/// <remarks>
/// The lease never changes inventory ownership. Callers remain responsible for any temporary item-slot
/// substitution and must nest that substitution inside this lease's lifetime.
/// </remarks>
internal sealed class OwnerContextLease : IDisposable
{
    private readonly Farmer owner;
    private readonly Vector2 originalPosition;
    private readonly GameLocation? originalLocation;
    private readonly int originalFacing;
    private readonly int originalToolIndex;
    private readonly float originalStamina;
    private readonly int originalToolPower;
    private readonly bool originalUsingTool;
    private readonly bool originalCanMove;
    private readonly int originalAnimationIndex;
    private readonly int originalSingleAnimation;
    private readonly bool originalPauseForSingleAnimation;
    private bool disposed;

    private OwnerContextLease(Farmer owner)
    {
        this.owner = owner;
        this.originalPosition = owner.Position;
        this.originalLocation = owner.currentLocation;
        this.originalFacing = owner.FacingDirection;
        this.originalToolIndex = owner.CurrentToolIndex;
        this.originalStamina = owner.Stamina;
        this.originalToolPower = owner.toolPower.Value;
        this.originalUsingTool = owner.UsingTool;
        this.originalCanMove = owner.CanMove;
        this.originalAnimationIndex = owner.FarmerSprite.currentAnimationIndex;
        this.originalSingleAnimation = owner.FarmerSprite.currentSingleAnimation;
        this.originalPauseForSingleAnimation = owner.FarmerSprite.PauseForSingleAnimation;
    }

    public static OwnerContextLease Project(Farmer owner, Vector2 position, int facingDirection, GameLocation? location = null)
    {
        var lease = new OwnerContextLease(owner);
        if (location is not null)
            owner.currentLocation = location;
        owner.Position = position;
        owner.faceDirection(facingDirection);
        return lease;
    }

    public static bool CanProject(Farmer owner) => owner.CanMove && !owner.UsingTool;

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.owner.currentLocation = this.originalLocation;
        this.owner.Position = this.originalPosition;
        this.owner.faceDirection(this.originalFacing);
        this.owner.CurrentToolIndex = this.originalToolIndex;
        this.owner.Stamina = this.originalStamina;
        this.owner.toolPower.Value = this.originalToolPower;
        if (this.originalCanMove && !this.originalUsingTool)
        {
            // Vanilla tools such as MilkPail and Shears attach delayed completion callbacks to the
            // Farmer animation. The projected action has already settled synchronously; discard its
            // animation queue before restoring the real player's free-control state.
            this.owner.FarmerSprite.StopAnimation();
            this.owner.forceCanMove();
        }
        this.owner.UsingTool = this.originalUsingTool;
        this.owner.CanMove = this.originalCanMove;
        this.owner.FarmerSprite.currentAnimationIndex = this.originalAnimationIndex;
        this.owner.FarmerSprite.currentSingleAnimation = this.originalSingleAnimation;
        this.owner.FarmerSprite.PauseForSingleAnimation = this.originalPauseForSingleAnimation;
    }
}
