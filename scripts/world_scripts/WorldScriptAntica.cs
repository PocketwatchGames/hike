using Godot;

// Antica's scripted events: the intro fairy's tutorial.
//
// Each named box below is a painter trigger. Whether it is live is the box's own
// data, not this script's: each carries a disabled variable (fairy_follow and
// the three combat lessons go off at antica_bridge_opened — an Unlock hook on
// the bridge portcullis — tut_follow at antica_lessons_done, the guide boxes at
// antica_guide_arrived, the rest at antica_dead_friend_found), and the lesson
// boxes repeat on every entry.
//
//   New game          -> her greeting; its last line vanishes her.
//   fairy_follow      -> appears at the `bear_body` waypoint, says `guts`.
//     (leaving it vanishes her again)
//   tut_melee         -> appears over `tut_goblin`, says `melee`.
//   tut_sneak         -> appears at the `sneak_waypoint`, says `sneak`.
//   tut_ranged        -> appears over `goblin_ranged`, says `ranged`.
//     (leaving any of those three vanishes her again)
//   tut_follow        -> appears over the `this_way1` waypoint, says `this_way`.
//   this_way2_start   -> lessons over; appears over `this_way2`, says
//                        `this_way2`.
//   this_way3_start   -> appears over `this_way3`, says `smell`.
//   this_way3_talk    -> guiding over; appears over `this_way4`, says
//                        `gross_man`.
//     (leaving any of those four vanishes her again)
//   tut_look          -> appears over `lantern_chest`, says `look`.
//   tut_spade         -> appears over `dead_friend`, says `tasty`.
//     (leaving either vanishes her again)
//   search dead_friend-> she vanishes for good; antica_dead_friend_found is set
//                        (what the festering man's conversation branches on).
//
// A lesson line is said only once she has arrived, and only if the player is
// still in the box by then. Every name here is a painter placement name or a
// conversation key of hers, so a rename on either side has to be made here too.
[GlobalClass]
public partial class WorldScriptAntica : WorldScriptData
{
    private const string IntroFairy = "intro_fairy";

    // Variables (declared in worlds/shared/script_variables).
    private const string LessonsDone = "antica_lessons_done";
    private const string GuideArrived = "antica_guide_arrived";
    private const string DeadFriendFound = "antica_dead_friend_found";

    // Where she appears: waypoints and named entities.
    private const string BearBody = "bear_body";
    private const string TutGoblin = "tut_goblin";
    private const string SneakSpot = "sneak_waypoint";
    private const string RangedSpot = "goblin_ranged";
    private const string ThisWay1 = "this_way1";
    private const string ThisWay2 = "this_way2";
    private const string ThisWay3 = "this_way3";
    private const string ThisWay4 = "this_way4";
    private const string LanternChest = "lantern_chest";
    private const string DeadFriend = "dead_friend";

    // Trigger boxes.
    private const string BodyTrigger = "fairy_follow";
    private const string MeleeTrigger = "tut_melee";
    private const string SneakTrigger = "tut_sneak";
    private const string RangedTrigger = "tut_ranged";
    private const string FollowTrigger = "tut_follow";
    private const string ThisWay2Trigger = "this_way2_start";
    private const string ThisWay3Trigger = "this_way3_start";
    private const string TalkTrigger = "this_way3_talk";
    private const string LookTrigger = "tut_look";
    private const string SpadeTrigger = "tut_spade";

    // Her conversation keys, said as standing lines.
    private const string SayGuts = "guts";
    private const string SayMelee = "melee";
    private const string SaySneak = "sneak";
    private const string SayRanged = "ranged";
    private const string SayThisWay = "this_way";
    private const string SayThisWay2 = "this_way2";
    private const string SaySmell = "smell";
    private const string SayGrossMan = "gross_man";
    private const string SayLook = "look";
    private const string SayTasty = "tasty";

    // How long the party lies there before the fairy speaks.
    [Export(PropertyHint.Range, "0,10,0.1")] public float introConversationDelaySeconds = 1.5f;
    // How long she lingers after the player steps out of a box before she
    // vanishes. Stepping back in, or into another box, cancels it.
    [Export(PropertyHint.Range, "0,5,0.1")] public float leaveGraceSeconds = 1f;

    // The party wakes to the fairy talking to them.
    public override void OnNewGame(WorldScriptApi api)
    {
        api.After(introConversationDelaySeconds, a => a.StartConversation(IntroFairy));
    }

    public override void OnTriggerEntered(WorldScriptApi api, string trigger, TriggerActor who)
    {
        switch (trigger)
        {
            case BodyTrigger:
                AppearAndSay(api, BearBody, trigger, SayGuts);
                break;
            case MeleeTrigger:
                AppearAndSay(api, TutGoblin, trigger, SayMelee);
                break;
            case SneakTrigger:
                AppearAndSay(api, SneakSpot, trigger, SaySneak);
                break;
            case RangedTrigger:
                AppearAndSay(api, RangedSpot, trigger, SayRanged);
                break;
            case FollowTrigger:
                AppearAndSay(api, ThisWay1, trigger, SayThisWay);
                break;
            case ThisWay2Trigger:
                api.SetBool(LessonsDone, true);
                AppearAndSay(api, ThisWay2, trigger, SayThisWay2);
                break;
            case ThisWay3Trigger:
                AppearAndSay(api, ThisWay3, trigger, SaySmell);
                break;
            case TalkTrigger:
                api.SetBool(GuideArrived, true);
                AppearAndSay(api, ThisWay4, trigger, SayGrossMan);
                break;
            case LookTrigger:
                AppearAndSay(api, LanternChest, trigger, SayLook);
                break;
            case SpadeTrigger:
                AppearAndSay(api, DeadFriend, trigger, SayTasty);
                break;
        }
    }

    // Leaving any box sends her away (after leaveGraceSeconds) until the next.
    public override void OnTriggerExited(WorldScriptApi api, string trigger, TriggerActor who)
    {
        switch (trigger)
        {
            case BodyTrigger:
            case MeleeTrigger:
            case SneakTrigger:
            case RangedTrigger:
            case FollowTrigger:
            case ThisWay2Trigger:
            case ThisWay3Trigger:
            case TalkTrigger:
            case LookTrigger:
            case SpadeTrigger:
                api.VanishAfter(IntroFairy, leaveGraceSeconds);
                break;
        }
    }

    // Searching the friend's body ends the tutorial: she goes for good, and the
    // variable switches off every box still live.
    public override void OnInteracted(WorldScriptApi api, string name)
    {
        if (name == DeadFriend && !api.GetBool(DeadFriendFound))
        {
            api.SetBool(DeadFriendFound, true);
            api.Vanish(IntroFairy);
        }
    }

    // She vanishes (if she is showing) and reappears at `at`, staying there; she
    // speaks `line` (if any) only once she has arrived and the player is still
    // in the box.
    private static void AppearAndSay(WorldScriptApi api, string at, string trigger, string line)
    {
        api.Reappear(IntroFairy, at, a =>
        {
            if (line != null && a.IsTriggerOccupied(trigger))
            {
                a.Say(IntroFairy, line);
            }
        });
    }
}
