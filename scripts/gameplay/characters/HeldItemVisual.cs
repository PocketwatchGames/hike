using Godot;

// Renders the player's currently-wielded item as a rigid 3D prop attached to a
// hand bone of the skinned character. Two independent channels:
//
//   - Weapon channel (persistent): the last-used weapon. It pops into the hand
//     when a weapon action fires and STAYS there between swings; using the other
//     weapon swaps it. Set via SetWeapon, which also picks which hand (left or
//     right) the model attaches to per the weapon's EHand.
//   - Item channel (transient): a consumable (potion / scroll) shown only for
//     the duration of its Use action, then cleared back to the weapon. Set via
//     SetActiveItem. Always shown in the right (main) hand.
//
// Each item's grip alignment is baked into its own model scene (offset the mesh
// so the scene origin sits in the palm), so this component never authors a
// per-item transform — it just instances the scene under the socket holder.
//
// The hand sockets are BoneAttachment3Ds built once against the imported
// character Skeleton3D (one per wrist joint). The rig is an instanced FBX with
// no hand-authored nodes to [Export] against, so the Skeleton3D is found by
// walking the `visual` subtree — the same pattern ModelAnimator uses for its
// material / mesh-hide passes. Socket construction is deferred out of _Ready
// (CallDeferred) so it runs after the scene-instantiation AddChild storm
// settles, matching the MovingLight lifecycle convention.
[GlobalClass]
public partial class HeldItemVisual : Node3D
{
	// Root of the imported character subtree to search for the Skeleton3D.
	// Wire this to the same node ModelAnimator drives as `visual` (PlayerModel).
	[Export] public Node3D visual;

	// Bones the hand sockets bind to. Defaults match the shared polysplit rig's
	// wrist joints; override per rig if a different skeleton is used.
	[Export] public StringName boneName = "R_wrist_joint";
	[Export] public StringName leftBoneName = "L_wrist_joint";

	private Node3D _weaponHolderRight;
	private Node3D _weaponHolderLeft;
	private Node3D _itemHolder;

	// Desired scenes are latched even before the sockets exist so a SetWeapon
	// that races the deferred build is applied once BuildSockets runs.
	private PackedScene _weaponScene;
	private EHand _weaponHand = EHand.Right;
	private PackedScene _itemScene;
	private Node3D _weaponInstance;
	private Node3D _itemInstance;
	private bool _weaponConcealed;

	// The weapon holder for the hand currently selected. Null until built.
	private Node3D ActiveWeaponHolder => _weaponHand == EHand.Left ? _weaponHolderLeft : _weaponHolderRight;

	public override void _Ready()
	{
		CallDeferred(MethodName.BuildSockets);
	}

	private void BuildSockets()
	{
		Skeleton3D skeleton = FindSkeleton(visual);
		if (skeleton == null)
		{
			GD.PushError($"HeldItemVisual '{Name}': no Skeleton3D found under `visual`; held-item models disabled.");
			return;
		}
		_weaponHolderRight = BuildHandSocket(skeleton, boneName, "HandSocketRight", "WeaponHolderRight");
		_weaponHolderLeft = BuildHandSocket(skeleton, leftBoneName, "HandSocketLeft", "WeaponHolderLeft");
		_weaponHolderRight.Visible = !_weaponConcealed;
		_weaponHolderLeft.Visible = !_weaponConcealed;
		// The transient consumable always rides the right hand.
		_itemHolder = new Node3D { Name = "ItemHolder" };
		_weaponHolderRight.GetParent().AddChild(_itemHolder);
		// Apply anything latched before the sockets existed.
		ApplyWeapon();
		ApplyItem();
	}

	// Builds a BoneAttachment3D for one wrist and returns its weapon holder.
	private static Node3D BuildHandSocket(Skeleton3D skeleton, StringName bone, string socketName, string holderName)
	{
		var socket = new BoneAttachment3D { Name = socketName, BoneName = bone.ToString() };
		skeleton.AddChild(socket);
		var holder = new Node3D { Name = holderName };
		socket.AddChild(holder);
		return holder;
	}

	// Sets the persistent weapon model and the hand it attaches to. No-op when
	// both are unchanged so the per-press call site can fire freely. Null model
	// clears the weapon channel.
	public void SetWeapon(PackedScene model, EHand hand = EHand.Right)
	{
		if (model == _weaponScene && hand == _weaponHand)
		{
			return;
		}
		_weaponScene = model;
		_weaponHand = hand;
		ApplyWeapon();
	}

	// Sets the transient consumable model. No-op when unchanged so the per-tick
	// call site can fire freely. Null clears the item channel.
	public void SetActiveItem(PackedScene model)
	{
		if (model == _itemScene)
		{
			return;
		}
		_itemScene = model;
		ApplyItem();
	}

	// Hides/shows the weapon model without discarding it (the potion-in-hand
	// swap and the AnimationData.hidesHeldItem poses both route here).
	public void SetWeaponConcealed(bool concealed)
	{
		_weaponConcealed = concealed;
		if (_weaponHolderRight != null)
		{
			_weaponHolderRight.Visible = !concealed;
		}
		if (_weaponHolderLeft != null)
		{
			_weaponHolderLeft.Visible = !concealed;
		}
	}

	private void ApplyWeapon()
	{
		// Sockets not built yet — BuildSockets re-applies the latched scene.
		// SwapInstance frees the old instance regardless of which holder it sat
		// in, so a hand change moves the weapon to the now-active holder.
		Node3D holder = ActiveWeaponHolder;
		if (holder == null)
		{
			return;
		}
		SwapInstance(ref _weaponInstance, holder, _weaponScene);
	}

	private void ApplyItem()
	{
		if (_itemHolder == null)
		{
			return;
		}
		SwapInstance(ref _itemInstance, _itemHolder, _itemScene);
	}

	private static void SwapInstance(ref Node3D current, Node3D holder, PackedScene model)
	{
		if (current != null)
		{
			current.QueueFree();
			current = null;
		}
		if (model != null)
		{
			current = model.Instantiate() as Node3D;
			if (current != null)
			{
				holder.AddChild(current);
			}
		}
	}

	private static Skeleton3D FindSkeleton(Node node)
	{
		if (node == null)
		{
			return null;
		}
		if (node is Skeleton3D skeleton)
		{
			return skeleton;
		}
		foreach (Node child in node.GetChildren())
		{
			Skeleton3D found = FindSkeleton(child);
			if (found != null)
			{
				return found;
			}
		}
		return null;
	}
}
