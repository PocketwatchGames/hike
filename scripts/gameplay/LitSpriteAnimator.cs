using Godot;

// Drives a LitSprite's Texture / RegionRect from a Godot SpriteFrames
// resource (authored via the built-in SpriteFrames editor panel). Exists
// instead of AnimatedSprite3D because LitSprite's shader resolves texels
// via texelFetch from sprite_region_origin + sprite_size uniforms, so
// animation is just "swap the region rect" — shadow proxy, water
// reflection, and AO decal follow automatically through LitSprite.SetFrame.
//
// SpriteFrames frames are typically AtlasTextures (when "Add frames from
// sprite sheet" was used); we unwrap each to (atlas, region). Plain
// Texture2D frames are treated as full-rect. Keep all frames of one
// animation in a single atlas — crossing atlases swaps LitSprite.Texture,
// which fires TextureChanged and rebuilds the material, losing per-instance
// uniforms until their next setter call.
[GlobalClass]
public partial class LitSpriteAnimator : Node
{
    [Export] public LitSprite target;
    [Export] public SpriteFrames frames;
    [Export] public StringName defaultAnimation;
    [Export] public float speed = 1f;

    public StringName CurrentAnimation { get; private set; }
    public bool Finished { get; private set; }

    private int _frame;
    private float _accum;

    public override void _Ready()
    {
        if (defaultAnimation != default)
        {
            Play(defaultAnimation);
        }
        // Mirror the LitSprite trick: subscribe to the target's
        // VisibilityChanged once and toggle SetProcess based on its
        // IsVisibleInTree state. Engine then stops dispatching _Process to
        // animators whose target sprite is hidden, so we don't pay the
        // per-frame visibility check at high mob count.
        if (target != null)
        {
            target.VisibilityChanged += OnTargetVisibilityChanged;
            SetProcess(target.IsVisibleInTree());
        }
    }

    private void OnTargetVisibilityChanged()
    {
        SetProcess(target != null && target.IsVisibleInTree());
    }

    public void Play(StringName name)
    {
        if (CurrentAnimation == name && !Finished)
        {
            return;
        }
        if (frames == null || !frames.HasAnimation(name))
        {
            GD.PushError($"LitSpriteAnimator '{Name}': unknown animation '{name}'");
            return;
        }
        CurrentAnimation = name;
        _frame = 0;
        _accum = 0f;
        Finished = false;
        ApplyFrame();
    }

    public override void _Process(double delta)
    {
        if (target == null || frames == null || Finished)
        {
            return;
        }
        // No target.IsVisibleInTree gate — the VisibilityChanged hookup in
        // _Ready toggles SetProcess so the engine stops dispatching to us
        // whenever the target sprite is hidden anywhere up the tree.
        // Animations resume from the last frame when visibility returns.
        if (CurrentAnimation == default || !frames.HasAnimation(CurrentAnimation))
        {
            return;
        }
        int count = frames.GetFrameCount(CurrentAnimation);
        if (count == 0)
        {
            return;
        }

        using var _profAnim = Profiler.Sample("LitSpriteAnimator.Process");

        float fps = (float)frames.GetAnimationSpeed(CurrentAnimation);
        float frameDuration = frames.GetFrameDuration(CurrentAnimation, _frame);
        if (frameDuration <= 0f)
        {
            frameDuration = 1f;
        }
        _accum += (float)delta * speed * fps / frameDuration;

        while (_accum >= 1f)
        {
            _accum -= 1f;
            _frame++;
            if (_frame >= count)
            {
                if (frames.GetAnimationLoop(CurrentAnimation))
                {
                    _frame = 0;
                }
                else
                {
                    _frame = count - 1;
                    Finished = true;
                    break;
                }
            }
        }
        ApplyFrame();
    }

    private void ApplyFrame()
    {
        if (target == null || frames == null || CurrentAnimation == default)
        {
            return;
        }
        if (!frames.HasAnimation(CurrentAnimation) || frames.GetFrameCount(CurrentAnimation) == 0)
        {
            return;
        }
        Texture2D tex = frames.GetFrameTexture(CurrentAnimation, _frame);
        if (tex == null)
        {
            return;
        }

        if (tex is AtlasTexture atlas && atlas.Atlas != null)
        {
            if (target.Texture != atlas.Atlas)
            {
                target.Texture = atlas.Atlas;
            }
            target.SetFrame(atlas.Region);
        }
        else
        {
            if (target.Texture != tex)
            {
                target.Texture = tex;
            }
            Vector2 size = tex.GetSize();
            target.SetFrame(new Rect2(0, 0, size.X, size.Y));
        }
    }
}
