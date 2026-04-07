using Godot;

[GlobalClass]
public partial class DebugSphere : DebugShape
{
	public static DebugSphere Create(Node parent, Color color, float fadeTime, Vector3 position, float radius)
	{
		var sphere = new DebugSphere();
		sphere.Init(color, fadeTime, position);

		var sphereMesh = new SphereMesh();
		sphereMesh.Radius = radius;
		sphereMesh.Height = radius * 2f;
		sphereMesh.Material = sphere._material;

		var meshInstance = new MeshInstance3D();
		meshInstance.Mesh = sphereMesh;
		sphere.AddChild(meshInstance);

		parent.AddChild(sphere);
		return sphere;
	}
}
