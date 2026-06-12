using System;

// Coarse classification flags on ItemData. An item can carry several at once
// (a roast is Meat | Food; a fairy corpse is Ingredient | Gross). Mobs read
// these through their MobData.itemPreferences taste model to decide how much
// they value an offered item (Mob.PerUnitValue / CalculatePersonalValue) — a
// dog only values Meat, a villager has layered likes and dislikes.
// Wire values are stable — append new bits, never reassign existing ones, so
// existing item .tres files keep loading.
[Flags]
public enum EItemType
{
    None = 0,
    Meat = 1 << 0,
    Weapon = 1 << 1,
    Food = 1 << 2,
    Armor = 1 << 3,
    Magic = 1 << 4,
    Potion = 1 << 5,
    Ingredient = 1 << 6,
    Gross = 1 << 7,
    Common = 1 << 8,
}
