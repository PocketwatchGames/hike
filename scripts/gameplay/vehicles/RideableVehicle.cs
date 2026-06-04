using Godot;
using Godot.Collections;

// Base for player-rideable vehicles (Boat now; Horse and other mounts later).
// Bundles the shared plumbing — the IInteractive "Board" flow, the seat anchor
// the rider parents under, the rider reference its subclass physics reads for
// steering, and the seated-animation wiring — so a concrete vehicle only has
// to implement its own _PhysicsProcess locomotion and GetDismountPosition.
//
// Boarding rides the normal interactive system: GetActions stashes the
// interacting Player (mirrors ClimbableTree._climber), and the authored Board
// InteractiveAction's OpenInteractive completion event calls Complete(), which
// mounts the stashed rider. The vehicle never collides with its own rider —
// the boat's CollisionMask excludes the Player layer and the rider stops
// running MoveAndSlide while mounted (see Player.Mount).
[GlobalClass]
public partial class RideableVehicle : CharacterBody3D, IInteractive, IWorldEntity, IRideable
{
    [Export] protected Node3D _seatAnchor;
    [Export] protected Node3D _hudNode;

    // Drives the vehicle's visual the same way Player/Mob do: camera-relative
    // 8-direction yaw faceting + stepped (quantized) animation playback, so the
    // hull reads like the game's pixel-art sprites. The body (this CharacterBody3D)
    // keeps its smooth physics yaw; only the model child it points at is faceted.
    // Optional — a vehicle without a model visual just leaves it null.
    [Export] protected ModelAnimator _modelAnimator;

    // Authored interactions — typically a single Board entry whose
    // completionEvents fire [OpenInteractive] to mount the rider.
    [Export] protected Array<InteractiveAction> _actions = new();

    // Stashed from GetActions (which receives the Player) so Complete — which
    // only gets the action index — can mount the right actor. Mirrors
    // ClimbableTree._climber.
    protected Player _pendingRider;

    // The rider currently aboard, or null when empty. Subclass physics reads
    // _rider.MountMoveInput for steering intent.
    protected Player _rider;

    protected World _world;

    // Vehicles float / move through the world; keep them porous so they never
    // register as permanent path blockers or stop smell / sound / sight.
    public bool Porous => true;

    public Vector3 hudPosition => _hudNode != null ? _hudNode.GlobalPosition : GlobalPosition;

    public Node3D SeatAnchor => _seatAnchor;

    // Concrete vehicles override with their authored data so the base can read
    // the shared animation slots.
    protected virtual RideableData RideData => null;

    public EAnimation IdleAnim => RideData?.idleAnim ?? EAnimation.Idle;
    public EAnimation MoveAnim => RideData?.moveAnim ?? EAnimation.Run;

    // Subclasses override with their propulsion test (default: never).
    public virtual bool IsPropelling => false;

    public override void _Ready()
    {
        // ModelAnimator defaults to inactive (visual hidden, _Process off) until
        // its owner picks it as the live visual — Player/Mob do this in their own
        // _Ready. A vehicle always shows its model, so activate it here. This
        // turns on the faceting / quantization passes for the hull.
        _modelAnimator?.SetActive(true);
    }

    public virtual void OnSpawned(World world)
    {
        _world = world;
    }

    public bool CanInteract()
    {
        return _rider == null;
    }

    public bool CanActorInteract(Player player)
    {
        return player != null && _rider == null && !player.IsMounted;
    }

    public Array<InteractiveAction> GetActions(Player player)
    {
        if (!CanActorInteract(player))
        {
            return null;
        }
        _pendingRider = player;
        return _actions != null && _actions.Count > 0 ? _actions : null;
    }

    public void Complete(int actionIndex)
    {
        // Single Board verb today; branch on _actions[actionIndex].verb when a
        // vehicle grows secondary interactions (e.g. a horse's Feed).
        _pendingRider?.Mount(this);
    }

    public virtual void OnMounted(Player rider)
    {
        _rider = rider;
    }

    public virtual void OnDismounted(Player rider)
    {
        if (_rider == rider)
        {
            _rider = null;
        }
    }

    // Default: drop the rider where the vehicle stands. Boat overrides with a
    // nearest-shore search so the player lands on dry ground.
    public virtual Vector3 GetDismountPosition()
    {
        return GlobalPosition;
    }

    public override void _ExitTree()
    {
        // If we're being freed while a rider is still aboard — typically our
        // origin chunk evicting after the player paddled far from where the
        // boat spawned — hand the rider back to the world first so freeing the
        // vehicle doesn't free the parented player along with it. The rider is
        // physically located where we are, so its own chunk normally keeps us
        // resident; this is the safety net for the edge case.
        if (_rider != null)
        {
            Player rider = _rider;
            _rider = null;
            rider.ForceDismount(_world, GlobalPosition);
        }
    }
}
