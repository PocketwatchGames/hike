using Godot;
using System.Collections.Generic;

// A 9-slice for pixel art that never stretches a texel. The texture is authored at
// NATIVE resolution and drawn magnified by `pixelScale`. On each axis the start cap,
// as many copies of the centre as the length needs, and the end cap are laid end to
// end; neighbouring pieces overlap by `overlap` native pixels of centre content and
// are alpha-blended across it, then the alpha is re-thresholded so edges stay hard.
// Below the source size the two halves simply overlap more.
//
// The composite is built on the CPU per native size and cached, already magnified,
// so it draws 1:1 and does not depend on the control's texture_filter. `modulate`
// is applied at draw time, so states that differ only in tint share one image.
//
// [Tool]: drawn by the editor's theme preview and inspector.
[Tool]
[GlobalClass]
public partial class PixelSliceStyleBox : StyleBox
{
	private Texture2D _texture;
	private int _pixelScale = 4;
	private int _sliceLeft = 4;
	private int _sliceTop = 3;
	private int _sliceRight = 4;
	private int _sliceBottom = 3;
	private Vector2I _overlap = new Vector2I(10, 6);
	private Color _modulate = Colors.White;
	private bool _alphaThreshold = true;

	// The texture at native pixel-art resolution (one texel per art pixel).
	[Export]
	public Texture2D texture
	{
		get => _texture;
		set
		{
			if (_texture == value)
			{
				return;
			}
			if (_texture is not null)
			{
				_texture.Changed -= OnTextureChanged;
			}
			_texture = value;
			if (_texture is not null)
			{
				_texture.Changed += OnTextureChanged;
			}
			EmitChanged();
		}
	}

	[Export(PropertyHint.Range, "1,16,1")]
	public int pixelScale
	{
		get => _pixelScale;
		set { _pixelScale = Mathf.Max(1, value); EmitChanged(); }
	}

	// Slice margins in native texels: the caps that are never repeated.
	[Export(PropertyHint.Range, "0,64,1")]
	public int sliceLeft
	{
		get => _sliceLeft;
		set { _sliceLeft = Mathf.Max(0, value); EmitChanged(); }
	}

	[Export(PropertyHint.Range, "0,64,1")]
	public int sliceTop
	{
		get => _sliceTop;
		set { _sliceTop = Mathf.Max(0, value); EmitChanged(); }
	}

	[Export(PropertyHint.Range, "0,64,1")]
	public int sliceRight
	{
		get => _sliceRight;
		set { _sliceRight = Mathf.Max(0, value); EmitChanged(); }
	}

	[Export(PropertyHint.Range, "0,64,1")]
	public int sliceBottom
	{
		get => _sliceBottom;
		set { _sliceBottom = Mathf.Max(0, value); EmitChanged(); }
	}

	// Width of each blended join, in native texels, per axis. Clamped to half the
	// centre so two joins never overlap.
	[Export]
	public Vector2I overlap
	{
		get => _overlap;
		set { _overlap = value; EmitChanged(); }
	}

	[Export]
	public Color modulate
	{
		get => _modulate;
		set { _modulate = value; EmitChanged(); }
	}

	// Snap blended alpha back to fully opaque/transparent. On for hard-edged pixel art;
	// off for art with authored soft alpha, which keeps the blended coverage as-is.
	[Export]
	public bool alphaThreshold
	{
		get => _alphaThreshold;
		set { _alphaThreshold = value; EmitChanged(); }
	}

	private const int BYTES_PER_PIXEL = 4;
	private const float ALPHA_THRESHOLD = 0.5f;

	private readonly record struct CompositeKey(ulong textureId, int pixelScale,
		int sliceLeft, int sliceTop, int sliceRight, int sliceBottom, Vector2I overlap, bool alphaThreshold,
		Vector2I size);

	// Shared across instances so hover/pressed/normal reuse one composite per size.
	// Keyed by every input, so editing a parameter needs no invalidation.
	private static readonly Dictionary<CompositeKey, ImageTexture> s_composites = new();
	private static readonly Dictionary<ulong, Image> s_sources = new();

	// A reimport reloads the texture IN PLACE (same instance id), so the caches would
	// otherwise keep compositing the old pixels until the process restarts.
	private void OnTextureChanged()
	{
		ulong textureId = _texture.GetInstanceId();
		s_sources.Remove(textureId);
		List<CompositeKey> stale = new List<CompositeKey>();
		foreach (CompositeKey key in s_composites.Keys)
		{
			if (key.textureId == textureId)
			{
				stale.Add(key);
			}
		}
		foreach (CompositeKey key in stale)
		{
			s_composites.Remove(key);
		}
		EmitChanged();
	}

	public override void _Draw(Rid toCanvasItem, Rect2 rect)
	{
		if (_texture is null)
		{
			return;
		}
		Vector2I size = new Vector2I(
			Mathf.Max(1, Mathf.RoundToInt(rect.Size.X / _pixelScale)),
			Mathf.Max(1, Mathf.RoundToInt(rect.Size.Y / _pixelScale)));
		ImageTexture composite = GetComposite(size);
		if (composite is null)
		{
			return;
		}
		Rect2 drawRect = new Rect2(rect.Position.Round(), size * _pixelScale);
		RenderingServer.CanvasItemAddTextureRect(toCanvasItem, drawRect, composite.GetRid(), false, _modulate);
	}

	private ImageTexture GetComposite(Vector2I size)
	{
		ulong textureId = _texture.GetInstanceId();
		CompositeKey key = new CompositeKey(textureId, _pixelScale, _sliceLeft, _sliceTop,
			_sliceRight, _sliceBottom, _overlap, _alphaThreshold, size);
		if (s_composites.TryGetValue(key, out ImageTexture cached))
		{
			return cached;
		}
		Image source = GetSource(textureId);
		if (source is null)
		{
			return null;
		}
		Image image = Composite(source, size);
		image.Resize(size.X * _pixelScale, size.Y * _pixelScale, Image.Interpolation.Nearest);
		ImageTexture result = ImageTexture.CreateFromImage(image);
		s_composites[key] = result;
		return result;
	}

	private Image GetSource(ulong textureId)
	{
		if (s_sources.TryGetValue(textureId, out Image cached))
		{
			return cached;
		}
		// Null under the dummy renderer (headless), where there is nothing to draw anyway.
		Image image = _texture.GetImage();
		if (image is null)
		{
			return null;
		}
		if (image.IsCompressed())
		{
			image.Decompress();
		}
		image.Convert(Image.Format.Rgba8);
		s_sources[textureId] = image;
		return image;
	}

	private Image Composite(Image source, Vector2I size)
	{
		int srcW = source.GetWidth();
		int srcH = source.GetHeight();
		AxisMap xs = BuildAxis(size.X, srcW, _sliceLeft, _sliceRight, _overlap.X);
		AxisMap ys = BuildAxis(size.Y, srcH, _sliceTop, _sliceBottom, _overlap.Y);
		byte[] src = source.GetData();
		byte[] dst = new byte[size.X * size.Y * BYTES_PER_PIXEL];

		for (int y = 0; y < size.Y; y++)
		{
			float ty = ys.t[y];
			for (int x = 0; x < size.X; x++)
			{
				float tx = xs.t[x];
				// Premultiplied bilinear mix of the (up to) four pieces covering this texel,
				// so a transparent texel's stored colour never bleeds into the fringe.
				float r = 0f, g = 0f, b = 0f, a = 0f;
				Accumulate(src, srcW, xs.a[x], ys.a[y], (1f - tx) * (1f - ty), ref r, ref g, ref b, ref a);
				Accumulate(src, srcW, xs.b[x], ys.a[y], tx * (1f - ty), ref r, ref g, ref b, ref a);
				Accumulate(src, srcW, xs.a[x], ys.b[y], (1f - tx) * ty, ref r, ref g, ref b, ref a);
				Accumulate(src, srcW, xs.b[x], ys.b[y], tx * ty, ref r, ref g, ref b, ref a);

				int o = (y * size.X + x) * BYTES_PER_PIXEL;
				if (a <= 0f || (_alphaThreshold && a < ALPHA_THRESHOLD))
				{
					continue;
				}
				dst[o] = (byte)Mathf.Clamp(Mathf.RoundToInt(r / a), 0, 255);
				dst[o + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(g / a), 0, 255);
				dst[o + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(b / a), 0, 255);
				dst[o + 3] = _alphaThreshold ? (byte)255 : (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255);
			}
		}
		return Image.CreateFromData(size.X, size.Y, false, Image.Format.Rgba8, dst);
	}

	private static void Accumulate(byte[] src, int srcW, int sx, int sy, float w,
		ref float r, ref float g, ref float b, ref float a)
	{
		if (w <= 0f)
		{
			return;
		}
		int i = (sy * srcW + sx) * BYTES_PER_PIXEL;
		float alpha = src[i + 3] / 255f * w;
		r += src[i] * alpha;
		g += src[i + 1] * alpha;
		b += src[i + 2] * alpha;
		a += alpha;
	}

	// Per destination texel: the source texel of the piece it is in (`a`), and while
	// inside a join, the next piece's texel (`b`) and the blend weight toward it (`t`).
	private sealed class AxisMap
	{
		public int[] a;
		public int[] b;
		public float[] t;
	}

	private readonly record struct Join(int start, int end);

	// Lays one axis out as copies of the source at increasing offsets: the first copy
	// (offset 0) supplies the start cap, copies spaced by (centre - overlap) supply the
	// centre, and the last (offset dest - src) supplies the end cap. Every join lies in
	// centre content of both copies it blends.
	private static AxisMap BuildAxis(int dest, int length, int capStart, int capEnd, int overlapTexels)
	{
		capStart = Mathf.Clamp(capStart, 0, length - 1);
		capEnd = Mathf.Clamp(capEnd, 0, length - 1 - capStart);
		int centre = length - capStart - capEnd;
		int o = Mathf.Clamp(overlapTexels, 0, centre / 2);
		int period = centre - o;
		int endOffset = dest - length;

		List<int> offsets = new List<int> { 0 };
		List<Join> joins = new List<Join>();
		if (dest >= capStart + capEnd + o && period > 0)
		{
			// The most centre copies whose leading join still leaves room for the end join.
			int copies = Mathf.Max(0, Mathf.FloorToInt((float)(dest - capStart - capEnd - 2 * o) / period));
			for (int k = 1; k <= copies; k++)
			{
				offsets.Add(k * period);
				joins.Add(new Join(k * period + capStart, k * period + capStart + o));
			}
			int end = Mathf.Min(copies * period + length - capEnd, dest - capEnd);
			joins.Add(new Join(end - o, end));
		}
		else
		{
			// Too short to fit a full join: blend whatever centre the two caps share.
			int end = Mathf.Max(0, dest - capEnd);
			int width = Mathf.Clamp(dest - capStart - capEnd, 0, o);
			joins.Add(new Join(end - width, end));
		}
		offsets.Add(endOffset);

		AxisMap map = new AxisMap { a = new int[dest], b = new int[dest], t = new float[dest] };
		int piece = 0;
		for (int d = 0; d < dest; d++)
		{
			while (piece < joins.Count && d >= joins[piece].end)
			{
				piece++;
			}
			int srcA = Mathf.Clamp(d - offsets[piece], 0, length - 1);
			map.a[d] = srcA;
			if (piece < joins.Count && d >= joins[piece].start)
			{
				Join join = joins[piece];
				map.b[d] = Mathf.Clamp(d - offsets[piece + 1], 0, length - 1);
				map.t[d] = (d - join.start + 0.5f) / (join.end - join.start);
			}
			else
			{
				map.b[d] = srcA;
				map.t[d] = 0f;
			}
		}
		return map;
	}
}
