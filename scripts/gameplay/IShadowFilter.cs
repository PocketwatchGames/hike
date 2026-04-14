// Opt-in filter for entities that should sometimes not cast a shadow even
// when their scene-tree visibility says otherwise. ShadowMapRenderer checks
// this on every entity root before descending into its caster nodes. The
// default (interface not implemented) is "cast".
//
// This is deliberately separate from scene-tree Visible. Some visibility
// toggles — notably the ceiling-clip cull in GameClient.CullProps — hide an
// entity from the main camera while the shadow should still cast (receivers
// above the clip are discarded at the receiver, so an invisible clipped
// entity's shadow still needs to appear on geometry below the clip). Mob
// discovery state is the opposite: a mob the player doesn't know about
// should be totally absent from the world, including its shadow.
public interface IShadowFilter
{
    bool CastsShadow { get; }
}
