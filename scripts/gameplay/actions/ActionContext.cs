using System.Collections.Generic;
using Godot;

// Captured at action start; passed to event handlers so they can resolve
// "what to apply this effect to," "which item to decrement," etc. Stable
// for the lifetime of the action — the runner doesn't mutate it after start.
//
// primaryItem is set for slot-driven actions (a weapon swing, a potion use).
// primaryInteractive is set for interactive-driven actions (chest, cookpot).
// The two are not mutually exclusive in principle, but slot-driven actions
// don't currently set primaryInteractive. supportingItems carries lockpicks,
// recipe ingredients, etc.; empty in the common case.
public struct ActionContext
{
	public EActionVerb verb;
	public ItemState primaryItem;
	public IInteractive primaryInteractive;
	// Index of the running action in primaryInteractive.GetActions(). Set
	// when the runner starts an interactive-driven action so OpenInteractive
	// can pass it back to IInteractive.Complete. Unused for slot-driven
	// (weapon / consumable) actions.
	public int interactiveActionIndex;
	public Node3D target;
	public List<ItemState> supportingItems;
	public EInventorySlot? sourceSlot;
	// World position the action originates from, when it isn't implied by the
	// actor — e.g. a found consumable applied on pickup carries the loot's
	// location here so a treasure map can roll its dig spot near where it was
	// found. Zero for the common case (the effect uses the actor instead).
	public Vector3 worldPosition;
}
