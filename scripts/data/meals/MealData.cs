using Godot;

// A meal cooked in a cookpot. It is never an item: cooking fills the pot with it
// (CookpotSimState.Meal), and anyone may eat from the pot until it goes off
// `potHours` after cooking — eating never empties it. Listed in SimData.meals,
// which is the cookpot's whole recipe list; known through the same recipe
// discovery as crafted items (SimState.IsRecipeDiscovered).
// [Tool]: reachable from ZoneData.zoneLoot through a cookbook scroll's
// MealTeachable — see the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class MealData : Resource
{
	[Export] public StringName displayName;
	[Export(PropertyHint.MultilineText)] public string description;
	[Export] public Texture2D icon;

	// What cooks into it at a cookpot.
	[Export] public RecipeData recipe;

	// Applied in order to whoever eats, after hunger is filled. A meal's status
	// is EEffectCategory.Meal, so eating replaces whatever meal was last eaten.
	[Export] public Godot.Collections.Array<ItemEffect> effects = new();

	// The colour of the pot's contents while this meal is in it.
	[Export] public Color potColor = new(0.55f, 0.35f, 0.2f);
	// In-world hours a cooked pot stays good.
	[Export(PropertyHint.Range, "1,240,1,or_greater")] public float potHours = 12f;

	public bool HasRecipe => recipe != null && recipe.HasInputs;

	// Eating fills hunger completely.
	public void Eat(Player player)
	{
		if (player == null)
		{
			return;
		}
		player.RestoreNeedFully(ENeed.Hunger);
		ItemEffect.ApplyToSelf(player, effects);
	}
}
