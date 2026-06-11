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
	// The held torch rides the off (left) hand so it can coexist with a baked
	// mob weapon, but it's hidden whenever a weapon is actually drawn (see
	// UpdateTorchVisibility), satisfying "show the torch when no weapon shows".
	private Node3D _torchHolder;

	// Desired scenes are latched even before the sockets exist so a SetWeapon
	// that races the deferred build is applied once BuildSockets runs.
	private PackedScene _weaponScene;
	private EHand _weaponHand = EHand.Right;
	private PackedScene _itemScene;
	private PackedScene _torchScene;
	private Node3D _weaponInstance;
	private Node3D _itemInstance;
	private Node3D _torchInstance;
	private bool _torchLit;
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
		_weaponHolderRight = BuildHandSocket(skeleton, boneName, false, "HandSocketRight", "WeaponHolderRight");
		_weaponHolderLeft = BuildHandSocket(skeleton, leftBoneName, true, "HandSocketLeft", "WeaponHolderLeft");
		_weaponHolderRight.Visible = !_weaponConcealed;
		_weaponHolderLeft.Visible = !_weaponConcealed;
		// The transient consumable always rides the right hand.
		_itemHolder = new Node3D { Name = "ItemHolder" };
		_weaponHolderRight.GetParent().AddChild(_itemHolder);
		// The held torch rides the left hand alongside the left weapon holder.
		_torchHolder = new Node3D { Name = "TorchHolder" };
		_weaponHolderLeft.GetParent().AddChild(_torchHolder);
		// Apply anything latched before the sockets existed.
		ApplyWeapon();
		ApplyItem();
		ApplyTorch();
	}

	// Builds a BoneAttachment3D for one wrist and returns its weapon holder.
	private static Node3D BuildHandSocket(Skeleton3D skeleton, StringName bone, bool leftSide, string socketName, string holderName)
	{
		var socket = new BoneAttachment3D { Name = socketName, BoneName = ResolveBoneName(skeleton, bone, leftSide) };
		skeleton.AddChild(socket);
		var holder = new Node3D { Name = holderName };
		socket.AddChild(holder);
		return holder;
	}

	// Resolves the wrist/hand bone to attach to. Prefers the authored name, but
	// falls back to a fuzzy hand/wrist match on the requested side so the same
	// component works across rigs with different naming (the player rig uses
	// `R_wrist_joint`; the goblin rig names its bones `Hand`/`Forearm`-style).
	private static string ResolveBoneName(Skeleton3D skeleton, StringName preferred, bool leftSide)
	{
		if (skeleton.FindBone(preferred) >= 0)
		{
			return preferred.ToString();
		}
		int firstHand = -1;
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			string name = skeleton.GetBoneName(i);
			string lower = name.ToLower();
			if (!lower.Contains("hand") && !lower.Contains("wrist"))
			{
				continue;
			}
			if (BoneIsSide(lower, leftSide))
			{
				return name;
			}
			if (firstHand < 0)
			{
				firstHand = i;
			}
		}
		if (firstHand >= 0)
		{
			return skeleton.GetBoneName(firstHand);
		}
		GD.PushWarning($"HeldItemVisual: no bone matching '{preferred}' on skeleton; held items may not track the hand.");
		return preferred.ToString();
	}

	// True when a lowercased bone name denotes the requested side. Handles the
	// common conventions across rigs: a standalone L/R token ("L Hand",
	// "Hand_R", "Hand.l") and the spelled-out word ("LeftHand").
	private static bool BoneIsSide(string lower, bool leftSide)
	{
		if (lower.Contains(leftSide ? "left" : "right"))
		{
			return true;
		}
		string sideTag = leftSide ? "l" : "r";
		foreach (string token in lower.Split(new[] { ' ', '_', '.', '-', '|', ':' }, System.StringSplitOptions.RemoveEmptyEntries))
		{
			if (token == sideTag)
			{
				return true;
			}
		}
		return false;
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
		UpdateTorchVisibility();
	}

	// Sets the persistent held-torch model (a HeldTorch scene). No-op when
	// unchanged so the per-refresh call site can fire freely. Null clears it.
	public void SetTorch(PackedScene model)
	{
		if (model == _torchScene)
		{
			return;
		}
		_torchScene = model;
		ApplyTorch();
	}

	// Lights/extinguishes the held torch (swaps its head visual and toggles the
	// flame fx). Latched so a lit state set before the model exists is applied
	// once the torch instance is built.
	public void SetTorchLit(bool lit)
	{
		_torchLit = lit;
		if (_torchInstance is HeldTorch torch)
		{
			torch.SetLit(lit);
		}
	}

	private void ApplyTorch()
	{
		if (_torchHolder == null)
		{
			return;
		}
		SwapInstance(ref _torchInstance, _torchHolder, _torchScene);
		if (_torchInstance is HeldTorch torch)
		{
			torch.SetLit(_torchLit);
		}
		UpdateTorchVisibility();
	}

	// The torch shows only when no weapon and no transient item are visible, so
	// it reads as "what you're holding when your weapon is away". A concealed
	// weapon (sheathed / unarmed pose) or an empty weapon channel both count as
	// "no weapon shown".
	private void UpdateTorchVisibility()
	{
		if (_torchHolder == null)
		{
			return;
		}
		bool weaponShown = !_weaponConcealed && _weaponInstance != null;
		bool itemShown = _itemInstance != null;
		_torchHolder.Visible = _torchInstance != null && !weaponShown && !itemShown;
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
		UpdateTorchVisibility();
	}

	private void ApplyItem()
	{
		if (_itemHolder == null)
		{
			return;
		}
		SwapInstance(ref _itemInstance, _itemHolder, _itemScene);
		UpdateTorchVisibility();
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
