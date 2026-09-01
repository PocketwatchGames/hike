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
// A held scene is authored in one canonical grip space: its ORIGIN is the grip
// point and the item extends along +Y (club, torch, knife all do). What varies
// is the rig — every skeleton orients its hand bone differently — so the socket
// carries a per-rig grip transform that maps that canonical space onto this
// rig's wrist, authored as the grip* / leftGrip* exports below. An item scene
// therefore never bakes a rig-specific offset, and one club is held correctly by
// every character.
//
// To find a rig's numbers: most character FBX ship an authored prop in the hand
// (the goblin's SM_Sword, the polysplit rig's M_Swordsman_Sword). Its transform
// RELATIVE TO THE BONE it hangs on is the rig's own answer for both position and
// orientation — compose it with whatever rotation carries a held scene's +Y onto
// the axis that rig's props run along (+Z on the goblin, +X on polysplit). Read
// those props out of the INSTANTIATED scene, never a live mob: ModelAnimator
// prunes hidden meshes and their sockets at runtime.
//
// Check for a dedicated equip bone FIRST. A rig built for gear often carries one
// (polysplit's R_equip_joint / L_equip_joint sit ~8.6cm out from the wrist), and
// binding to it beats hand-authoring the same offset from the wrist — it is the
// rig's designed grip point and it follows any animation authored on that bone.
// Getting this wrong is not subtle: an item on the wrist orbits a pivot 8.6cm
// off from the one the character animates around, so it traces a visibly
// different arc from the rig's own weapon through the same swing.
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

	// Bones the hand sockets bind to. Defaults are the shared polysplit rig's
	// dedicated equip joints — NOT its wrists, which sit 8.6cm back up the arm and
	// swing on a different radius. Override per rig if a different skeleton is used.
	[Export] public StringName boneName = "R_equip_joint";
	[Export] public StringName leftBoneName = "L_equip_joint";

	// Per-rig grip transform: where a held scene's origin sits on this rig's hand
	// bone, and how its +Y is oriented. Identity (the default) means the bone's
	// own axes already are the grip space — true for the player rig, which every
	// held scene was authored against, and false for an imported character whose
	// wrist axes point somewhere else entirely. Applied to the weapon, consumable
	// and torch holders alike, so everything in a hand agrees.
	[Export] public Vector3 gripOffset = Vector3.Zero;
	[Export] public Vector3 gripRotationDegrees = Vector3.Zero;
	[Export] public Vector3 leftGripOffset = Vector3.Zero;
	[Export] public Vector3 leftGripRotationDegrees = Vector3.Zero;
	// Bone the lit lantern clips to (a belt slot), with a fuzzy waist/pelvis/hip
	// fallback for rigs that name it differently. A rig with no match just hides
	// the lantern when it would otherwise hang on the belt.
	[Export] public StringName beltBoneName = "waist_joint";
	// Local placement of the lantern on the belt socket. Tune per rig in the
	// inspector — bone axes vary, so the default is only a hip-side starting guess.
	[Export] public Vector3 beltOffset = new(0.18f, 0f, 0.05f);
	[Export] public Vector3 beltRotationDegrees = Vector3.Zero;

	private Node3D _weaponHolderRight;
	private Node3D _weaponHolderLeft;
	private Node3D _itemHolder;
	// The held torch/lantern rides the off (left) hand while it's the actively-held
	// unlit prop. Once lit it clips to the belt socket so it stays visible and
	// keeps lighting with the hands free. See UpdateTorchPlacement.
	private Node3D _torchHolder;
	private Node3D _beltHolder;
	// Nearest PhysicsBody3D ancestor (the Mob / Player body) — the stable, body-
	// level node a held torch's world light parents to so the deposit tracks the
	// body rather than the swinging hand bone. Resolved once in BuildSockets.
	private Node3D _carrierRoot;

	// Desired scenes are latched even before the sockets exist so a SetWeapon
	// that races the deferred build is applied once BuildSockets runs.
	private PackedScene _weaponScene;
	private EHand _weaponHand = EHand.Right;
	// Idle Fx the wielded weapon's mods add to the in-hand model (a Flaming
	// sword's flame). Latched so a SetWeaponIdleFx that races the deferred socket
	// build — or arrives before the weapon instance swaps in — re-applies once
	// the HeldWeapon instance exists. Re-pushed after every weapon swap.
	private Godot.Collections.Array<PackedScene> _weaponIdleFx;
	private PackedScene _itemScene;
	private PackedScene _torchScene;
	private Node3D _weaponInstance;
	private Node3D _itemInstance;
	private Node3D _torchInstance;
	private bool _torchLit;
	// Whether the current torch is the one the carrier is actively holding (true)
	// vs a lit torch carried in reserve that should stow on the back (false).
	private bool _torchInHand = true;
	// Carrier root the torch's world light parents to while lit (passed through to
	// HeldTorch.SetLit). Latched so a relight after a model swap reuses it.
	private Node3D _torchLightParent;
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
		_weaponHolderRight = BuildHandSocket(skeleton, boneName, false, "HandSocketRight", "WeaponHolderRight",
			gripOffset, gripRotationDegrees);
		_weaponHolderLeft = BuildHandSocket(skeleton, leftBoneName, true, "HandSocketLeft", "WeaponHolderLeft",
			leftGripOffset, leftGripRotationDegrees);
		_weaponHolderRight.Visible = !_weaponConcealed;
		_weaponHolderLeft.Visible = !_weaponConcealed;
		// The transient consumable always rides the right hand.
		// Sibling of the weapon holder rather than a child, so concealing the weapon
		// to show a potion doesn't hide the potion — hence its own copy of the grip.
		_itemHolder = new Node3D
		{
			Name = "ItemHolder",
			Position = gripOffset,
			RotationDegrees = gripRotationDegrees,
		};
		_weaponHolderRight.GetParent().AddChild(_itemHolder);
		// The held torch rides the left hand alongside the left weapon holder.
		_torchHolder = new Node3D
		{
			Name = "TorchHolder",
			Position = leftGripOffset,
			RotationDegrees = leftGripRotationDegrees,
		};
		_weaponHolderLeft.GetParent().AddChild(_torchHolder);
		// Belt socket for a lit lantern (hands-free, stays visible + lighting).
		// Optional — rigs with no waist/pelvis bone just hide the belted lantern.
		_beltHolder = BuildBeltHolder(skeleton);
		// The body the carried-light deposits from (Mob / Player) — used to light a
		// startLit weapon torch (a goblin's burning torch) so its world light
		// tracks the body, not the hand.
		_carrierRoot = FindCarrierRoot();
		// Apply anything latched before the sockets existed.
		ApplyWeapon();
		ApplyWeaponIdleFx();
		ApplyItem();
		ApplyTorch();
	}

	// Builds the belt socket + holder for the lit lantern. Returns null when the
	// rig has no waist/pelvis bone, in which case a belted lantern is simply
	// hidden. The holder carries the inspector-tunable offset/rotation.
	private Node3D BuildBeltHolder(Skeleton3D skeleton)
	{
		int bone = skeleton.FindBone(beltBoneName);
		if (bone < 0)
		{
			bone = FuzzyBeltBone(skeleton);
		}
		if (bone < 0)
		{
			return null;
		}
		var socket = new BoneAttachment3D { Name = "BeltSocket", BoneName = skeleton.GetBoneName(bone) };
		skeleton.AddChild(socket);
		var holder = new Node3D
		{
			Name = "BeltHolder",
			Position = beltOffset,
			RotationDegrees = beltRotationDegrees,
		};
		socket.AddChild(holder);
		return holder;
	}

	// First bone whose name reads as a waist/hip anchor, in preference order. Used
	// as the belt attach point when the authored beltBoneName isn't present.
	private static int FuzzyBeltBone(Skeleton3D skeleton)
	{
		string[] prefer = { "waist", "pelvis", "hip", "belt", "spine" };
		foreach (string key in prefer)
		{
			for (int i = 0; i < skeleton.GetBoneCount(); i++)
			{
				if (skeleton.GetBoneName(i).ToLower().Contains(key))
				{
					return i;
				}
			}
		}
		return -1;
	}

	// Builds a BoneAttachment3D for one wrist and returns its weapon holder, placed
	// at the rig's grip transform.
	private static Node3D BuildHandSocket(Skeleton3D skeleton, StringName bone, bool leftSide, string socketName, string holderName,
		Vector3 gripOffset, Vector3 gripRotationDegrees)
	{
		var socket = new BoneAttachment3D { Name = socketName, BoneName = ResolveBoneName(skeleton, bone, leftSide) };
		skeleton.AddChild(socket);
		var holder = new Node3D
		{
			Name = holderName,
			Position = gripOffset,
			RotationDegrees = gripRotationDegrees,
		};
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

	// Sets the idle Fx played on the wielded weapon model (a weapon mod's
	// held-weapon visual, e.g. a Flaming sword's flame). Routed to the in-hand
	// HeldWeapon instance, which diffs the set so the per-press call site can
	// fire freely. Null/empty clears the weapon's idle fx.
	public void SetWeaponIdleFx(Godot.Collections.Array<PackedScene> scenes)
	{
		_weaponIdleFx = scenes;
		ApplyWeaponIdleFx();
	}

	private void ApplyWeaponIdleFx()
	{
		if (_weaponInstance is HeldWeapon weapon)
		{
			weapon.SetIdleFx(_weaponIdleFx);
		}
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

	// Extinguishes the held weapon if it's a lit torch — kills its world light and
	// flame (not just the mesh). Called on mob death so a corpse's torch goes dark
	// rather than burning on. Separate from SetWeaponConcealed, which only hides
	// the mesh and is also used for transient anim poses that must keep the flame.
	public void ExtinguishWeaponTorch()
	{
		if (_weaponInstance is HeldTorch torch)
		{
			torch.SetLit(false);
		}
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
		UpdateTorchPlacement();
	}

	// Sets the persistent held-torch model (a HeldTorch scene). `inHand` marks
	// whether the carrier is actively holding this torch (off-hand when free) vs
	// carrying it lit in reserve (stowed on the back). Recreates the instance only
	// when the model changes, so a placement-only change (inHand flip) doesn't
	// re-spawn the torch and flicker its light. Null model clears the channel.
	public void SetTorch(PackedScene model, bool inHand = true)
	{
		bool modelChanged = model != _torchScene;
		_torchScene = model;
		_torchInHand = inHand;
		if (modelChanged)
		{
			ApplyTorch();
		}
		else
		{
			UpdateTorchPlacement();
		}
	}

	// Lights/extinguishes the held torch (swaps its head visual, toggles the flame
	// fx, and brings its world light up/down). lightParent is the carrier root the
	// world light attaches to while lit. Latched so a lit state set before the
	// model exists is applied once the torch instance is built.
	public void SetTorchLit(bool lit, Node3D lightParent = null)
	{
		_torchLit = lit;
		if (lightParent != null)
		{
			_torchLightParent = lightParent;
		}
		if (_torchInstance is HeldTorch torch)
		{
			torch.SetLit(lit, _torchLightParent);
		}
		UpdateTorchPlacement();
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
			torch.SetLit(_torchLit, _torchLightParent);
		}
		UpdateTorchPlacement();
	}

	// Places the held torch/lantern: clipped to the belt whenever lit (hands-free,
	// keeps lighting); held in the off-hand when it's the actively-held *unlit*
	// prop; hidden otherwise. The HeldTorch instance is only reparented / hidden,
	// never freed here, so its world light persists across placement changes.
	private void UpdateTorchPlacement()
	{
		if (_torchHolder == null || _torchInstance == null)
		{
			return;
		}
		bool weaponShown = !_weaponConcealed && _weaponInstance != null;
		bool handBusy = weaponShown || _itemInstance != null;
		Node3D target;
		if (_torchLit)
		{
			// Lit → belt slot (null on rigs without a waist bone → hidden, but the
			// instance stays alive so the light keeps burning).
			target = _beltHolder;
		}
		else if (_torchInHand && !handBusy)
		{
			target = _torchHolder;
		}
		else
		{
			target = null;
		}
		if (target != null)
		{
			Node parent = _torchInstance.GetParent();
			if (parent != target)
			{
				parent?.RemoveChild(_torchInstance);
				target.AddChild(_torchInstance);
			}
			_torchInstance.Visible = true;
		}
		else
		{
			_torchInstance.Visible = false;
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
		// A burning-torch weapon (HeldTorch.startLit) lights itself here, depositing
		// its world light on the carrier body so it illuminates like a real torch.
		// Deferred so the flame/light Fx spawn after the instancing AddChild storm.
		if (_weaponInstance is HeldTorch weaponTorch && weaponTorch.startLit)
		{
			Node3D lightParent = _carrierRoot;
			Callable.From(() => weaponTorch.SetLit(true, lightParent)).CallDeferred();
		}
		// A fresh instance starts with no idle fx; the caller re-pushes the new
		// weapon's idle fx via SetWeaponIdleFx right after SetWeapon. (Replaying
		// the latch here would briefly spawn the PREVIOUS weapon's flame on the
		// new model before that correcting call lands.) The deferred socket-build
		// path replays the latch itself — see BuildSockets.
		UpdateTorchPlacement();
	}

	// Walks ancestors to the nearest PhysicsBody3D — the Mob / Player body that
	// owns this visual. Null only if the visual isn't parented under a body yet.
	private Node3D FindCarrierRoot()
	{
		Node n = GetParent();
		while (n != null)
		{
			if (n is PhysicsBody3D body)
			{
				return body;
			}
			n = n.GetParent();
		}
		return null;
	}

	private void ApplyItem()
	{
		if (_itemHolder == null)
		{
			return;
		}
		SwapInstance(ref _itemInstance, _itemHolder, _itemScene);
		UpdateTorchPlacement();
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
