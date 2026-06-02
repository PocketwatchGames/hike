using Godot;

// Renders the player's currently-wielded item as a rigid 3D prop attached to a
// hand bone of the skinned character. Two independent channels share one hand
// socket:
//
//   - Weapon channel (persistent): the last-used weapon. It pops into the hand
//     when a weapon action fires and STAYS there between swings; using the other
//     weapon swaps it. Set via SetWeapon.
//   - Item channel (transient): a consumable (potion / scroll) shown only for
//     the duration of its Use action, then cleared back to the weapon. Set via
//     SetActiveItem.
//
// Each item's grip alignment is baked into its own model scene (offset the mesh
// so the scene origin sits in the palm), so this component never authors a
// per-item transform — it just instances the scene under the socket holder.
//
// The hand socket is a BoneAttachment3D built once against the imported
// character Skeleton3D. The rig is an instanced FBX with no hand-authored nodes
// to [Export] against, so the Skeleton3D is found by walking the `visual`
// subtree — the same pattern ModelAnimator uses for its material / mesh-hide
// passes. Socket construction is deferred out of _Ready (CallDeferred) so it
// runs after the scene-instantiation AddChild storm settles, matching the
// MovingLight lifecycle convention.
[GlobalClass]
public partial class HeldItemVisual : Node3D
{
	// Root of the imported character subtree to search for the Skeleton3D.
	// Wire this to the same node ModelAnimator drives as `visual` (PlayerModel).
	[Export] public Node3D visual;

	// Bone the hand socket binds to. Default matches the F_Swordsman rig's right
	// wrist joint; override per rig if a different skeleton is used.
	[Export] public StringName boneName = "R_wrist_joint";

	private BoneAttachment3D _socket;
	private Node3D _weaponHolder;
	private Node3D _itemHolder;

	// Desired scenes are latched even before the socket exists so a SetWeapon
	// that races the deferred build is applied once BuildSocket runs.
	private PackedScene _weaponScene;
	private PackedScene _itemScene;
	private Node3D _weaponInstance;
	private Node3D _itemInstance;
	private bool _weaponConcealed;

	public override void _Ready()
	{
		CallDeferred(MethodName.BuildSocket);
	}

	private void BuildSocket()
	{
		Skeleton3D skeleton = FindSkeleton(visual);
		if (skeleton == null)
		{
			GD.PushError($"HeldItemVisual '{Name}': no Skeleton3D found under `visual`; held-item models disabled.");
			return;
		}
		_socket = new BoneAttachment3D { Name = "HandSocket", BoneName = boneName.ToString() };
		skeleton.AddChild(_socket);
		_weaponHolder = new Node3D { Name = "WeaponHolder" };
		_itemHolder = new Node3D { Name = "ItemHolder" };
		_socket.AddChild(_weaponHolder);
		_socket.AddChild(_itemHolder);
		_weaponHolder.Visible = !_weaponConcealed;
		// Apply anything latched before the socket existed.
		ApplyWeapon();
		ApplyItem();
	}

	// Sets the persistent weapon model. No-op when unchanged so the per-press
	// call site can fire freely. Null clears the weapon channel.
	public void SetWeapon(PackedScene model)
	{
		if (model == _weaponScene)
		{
			return;
		}
		_weaponScene = model;
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
		if (_weaponHolder != null)
		{
			_weaponHolder.Visible = !concealed;
		}
	}

	private void ApplyWeapon()
	{
		// Socket not built yet — BuildSocket re-applies the latched scene.
		if (_weaponHolder == null)
		{
			return;
		}
		SwapInstance(ref _weaponInstance, _weaponHolder, _weaponScene);
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
