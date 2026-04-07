using Godot;

[GlobalClass]
public partial class DebugBox : DebugShape
{
	public static DebugBox Create(Node parent, Color color, float fadeTime, Vector3 from, Vector3 to, float width, float height)
	{
		var box = new DebugBox();
		Vector3 center = (from + to) * 0.5f;
		box.Init(color, fadeTime, center);

		Vector3 delta = to - from;
		float length = delta.Length();

		var boxMesh = new BoxMesh();
		boxMesh.Size = new Vector3(width, height, length);
		boxMesh.Material = box._material;

		var meshInstance = new MeshInstance3D();
		meshInstance.Mesh = boxMesh;
		box.AddChild(meshInstance);

		parent.AddChild(box);
		if (length > 0.0001f && Mathf.Abs(delta.Normalized().Dot(Vector3.Up)) < 0.999f)
		{
			box.LookAt(to, Vector3.Up);
		}
		return box;
	}
}
