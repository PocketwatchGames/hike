// The moments a merchant remarks on at the trade screen, spelled as the
// `shop:<moment>` keys of a conversation sheet. A character's sheet names the
// ones it says its own way; the rest fall back to SimData.merchantDefaults.
//
// Plain C# with no Godot dependency: tools/conversation_import compiles this
// same file to validate the sheet, so this list is the only one.
public static class ShopMoment
{
	public const string SheetPrefix = "shop:";

	// Opening the screen to trade / to give gifts.
	public const string GreetTrade = "greet_trade";
	public const string GreetGift = "greet_gift";
	// The party moved an item onto the offer side.
	public const string OfferTrade = "offer_trade";
	public const string OfferGift = "offer_gift";
	// The party took an offered item back.
	public const string TakeBack = "take_back";
	// The party picked an item out of the merchant's stock, or put one back.
	public const string PickStock = "pick_stock";
	public const string ReturnStock = "return_stock";
	// Leaving with items still on the offer side and no room for them.
	public const string EmptySack = "empty_sack";
	// Trade pressed with nothing on either side / a deal the merchant loses on.
	public const string TradeNothing = "trade_nothing";
	public const string TradeRefused = "trade_refused";
	// A completed trade, with and without a loyalty gift thrown in.
	public const string TradeDone = "trade_done";
	public const string TradeDoneGift = "trade_done_gift";
	// Give pressed with nothing offered / the merchant has nothing to give back
	// / none of the offer is wanted.
	public const string GiftNothingOffered = "gift_nothing_offered";
	public const string GiftNothingToReturn = "gift_nothing_to_return";
	public const string GiftNoneAccepted = "gift_none_accepted";
	// A gift taken: a gift came back / some of the offer was refused / all taken.
	public const string GiftRewarded = "gift_rewarded";
	public const string GiftPartial = "gift_partial";
	public const string GiftThanks = "gift_thanks";

	public static readonly string[] All =
	{
		GreetTrade, GreetGift, OfferTrade, OfferGift, TakeBack, PickStock, ReturnStock, EmptySack,
		TradeNothing, TradeRefused, TradeDone, TradeDoneGift,
		GiftNothingOffered, GiftNothingToReturn, GiftNoneAccepted, GiftRewarded, GiftPartial, GiftThanks,
	};

	public static bool IsKnown(string moment)
	{
		return System.Array.IndexOf(All, moment) >= 0;
	}
}
